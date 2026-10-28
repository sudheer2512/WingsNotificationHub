using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Amazon;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Messaging;

namespace Wings.NotificationHub.Infrastructure.Providers;

internal static class MimeBuilder
{
    public static MimeMessage Build(NotificationMessage m, string defaultFrom, string? defaultFromName, string messageIdDomain)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(m.FromName ?? defaultFromName ?? "", m.From ?? defaultFrom));
        msg.To.Add(MailboxAddress.Parse(m.To));
        foreach (var cc in m.Content.Cc ?? []) msg.Cc.Add(MailboxAddress.Parse(cc));
        foreach (var bcc in m.Content.Bcc ?? []) msg.Bcc.Add(MailboxAddress.Parse(bcc));
        if (!string.IsNullOrEmpty(m.ReplyTo)) msg.ReplyTo.Add(MailboxAddress.Parse(m.ReplyTo));
        msg.Subject = m.Content.Subject ?? "";
        // Stable Message-ID per delivery: lets mail systems and our bounce correlation identify retries of the same message.
        msg.MessageId = $"{m.DeliveryId:N}@{messageIdDomain}";
        msg.Headers.Add("X-Wings-Delivery-Id", m.DeliveryId.ToString());
        foreach (var (k, v) in m.Headers) msg.Headers.Add(k, v);
        if (m.Priority == NotificationPriority.Critical) msg.Priority = MessagePriority.Urgent;

        var builder = new BodyBuilder();
        if (m.Content.Format == BodyFormat.Html)
        {
            builder.HtmlBody = m.Content.Body;
            builder.TextBody = m.Content.TextBody;
        }
        else builder.TextBody = m.Content.Body;
        foreach (var a in m.Attachments)
        {
            if (a.Inline && m.Content.Format == BodyFormat.Html)
            {
                var res = builder.LinkedResources.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
                res.ContentId = a.ContentId ?? a.FileName;
            }
            else builder.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        }
        msg.Body = builder.ToMessageBody();
        return msg;
    }
}

// ───────────────────────────── SMTP ─────────────────────────────

public sealed class SmtpEmailProviderFactory : IProviderFactory
{
    public string ProviderType => "smtp";
    public IReadOnlyList<Channel> Channels => [Channel.Email];
    public string DisplayName => "SMTP (any mail server, Microsoft 365 SMTP AUTH, Gmail, Postfix…)";
    public IReadOnlyList<string> SecretKeys => ["password"];
    public IReadOnlyList<string> SettingKeys => ["host", "port", "security", "username", "fromAddress", "fromName", "messageIdDomain"];
    public INotificationProvider Create(ProviderAccountConfig config) => new SmtpEmailProvider(config);
}

/// <summary>SMTP via MailKit. 5xx replies are permanent, 4xx transient; failures after DATA is sent are ambiguous.</summary>
public sealed class SmtpEmailProvider(ProviderAccountConfig config) : INotificationProvider
{
    public ProviderCapabilities Capabilities => new() { SupportsDeliveryReceipts = false };

    private SecureSocketOptions Security => (config.Setting("security") ?? "auto").ToLowerInvariant() switch
    {
        "none" => SecureSocketOptions.None,
        "ssl" or "ssltls" or "tls" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.Auto,
    };

