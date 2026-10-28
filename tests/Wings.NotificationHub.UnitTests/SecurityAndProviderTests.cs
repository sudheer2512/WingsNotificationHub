using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Rules;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Providers;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.UnitTests;

public class SecurityTests
{
    [Fact]
    public void Webhook_signature_round_trip_and_replay_window()
    {
        var now = DateTimeOffset.UtcNow;
        var header = WebhookSignature.Sign("secret", "{\"a\":1}", now);
        Assert.True(WebhookSignature.Verify("secret", "{\"a\":1}", header, now));
        Assert.False(WebhookSignature.Verify("secret", "{\"a\":2}", header, now));
        Assert.False(WebhookSignature.Verify("other", "{\"a\":1}", header, now));
        Assert.False(WebhookSignature.Verify("secret", "{\"a\":1}", header, now.AddMinutes(10))); // replay
        Assert.True(Wings.NotificationHub.Client.WebhookVerifier.Verify("secret", "{\"a\":1}", header, now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Passwords_and_secrets_hash_and_verify()
    {
        var h = Hashing.HashPassword("correct horse battery staple");
        Assert.True(Hashing.VerifyPassword("correct horse battery staple", h));
        Assert.False(Hashing.VerifyPassword("wrong", h));
        var s = Hashing.NewSecret("wnhs_");
        Assert.True(Hashing.VerifySecret(s, Hashing.HashSecret(s)));
        Assert.False(Hashing.VerifySecret(s + "x", Hashing.HashSecret(s)));
    }

    [Fact]
    public void Secret_protector_binds_purpose_and_supports_key_rotation()
    {
        var k1 = RandomNumberGenerator.GetBytes(32);
        var k2 = RandomNumberGenerator.GetBytes(32);
        var old = new AesGcmSecretProtector(new Dictionary<string, byte[]> { ["k1"] = k1 }, "k1");
        var cipher = old.Protect("api-key-123", Purposes.ProviderSecrets);
        Assert.DoesNotContain("api-key-123", cipher);
        var rotated = new AesGcmSecretProtector(new Dictionary<string, byte[]> { ["k1"] = k1, ["k2"] = k2 }, "k2");
        Assert.Equal("api-key-123", rotated.Unprotect(cipher, Purposes.ProviderSecrets));
        Assert.StartsWith("v1.k2.", rotated.Protect("x", "p"));
        Assert.ThrowsAny<CryptographicException>(() => rotated.Unprotect(cipher, Purposes.WebhookSecret));
    }

    [Fact]
    public void Redaction_masks_secrets_and_codes()
    {
        var s = Redact.Sanitize("{\"access_token\":\"abc123\",\"password\": \"p@ss\",\"ok\":1}")!;
        Assert.DoesNotContain("abc123", s);
        Assert.DoesNotContain("p@ss", s);
        Assert.Equal("Your code is ••••••", Redact.MaskCodes("Your code is 482913"));
    }

    [Fact]
    public void Caller_scopes_restrict_access_by_tenant_company_and_application()
    {
        var t1 = Guid.NewGuid(); var t2 = Guid.NewGuid(); var c1 = Guid.NewGuid(); var c2 = Guid.NewGuid(); var app = Guid.NewGuid();
        var companyAdmin = new CallerContext { Kind = CallerKind.AdminUser, Grants = [new ScopeGrant(Roles.CompanyAdmin, t1, c1, null, null)] };
        Assert.True(companyAdmin.Can(Permission.ManageTemplates, t1, c1));
        Assert.False(companyAdmin.Can(Permission.ManageTemplates, t1, c2));
        Assert.False(companyAdmin.Can(Permission.ManageTemplates, t1, null));   // cannot edit tenant-wide templates
        Assert.True(companyAdmin.Can(Permission.ViewTemplates, t1, null));      // but can read them
        Assert.False(companyAdmin.Can(Permission.ViewTemplates, t2, null));
        Assert.False(companyAdmin.Can(Permission.ManageTenancy, t1));

        var auditor = new CallerContext { Kind = CallerKind.AdminUser, Grants = [new ScopeGrant(Roles.Auditor, t1, null, null, null)] };
        Assert.True(auditor.Can(Permission.ViewAudit, t1));
        Assert.False(auditor.Can(Permission.ViewMessageContent, t1, c1));
        Assert.False(auditor.Can(Permission.RetryDeliveries, t1, c1));

        var appCaller = new CallerContext
        {
            Kind = CallerKind.Application, TenantId = t1, ApplicationId = app, AllowedCompanyIds = [c1], Scopes = new HashSet<string> { Scopes.NotificationsSend },
            Grants = [new ScopeGrant(Roles.ApiClient, t1, null, app, null)],
        };
        Assert.True(appCaller.Can(Permission.SendNotifications, t1, c1, app));
        Assert.False(appCaller.Can(Permission.SendNotifications, t1, c2, app));
        Assert.False(appCaller.Can(Permission.SendNotifications, t1, c1, Guid.NewGuid()));
        Assert.False(appCaller.Can(Permission.ViewNotifications, t1, c1, app)); // no notifications.read scope

        var channelOperator = new CallerContext { Kind = CallerKind.AdminUser, Grants = [new ScopeGrant(Roles.Operator, t1, null, null, Channel.Sms)] };
        Assert.True(channelOperator.Can(Permission.RetryDeliveries, t1, c1, app, Channel.Sms));
        Assert.False(channelOperator.Can(Permission.RetryDeliveries, t1, c1, app, Channel.Email));

        var f = companyAdmin.FilterFor(Permission.ViewNotifications);
        Assert.False(f.IsUnrestricted);
        Assert.Equal(c1, Assert.Single(f.Clauses).CompanyIds!.Single());
        Assert.True(new CallerContext { Kind = CallerKind.AdminUser, Grants = [new ScopeGrant(Roles.PlatformAdmin, null, null, null, null)] }.FilterFor(Permission.ViewNotifications).IsUnrestricted);
    }
}

public class ConditionTests
{
    private static readonly JsonNode Doc = JsonNode.Parse("""{"eventType":"payment.overdue","payload":{"AmountDue":15000,"DaysOverdue":"18","customer":{"tier":"gold","tags":["vip","north"]},"Due":"2026-10-10"}}""")!;

    private static ConditionNode Leaf(string f, string c, object? v) => new() { Field = f, Comparison = c, Value = v is null ? null : JsonSerializer.SerializeToNode(v) };

    [Theory]
    [InlineData("payload.AmountDue", "gt", 10000, true)]
    [InlineData("payload.AmountDue", "lte", 10000, false)]
    [InlineData("payload.DaysOverdue", "gte", 7, true)]      // numeric string vs number
    [InlineData("payload.customer.tier", "eq", "GOLD", true)]  // case-insensitive
    [InlineData("payload.customer.tags", "contains", "vip", true)]
    [InlineData("payload.customer.missing", "exists", null, false)]
    [InlineData("payload.customer.missing", "notexists", null, true)]
    [InlineData("payload.Due", "lt", "2026-10-28", true)]     // dates
    [InlineData("AmountDue", "eq", 15000, true)]              // payload. prefix optional
    public void Leaf_comparisons(string field, string cmp, object? value, bool expected)
        => Assert.Equal(expected, ConditionEvaluator.Evaluate(Leaf(field, cmp, value), Doc));

    [Fact]
    public void Nested_and_or_groups()
    {
        var tree = new ConditionNode
        {
            Op = "and",
            Children =
            [
                Leaf("payload.DaysOverdue", "gte", 7),
                new ConditionNode { Op = "or", Children = [Leaf("payload.AmountDue", "gt", 100000), Leaf("payload.customer.tier", "eq", "gold")] },
            ],
        };
        Assert.True(ConditionEvaluator.Evaluate(tree, Doc));
        tree.Children![1].Children![1] = Leaf("payload.customer.tier", "eq", "silver");
        Assert.False(ConditionEvaluator.Evaluate(tree, Doc));
        Assert.True(ConditionEvaluator.Evaluate(null, Doc));
    }

    [Fact]
    public void In_operator_and_validation()
    {
        Assert.True(ConditionEvaluator.Evaluate(Leaf("payload.customer.tier", "in", new[] { "gold", "platinum" }), Doc));
        Assert.NotEmpty(ConditionEvaluator.Validate(new ConditionNode { Op = "xor", Children = [] }));
        Assert.NotEmpty(ConditionEvaluator.Validate(Leaf("x", "regex", ".*")));
    }

    [Fact]
    public void Condition_tree_round_trips_through_rows()
    {
        var tree = new ConditionNode { Op = "or", Children = [Leaf("a", "eq", 1), new ConditionNode { Op = "and", Children = [Leaf("b", "gt", 2), Leaf("c", "exists", null)] }] };
        var rows = ConditionTree.Flatten(Guid.NewGuid(), tree);
        Assert.Equal(5, rows.Count);
        var rebuilt = ConditionTree.Build(rows)!;
        Assert.Equal("or", rebuilt.Op);
        Assert.Equal("and", rebuilt.Children![1].Op);
        Assert.Equal("b", rebuilt.Children[1].Children![0].Field);
    }
}

public class ProviderPayloadTests
{
    private static NotificationMessage Message(RenderedContent content, string to = "+919876543210") => new()
    {
        DeliveryId = Guid.NewGuid(), Channel = Channel.WhatsApp, To = to, Content = content, IdempotencyKey = "k",
        Push = new PushTarget { Token = "tok", Platform = DevicePlatform.Android },
    };

    [Fact]
    public void Whatsapp_template_payload_has_components()
    {
        var p = WhatsAppCloudProvider.BuildPayload(Message(new RenderedContent
        {
            WhatsAppTemplate = new WhatsAppTemplateCall
            {
                Name = "invoice_created_v1", Language = "en", HeaderParameters = ["INV-1"], BodyParameters = ["Ravi", "INV-1"],
                Buttons = [new WhatsAppButtonParameter { Index = 0, SubType = "url", Value = "abc" }],
            },
        }));
        Assert.Equal("919876543210", p["to"]!.GetValue<string>());
        Assert.Equal("template", p["type"]!.GetValue<string>());
        var comps = p["template"]!["components"]!.AsArray();
        Assert.Equal(["header", "body", "button"], comps.Select(c => c!["type"]!.GetValue<string>()));
        Assert.Equal("Ravi", comps[1]!["parameters"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Whatsapp_free_form_and_interactive_payloads()
    {
        Assert.Equal("text", WhatsAppCloudProvider.BuildPayload(Message(new RenderedContent { Body = "hi" }))["type"]!.GetValue<string>());
        var interactive = WhatsAppCloudProvider.BuildPayload(Message(new RenderedContent { Body = "Approve?", App = new AppNotificationOptionsDto { Actions = [new AppActionDto { Id = "yes", Label = "Yes" }] } }));
        Assert.Equal("interactive", interactive["type"]!.GetValue<string>());
    }

    [Fact]
    public void Whatsapp_webhook_parsing_and_signature()
    {
        const string body = """
        {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"messages","value":{"messaging_product":"whatsapp","metadata":{"display_phone_number":"911140000000","phone_number_id":"p1"},
        "contacts":[{"profile":{"name":"Ramesh"},"wa_id":"919876543210"}],
        "messages":[{"from":"919876543210","id":"wamid.IN1","timestamp":"1761625200","type":"text","text":{"body":"Please send my account statement."},"context":{"from":"911140000000","id":"wamid.OUT1"}}],
        "statuses":[{"id":"wamid.OUT1","status":"read","timestamp":"1761625100","recipient_id":"919876543210"},{"id":"wamid.OUT2","status":"failed","timestamp":"1761625100","recipient_id":"919800000000","errors":[{"code":131026,"title":"Message undeliverable"}]}]}}]}]}
        """;
        var events = WhatsAppCallbackHandler.Parse(body);
        var inbound = Assert.Single(events, e => e.Inbound is not null);
        Assert.Equal("+919876543210", inbound.Inbound!.From);
        Assert.Equal("wamid.OUT1", inbound.Inbound.InReplyToProviderMessageId);
        Assert.Equal("Ramesh", inbound.Inbound.FromName);
        Assert.Contains(events, e => e.ProviderMessageId == "wamid.OUT1" && e.Status == DeliveryStatus.Read);
        var failed = Assert.Single(events, e => e.Status == DeliveryStatus.Failed);
        Assert.Equal(SuppressionReason.InvalidAddress, failed.Suppress);

        var handler = new WhatsAppCallbackHandler("meta-whatsapp");
        var cfg = new ProviderAccountConfig { Secrets = new Dictionary<string, string> { ["appSecret"] = "s3cret" } };
        var sig = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes(body)));
        Assert.True(handler.ParseAsync(new CallbackRequest { Body = body, Headers = new Dictionary<string, string> { ["X-Hub-Signature-256"] = sig } }, cfg, default).Result.Authentic);
        Assert.False(handler.ParseAsync(new CallbackRequest { Body = body + " ", Headers = new Dictionary<string, string> { ["X-Hub-Signature-256"] = sig } }, cfg, default).Result.Authentic);
        var hs = handler.ParseAsync(new CallbackRequest { Method = "GET", Query = new Dictionary<string, string> { ["hub.mode"] = "subscribe", ["hub.verify_token"] = "vt", ["hub.challenge"] = "42" } },
            new ProviderAccountConfig { Settings = new JsonObject { ["verifyToken"] = "vt" } }, default).Result;
        Assert.Equal("42", hs.HandshakeResponse);
    }

    [Fact]
    public void Twilio_signature_validation()
    {
        var form = new Dictionary<string, string> { ["MessageSid"] = "SM1", ["MessageStatus"] = "delivered", ["To"] = "+919876543210" };
        const string url = "https://hub.example.com/api/v1/callbacks/abc";
        var data = url + string.Concat(form.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + k.Value));
        var sig = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes("tok"), Encoding.UTF8.GetBytes(data)));
        var cfg = new ProviderAccountConfig { Secrets = new Dictionary<string, string> { ["authToken"] = "tok" } };
        var ok = new TwilioCallbackHandler().ParseAsync(new CallbackRequest { Url = url, Form = form, Headers = new Dictionary<string, string> { ["X-Twilio-Signature"] = sig } }, cfg, default).Result;
        Assert.True(ok.Authentic);
        Assert.Equal(DeliveryStatus.Delivered, Assert.Single(ok.Events).Status);
        var bad = new TwilioCallbackHandler().ParseAsync(new CallbackRequest { Url = url + "x", Form = form, Headers = new Dictionary<string, string> { ["X-Twilio-Signature"] = sig } }, cfg, default).Result;
        Assert.False(bad.Authentic);
    }

    [Fact]
    public void Web_push_payload_encryption_round_trips()
    {
        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = WebPushCrypto.PublicKeyBytes(ua.ExportParameters(false));
        var auth = RandomNumberGenerator.GetBytes(16);
        var body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes("{\"title\":\"hi\"}"), uaPublic, auth);
        Assert.Equal(4096u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
        Assert.Equal("{\"title\":\"hi\"}", Encoding.UTF8.GetString(WebPushCrypto.Decrypt(body, ua, auth)));
    }

    [Fact]
    public void Vapid_header_is_a_valid_es256_jwt()
    {
        var (pub, priv) = WebPushCrypto.GenerateVapidKeys();
        var header = WebPushCrypto.VapidHeader(new Uri("https://fcm.googleapis.com/fcm/send/abc"), pub, priv, "mailto:ops@example.com");
        var jwt = header.Split(' ')[1].TrimStart('t', '=').TrimEnd(',');
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        var claims = JsonNode.Parse(Encoding.UTF8.GetString(Hashing.FromBase64Url(parts[1])))!;
        Assert.Equal("https://fcm.googleapis.com", claims["aud"]!.GetValue<string>());
        using var key = WebPushCrypto.ImportVapidKey(pub, priv);
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Hashing.FromBase64Url(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Fcm_payload_maps_priority_ttl_and_deep_link()
    {
        var m = new NotificationMessage
        {
            DeliveryId = Guid.NewGuid(), Channel = Channel.Push, To = "d", Priority = NotificationPriority.High, IdempotencyKey = "k",
            Content = new RenderedContent { Title = "T", Body = "B", App = new AppNotificationOptionsDto { DeepLink = "wingserp://orders/1", TimeToLiveSeconds = 60 } },
            Push = new PushTarget { Token = "fcm-token", Platform = DevicePlatform.Android },
        };
        var p = FcmPushProvider.BuildPayload(m, "alerts")["message"]!;
        Assert.Equal("fcm-token", p["token"]!.GetValue<string>());
        Assert.Equal("HIGH", p["android"]!["priority"]!.GetValue<string>());
        Assert.Equal("60s", p["android"]!["ttl"]!.GetValue<string>());
        Assert.Equal("wingserp://orders/1", p["data"]!["deepLink"]!.GetValue<string>());
    }

    [Fact]
    public void Ssrf_guard_blocks_private_addresses()
    {
        Assert.True(UrlGuard.IsPrivate(System.Net.IPAddress.Parse("127.0.0.1")));
        Assert.True(UrlGuard.IsPrivate(System.Net.IPAddress.Parse("10.1.2.3")));
        Assert.True(UrlGuard.IsPrivate(System.Net.IPAddress.Parse("169.254.169.254")));
        Assert.True(UrlGuard.IsPrivate(System.Net.IPAddress.Parse("::1")));
        Assert.False(UrlGuard.IsPrivate(System.Net.IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Csv_parser_handles_quotes()
        => Assert.Equal(["a", "b,c", "d\"e", ""], CampaignService.ParseCsvLine("a,\"b,c\",\"d\"\"e\","));

    [Fact]
    public void Channel_names_round_trip_on_the_wire()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal("\"whatsapp\"", JsonSerializer.Serialize(Channel.WhatsApp, o));
        Assert.Equal(Channel.InApp, JsonSerializer.Deserialize<Channel>("\"in_app\"", o));
        Assert.Equal(Channel.WebPush, JsonSerializer.Deserialize<Channel>("\"web-push\"", o));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Channel>("\"fax\"", o));
    }
}
