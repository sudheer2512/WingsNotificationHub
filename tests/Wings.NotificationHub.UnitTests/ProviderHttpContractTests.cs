using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Providers;

namespace Wings.NotificationHub.UnitTests;

/// <summary>Provider adapters against HTTP doubles: request shape, authentication and outcome mapping.</summary>
public class ProviderHttpContractTests
{
    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];
        public List<byte[]> RawBodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var raw = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            RawBodies.Add(raw);
            Calls.Add((request, Encoding.UTF8.GetString(raw)));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static NotificationMessage Msg(Channel ch, string to, RenderedContent? content = null, PushTarget? push = null) => new()
    {
        DeliveryId = Guid.NewGuid(), Channel = ch, To = to, IdempotencyKey = "wnh-1", Content = content ?? new RenderedContent { Body = "Hello", Title = "T" }, Push = push,
        CallbackUrl = "https://hub.example.com/api/v1/callbacks/x",
    };

    [Fact]
    public async Task Meta_whatsapp_send_uses_graph_api_with_bearer_token()
    {
        var rec = new Recorder(_ => Json(HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.ABC"}]}"""));
        var provider = new MetaWhatsAppProviderFactory(new Factory(rec)).Create(new ProviderAccountConfig
        {
            Name = "wa", Settings = new JsonObject { ["phoneNumberId"] = "12345" }, Secrets = new Dictionary<string, string> { ["accessToken"] = "EAAG" },
        });
        var r = await provider.SendAsync(Msg(Channel.WhatsApp, "+919876543210"), default);
        Assert.Equal(AttemptOutcome.Accepted, r.Outcome);
        Assert.Equal("wamid.ABC", r.ProviderMessageId);
        var call = Assert.Single(rec.Calls);
        Assert.Equal("https://graph.facebook.com/v21.0/12345/messages", call.Request.RequestUri!.ToString());
        Assert.Equal("Bearer EAAG", call.Request.Headers.Authorization!.ToString());
        Assert.Equal("919876543210", JsonNode.Parse(call.Body)!["to"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("131026", AttemptOutcome.PermanentFailure, true)]
    [InlineData("131047", AttemptOutcome.PermanentFailure, false)]
    [InlineData("130429", AttemptOutcome.Throttled, false)]
    [InlineData("131000", AttemptOutcome.TransientFailure, false)]
    public async Task Meta_whatsapp_error_codes_map_to_outcomes(string code, AttemptOutcome expected, bool invalid)
    {
        var rec = new Recorder(_ => Json(HttpStatusCode.BadRequest, "{\"error\":{\"code\":" + code + ",\"message\":\"x\"}}"));
        var provider = new MetaWhatsAppProviderFactory(new Factory(rec)).Create(new ProviderAccountConfig { Settings = new JsonObject { ["phoneNumberId"] = "1" }, Secrets = new Dictionary<string, string> { ["accessToken"] = "t" } });
        var r = await provider.SendAsync(Msg(Channel.WhatsApp, "+919876543210"), default);
        Assert.Equal(expected, r.Outcome);
        Assert.Equal(invalid, r.InvalidAddress);
    }

    [Fact]
    public async Task Twilio_send_is_form_encoded_with_basic_auth_and_maps_errors()
    {
        var rec = new Recorder(req => req.Content is FormUrlEncodedContent
            ? Json(HttpStatusCode.Created, """{"sid":"SM123","status":"queued","num_segments":"2"}""")
            : Json(HttpStatusCode.OK, "{}"));
        var cfg = new ProviderAccountConfig { Settings = new JsonObject { ["accountSid"] = "AC1", ["fromNumber"] = "+15550001111" }, Secrets = new Dictionary<string, string> { ["authToken"] = "tok" } };
        var r = await new TwilioSmsProviderFactory(new Factory(rec)).Create(cfg).SendAsync(Msg(Channel.Sms, "+919876543210"), default);
        Assert.Equal("SM123", r.ProviderMessageId);
        Assert.Equal(2, r.Units);
        var call = rec.Calls.Single();
        Assert.EndsWith("/Accounts/AC1/Messages.json", call.Request.RequestUri!.AbsolutePath);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("AC1:tok")), call.Request.Headers.Authorization!.ToString());
        Assert.Contains("StatusCallback=https%3A%2F%2Fhub.example.com", call.Body);

        var bad = new Recorder(_ => Json(HttpStatusCode.BadRequest, """{"code":21211,"message":"Invalid 'To' Phone Number"}"""));
        var r2 = await new TwilioSmsProviderFactory(new Factory(bad)).Create(cfg).SendAsync(Msg(Channel.Sms, "+910000"), default);
        Assert.Equal(AttemptOutcome.PermanentFailure, r2.Outcome);
        Assert.True(r2.InvalidAddress);
        var down = new Recorder(_ => Json(HttpStatusCode.InternalServerError, "{}"));
        Assert.Equal(AttemptOutcome.Ambiguous, (await new TwilioSmsProviderFactory(new Factory(down)).Create(cfg).SendAsync(Msg(Channel.Sms, "+919876543210"), default)).Outcome);
    }

    [Fact]
    public async Task Msg91_requires_dlt_fields_for_indian_numbers()
    {
        var rec = new Recorder(_ => Json(HttpStatusCode.OK, """{"type":"success","message":"req-1"}"""));
        var provider = new Msg91SmsProviderFactory(new Factory(rec)).Create(new ProviderAccountConfig { Settings = new JsonObject { ["senderId"] = "ABCTRD" }, Secrets = new Dictionary<string, string> { ["authKey"] = "k" } });
        var missing = await provider.SendAsync(Msg(Channel.Sms, "+919876543210"), default);
        Assert.Equal("dlt_missing", missing.ErrorCode);
        Assert.Empty(rec.Calls);
        var ok = await provider.SendAsync(new NotificationMessage { DeliveryId = Guid.NewGuid(), Channel = Channel.Sms, To = "+919876543210", IdempotencyKey = "k", DltEntityId = "110", DltTemplateId = "1107", Content = new RenderedContent { Body = "Hi" } }, default);
        Assert.Equal("req-1", ok.ProviderMessageId);
        var body = JsonNode.Parse(rec.Calls.Single().Body)!;
        Assert.Equal("1107", body["DLT_TE_ID"]!.GetValue<string>());
        Assert.Equal("110", body["PE_ID"]!.GetValue<string>());
    }

    [Fact]
    public async Task Generic_http_sms_escapes_json_values()
    {
        var rec = new Recorder(_ => Json(HttpStatusCode.OK, """{"data":{"id":"g-9"},"ok":true}"""));
        var provider = new HttpSmsProviderFactory(new Factory(rec)).Create(new ProviderAccountConfig
        {
            Settings = new JsonObject { ["url"] = "https://gw.example/send", ["messageIdPath"] = "data.id", ["successPath"] = "ok", ["successValue"] = "true" },
            Secrets = new Dictionary<string, string> { ["apiKey"] = "secret" },
        });
        var r = await provider.SendAsync(Msg(Channel.Sms, "+919876543210", new RenderedContent { Body = "Quote \" and \\ back" }), default);
        Assert.Equal("g-9", r.ProviderMessageId);
        Assert.Equal("Quote \" and \\ back", JsonNode.Parse(rec.Calls.Single().Body)!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Fcm_unregistered_token_is_invalid_address()
    {
        using var rsa = RSA.Create(2048);
        var sa = new JsonObject { ["client_email"] = "svc@p.iam.gserviceaccount.com", ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(), ["token_uri"] = "https://oauth2.example/token" };
        var rec = new Recorder(req => req.RequestUri!.Host == "oauth2.example"
            ? Json(HttpStatusCode.OK, """{"access_token":"ya29","expires_in":3600}""")
            : Json(HttpStatusCode.NotFound, """{"error":{"status":"UNREGISTERED"}}"""));
        var provider = new FcmPushProviderFactory(new Factory(rec)).Create(new ProviderAccountConfig { AccountId = Guid.NewGuid(), Settings = new JsonObject { ["projectId"] = "p" }, Secrets = new Dictionary<string, string> { ["serviceAccountJson"] = sa.ToJsonString() } });
        var r = await provider.SendAsync(Msg(Channel.Push, "dev", push: new PushTarget { Token = "tok", Platform = DevicePlatform.Android }), default);
        Assert.Equal(AttemptOutcome.PermanentFailure, r.Outcome);
        Assert.True(r.InvalidAddress);
        var assertion = rec.Calls[0].Body;
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", assertion);
        Assert.Equal("Bearer ya29", rec.Calls[1].Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Web_push_posts_encrypted_payload_with_vapid_and_handles_gone()
    {
        var (pub, priv) = WebPushCrypto.GenerateVapidKeys();
        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var auth = RandomNumberGenerator.GetBytes(16);
        var target = new PushTarget { Token = "https://push.example/sub/1", Platform = DevicePlatform.Web, P256dh = Hashing.Base64Url(WebPushCrypto.PublicKeyBytes(ua.ExportParameters(false))), Auth = Hashing.Base64Url(auth) };
        var rec = new Recorder(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var cfg = new ProviderAccountConfig { Settings = new JsonObject { ["vapidPublicKey"] = pub }, Secrets = new Dictionary<string, string> { ["vapidPrivateKey"] = priv } };
        var r = await new WebPushProviderFactory(new Factory(rec)).Create(cfg).SendAsync(Msg(Channel.WebPush, "dev", new RenderedContent { Title = "Hi", Body = "There" }, target), default);
        Assert.Equal(AttemptOutcome.Accepted, r.Outcome);
        var call = rec.Calls.Single();
        Assert.StartsWith("vapid t=", call.Request.Headers.GetValues("Authorization").Single());
        Assert.Equal("aes128gcm", call.Request.Content!.Headers.ContentEncoding.Single());
        var bytes = rec.RawBodies.Single();
        Assert.Contains("\"title\":\"Hi\"", Encoding.UTF8.GetString(WebPushCrypto.Decrypt(bytes, ua, auth)));

        var gone = new Recorder(_ => new HttpResponseMessage(HttpStatusCode.Gone));
        var r2 = await new WebPushProviderFactory(new Factory(gone)).Create(cfg).SendAsync(Msg(Channel.WebPush, "dev", null, target), default);
        Assert.True(r2.InvalidAddress);
    }

    [Fact]
    public async Task Microsoft365_send_mail_uses_client_credentials_then_graph()
    {
        var rec = new Recorder(req => req.RequestUri!.Host == "login.microsoftonline.com"
            ? Json(HttpStatusCode.OK, """{"access_token":"m365","expires_in":3600}""")
            : new HttpResponseMessage(HttpStatusCode.Accepted));
        var provider = new Microsoft365ProviderFactory(new Factory(rec)).Create(new ProviderAccountConfig
        {
            AccountId = Guid.NewGuid(), Settings = new JsonObject { ["tenantId"] = "t1", ["clientId"] = "c1", ["senderMailbox"] = "billing@contoso.com" },
            Secrets = new Dictionary<string, string> { ["clientSecret"] = "s" },
        });
        var r = await provider.SendAsync(Msg(Channel.Email, "a@b.com", new RenderedContent { Subject = "S", Body = "<p>x</p>", Format = BodyFormat.Html }), default);
        Assert.Equal(AttemptOutcome.Accepted, r.Outcome);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/billing%40contoso.com/sendMail", rec.Calls[1].Request.RequestUri!.ToString());
        Assert.Equal("HTML", JsonNode.Parse(rec.Calls[1].Body)!["message"]!["body"]!["contentType"]!.GetValue<string>());
    }
}