    public async Task<SendResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        var host = config.RequireSetting("host");
        var port = int.TryParse(config.Setting("port"), out var p) ? p : 587;
        var from = config.Setting("fromAddress") ?? message.From ?? throw new ProviderConfigurationException("SMTP provider needs a sender address.");
        var mime = MimeBuilder.Build(message, from, config.Setting("fromName"), config.Setting("messageIdDomain") ?? from.Split('@').Last());
        using var client = new SmtpClient { Timeout = (int)config.Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(host, port, Security, ct);
            if (config.Setting("username") is { Length: > 0 } user) await client.AuthenticateAsync(user, config.RequireSecret("password"), ct);
        }
        catch (Exception ex) when (ex is SocketException or SslHandshakeException or AuthenticationException or SmtpProtocolException or IOException or SmtpCommandException)
        {
            // Nothing was submitted yet.
            return SendResult.Transient("smtp_connect", $"{ex.GetType().Name}: {ex.Message}");
        }
        try
        {
            var response = await client.SendAsync(mime, ct);
            await client.DisconnectAsync(true, CancellationToken.None);
            return SendResult.Accepted(mime.MessageId, Truncate(response, 300));
        }
        catch (SmtpCommandException ex)
        {
            var code = (int)ex.StatusCode;
            var invalid = ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted && code >= 500;
            return code >= 500
                ? SendResult.Permanent($"smtp_{code}", ex.Message, invalid, code.ToString())
                : SendResult.Transient($"smtp_{code}", ex.Message, code.ToString());
        }
        catch (Exception ex) when (ex is IOException or SmtpProtocolException or SocketException)
        {
            return SendResult.Ambiguous("smtp_transport", $"Connection lost while sending: {ex.Message}");
        }
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = new SmtpClient { Timeout = 10_000 };
            await client.ConnectAsync(config.RequireSetting("host"), int.TryParse(config.Setting("port"), out var p) ? p : 587, Security, ct);
            if (config.Setting("username") is { Length: > 0 } user) await client.AuthenticateAsync(user, config.RequireSecret("password"), ct);
            await client.DisconnectAsync(true, ct);
            return ProviderHealth.Healthy("SMTP connect/auth succeeded.", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex) { return ProviderHealth.Unhealthy($"{ex.GetType().Name}: {ex.Message}"); }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

// ───────────────────────────── Amazon SES v2 ─────────────────────────────

public sealed class AmazonSesProviderFactory : IProviderFactory
{
    public string ProviderType => "ses";
    public IReadOnlyList<Channel> Channels => [Channel.Email];
    public string DisplayName => "Amazon SES (v2 API)";
    public IReadOnlyList<string> SecretKeys => ["accessKeyId", "secretAccessKey"];
    public IReadOnlyList<string> SettingKeys => ["region", "configurationSet", "fromAddress", "fromName", "messageIdDomain"];
    public INotificationProvider Create(ProviderAccountConfig config) => new AmazonSesProvider(config);
}

/// <summary>SES SendEmail with raw MIME (attachments, inline images, custom headers). Bounces/complaints arrive via SNS.</summary>
public sealed class AmazonSesProvider(ProviderAccountConfig config) : INotificationProvider
{
    public ProviderCapabilities Capabilities => new() { SupportsDeliveryReceipts = true, SupportsReadReceipts = true };

    private AmazonSimpleEmailServiceV2Client Client()
    {
        var region = RegionEndpoint.GetBySystemName(config.Setting("region") ?? "ap-south-1");
        var cfg = new AmazonSimpleEmailServiceV2Config { RegionEndpoint = region, Timeout = config.Timeout, MaxErrorRetry = 0 };
        return config.Secret("accessKeyId") is { Length: > 0 } key
            ? new AmazonSimpleEmailServiceV2Client(new Amazon.Runtime.BasicAWSCredentials(key, config.RequireSecret("secretAccessKey")), cfg)
            : new AmazonSimpleEmailServiceV2Client(cfg); // instance role / environment credentials
    }

