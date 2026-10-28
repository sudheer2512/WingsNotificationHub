using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wings.NotificationHub.Client;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Providers;

namespace Wings.NotificationHub.IntegrationTests;

[Collection("hub")]
public class DeliveryAcceptanceTests(HubFactory hub)
{
    private readonly NotificationHubClient _sdk = hub.Sdk();

    [Fact]
    public async Task Single_notification_is_accepted_durably_then_delivered()
    {
        var resp = await _sdk.SendAsync(Req.InvoiceTo(hub.AbcCompany, new RecipientDto { Email = Req.Email() }, Channel.Email));
        Assert.Equal("accepted", resp.Status);
        Assert.Equal(DeliveryStatus.Queued, Assert.Single(resp.Deliveries).Status);
        var final = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Status == RequestStatus.Completed && s.Deliveries[0].Status >= DeliveryStatus.Delivered);
        var d = final.Deliveries[0];
        Assert.NotNull(d.ProviderMessageId);
        Assert.Equal(1, d.Attempts);
        Assert.NotNull(d.TemplateVersionId);
        Assert.Contains("@example.com", d.Recipient);
        Assert.Contains("*", d.Recipient); // masked
    }

    [Fact]
    public async Task Sdk_anonymous_overload_used_by_generated_code_works()
    {
        var resp = await _sdk.SendAsync(new
        {
            NotificationType = "ORDER_APPROVED", RecipientUserId = "u-1002",
            Variables = new { OrderNumber = "SO-9", OrderAmount = "5000", ApprovedBy = "Priya" },
        });
        Assert.Contains(resp.Deliveries, d => d.Channel == Channel.InApp);
        Assert.Contains(resp.Deliveries, d => d.Channel == Channel.Push); // Rahul has an iOS device
    }

    [Fact]
    public async Task Multi_channel_request_tracks_each_channel_independently()
    {
        var resp = await _sdk.SendAsync(new SendNotificationRequest
        {
            CompanyId = hub.AbcCompany, NotificationType = "INVOICE_CREATED", TemplateCode = "INVOICE_CREATED_V1",
            Recipient = new RecipientDto { CustomerId = "CUST-1001", Email = "customer@example.com", Mobile = "+919876543210" },
            Channels = [Channel.Email, Channel.WhatsApp, Channel.InApp, Channel.Sms], Variables = Req.Invoice("INV-MULTI"),
        });
        // CUST-1001 is a customer (no userId) → in-app is skipped with a warning; the others are created.
        Assert.Equal(3, resp.Deliveries.Count);
        Assert.Contains(resp.Warnings, w => w.StartsWith("inapp"));
        var final = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries.All(d => d.Status is DeliveryStatus.Delivered or DeliveryStatus.Read));
        Assert.Equal(3, final.Deliveries.Select(d => d.ProviderAccountId).Distinct().Count());
        var history = await _sdk.GetHistoryAsync(resp.NotificationId);
        Assert.Equal(3, history.Attempts.Count);
        Assert.All(final.Deliveries, d => Assert.Contains(history.Events, e => e.DeliveryId == d.DeliveryId && e.Status == DeliveryStatus.Delivered));
    }

    [Fact]
    public async Task Template_versioning_keeps_history_and_requires_maker_checker()
    {
        var designer = await hub.AdminHttpAsync("DESIGNER");
        var approver = await hub.AdminHttpAsync("APPROVER");
        var code = "VERSIONED_" + Guid.NewGuid().ToString("N")[..6].ToUpper();
        var create = await designer.PostAsJsonAsync("/api/v1/templates", new
        {
            template = new { tenantId = hub.TenantId, code, name = "Versioned", channel = "sms", locale = "en" },
            version = new { body = "v1 for {{Name}}" },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await designer.PostAsync($"/api/v1/templates/{id}/versions/1/submit", null)).EnsureSuccessStatusCode();
        // The author cannot approve their own version.
        var self = await designer.PostAsJsonAsync($"/api/v1/templates/{id}/versions/1/review", new { approve = true });
        Assert.False(self.IsSuccessStatusCode);
        (await approver.PostAsJsonAsync($"/api/v1/templates/{id}/versions/1/review", new { approve = true, activate = true })).EnsureSuccessStatusCode();

        var first = await _sdk.SendAsync(new SendNotificationRequest { CompanyId = hub.AbcCompany, TemplateCode = code, Channels = [Channel.Sms], Recipient = new() { Mobile = Req.Phone() }, Variables = Req.Vars(new { Name = "A" }) });

        (await designer.PutAsJsonAsync($"/api/v1/templates/{id}/draft", new { body = "v2 for {{Name}}" })).EnsureSuccessStatusCode();
        (await designer.PostAsync($"/api/v1/templates/{id}/versions/2/submit", null)).EnsureSuccessStatusCode();
        // Until v2 is approved, sends still use v1.
        var stillV1 = await _sdk.SendAsync(new SendNotificationRequest { CompanyId = hub.AbcCompany, TemplateCode = code, Channels = [Channel.Sms], Recipient = new() { Mobile = Req.Phone() }, Variables = Req.Vars(new { Name = "B" }) });
        (await approver.PostAsJsonAsync($"/api/v1/templates/{id}/versions/2/review", new { approve = true, activate = true })).EnsureSuccessStatusCode();
        var second = await _sdk.SendAsync(new SendNotificationRequest { CompanyId = hub.AbcCompany, TemplateCode = code, Channels = [Channel.Sms], Recipient = new() { Mobile = Req.Phone() }, Variables = Req.Vars(new { Name = "C" }) });

        var s1 = await _sdk.GetStatusAsync(first.NotificationId);
        var s1b = await _sdk.GetStatusAsync(stillV1.NotificationId);
        var s2 = await _sdk.GetStatusAsync(second.NotificationId);
        Assert.Equal(1, s1.Deliveries[0].TemplateVersion);
        Assert.Equal(1, s1b.Deliveries[0].TemplateVersion);
        Assert.Equal(2, s2.Deliveries[0].TemplateVersion);
        var mock = hub.Services.GetRequiredService<MockProviderState>();
        await HubFactory.EventuallyAsync(() => Task.FromResult(mock.Sent.Count(m => m.DeliveryId == s1.Deliveries[0].DeliveryId || m.DeliveryId == s2.Deliveries[0].DeliveryId)), n => n == 2);
        Assert.Equal("v1 for A", mock.Sent.Single(m => m.DeliveryId == s1.Deliveries[0].DeliveryId).Body);
        Assert.Equal("v2 for C", mock.Sent.Single(m => m.DeliveryId == s2.Deliveries[0].DeliveryId).Body);
    }

    [Fact]
    public async Task Enum_query_filters_accept_wire_casing()
    {
        var templates = await _sdk.ListTemplatesAsync(Channel.InApp);
        Assert.NotEmpty(templates);
        Assert.All(templates, t => Assert.Equal(Channel.InApp, t.Channel));
        var http = await hub.AppHttpAsync();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/deliveries?channel=whatsapp&status=delivered")).StatusCode);
    }

    [Fact]
    public async Task Invalid_template_variables_are_rejected_before_acceptance()
    {
        var ex = await Assert.ThrowsAsync<NotificationHubException>(() => _sdk.SendAsync(new SendNotificationRequest
        {
            CompanyId = hub.AbcCompany, NotificationType = "INVOICE_CREATED", Channels = [Channel.Email], Recipient = new() { Email = Req.Email() }, Variables = Req.Vars(new { CustomerName = "x" }),
        }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ex.StatusCode);
        Assert.Contains(ex.Error!.Errors!.SelectMany(e => e.Value), m => m.Contains("InvoiceNumber"));

        var unknownType = await Assert.ThrowsAsync<NotificationHubException>(() => _sdk.SendAsync(new SendNotificationRequest { CompanyId = hub.AbcCompany, NotificationType = "NOPE", Recipient = new() { Email = Req.Email() } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownType.StatusCode);
        var badEmail = await Assert.ThrowsAsync<NotificationHubException>(() => _sdk.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { Email = "not-an-email" }, Channel.Email)));
        Assert.Equal("validation_failed", badEmail.Error!.Code);
    }

    [Fact]
    public async Task Scheduled_delivery_waits_until_due_and_can_be_cancelled()
    {
        var req = Req.InvoiceTo(hub.AbcCompany, new() { Email = Req.Email() }, Channel.Email);
        req.ScheduleAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var resp = await _sdk.SendAsync(req);
        Assert.Equal("scheduled", resp.Status);
        Assert.Equal(DeliveryStatus.Scheduled, resp.Deliveries[0].Status);
        await Task.Delay(1000);
        Assert.Equal(DeliveryStatus.Scheduled, (await _sdk.GetStatusAsync(resp.NotificationId)).Deliveries[0].Status);
        await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status >= DeliveryStatus.ProviderAccepted && s.Deliveries[0].Status < DeliveryStatus.Failed);

        var later = Req.InvoiceTo(hub.AbcCompany, new() { Email = Req.Email() }, Channel.Email);
        later.ScheduleAt = DateTimeOffset.UtcNow.AddHours(1);
        var r2 = await _sdk.SendAsync(later);
        await _sdk.CancelAsync(r2.NotificationId);
        var s2 = await _sdk.GetStatusAsync(r2.NotificationId);
        Assert.Equal(RequestStatus.Cancelled, s2.Status);
        Assert.Equal(DeliveryStatus.Cancelled, s2.Deliveries[0].Status);
    }

    [Fact]
    public async Task Batch_dispatch_accepts_items_independently()
    {
        var items = Enumerable.Range(0, 40).Select(i => Req.InvoiceTo(hub.AbcCompany, new() { Email = Req.Email() }, Channel.Email)).ToList();
        items.Add(Req.InvoiceTo(hub.AbcCompany, new() { Email = "broken" }, Channel.Email));
        var result = await _sdk.SendBatchAsync(items);
        Assert.Equal(40, result.Accepted);
        Assert.Equal(1, result.Rejected);
        Assert.Equal(40, result.Results.Last().Index);
        var ids = result.Results.Where(r => r.NotificationId is not null).Select(r => r.NotificationId!.Value).ToList();
        await HubFactory.EventuallyAsync(() => hub.DbAsync(db => db.NotificationRequests.CountAsync(r => ids.Contains(r.Id) && r.Status == RequestStatus.Completed)), n => n == 40, 30000);
    }

    [Fact]
    public async Task Duplicate_requests_with_one_idempotency_key_create_one_notification()
    {
        var key = "dup-" + Guid.NewGuid();
        var email = Req.Email();
        SendNotificationRequest Make() { var r = Req.InvoiceTo(hub.AbcCompany, new() { Email = email }, Channel.Email); r.Variables = Req.Invoice("INV-DUP"); r.IdempotencyKey = key; return r; }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => hub.Sdk().SendAsync(Make())));
        Assert.Single(results.Select(r => r.NotificationId).Distinct());
        Assert.Equal(7, results.Count(r => r.Duplicate));
        Assert.Equal(1, await hub.DbAsync(db => db.NotificationRequests.CountAsync(r => r.IdempotencyKey == key)));
        var conflict = Make();
        conflict.Variables = Req.Invoice("DIFFERENT");
        var ex = await Assert.ThrowsAsync<NotificationHubException>(() => _sdk.SendAsync(conflict));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
    }

    [Fact]
    public async Task Provider_failover_moves_to_secondary_when_primary_is_down()
    {
        var mock = hub.Services.GetRequiredService<MockProviderState>();
        var primary = await hub.DbAsync(db => db.ProviderAccounts.FirstAsync(p => p.Name == "Mock SMS (primary)"));
        mock.Outages[primary.Id] = true;
        try
        {
            var resp = await _sdk.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { Mobile = Req.Phone() }, Channel.Sms));
            var final = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status is DeliveryStatus.Delivered, 30000);
            var history = await _sdk.GetHistoryAsync(resp.NotificationId);
            Assert.Equal(AttemptOutcome.TransientFailure, history.Attempts[0].Outcome);
            Assert.Equal(primary.Id, history.Attempts[0].ProviderAccountId);
            Assert.Equal("Mock SMS (secondary)", final.Deliveries[0].Provider);
            Assert.Equal(2, history.Attempts.Count); // the failed attempt is preserved, not overwritten
        }
        finally { mock.Outages[primary.Id] = false; }
    }

    [Fact]
    public async Task Ambiguous_timeout_with_idempotent_provider_retries_without_duplicate()
    {
        var resp = await _sdk.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { Mobile = Req.Phone("2222") }, Channel.Sms));
        var final = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status == DeliveryStatus.Delivered, 30000);
        var history = await _sdk.GetHistoryAsync(resp.NotificationId);
        Assert.Equal(AttemptOutcome.Ambiguous, history.Attempts[0].Outcome);
        Assert.Equal(history.Attempts[0].ProviderAccountId, history.Attempts[1].ProviderAccountId); // pinned: no cross-provider duplicate
        var sent = hub.Services.GetRequiredService<MockProviderState>().Sent.Count(m => m.DeliveryId == final.Deliveries[0].DeliveryId);
        Assert.Equal(1, sent); // the provider deduplicated by idempotency key
    }

    [Fact]
    public async Task Ambiguous_timeout_without_idempotency_is_reconciled_or_held_for_an_operator()
    {
        var admin = await hub.AdminHttpAsync();
        var appId = await CreateIsolatedSmsProviderAsync(admin, "{\"supportsIdempotency\":\"false\",\"supportsStatusQuery\":\"false\",\"autoDeliver\":true}", "Mock SMS no-dedupe");
        var resp = await _sdk.SendAsync(Req.InvoiceTo(hub.SunriseCompany, new() { Mobile = Req.Phone("2222") }, Channel.Sms));
        var held = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status == DeliveryStatus.Unknown);
        var deliveryId = held.Deliveries[0].DeliveryId;
        Assert.True(await hub.DbAsync(db => db.NotificationDeliveries.Where(d => d.Id == deliveryId).Select(d => d.AwaitingOperator).FirstAsync()));
        await Task.Delay(1500);
        Assert.Equal(1, hub.Services.GetRequiredService<MockProviderState>().Sent.Count(m => m.DeliveryId == deliveryId)); // never resent automatically

        var resolve = await admin.PostAsJsonAsync($"/api/v1/deliveries/{deliveryId}/resolve", new { action = "mark_sent", comment = "confirmed with provider portal" });
        resolve.EnsureSuccessStatusCode();
        Assert.Equal(DeliveryStatus.ProviderAccepted, (await _sdk.GetStatusAsync(resp.NotificationId)).Deliveries[0].Status);
        await DisableAsync(admin, appId);
    }

    [Fact]
    public async Task Ambiguous_timeout_with_status_query_is_reconciled_without_resend()
    {
        var admin = await hub.AdminHttpAsync();
        var providerId = await CreateIsolatedSmsProviderAsync(admin, "{\"supportsIdempotency\":\"false\",\"supportsStatusQuery\":\"true\",\"autoDeliver\":false}", "Mock SMS queryable");
        try
        {
            var resp = await _sdk.SendAsync(Req.InvoiceTo(hub.SunriseCompany, new() { Mobile = Req.Phone("2222") }, Channel.Sms));
            var unknown = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status == DeliveryStatus.Unknown);
            var id = unknown.Deliveries[0].DeliveryId;
            // Reconciliation runs once a minute after the ambiguous attempt; pull it forward.
            await hub.DbAsync(db => db.NotificationDeliveries.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTime.UtcNow)));
            var final = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status == DeliveryStatus.ProviderAccepted, 30000);
            Assert.Equal(1, hub.Services.GetRequiredService<MockProviderState>().Sent.Count(m => m.DeliveryId == id));
            var history = await _sdk.GetHistoryAsync(resp.NotificationId);
            Assert.Contains(history.Events, e => e.Source == "reconciliation");
        }
        finally { await DisableAsync(admin, providerId); }
    }

    /// <summary>Creates a Sunrise-company-scoped mock SMS provider that outranks the shared ones.</summary>
    private async Task<Guid> CreateIsolatedSmsProviderAsync(HttpClient admin, string settings, string name)
    {
        var resp = await admin.PostAsJsonAsync("/api/v1/providers", new
        {
            name, channel = "sms", providerType = "mock", scope = "Company", tenantId = hub.TenantId, companyId = hub.SunriseCompany,
            settings = JsonNode.Parse(settings), priority = 1, duplicateRiskPolicy = "Never", timeoutSeconds = 5,
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task DisableAsync(HttpClient admin, Guid providerId)
        => (await admin.PostAsJsonAsync($"/api/v1/providers/{providerId}/suspend", new { suspended = true, reason = "test cleanup" })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Worker_crash_recovery_resets_abandoned_leases()
    {
        var req = Req.InvoiceTo(hub.AbcCompany, new() { Email = Req.Email() }, Channel.Email);
        req.ScheduleAt = DateTimeOffset.UtcNow.AddHours(2);
        var resp = await _sdk.SendAsync(req);
        var id = resp.Deliveries[0].DeliveryId;
        // Simulate a worker that claimed the delivery and died before submitting it.
        await hub.DbAsync(db => db.NotificationDeliveries.Where(d => d.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, DeliveryStatus.Processing).SetProperty(d => d.LeaseOwner, "dead-worker")
            .SetProperty(d => d.LeaseExpiresAt, DateTime.UtcNow.AddSeconds(-1)).SetProperty(d => d.NextAttemptAt, DateTime.UtcNow)));
        var final = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status >= DeliveryStatus.ProviderAccepted && s.Deliveries[0].Status < DeliveryStatus.Failed, 40000);
        Assert.Equal(1, final.Deliveries[0].Attempts);
    }

    [Fact]
    public async Task Queue_message_failures_are_retried_and_dead_lettered_with_alert()
    {
        var bogus = Guid.NewGuid();
        await hub.DbAsync(async db =>
        {
            db.QueueMessages.Add(new QueueMessage { Queue = "wnh.events", Type = "inbound.received", PayloadJson = "{not json", MessageId = bogus, DequeueCount = 9 });
            return await db.SaveChangesAsync();
        });
        await HubFactory.EventuallyAsync(() => hub.DbAsync(db => db.QueueMessages.Where(q => q.MessageId == bogus).Select(q => q.DeadLetteredAt).FirstOrDefaultAsync()), d => d is not null, 20000);
        // The alert is written right after the dead-letter update (separate statement).
        await HubFactory.EventuallyAsync(() => hub.DbAsync(db => db.SystemAlerts.AnyAsync(a => a.Source == "queue" && a.Message.Contains(bogus.ToString()))), b => b, 10000);
    }

    [Fact]
    public async Task Provider_callbacks_are_authenticated_idempotent_and_never_regress()
    {
        var resp = await _sdk.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { Mobile = Req.Phone() }, Channel.Sms));
        var st = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status == DeliveryStatus.Delivered);
        var d = st.Deliveries[0];
        var account = await hub.DbAsync(db => db.ProviderAccounts.FirstAsync(p => p.Id == d.ProviderAccountId));
        var secret = hub.Services.GetRequiredService<Infrastructure.Common.ISecretProtector>().Unprotect(account.CallbackSecretCipher!, Infrastructure.Common.Purposes.CallbackSecret);
        var http = hub.CreateClient();

        async Task<HttpResponseMessage> Post(string body, string? sig = null)
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/callbacks/{account.Id}") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            msg.Headers.Add(WebhookSignature.Header, sig ?? WebhookSignature.Sign(secret, body, DateTimeOffset.UtcNow));
            return await http.SendAsync(msg);
        }

        // Late "sent" after "delivered": recorded but not applied.
        var late = $"{{\"events\":[{{\"eventId\":\"late-{Guid.NewGuid():N}\",\"messageId\":\"{d.ProviderMessageId}\",\"status\":\"Sent\"}}]}}";
        Assert.Equal(HttpStatusCode.OK, (await Post(late)).StatusCode);
        Assert.Equal(DeliveryStatus.Delivered, (await _sdk.GetStatusAsync(resp.NotificationId)).Deliveries[0].Status);

        // Read receipt applies; replaying the same event id changes nothing.
        var readId = $"read-{Guid.NewGuid():N}";
        var read = $"{{\"events\":[{{\"eventId\":\"{readId}\",\"messageId\":\"{d.ProviderMessageId}\",\"status\":\"Read\"}}]}}";
        var first = await (await Post(read)).Content.ReadFromJsonAsync<JsonElement>();
        var replay = await (await Post(read)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, first.GetProperty("accepted").GetInt32());
        Assert.Equal(1, replay.GetProperty("duplicates").GetInt32());
        Assert.Equal(DeliveryStatus.Read, (await _sdk.GetStatusAsync(resp.NotificationId)).Deliveries[0].Status);

        // Forged / stale signatures are rejected.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(read, "t=1,v1=deadbeef")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(read, WebhookSignature.Sign(secret, read, DateTimeOffset.UtcNow.AddMinutes(-20)))).StatusCode);

        var history = await _sdk.GetHistoryAsync(resp.NotificationId);
        Assert.Contains(history.Events, e => e.Status == DeliveryStatus.Sent && e.Detail?.Contains("not applied") == true);
    }

    [Fact]
    public async Task Callback_arriving_before_acceptance_is_applied_afterwards()
    {
        // Mock receipts can race the dispatcher's own save; the orphan is parked and applied once the message id is known.
        var resp = await _sdk.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { Mobile = Req.Phone() }, Channel.Sms));
        await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status == DeliveryStatus.Delivered);
        Assert.False(await hub.DbAsync(db => db.DeliveryEvents.AnyAsync(e => e.DeliveryId == null && e.RecordedAt < DateTime.UtcNow.AddSeconds(-30) && e.Source == "provider_callback" && e.Type == "provider_status" && e.ProviderMessageId != null
            && db.NotificationDeliveries.Any(d => d.ProviderMessageId == e.ProviderMessageId))));
    }

    [Fact]
    public async Task Permanent_failure_suppresses_the_address_and_triggers_fallback()
    {
        var phone = Req.Phone("0000");
        var resp = await _sdk.SendAsync(new SendNotificationRequest
        {
            CompanyId = hub.AbcCompany, NotificationType = "INVOICE_CREATED", Channels = [Channel.Sms], Variables = Req.Invoice(),
            Recipient = new() { Mobile = phone, Email = Req.Email() }, Fallbacks = [new FallbackDto { From = Channel.Sms, To = Channel.Email }],
        });
        var final = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries.Count == 2 && s.Deliveries.Any(d => d.Channel == Channel.Email && d.Status >= DeliveryStatus.Delivered && d.Status < DeliveryStatus.Failed));
        var sms = final.Deliveries.Single(d => d.Channel == Channel.Sms);
        Assert.Equal(DeliveryStatus.Failed, sms.Status);
        Assert.Equal(sms.DeliveryId, final.Deliveries.Single(d => d.Channel == Channel.Email).FallbackOfDeliveryId);
        Assert.Equal(RequestStatus.PartiallyFailed, final.Status);
        Assert.True(await hub.DbAsync(db => db.Suppressions.AnyAsync(s => s.Address == phone && s.Reason == SuppressionReason.InvalidAddress)));
        var again = await _sdk.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { Mobile = phone }, Channel.Sms));
        Assert.Equal(DeliveryStatus.Suppressed, again.Deliveries[0].Status);
    }

    [Fact]
    public async Task Escalation_fires_when_not_read_in_time()
    {
        var resp = await _sdk.SendAsync(new SendNotificationRequest
        {
            CompanyId = hub.AbcCompany, NotificationType = "INVOICE_CREATED", Channels = [Channel.Sms], Variables = Req.Invoice(),
            Recipient = new() { Mobile = Req.Phone(), Email = Req.Email() },
            Escalation = new EscalationDto { AfterMinutes = 30, UnlessStatus = DeliveryStatus.Read, Channel = Channel.Email },
        });
        var smsId = resp.Deliveries[0].DeliveryId;
        await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status == DeliveryStatus.Delivered);
        await hub.DbAsync(db => db.NotificationDeliveries.Where(d => d.Id == smsId).ExecuteUpdateAsync(s => s.SetProperty(d => d.EscalateAt, DateTime.UtcNow)));
        var final = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries.Any(d => d.EscalationOfDeliveryId == smsId));
        Assert.Equal(Channel.Email, final.Deliveries.Single(d => d.EscalationOfDeliveryId == smsId).Channel);
    }

    [Fact]
    public async Task Webhook_subscribers_receive_signed_events_with_retries()
    {
        var receiver = hub.Services.GetRequiredService<FakeWebhookReceiver>();
        var app = await hub.AppHttpAsync("BOOKS_DEMO");
        var sub = await app.PostAsJsonAsync("/api/v1/webhooks", new { name = "books", url = "https://books.example.com/hooks", eventTypes = new[] { "delivery.status_changed", "notification.completed" } });
        sub.EnsureSuccessStatusCode();
        var subJson = await sub.Content.ReadFromJsonAsync<JsonElement>();
        var secret = subJson.GetProperty("signingSecret").GetString()!;
        var subId = subJson.GetProperty("id").GetGuid();
        receiver.FailPrefix = "https://books.example.com";
        Interlocked.Exchange(ref receiver.FailNext, 1);
        var books = hub.Sdk("BOOKS_DEMO");
        var resp = await books.SendAsync(Req.InvoiceTo(hub.AbcCompany, new() { Email = Req.Email() }, Channel.Email));
        // Every event type eventually succeeds; the first post failed (503) and was retried with backoff.
        var attempts = await HubFactory.EventuallyAsync(() => hub.DbAsync(db => db.WebhookDeliveryAttempts.Where(a => a.SubscriptionId == subId).ToListAsync()),
            l => l.Any(a => !a.Success && a.NextRetryAt != null) && l.Any(a => a.Success && a.EventType == "notification.completed")
                 && l.Where(a => !a.Success).All(f => l.Any(a => a.Success && a.EventId == f.EventId)), 45000);
        var retried = attempts.First(a => !a.Success);
        Assert.Contains(attempts, a => a.EventId == retried.EventId && a.AttemptNumber == 2 && a.Success);
        var completed = receiver.Received.Where(r => r.Url.StartsWith("https://books.example.com") && r.Body.Contains(resp.NotificationId.ToString()) && r.Body.Contains("notification.completed")).ToList();
        Assert.All(completed, c => Assert.True(WebhookVerifier.Verify(secret, c.Body, c.Signature, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5))));
        // Receivers can deduplicate retries: every post of one event carries the same event id.
        Assert.Equal(1, completed.Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("id").GetGuid()).Distinct().Count());
    }

    [Fact]
    public async Task Usage_and_billing_reconcile_with_accepted_deliveries()
    {
        var resp = await _sdk.SendAsync(new SendNotificationRequest
        {
            CompanyId = hub.AbcCompany, TemplateCode = "PAYMENT_OVERDUE_V1", NotificationType = "PAYMENT_OVERDUE", Channels = [Channel.Sms],
            Recipient = new() { Mobile = Req.Phone() }, Variables = Req.Vars(new { CustomerName = "X", InvoiceNumber = "I", AmountDue = "1", DueDate = "2026-10-01", DaysOverdue = "27" }),
        });
        var st = await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status == DeliveryStatus.Delivered);
        var usage = await hub.DbAsync(db => db.UsageRecords.SingleAsync(u => u.DeliveryId == st.Deliveries[0].DeliveryId));
        Assert.Equal(0.18m, usage.EstimatedCost);         // +91 SMS cost rate
        Assert.Equal(0.225m, usage.BilledAmount);         // 25% margin billing rate
        Assert.Equal(Channel.Sms, usage.Channel);

        // Every accepted delivery has exactly one usage record; nothing else does.
        var accepted = await hub.DbAsync(db => db.NotificationDeliveries.Where(d => d.AcceptedAt != null).Select(d => d.Id).ToListAsync());
        var recorded = await hub.DbAsync(db => db.UsageRecords.Select(u => u.DeliveryId).ToListAsync());
        Assert.Equal(recorded.Count, recorded.Distinct().Count());
        Assert.Empty(recorded.Except(accepted));
    }

    [Fact]
    public async Task Prepaid_credits_are_reserved_consumed_and_enforced()
    {
        var before = await hub.DbAsync(db => db.CreditAccounts.AsNoTracking().FirstAsync(a => a.CompanyId == hub.SunriseCompany));
        var resp = await _sdk.SendAsync(Req.InvoiceTo(hub.SunriseCompany, new() { Mobile = "+919811110000".Replace("0000", Random.Shared.Next(1000, 9999).ToString()) }, Channel.Sms));
        await hub.WaitForStatusAsync(_sdk, resp.NotificationId, s => s.Deliveries[0].Status is DeliveryStatus.Delivered or DeliveryStatus.ProviderAccepted, 30000);
        var after = await HubFactory.EventuallyAsync(() => hub.DbAsync(db => db.CreditAccounts.AsNoTracking().FirstAsync(a => a.CompanyId == hub.SunriseCompany)), a => a.Balance < before.Balance);
        Assert.True(after.Balance < before.Balance);
        Assert.True(await hub.DbAsync(db => db.CreditTransactions.AnyAsync(t => t.AccountId == after.Id && t.Kind == CreditTransactionKind.Consume)));

        await hub.DbAsync(db => db.CreditAccounts.Where(a => a.Id == after.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Balance, 0m).SetProperty(a => a.Reserved, 0m)));
        try
        {
            var ex = await Assert.ThrowsAsync<NotificationHubException>(() => _sdk.SendAsync(Req.InvoiceTo(hub.SunriseCompany, new() { Mobile = Req.Phone() }, Channel.Sms)));
            Assert.Equal(HttpStatusCode.PaymentRequired, ex.StatusCode);
        }
        finally
        {
            await hub.DbAsync(db => db.CreditAccounts.Where(a => a.Id == after.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Balance, 500m)));
        }
    }

    [Fact]
    public async Task Monthly_quota_blocks_sends_beyond_the_limit()
    {
        var admin = await hub.AdminHttpAsync();
        var appId = (await (await admin.PostAsJsonAsync("/api/v1/applications", new { tenantId = hub.TenantId, code = "QUOTA-" + Guid.NewGuid().ToString("N")[..6], name = "Quota app", sandbox = true }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/api/v1/applications/{appId}/companies", new { companyId = hub.AbcCompany })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/v1/quotas", new { tenantId = hub.TenantId, applicationId = appId, channel = "inapp", monthlyLimit = 1 })).EnsureSuccessStatusCode();
        object Body() => new { applicationId = appId, companyId = hub.AbcCompany, templateCode = "STOCK_BELOW_REORDER_V1", channels = new[] { "inapp" }, recipient = new { userId = "u-1003" }, variables = new { ItemName = "x", Quantity = 1, ReorderLevel = 2, Warehouse = "w" } };
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync("/api/v1/notifications/send", Body())).StatusCode);
        var over = await admin.PostAsJsonAsync("/api/v1/notifications/send", Body());
        Assert.Equal((HttpStatusCode)429, over.StatusCode);
        Assert.Equal("quota_exceeded", (await over.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Business_event_triggers_rules_with_nested_conditions()
    {
        var match = await _sdk.PublishEventAsync(new BusinessEventRequest
        {
            CompanyId = hub.AbcCompany, EventType = "payment.overdue",
            Payload = JsonNode.Parse("""{"customer":{"customerId":"CUST-1001","tier":"gold"},"CustomerName":"ABC","InvoiceNumber":"INV-7","AmountDue":"500","DueDate":"2026-10-01","DaysOverdue":12}""")!.AsObject(),
        });
        var noMatch = await _sdk.PublishEventAsync(new BusinessEventRequest
        {
            CompanyId = hub.AbcCompany, EventType = "payment.overdue",
            Payload = JsonNode.Parse("""{"customer":{"customerId":"CUST-1001","tier":"silver"},"CustomerName":"ABC","InvoiceNumber":"INV-8","AmountDue":"500","DueDate":"2026-10-01","DaysOverdue":12}""")!.AsObject(),
        });
        var http = await hub.AppHttpAsync();
        var ev = await HubFactory.EventuallyAsync(() => http.GetFromJsonAsync<JsonElement>($"/api/v1/events/{match.EventId}"), e => e.TryGetProperty("processedAt", out var p) && p.ValueKind != JsonValueKind.Null);
        Assert.Equal(1, ev.GetProperty("matchedRules").GetInt32());
        Assert.Equal(1, ev.GetProperty("notificationsCreated").GetInt32());
        var ev2 = await HubFactory.EventuallyAsync(() => http.GetFromJsonAsync<JsonElement>($"/api/v1/events/{noMatch.EventId}"), e => e.TryGetProperty("processedAt", out var p) && p.ValueKind != JsonValueKind.Null);
        Assert.Equal(0, ev2.GetProperty("matchedRules").GetInt32());

        var dup = await _sdk.PublishEventAsync(new BusinessEventRequest { CompanyId = hub.AbcCompany, EventType = "x", Payload = new JsonObject(), IdempotencyKey = "evt-" + match.EventId });
        var dup2 = await _sdk.PublishEventAsync(new BusinessEventRequest { CompanyId = hub.AbcCompany, EventType = "x", Payload = new JsonObject(), IdempotencyKey = "evt-" + match.EventId });
        Assert.True(dup2.Duplicate);
        Assert.Equal(dup.EventId, dup2.EventId);
    }

    [Fact]
    public async Task Role_targeted_rule_and_simulation()
    {
        var admin = await hub.AdminHttpAsync();
        var sim = await admin.PostAsJsonAsync("/api/v1/rules/simulate", new
        {
            @event = new { applicationId = hub.ErpApp, companyId = hub.AbcCompany, eventType = "stock.below_reorder", payload = new { ItemName = "Paper", Quantity = 5, ReorderLevel = 50, Warehouse = "Main" } },
        });
        sim.EnsureSuccessStatusCode();
        var result = await sim.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("rules")[0].GetProperty("matched").GetBoolean());
        var preview = result.GetProperty("notifications")[0];
        Assert.Equal("dry_run", preview.GetProperty("status").GetString());
        var recipients = preview.GetProperty("deliveries").EnumerateArray().Select(d => d.GetProperty("recipient").GetString()).ToList();
        Assert.Contains("u-1003", recipients); // StoreKeeper
        Assert.Contains("u-1002", recipients); // Manager
        Assert.DoesNotContain("u-1001", recipients);
        Assert.Contains("Paper", preview.GetProperty("preview")[0].GetProperty("body").GetString());
        // Simulation persists nothing: every rule-created request belongs to a real event.
        Assert.Equal(0, await hub.DbAsync(db => db.NotificationRequests.CountAsync(r => r.RuleId != null && r.BusinessEventId == null)));
    }

    [Fact]
    public async Task Recurring_schedule_runs_on_cron()
    {
        var http = await hub.AppHttpAsync();
        var resp = await http.PostAsJsonAsync("/api/v1/schedules", new
        {
            companyId = hub.AbcCompany, name = "Every minute digest", cronExpression = "* * * * *", timeZone = "Asia/Kolkata", maxRuns = 1,
            notification = new { companyId = hub.AbcCompany, templateCode = "STOCK_BELOW_REORDER_V1", channels = new[] { "inapp" }, recipient = new { userId = "u-1003" }, variables = new { ItemName = "Toner", Quantity = 1, ReorderLevel = 5, Warehouse = "Main" } },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await hub.DbAsync(db => db.NotificationSchedules.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAt, DateTime.UtcNow)));
        var request = await HubFactory.EventuallyAsync(() => hub.DbAsync(db => db.NotificationRequests.FirstOrDefaultAsync(r => r.ScheduleId == id)), r => r is not null);
        Assert.Equal(RequestSource.Schedule, request!.Source);
        var schedule = await hub.DbAsync(db => db.NotificationSchedules.FirstAsync(s => s.Id == id));
        Assert.False(schedule.Enabled); // maxRuns reached
    }
}
