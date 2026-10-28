using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wings.NotificationHub.Client;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Infrastructure.Providers;

namespace Wings.NotificationHub.IntegrationTests;

[Collection("hub")]
public class AccessAndChannelTests(HubFactory hub)
{
    private readonly NotificationHubClient _erp = hub.Sdk();

    // ───────── tenant isolation & authorization ─────────

    [Fact]
    public async Task Tenants_cannot_see_or_act_on_each_others_data()
    {
        var abc = await _erp.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { Email = Req.Email() }, Channel.Email));
        var acme = hub.Sdk("ACME_BOOKS_DEMO");

        var read = await Assert.ThrowsAsync<NotificationHubException>(() => acme.GetStatusAsync(abc.NotificationId));
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode); // indistinguishable from "does not exist"

        var send = await Assert.ThrowsAsync<NotificationHubException>(() => acme.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { Email = Req.Email() }, Channel.Email)));
        Assert.True(send.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);

        // Payload tenancy is never trusted: an explicit foreign applicationId is refused.
        var spoof = Req.InvoiceTo(hub.AcmeCompany, new() { Email = Req.Email() }, Channel.Email);
        spoof.ApplicationId = hub.ErpApp;
        var spoofed = await Assert.ThrowsAsync<NotificationHubException>(() => acme.SendAsync(spoof));
        Assert.Equal(HttpStatusCode.Forbidden, spoofed.StatusCode);

        var acmeHttp = await hub.AppHttpAsync("ACME_BOOKS_DEMO");
        var templates = await acmeHttp.GetFromJsonAsync<JsonElement>("/api/v1/templates");
        Assert.Equal(0, templates.GetArrayLength()); // Acme has no templates; ABC's are invisible
        var deliveries = await acmeHttp.GetFromJsonAsync<JsonElement>("/api/v1/deliveries?pageSize=500");
        Assert.DoesNotContain(deliveries.GetProperty("items").EnumerateArray(), d => d.GetProperty("notificationId").GetGuid() == abc.NotificationId);
        Assert.Equal(HttpStatusCode.NotFound, (await acmeHttp.GetAsync($"/api/v1/deliveries/{abc.Deliveries[0].DeliveryId}")).StatusCode);

        // A company administrator only sees their company.
        var abcAdmin = await hub.AdminHttpAsync("ABC_ADMIN");
        var companies = await abcAdmin.GetFromJsonAsync<JsonElement>("/api/v1/companies");
        Assert.All(companies.EnumerateArray(), c => Assert.Equal(hub.AbcCompany, c.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Unauthorized_access_is_refused()
    {
        var anon = hub.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/notifications/send", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/dashboard")).StatusCode);
        var badClient = await anon.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = hub.Credentials["ERP_AUTO_DEMO_CLIENT_ID"], ["client_secret"] = "wrong" }));
        Assert.Equal(HttpStatusCode.Unauthorized, badClient.StatusCode);
        var forged = hub.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/v1/notifications")).StatusCode);

        // Scope narrowing: a token without notifications.send cannot send.
        var readOnly = await hub.AppHttpAsync(scope: "notifications.read");
        var r = await readOnly.PostAsJsonAsync("/api/v1/notifications/send", Req.InvoiceTo(hub.AbcCompany, new() { Email = Req.Email() }, Channel.Email), NotificationHubClient.JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        // Application tokens cannot use administrator endpoints; auditors cannot change anything.
        var app = await hub.AppHttpAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("/api/v1/tenants")).StatusCode);
        var auditor = await hub.AdminHttpAsync("AUDITOR");
        var suspend = await auditor.PostAsJsonAsync("/api/v1/suspend/company/" + hub.AbcCompany, new { suspended = true });
        Assert.True(suspend.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Fact]
    public async Task Revoked_credentials_stop_working_immediately()
    {
        var admin = await hub.AdminHttpAsync();
        var created = await (await admin.PostAsJsonAsync($"/api/v1/applications/{hub.ErpApp}/credentials", new { name = "temp", scopes = new[] { "notifications.read" } })).Content.ReadFromJsonAsync<JsonElement>();
        var client = new NotificationHubClient(hub.CreateClient(), new NotificationHubClientOptions { ClientId = created.GetProperty("clientId").GetString()!, ClientSecret = created.GetProperty("clientSecret").GetString()!, MaxRetries = 0 });
        await client.GetAsync<JsonElement>("api/v1/notifications", default);
        (await admin.PostAsync($"/api/v1/credentials/{created.GetProperty("id").GetGuid()}/revoke", null)).EnsureSuccessStatusCode();
        await Task.Delay(31_000); // revocation cache TTL
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync<JsonElement>("api/v1/notifications", default));
        Assert.True(ex is NotificationHubException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized });
    }

    [Fact]
    public async Task Per_application_rate_limit_returns_429()
    {
        var admin = await hub.AdminHttpAsync();
        var appResp = await admin.PostAsJsonAsync("/api/v1/applications", new { tenantId = hub.TenantId, code = "RATE-" + Guid.NewGuid().ToString("N")[..6], name = "Rate limited", sandbox = true, rateLimitPerMinute = 3 });
        var appId = (await appResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var cred = await (await admin.PostAsJsonAsync($"/api/v1/applications/{appId}/credentials", new { name = "rl", scopes = new[] { "notifications.read" } })).Content.ReadFromJsonAsync<JsonElement>();
        var http = hub.CreateClient();
        var token = await (await http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "client_credentials", ["client_id"] = cred.GetProperty("clientId").GetString()!, ["client_secret"] = cred.GetProperty("clientSecret").GetString()! }))).Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("access_token").GetString());
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await http.GetAsync("/api/v1/notifications")).StatusCode);
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.OK));
        var limited = await http.GetAsync("/api/v1/notifications");
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);
    }

    // ───────── consent, preferences, opt-out ─────────

    [Fact]
    public async Task Consent_is_enforced_per_channel_and_class()
    {
        // CUST-1002 has no WhatsApp opt-in.
        var wa = await _erp.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { CustomerId = "CUST-1002" }, Channel.WhatsApp));
        Assert.Equal("suppressed", wa.Status);
        Assert.Equal(DeliveryStatus.Suppressed, wa.Deliveries[0].Status);

        // Promotional email without marketing consent is suppressed; with consent it goes.
        SendNotificationRequest Promo(RecipientDto r) => new()
        {
            CompanyId = hub.AbcCompany, NotificationType = "PROMO_FESTIVE", Channels = [Channel.Email], Recipient = r,
            Variables = Req.Vars(new { CustomerName = "X", Discount = "10", ValidTill = "2026-11-30" }),
        };
        var noConsent = await _erp.SendAsync(Promo(new() { Email = Req.Email() }));
        Assert.Equal(DeliveryStatus.Suppressed, noConsent.Deliveries[0].Status);
        Assert.Contains("marketing", noConsent.Deliveries[0].Reason, StringComparison.OrdinalIgnoreCase);
        var withConsent = await _erp.SendAsync(Promo(new() { CustomerId = "CUST-1001" }));
        Assert.Equal(DeliveryStatus.Queued, withConsent.Deliveries[0].Status);
        // CUST-1003 withdrew marketing consent.
        Assert.Equal(DeliveryStatus.Suppressed, (await _erp.SendAsync(Promo(new() { CustomerId = "CUST-1003" }))).Deliveries[0].Status);
    }

    [Fact]
    public async Task Preferences_apply_to_transactional_but_not_critical_messages()
    {
        var ext = "u-pref-" + Guid.NewGuid().ToString("N")[..6];
        var email = Req.Email();
        await _erp.UpsertContactAsync(new ContactUpsertRequest { CompanyId = hub.AbcCompany, ExternalId = ext, Name = "Pref User", Email = email, Roles = ["Administrator"] });
        var http = await hub.AppHttpAsync();
        (await http.PutAsJsonAsync("/api/v1/preferences", new { companyId = hub.AbcCompany, externalId = ext, preferences = new[] { new { category = "*", channel = "email", enabled = false } } })).EnsureSuccessStatusCode();
        var prefs = await _erp.GetPreferencesAsync(hub.AbcCompany, ext);
        Assert.False(Assert.Single(prefs.Preferences).Enabled);

        var invoice = await _erp.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { UserId = ext }, Channel.Email));
        Assert.Equal(DeliveryStatus.Suppressed, invoice.Deliveries[0].Status);
        var critical = await _erp.SendAsync(new SendNotificationRequest
        {
            CompanyId = hub.AbcCompany, NotificationType = "BACKUP_FAILED", Channels = [Channel.Email], Recipient = new() { UserId = ext },
            Variables = Req.Vars(new { ServerName = "DB", ErrorMessage = "disk", FailedAt = "now" }),
        });
        Assert.Equal(DeliveryStatus.Queued, critical.Deliveries[0].Status);
    }

    [Fact]
    public async Task Opt_out_after_acceptance_is_honoured_at_send_time()
    {
        var ext = "c-optout-" + Guid.NewGuid().ToString("N")[..6];
        var email = Req.Email();
        await _erp.UpsertContactAsync(new ContactUpsertRequest { CompanyId = hub.AbcCompany, Kind = ContactKind.Customer, ExternalId = ext, Email = email });
        var req = Req.InvoiceTo(hub.AbcCompany, new() { CustomerId = ext }, Channel.Email);
        req.ScheduleAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var resp = await _erp.SendAsync(req);
        var http = await hub.AppHttpAsync();
        (await http.PutAsJsonAsync("/api/v1/preferences", new { companyId = hub.AbcCompany, externalId = ext, preferences = new[] { new { category = "Invoice", channel = "email", enabled = false } } })).EnsureSuccessStatusCode();
        var final = await hub.WaitForStatusAsync(_erp, resp.NotificationId, s => s.Deliveries[0].Status == DeliveryStatus.Suppressed, 20000);
        Assert.Equal(0, final.Deliveries[0].Attempts);
    }

    [Fact]
    public async Task Whatsapp_stop_reply_and_unsubscribe_link_opt_the_recipient_out()
    {
        // Contact with WhatsApp opt-in receives a message, replies STOP → consent withdrawn + suppression.
        var phone = Req.Phone();
        var ext = "c-stop-" + Guid.NewGuid().ToString("N")[..6];
        await _erp.UpsertContactAsync(new ContactUpsertRequest { CompanyId = hub.AbcCompany, Kind = ContactKind.Customer, ExternalId = ext, Mobile = phone, Email = Req.Email() });
        await _erp.RecordConsentAsync(new ConsentRequest { CompanyId = hub.AbcCompany, ExternalId = ext, Channel = Channel.WhatsApp, Purpose = ConsentPurpose.ChannelOptIn, Status = ConsentStatus.Granted, Source = "test", Evidence = "checkbox" });
        var first = await _erp.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { CustomerId = ext }, Channel.WhatsApp));
        await hub.WaitForStatusAsync(_erp, first.NotificationId, s => s.Deliveries[0].Status >= DeliveryStatus.Delivered);
        var admin = await hub.AdminHttpAsync();
        var wa = await hub.DbAsync(db => db.ProviderAccounts.FirstAsync(p => p.Name == "Mock WhatsApp Business"));
        (await admin.PostAsJsonAsync("/api/v1/dev/simulate-inbound", new { providerAccountId = wa.Id, from = phone, body = "STOP" })).EnsureSuccessStatusCode();
        var prefs = await HubFactory.EventuallyAsync(() => _erp.GetPreferencesAsync(hub.AbcCompany, ext), p => p.Consents.Any(c => c.Channel == Channel.WhatsApp && c.Status == ConsentStatus.Withdrawn));
        Assert.Contains(prefs.Consents, c => c.Source == "inbound-reply");
        var second = await _erp.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { CustomerId = ext }, Channel.WhatsApp));
        Assert.Equal(DeliveryStatus.Suppressed, second.Deliveries[0].Status);

        // Unsubscribe link in a promotional email.
        var email = Req.Email();
        var ext2 = "c-unsub-" + Guid.NewGuid().ToString("N")[..6];
        await _erp.UpsertContactAsync(new ContactUpsertRequest { CompanyId = hub.AbcCompany, Kind = ContactKind.Customer, ExternalId = ext2, Email = email });
        await _erp.RecordConsentAsync(new ConsentRequest { CompanyId = hub.AbcCompany, ExternalId = ext2, Channel = Channel.Email, Purpose = ConsentPurpose.Marketing, Status = ConsentStatus.Granted, Source = "signup" });
        var promo = await _erp.SendAsync(new SendNotificationRequest { CompanyId = hub.AbcCompany, NotificationType = "PROMO_FESTIVE", Channels = [Channel.Email], Recipient = new() { CustomerId = ext2 }, Variables = Req.Vars(new { CustomerName = "X", Discount = "5", ValidTill = "2026-12-01" }) });
        var mock = hub.Services.GetRequiredService<MockProviderState>();
        var sent = await HubFactory.EventuallyAsync(() => Task.FromResult(mock.Sent.FirstOrDefault(m => m.DeliveryId == promo.Deliveries[0].DeliveryId)), m => m is not null);
        var link = System.Text.RegularExpressions.Regex.Match(sent!.Body, "http://localhost(/u/[A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.NotEmpty(link);
        var anon = hub.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(link)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsync(link, null)).StatusCode);
        var again = await _erp.SendAsync(new SendNotificationRequest { CompanyId = hub.AbcCompany, NotificationType = "PROMO_FESTIVE", Channels = [Channel.Email], Recipient = new() { CustomerId = ext2 }, Variables = Req.Vars(new { CustomerName = "X", Discount = "5", ValidTill = "2026-12-01" }) });
        Assert.Equal(DeliveryStatus.Suppressed, again.Deliveries[0].Status);
        var invoice = await _erp.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { CustomerId = ext2 }, Channel.Email));
        Assert.Equal(DeliveryStatus.Queued, invoice.Deliveries[0].Status); // unsubscribe never blocks transactional mail
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsync("/u/forged-token", null)).StatusCode);
    }

    // ───────── in-app, devices, inbound ─────────

    [Fact]
    public async Task In_app_notifications_arrive_in_real_time_and_track_reads()
    {
        var userId = "u-inapp-" + Guid.NewGuid().ToString("N")[..6];
        await _erp.UpsertContactAsync(new ContactUpsertRequest { CompanyId = hub.AbcCompany, ExternalId = userId, Name = "Inbox User" });
        var token = await _erp.CreateInboxTokenAsync(new InboxTokenRequest { CompanyId = hub.AbcCompany, UserId = userId });

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(hub.Server.BaseAddress, "/hubs/inapp"), o => { o.HttpMessageHandlerFactory = _ => hub.Server.CreateHandler(); o.AccessTokenProvider = () => Task.FromResult<string?>(token.AccessToken); o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling; })
            .Build();
        conn.On<JsonElement>("notification", n => received.TrySetResult(n));
        await conn.StartAsync();
        try
        {
            var resp = await _erp.SendAsync(new SendNotificationRequest
            {
                CompanyId = hub.AbcCompany, NotificationType = "APPROVAL_PENDING", Channels = [Channel.InApp], Recipient = new() { UserId = userId },
                Variables = Req.Vars(new { DocumentType = "PO", DocumentNumber = "PO-1", RequestedBy = "Anita" }),
                App = new AppNotificationOptionsDto { DeepLink = "/purchase/PO-1", Actions = [new AppActionDto { Id = "approve", Label = "Approve" }] },
            });
            var pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("Approval needed: PO-1", pushed.GetProperty("title").GetString());

            var inbox = hub.CreateClient();
            inbox.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            var list = await inbox.GetFromJsonAsync<JsonElement>("/api/v1/inapp/messages");
            Assert.Equal(1, list.GetProperty("unread").GetInt32());
            var msgId = list.GetProperty("items")[0].GetProperty("id").GetGuid();
            Assert.Equal("/purchase/PO-1", list.GetProperty("items")[0].GetProperty("deepLink").GetString());
            (await inbox.PostAsync($"/api/v1/inapp/messages/{msgId}/archive", null)).EnsureSuccessStatusCode();   // body-less action
            (await inbox.PostAsJsonAsync($"/api/v1/inapp/messages/{msgId}/action", new { actionId = "approve" })).EnsureSuccessStatusCode();
            var status = await hub.WaitForStatusAsync(_erp, resp.NotificationId, s => s.Deliveries[0].Status == DeliveryStatus.Read);
            Assert.NotNull(status.Deliveries[0].ReadAt);
            Assert.Equal(0, (await inbox.GetFromJsonAsync<JsonElement>("/api/v1/inapp/messages?archived=true")).GetProperty("unread").GetInt32());

            // Inbox tokens cannot reach application/admin APIs or other users' messages.
            Assert.Equal(HttpStatusCode.Forbidden, (await inbox.PostAsJsonAsync("/api/v1/notifications/send", new { })).StatusCode);
            var other = await _erp.CreateInboxTokenAsync(new InboxTokenRequest { CompanyId = hub.AbcCompany, UserId = "someone-else" });
            var otherClient = hub.CreateClient();
            otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
            Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsync($"/api/v1/inapp/messages/{msgId}/read", null)).StatusCode);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task Device_registration_and_invalid_token_cleanup()
    {
        var userId = "u-dev-" + Guid.NewGuid().ToString("N")[..6];
        var good = await _erp.RegisterDeviceAsync(new RegisterDeviceRequest { CompanyId = hub.AbcCompany, UserId = userId, Platform = DevicePlatform.Android, Token = "fcm-good-" + Guid.NewGuid() });
        var bad = await _erp.RegisterDeviceAsync(new RegisterDeviceRequest { CompanyId = hub.AbcCompany, UserId = userId, Platform = DevicePlatform.Ios, Token = "apns-fail-permanent-" + Guid.NewGuid() });
        var again = await _erp.RegisterDeviceAsync(new RegisterDeviceRequest { CompanyId = hub.AbcCompany, UserId = userId, Platform = DevicePlatform.Android, Token = (await hub.DbAsync(db => db.DeviceRegistrations.FirstAsync(d => d.Id == good.Id))).Token });
        Assert.Equal(good.Id, again.Id); // re-registration is idempotent

        var resp = await _erp.SendAsync(new SendNotificationRequest
        {
            CompanyId = hub.AbcCompany, NotificationType = "ORDER_APPROVED", Channels = [Channel.Push], Recipient = new() { UserId = userId },
            Variables = Req.Vars(new { OrderNumber = "SO-1", OrderAmount = "1", ApprovedBy = "x" }),
        });
        Assert.Equal(2, resp.Deliveries.Count); // one delivery per device
        var final = await hub.WaitForStatusAsync(_erp, resp.NotificationId, s => s.Deliveries.All(d => d.Status is DeliveryStatus.Failed or DeliveryStatus.ProviderAccepted or DeliveryStatus.Delivered));
        Assert.Contains(final.Deliveries, d => d.Status == DeliveryStatus.Failed);
        // Push acceptance is not reported as delivery until the device confirms.
        Assert.Contains(final.Deliveries, d => d.Status == DeliveryStatus.ProviderAccepted);
        var invalidated = await hub.DbAsync(db => db.DeviceRegistrations.FirstAsync(d => d.Id == bad.Id));
        Assert.NotNull(invalidated.InvalidatedAt);

        var http = await hub.AppHttpAsync();
        var accepted = final.Deliveries.Single(d => d.Status == DeliveryStatus.ProviderAccepted);
        (await http.PostAsJsonAsync("/api/v1/push/events", new { deliveryId = accepted.DeliveryId, interaction = "opened" })).EnsureSuccessStatusCode();
        Assert.Equal(DeliveryStatus.Read, (await _erp.GetStatusAsync(resp.NotificationId)).Deliveries.Single(d => d.DeliveryId == accepted.DeliveryId).Status);

        var next = await _erp.SendAsync(new SendNotificationRequest
        {
            CompanyId = hub.AbcCompany, NotificationType = "ORDER_APPROVED", Channels = [Channel.Push], Recipient = new() { UserId = userId },
            Variables = Req.Vars(new { OrderNumber = "SO-2", OrderAmount = "1", ApprovedBy = "x" }),
        });
        Assert.Single(next.Deliveries); // the invalid token is no longer targeted
    }

    [Fact]
    public async Task Inbound_whatsapp_reply_is_correlated_and_routed_to_the_application()
    {
        var first = await _erp.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { CustomerId = "CUST-1001" }, Channel.WhatsApp));
        var st = await hub.WaitForStatusAsync(_erp, first.NotificationId, s => s.Deliveries[0].ProviderMessageId is not null && s.Deliveries[0].Status >= DeliveryStatus.Delivered);
        var admin = await hub.AdminHttpAsync();
        var sim = await admin.PostAsJsonAsync("/api/v1/dev/simulate-inbound", new { providerAccountId = st.Deliveries[0].ProviderAccountId, from = "+919876543210", body = "Please send my account statement.", inReplyToProviderMessageId = st.Deliveries[0].ProviderMessageId });
        Assert.True(sim.IsSuccessStatusCode, await sim.Content.ReadAsStringAsync());
        var inbound = await HubFactory.EventuallyAsync(() => hub.DbAsync(db => db.InboundMessages.OrderByDescending(m => m.ReceivedAt).FirstOrDefaultAsync(m => m.Body == "Please send my account statement." && m.CorrelatedRequestId == first.NotificationId)),
            m => m is not null && m.RoutingStatus != InboundRoutingStatus.Pending);
        Assert.Equal(InboundRoutingStatus.Routed, inbound!.RoutingStatus); // ERP has an inbound webhook subscription
        Assert.Equal(hub.ErpApp, inbound.ApplicationId);
        var receiver = hub.Services.GetRequiredService<FakeWebhookReceiver>();
        await HubFactory.EventuallyAsync(() => Task.FromResult(receiver.Received.Any(r => r.Body.Contains("inbound.message_received") && r.Body.Contains(inbound.Id.ToString()))), b => b, 20000);

        var threads = await admin.GetFromJsonAsync<JsonElement>("/api/v1/conversations?channel=whatsapp");
        var thread = threads.EnumerateArray().First(t => t.GetProperty("id").GetGuid() == inbound.ThreadId);
        Assert.True(thread.GetProperty("windowOpen").GetBoolean());
        // Inside the 24h window a free-form reply is allowed.
        var reply = await admin.PostAsJsonAsync($"/api/v1/conversations/{inbound.ThreadId}/reply", new { body = "Sure, sending it now." });
        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        var replyId = (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notificationId").GetGuid();
        await hub.WaitForStatusAsync(_erp, replyId, s => s.Deliveries[0].Status >= DeliveryStatus.Delivered);
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{inbound.ThreadId}");
        Assert.Contains(detail.GetProperty("messages").EnumerateArray(), m => m.GetProperty("direction").GetString() == "outbound" && m.GetProperty("body").GetString() == "Sure, sending it now.");
    }

    [Fact]
    public async Task Meta_whatsapp_webhook_requires_valid_signature()
    {
        var admin = await hub.AdminHttpAsync();
        var create = await admin.PostAsJsonAsync("/api/v1/providers", new
        {
            name = "Meta test", channel = "whatsapp", providerType = "meta-whatsapp", scope = "Tenant", tenantId = hub.TenantId,
            settings = new { phoneNumberId = "p1", verifyToken = "vt-123" }, secrets = new { accessToken = "EAAG-test", appSecret = "app-secret" }, enabled = false,
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("••••••••", created.GetProperty("secrets").GetProperty("accessToken").GetString()); // never echoed
        Assert.DoesNotContain("EAAG-test", await (await admin.GetAsync($"/api/v1/providers/{id}")).Content.ReadAsStringAsync());

        var anon = hub.CreateClient();
        Assert.Equal("42", await anon.GetStringAsync($"/api/v1/callbacks/{id}?hub.mode=subscribe&hub.verify_token=vt-123&hub.challenge=42"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/callbacks/{id}?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=42")).StatusCode);
        const string body = """{"entry":[{"changes":[{"value":{"metadata":{"display_phone_number":"911140000000"},"messages":[{"from":"919876543210","id":"wamid.META1","timestamp":"1761625200","type":"text","text":{"body":"hello"}}]}}]}]}""";
        var sig = "sha256=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes("app-secret"), Encoding.UTF8.GetBytes(body)));
        var bad = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/callbacks/{id}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        bad.Headers.Add("X-Hub-Signature-256", "sha256=00");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(bad)).StatusCode);
        var good = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/callbacks/{id}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        good.Headers.Add("X-Hub-Signature-256", sig);
        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(good)).StatusCode);
        var msg = await hub.DbAsync(db => db.InboundMessages.FirstOrDefaultAsync(m => m.ProviderMessageId == "wamid.META1"));
        Assert.NotNull(msg);
        Assert.Equal(hub.TenantId, msg!.TenantId);

        // Credential changes require a second administrator.
        var update = await admin.PutAsJsonAsync($"/api/v1/providers/{id}", new
        {
            name = "Meta test", channel = "whatsapp", providerType = "meta-whatsapp", scope = "Tenant", tenantId = hub.TenantId, settings = new { phoneNumberId = "p1", verifyToken = "vt-123" },
            secrets = new { accessToken = "EAAG-rotated" }, enabled = false,
        });
        var pending = (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pendingApprovalId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/v1/approvals/{pending}/decide", new { approve = true })).StatusCode);
        var tenantAdmin = await hub.AdminHttpAsync("TENANT_ADMIN");
        (await tenantAdmin.PostAsJsonAsync($"/api/v1/approvals/{pending}/decide", new { approve = true })).EnsureSuccessStatusCode();
    }

    // ───────── Wings Code, MCP, campaigns, redaction ─────────

    [Fact]
    public async Task Wings_code_registers_a_sandbox_application_from_a_manifest()
    {
        var provisioning = hub.Sdk("WINGS_CODE_PROVISIONING");
        var code = "GEN-APP-" + Guid.NewGuid().ToString("N")[..6].ToUpper();
        var manifest = new NotificationManifest
        {
            Application = new ManifestApplication { Code = code, Name = "Generated app", Sandbox = true },
            Companies = ["ABC"], Channels = [Channel.Email, Channel.InApp],
            NotificationTypes = [new ManifestNotificationType { Code = "TASK_ASSIGNED", Name = "Task assigned", Category = "Tasks", DefaultChannels = [Channel.Email], DefaultTemplateCode = "TASK_ASSIGNED_V1", Events = ["task.assigned"] }],
            Templates = [new ManifestTemplate { Code = "TASK_ASSIGNED_V1", Name = "Task assigned", Channel = Channel.Email, Subject = "Task {{TaskTitle}}", Body = "<p>You were assigned {{TaskTitle}}</p>", BodyFormat = BodyFormat.Html }],
            Credential = new ManifestCredential { Name = "deploy" },
        };
        var result = await provisioning.RegisterManifestAsync(manifest);
        Assert.True(result.Created);
        Assert.Equal(1, result.TemplatesRegistered);
        Assert.NotNull(result.ClientSecret);
        var again = await provisioning.RegisterManifestAsync(manifest);
        Assert.False(again.Created);
        Assert.Equal(0, again.TemplatesRegistered);
        Assert.Null(again.ClientSecret);

        var generated = new NotificationHubClient(hub.CreateClient(), new NotificationHubClientOptions { ClientId = result.ClientId!, ClientSecret = result.ClientSecret!, DefaultCompanyId = hub.AbcCompany, MaxRetries = 0 });
        var sent = await generated.SendAsync(new { NotificationType = "TASK_ASSIGNED", RecipientEmail = Req.Email(), Variables = new { TaskTitle = "Review PR" } });
        var final = await hub.WaitForStatusAsync(generated, sent.NotificationId, s => s.Status == RequestStatus.Completed);
        Assert.Equal("Mock Email (primary)", final.Deliveries[0].Provider); // sandbox apps only reach mock providers

        // Application credentials cannot register applications.
        var ex = await Assert.ThrowsAsync<NotificationHubException>(() => _erp.RegisterManifestAsync(manifest));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task Mcp_tools_enforce_ai_restrictions_and_human_approval()
    {
        var http = await hub.AppHttpAsync("AI_AGENT_DEMO");
        async Task<JsonElement> Call(string name, object args)
        {
            var r = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments = args } });
            r.EnsureSuccessStatusCode();
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        }
        var list = await (await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, list.GetProperty("result").GetProperty("tools").GetArrayLength());

        var ok = await Call("notification.send", new { companyId = hub.AbcCompany, notificationType = "INVOICE_CREATED", recipient = new { customerId = "CUST-1001" }, channels = new[] { "email" }, variables = new { CustomerName = "A", InvoiceNumber = "I", InvoiceAmount = "1", InvoiceDate = "2026-10-28" } });
        Assert.False(ok.GetProperty("isError").GetBoolean());
        Assert.Equal("accepted", ok.GetProperty("structuredContent").GetProperty("status").GetString());

        Assert.True((await Call("notification.send", new { companyId = hub.AbcCompany, notificationType = "INVOICE_CREATED", recipient = new { email = "x@evil.example" } })).GetProperty("isError").GetBoolean());
        var sensitive = await Call("notification.send", new { companyId = hub.AbcCompany, notificationType = "OTP_LOGIN", recipient = new { userId = "u-1001" }, variables = new { Otp = "1" } });
        Assert.Contains("ai_not_permitted", sensitive.GetProperty("content")[0].GetProperty("text").GetString());
        var unknownCustomer = await Call("notification.send", new { companyId = hub.AbcCompany, notificationType = "INVOICE_CREATED", recipient = new { customerId = "NOBODY" }, channels = new[] { "email" }, variables = new { CustomerName = "A", InvoiceNumber = "I", InvoiceAmount = "1", InvoiceDate = "2026-10-28" } });
        Assert.True(unknownCustomer.GetProperty("isError").GetBoolean());

        var pending = await Call("notification.send", new { companyId = hub.AbcCompany, notificationType = "PAYMENT_OVERDUE", recipient = new { customerId = "CUST-1001" }, channels = new[] { "email" }, variables = new { CustomerName = "A", InvoiceNumber = "I", AmountDue = "1", DueDate = "2026-10-01", DaysOverdue = "27" } });
        Assert.Equal("pending_approval", pending.GetProperty("structuredContent").GetProperty("status").GetString());
        var notificationId = pending.GetProperty("structuredContent").GetProperty("notificationId").GetGuid();
        var approver = await hub.AdminHttpAsync("TENANT_ADMIN");
        var approvals = await approver.GetFromJsonAsync<JsonElement>("/api/v1/approvals");
        var approval = approvals.EnumerateArray().First(a => a.GetProperty("entityId").GetGuid() == notificationId);
        (await approver.PostAsJsonAsync($"/api/v1/approvals/{approval.GetProperty("id").GetGuid()}/decide", new { approve = true })).EnsureSuccessStatusCode();
        var status = await Call("notification.get_status", new { notificationId });
        Assert.NotEqual("PendingApproval", status.GetProperty("structuredContent").GetProperty("status").GetString());
        Assert.Single(status.GetProperty("structuredContent").GetProperty("deliveries").EnumerateArray());

        var batch = await http.PostAsJsonAsync("/api/v1/notifications/batch", new { notifications = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, batch.StatusCode); // no bulk messaging for AI agents
        var health = await Call("notification.get_provider_health", new { });
        Assert.DoesNotContain("secret", health.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Campaign_import_approval_and_throttled_dispatch()
    {
        var manager = await hub.AdminHttpAsync("CAMPAIGNS");
        var create = await manager.PostAsJsonAsync("/api/v1/campaigns", new
        {
            companyId = hub.AbcCompany, applicationId = hub.ErpApp, name = "Test campaign " + Guid.NewGuid().ToString("N")[..4], channels = new[] { "email" },
            templateCode = "PROMO_FESTIVE_V1", notificationTypeCode = "PROMO_FESTIVE", messageClass = "Transactional", variables = new { Discount = "20", ValidTill = "2026-12-31" }, throttlePerMinute = 600,
        });
        create.EnsureSuccessStatusCode();
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var csv = "email,name,CustomerName\n" + string.Join("\n", Enumerable.Range(0, 12).Select(i => $"{Req.Email()},User {i},Customer {i}")) + "\nnot-an-email,Bad,Bad\n";
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "list.csv" } };
        var import = await (await manager.PostAsync($"/api/v1/campaigns/{id}/import", form)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12, import.GetProperty("imported").GetInt32());
        Assert.Equal(1, import.GetProperty("rejected").GetInt32());
        (await manager.PostAsync($"/api/v1/campaigns/{id}/submit", null)).EnsureSuccessStatusCode();
        Assert.False((await manager.PostAsync($"/api/v1/campaigns/{id}/approve", null)).IsSuccessStatusCode); // maker-checker
        var tenantAdmin = await hub.AdminHttpAsync("TENANT_ADMIN");
        (await tenantAdmin.PostAsync($"/api/v1/campaigns/{id}/approve", null)).EnsureSuccessStatusCode();
        await HubFactory.EventuallyAsync(() => hub.DbAsync(db => db.Campaigns.Where(c => c.Id == id).Select(c => c.Status).FirstAsync()), s => s == CampaignStatus.Completed, 30000);
        var queued = await hub.DbAsync(db => db.CampaignRecipients.CountAsync(r => r.CampaignId == id && r.Status == CampaignRecipientStatus.Queued));
        Assert.Equal(12, queued);
        await HubFactory.EventuallyAsync(() => hub.DbAsync(db => db.NotificationDeliveries.CountAsync(d => d.CampaignId == id && (d.Status == DeliveryStatus.Read || d.Status == DeliveryStatus.Delivered))), n => n == 12, 30000);
    }

    [Fact]
    public async Task Sensitive_content_is_redacted_for_roles_and_logs()
    {
        var resp = await _erp.SendAsync(new SendNotificationRequest
        {
            CompanyId = hub.AbcCompany, NotificationType = "OTP_LOGIN", Channels = [Channel.Sms], Recipient = new() { Mobile = Req.Phone() }, Variables = Req.Vars(new { Otp = "482913" }),
        });
        await hub.WaitForStatusAsync(_erp, resp.NotificationId, s => s.Deliveries[0].Status >= DeliveryStatus.ProviderAccepted);
        var operatorClient = await hub.AdminHttpAsync("OPERATOR");
        var detail = await operatorClient.GetStringAsync($"/api/v1/deliveries/{resp.Deliveries[0].DeliveryId}");
        Assert.DoesNotContain("482913", detail);   // sensitive type: codes masked even for operators
        Assert.Contains("••••••", detail);
        var auditor = await hub.AdminHttpAsync("AUDITOR");
        var audited = await auditor.GetFromJsonAsync<JsonElement>($"/api/v1/deliveries/{resp.Deliveries[0].DeliveryId}");
        Assert.False(audited.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null); // auditors never see bodies
        Assert.Contains("*", audited.GetProperty("delivery").GetProperty("recipient").GetString()); // masked address

        // Stored rendered content and provider secrets are encrypted at rest.
        var raw = await hub.DbAsync(db => db.NotificationDeliveries.Where(d => d.Id == resp.Deliveries[0].DeliveryId).Select(d => d.ContentCipher).FirstAsync());
        Assert.DoesNotContain("482913", raw);
        var secrets = await hub.DbAsync(db => db.ProviderAccounts.Where(p => p.SecretsCipher != null).Select(p => p.SecretsCipher).ToListAsync());
        Assert.All(secrets, s => Assert.StartsWith("v1.", s));
        // The attempt record holds a sanitized summary, not the message.
        var attempts = await hub.DbAsync(db => db.DeliveryAttempts.Where(a => a.DeliveryId == resp.Deliveries[0].DeliveryId).ToListAsync());
        Assert.All(attempts, a => Assert.DoesNotContain("482913", a.ResponseSummary ?? ""));
    }

    [Fact]
    public async Task Emergency_suspension_holds_messages_until_resumed()
    {
        var admin = await hub.AdminHttpAsync();
        var appResp = await admin.PostAsJsonAsync("/api/v1/applications", new { tenantId = hub.TenantId, code = "SUSP-" + Guid.NewGuid().ToString("N")[..6], name = "Suspendable", sandbox = true });
        var appId = (await appResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/api/v1/applications/{appId}/companies", new { companyId = hub.AbcCompany })).EnsureSuccessStatusCode();
        var send = await admin.PostAsJsonAsync("/api/v1/notifications/send", new
        {
            applicationId = appId, companyId = hub.AbcCompany, templateCode = "STOCK_BELOW_REORDER_V1", channels = new[] { "inapp" }, recipient = new { userId = "u-1003" },
            variables = new { ItemName = "x", Quantity = 1, ReorderLevel = 2, Warehouse = "w" }, scheduleAt = DateTimeOffset.UtcNow.AddSeconds(2),
        });
        Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);
        var id = (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notificationId").GetGuid();
        (await admin.PostAsJsonAsync($"/api/v1/suspend/application/{appId}", new { suspended = true, reason = "incident" })).EnsureSuccessStatusCode();
        var blocked = await admin.PostAsJsonAsync("/api/v1/notifications/send", new { applicationId = appId, companyId = hub.AbcCompany, templateCode = "STOCK_BELOW_REORDER_V1", channels = new[] { "inapp" }, recipient = new { userId = "u-1003" }, variables = new { ItemName = "x", Quantity = 1, ReorderLevel = 2, Warehouse = "w" } });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        await Task.Delay(4000);
        var held = await hub.DbAsync(db => db.NotificationDeliveries.FirstAsync(d => d.RequestId == id));
        Assert.Equal(DeliveryStatus.RetryPending, held.Status);
        Assert.Contains("held", held.StatusReason);
        Assert.Equal(0, held.AttemptCount);
    }

    [Fact]
    public async Task Openapi_document_and_health_are_available()
    {
        var anon = hub.CreateClient();
        var doc = await anon.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(doc.GetProperty("paths").TryGetProperty("/api/v1/notifications/send", out _));
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/health/ready")).StatusCode);
    }
}