    public async Task<SendResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        var from = config.Setting("fromAddress") ?? message.From ?? throw new ProviderConfigurationException("SES provider needs a sender address.");
        var mime = MimeBuilder.Build(message, from, config.Setting("fromName"), config.Setting("messageIdDomain") ?? from.Split('@').Last());
        using var ms = new MemoryStream();
        await mime.WriteToAsync(ms, ct);
        using var client = Client();
        try
        {
            var resp = await client.SendEmailAsync(new SendEmailRequest
            {
                Content = new EmailContent { Raw = new RawMessage { Data = new MemoryStream(ms.ToArray()) } },
                ConfigurationSetName = config.Setting("configurationSet"),
                EmailTags = [new MessageTag { Name = "wnh_delivery", Value = message.DeliveryId.ToString("N") }],
            }, ct);
            return SendResult.Accepted(resp.MessageId, "SES accepted");
        }
        catch (MessageRejectedException ex) { return SendResult.Permanent("ses_rejected", ex.Message); }
        catch (AccountSuspendedException ex) { return SendResult.Transient("ses_account_suspended", ex.Message); }
        catch (SendingPausedException ex) { return SendResult.Transient("ses_sending_paused", ex.Message); }
        catch (TooManyRequestsException ex) { return SendResult.Throttled(ex.Message, TimeSpan.FromSeconds(5)); }
        catch (LimitExceededException ex) { return SendResult.Throttled(ex.Message, TimeSpan.FromSeconds(30)); }
        catch (BadRequestException ex) { return SendResult.Permanent("ses_bad_request", ex.Message); }
        catch (Amazon.Runtime.AmazonServiceException ex) when ((int)ex.StatusCode >= 500)
        {
            return SendResult.Ambiguous("ses_server_error", ex.Message);
        }
        catch (Amazon.Runtime.AmazonServiceException ex) { return SendResult.Transient("ses_error", ex.Message, ((int)ex.StatusCode).ToString()); }
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var client = Client();
            var acct = await client.GetAccountAsync(new GetAccountRequest(), ct);
            return acct.SendingEnabled == true ? ProviderHealth.Healthy($"SES sending enabled; production access={acct.ProductionAccessEnabled}") : ProviderHealth.Unhealthy("SES sending is disabled for the account.");
        }
        catch (Exception ex) { return ProviderHealth.Unhealthy(ex.Message); }
    }
}

/// <summary>
/// SES events via SNS (HTTP/S subscription). Verifies the SNS message signature against the AWS signing certificate
/// (host must be sns.&lt;region&gt;.amazonaws.com), auto-confirms subscriptions, and maps Delivery/Bounce/Complaint/Open/Click.
/// </summary>
public sealed class SesSnsCallbackHandler(IHttpClientFactory http) : ICallbackHandler
{
    private static readonly Dictionary<string, X509Certificate2> CertCache = [];
    public string ProviderType => "ses";

    public async Task<CallbackParseResult> ParseAsync(CallbackRequest request, ProviderAccountConfig config, CancellationToken ct)
    {
        var sns = JsonNode.Parse(request.Body)?.AsObject();
        if (sns is null) return CallbackParseResult.Reject("Empty SNS body.");
        if (!await VerifyAsync(sns, ct)) return CallbackParseResult.Reject("SNS signature verification failed.");
        var type = sns["Type"]?.GetValue<string>();
        if (type == "SubscriptionConfirmation")
        {
            var url = sns["SubscribeURL"]?.GetValue<string>();
            if (url is not null && IsAwsHost(url)) await http.CreateClient("providers").GetAsync(url, ct);
            return new CallbackParseResult { Authentic = true, HandshakeResponse = "confirmed" };
        }
        if (type != "Notification") return new CallbackParseResult { Authentic = true };
        var msg = JsonNode.Parse(sns["Message"]!.GetValue<string>())!.AsObject();
        var eventType = (msg["eventType"] ?? msg["notificationType"])?.GetValue<string>();
        var mail = msg["mail"]!;
        var messageId = mail["messageId"]?.GetValue<string>();
        var eventId = sns["MessageId"]?.GetValue<string>();
        var events = new List<ProviderCallbackEvent>();
        switch (eventType)
        {
            case "Delivery":
                events.Add(new ProviderCallbackEvent { EventId = eventId, ProviderMessageId = messageId, Status = DeliveryStatus.Delivered });
                break;
            case "Bounce":
                var permanent = msg["bounce"]?["bounceType"]?.GetValue<string>() == "Permanent";
                foreach (var r in msg["bounce"]?["bouncedRecipients"]?.AsArray() ?? [])
                    events.Add(new ProviderCallbackEvent
                    {
                        EventId = eventId, ProviderMessageId = messageId, Status = permanent ? DeliveryStatus.Failed : null,
                        ErrorCode = permanent ? "hard_bounce" : "soft_bounce", ErrorMessage = r?["diagnosticCode"]?.GetValue<string>(),
                        Suppress = permanent ? SuppressionReason.HardBounce : null, SuppressAddress = r?["emailAddress"]?.GetValue<string>(),
                    });
                break;
            case "Complaint":
                foreach (var r in msg["complaint"]?["complainedRecipients"]?.AsArray() ?? [])
                    events.Add(new ProviderCallbackEvent { EventId = eventId, ProviderMessageId = messageId, Interaction = "complaint", Suppress = SuppressionReason.Complaint, SuppressAddress = r?["emailAddress"]?.GetValue<string>() });
                break;
            case "Open":
                events.Add(new ProviderCallbackEvent { EventId = eventId, ProviderMessageId = messageId, Status = DeliveryStatus.Read, Interaction = "open" });
                break;
            case "Click":
                events.Add(new ProviderCallbackEvent { EventId = eventId, ProviderMessageId = messageId, Interaction = "click:" + msg["click"]?["link"]?.GetValue<string>() });
                break;
            case "Reject":
            case "Rendering Failure":
                events.Add(new ProviderCallbackEvent { EventId = eventId, ProviderMessageId = messageId, Status = DeliveryStatus.Failed, ErrorCode = "ses_" + eventType.Replace(' ', '_').ToLowerInvariant() });
                break;
        }
        return new CallbackParseResult { Authentic = true, Events = events };
    }

    private static bool IsAwsHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https"
           && System.Text.RegularExpressions.Regex.IsMatch(u.Host, @"^sns\.[a-z0-9-]+\.amazonaws\.com(\.cn)?$");

    private async Task<bool> VerifyAsync(JsonObject sns, CancellationToken ct)
    {
        var certUrl = sns["SigningCertURL"]?.GetValue<string>() ?? sns["SigningCertUrl"]?.GetValue<string>();
        var signature = sns["Signature"]?.GetValue<string>();
        if (certUrl is null || signature is null || !IsAwsHost(certUrl)) return false;
        X509Certificate2? cert;
        lock (CertCache) CertCache.TryGetValue(certUrl, out cert);
        if (cert is null)
        {
            var pem = await http.CreateClient("providers").GetStringAsync(certUrl, ct);
            cert = X509Certificate2.CreateFromPem(pem);
            lock (CertCache) CertCache[certUrl] = cert;
        }
        var type = sns["Type"]?.GetValue<string>();
        string[] keys = type == "Notification"
            ? ["Message", "MessageId", "Subject", "Timestamp", "TopicArn", "Type"]
            : ["Message", "MessageId", "SubscribeURL", "Timestamp", "Token", "TopicArn", "Type"];
        var sb = new StringBuilder();
        foreach (var k in keys)
        {
            if (sns[k] is not { } v) continue;
            sb.Append(k).Append('\n').Append(v.GetValue<string>()).Append('\n');
        }
        var alg = sns["SignatureVersion"]?.GetValue<string>() == "2" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1;
        using var rsa = cert.GetRSAPublicKey();
        return rsa is not null && rsa.VerifyData(Encoding.UTF8.GetBytes(sb.ToString()), Convert.FromBase64String(signature), alg, RSASignaturePadding.Pkcs1);
    }
}

// ───────────────────────────── Microsoft 365 (Graph) ─────────────────────────────

public sealed class Microsoft365ProviderFactory(IHttpClientFactory http) : IProviderFactory
{
    public string ProviderType => "m365";
    public IReadOnlyList<Channel> Channels => [Channel.Email];
    public string DisplayName => "Microsoft 365 (Graph sendMail, app-only)";
    public IReadOnlyList<string> SecretKeys => ["clientSecret"];
    public IReadOnlyList<string> SettingKeys => ["tenantId", "clientId", "senderMailbox", "saveToSentItems"];
    public INotificationProvider Create(ProviderAccountConfig config) => new Microsoft365EmailProvider(config, http);
}

/// <summary>
/// Sends through Microsoft Graph <c>/users/{mailbox}/sendMail</c> with an app-only (client credentials) token.
/// Requires the Mail.Send application permission, ideally scoped with an Exchange application access policy.
/// </summary>
public sealed class Microsoft365EmailProvider(ProviderAccountConfig config, IHttpClientFactory http) : INotificationProvider
{
    private static readonly Dictionary<Guid, (string Token, DateTime Expires)> Tokens = [];
    public ProviderCapabilities Capabilities => new();

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        lock (Tokens) if (Tokens.TryGetValue(config.AccountId, out var t) && t.Expires > DateTime.UtcNow.AddMinutes(2)) return t.Token;
        var client = http.CreateClient("providers");
        var resp = await client.PostAsync($"https://login.microsoftonline.com/{config.RequireSetting("tenantId")}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.RequireSetting("clientId"), ["client_secret"] = config.RequireSecret("clientSecret"),
            ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials",
        }), ct);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!resp.IsSuccessStatusCode) throw new ProviderConfigurationException("Microsoft identity platform rejected the client credentials: " + json?["error_description"]);
        var token = json!["access_token"]!.GetValue<string>();
        lock (Tokens) Tokens[config.AccountId] = (token, DateTime.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3000));
        return token;
    }

    public async Task<SendResult> SendAsync(NotificationMessage m, CancellationToken ct)
    {
        var mailbox = m.From ?? config.RequireSetting("senderMailbox");
        var html = m.Content.Format == BodyFormat.Html;
        var payload = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["subject"] = m.Content.Subject,
                ["body"] = new JsonObject { ["contentType"] = html ? "HTML" : "Text", ["content"] = m.Content.Body },
                ["toRecipients"] = new JsonArray(Recipient(m.To)),
                ["ccRecipients"] = new JsonArray((m.Content.Cc ?? []).Select(Recipient).ToArray()),
                ["bccRecipients"] = new JsonArray((m.Content.Bcc ?? []).Select(Recipient).ToArray()),
                ["replyTo"] = m.ReplyTo is null ? new JsonArray() : new JsonArray(Recipient(m.ReplyTo)),
                ["internetMessageHeaders"] = new JsonArray(new JsonObject { ["name"] = "X-Wings-Delivery-Id", ["value"] = m.DeliveryId.ToString() }),
                ["attachments"] = new JsonArray(m.Attachments.Select(a => (JsonNode)new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = a.FileName, ["contentType"] = a.ContentType,
                    ["contentBytes"] = Convert.ToBase64String(a.Content), ["isInline"] = a.Inline, ["contentId"] = a.ContentId,
                }).ToArray()),
            },
            ["saveToSentItems"] = config.Setting("saveToSentItems") != "false",
        };
        var client = http.CreateClient("providers");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(mailbox)}/sendMail") { Content = JsonContent.Create(payload) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return resp.StatusCode switch
        {
            HttpStatusCode.Accepted => SendResult.Accepted($"m365-{m.DeliveryId:N}", "Graph accepted (202)"),
            HttpStatusCode.TooManyRequests => SendResult.Throttled(body, resp.Headers.RetryAfter?.Delta),
            >= HttpStatusCode.InternalServerError => SendResult.Ambiguous($"graph_{(int)resp.StatusCode}", body),
            _ => SendResult.Permanent($"graph_{(int)resp.StatusCode}", body, responseCode: ((int)resp.StatusCode).ToString()),
        };

        static JsonNode Recipient(string address) => new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = address } };
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        try { await TokenAsync(ct); return ProviderHealth.Healthy("Token acquired from Microsoft identity platform."); }
        catch (Exception ex) { return ProviderHealth.Unhealthy(ex.Message); }
    }
}
