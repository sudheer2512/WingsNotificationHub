using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Security;

namespace Wings.NotificationHub.Infrastructure.Providers;

internal static class Jwt
{
    public static string Create(JsonObject header, JsonObject payload, Func<byte[], byte[]> sign)
    {
        var h = Hashing.Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()));
        var p = Hashing.Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var input = Encoding.ASCII.GetBytes($"{h}.{p}");
        return $"{h}.{p}.{Hashing.Base64Url(sign(input))}";
    }

    public static string Es256(ECDsa key, JsonObject header, JsonObject payload)
        => Create(header, payload, data => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}

// ───────────────────────────── Firebase Cloud Messaging (HTTP v1) ─────────────────────────────

public sealed class FcmPushProviderFactory(IHttpClientFactory http) : IProviderFactory
{
    public string ProviderType => "fcm";
    public IReadOnlyList<Channel> Channels => [Channel.Push, Channel.WebPush];
    public string DisplayName => "Firebase Cloud Messaging (HTTP v1)";
    public IReadOnlyList<string> SecretKeys => ["serviceAccountJson"];
    public IReadOnlyList<string> SettingKeys => ["projectId", "androidChannelId"];
    public INotificationProvider Create(ProviderAccountConfig config) => new FcmPushProvider(config, http);
}

/// <summary>FCM v1. Acceptance means Google accepted the message; device delivery is not confirmed by FCM.</summary>
public sealed class FcmPushProvider(ProviderAccountConfig config, IHttpClientFactory http) : INotificationProvider
{
    private static readonly Dictionary<Guid, (string Token, DateTime Expires)> Tokens = [];
    public ProviderCapabilities Capabilities => new();

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        lock (Tokens) if (Tokens.TryGetValue(config.AccountId, out var t) && t.Expires > DateTime.UtcNow.AddMinutes(2)) return t.Token;
        var sa = JsonNode.Parse(config.RequireSecret("serviceAccountJson"))!;
        var tokenUri = sa["token_uri"]?.ToString() ?? "https://oauth2.googleapis.com/token";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(sa["private_key"]!.ToString());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var assertion = Jwt.Create(new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = sa["private_key_id"]?.ToString() },
            new JsonObject { ["iss"] = sa["client_email"]!.ToString(), ["scope"] = "https://www.googleapis.com/auth/firebase.messaging", ["aud"] = tokenUri, ["iat"] = now, ["exp"] = now + 3600 },
            data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var resp = await http.CreateClient("providers").PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion }), ct);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!resp.IsSuccessStatusCode) throw new ProviderConfigurationException("Google OAuth rejected the service account: " + json?["error_description"]);
        var token = json!["access_token"]!.ToString();
        lock (Tokens) Tokens[config.AccountId] = (token, DateTime.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3000));
        return token;
    }

    public static JsonObject BuildPayload(NotificationMessage m, string? androidChannelId)
    {
        var app = m.Content.App;
        var data = new JsonObject { ["deliveryId"] = m.DeliveryId.ToString() };
        if (app?.DeepLink is { } link) data["deepLink"] = link;
        if (app?.Data is { } extra) foreach (var (k, v) in extra) data[k] = v?.ToString();
        var ttl = app?.TimeToLiveSeconds ?? (m.ExpiresAt is { } e ? (int)Math.Max(0, (e - DateTime.UtcNow).TotalSeconds) : 86400);
        var high = m.Priority >= NotificationPriority.High;
        return new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["token"] = m.Push!.Token,
                ["notification"] = new JsonObject { ["title"] = m.Content.Title, ["body"] = m.Content.Body, ["image"] = app?.ImageUrl },
                ["data"] = data,
                ["android"] = new JsonObject
                {
                    ["priority"] = high ? "HIGH" : "NORMAL", ["ttl"] = $"{ttl}s",
                    ["notification"] = new JsonObject { ["channel_id"] = androidChannelId, ["icon"] = app?.Icon, ["click_action"] = app?.DeepLink is null ? null : "OPEN_DEEP_LINK" },
                },
                ["apns"] = new JsonObject
                {
                    ["headers"] = new JsonObject { ["apns-priority"] = high ? "10" : "5", ["apns-expiration"] = DateTimeOffset.UtcNow.AddSeconds(ttl).ToUnixTimeSeconds().ToString() },
                    ["payload"] = new JsonObject { ["aps"] = new JsonObject { ["mutable-content"] = 1 } },
                },
                ["webpush"] = app?.DeepLink is { } wl && wl.StartsWith("https://") ? new JsonObject { ["fcm_options"] = new JsonObject { ["link"] = wl } } : null,
            },
        };
    }

    public async Task<SendResult> SendAsync(NotificationMessage m, CancellationToken ct)
        => (await HttpOutcome.Guard(async () =>
        {
            if (m.Push is null) return SendResult.Permanent("no_device", "Push target missing.");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{config.RequireSetting("projectId")}/messages:send")
            { Content = new StringContent(BuildPayload(m, config.Setting("androidChannelId")).ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
            var resp = await http.CreateClient("providers").SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return SendResult.Accepted(JsonNode.Parse(body)?["name"]?.ToString(), "FCM accepted");
            var status = JsonNode.Parse(string.IsNullOrEmpty(body) ? "{}" : body)?["error"]?["status"]?.ToString();
            return status switch
            {
                "UNREGISTERED" => SendResult.Permanent("fcm_unregistered", "Device token is no longer registered.", invalidAddress: true, "404"),
                "INVALID_ARGUMENT" when body.Contains("registration token", StringComparison.OrdinalIgnoreCase)
                    => SendResult.Permanent("fcm_invalid_token", "Invalid registration token.", invalidAddress: true, "400"),
                "SENDER_ID_MISMATCH" => SendResult.Permanent("fcm_sender_mismatch", "Token belongs to a different Firebase project.", invalidAddress: true, "403"),
                "QUOTA_EXCEEDED" => SendResult.Throttled("FCM quota exceeded.", resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1)),
                "UNAVAILABLE" or "INTERNAL" => SendResult.Transient($"fcm_{status!.ToLowerInvariant()}", HttpOutcome.Trim(body), ((int)resp.StatusCode).ToString(), resp.Headers.RetryAfter?.Delta),
                _ => HttpOutcome.FromStatus(resp, body, "fcm"),
            };
        }))!;

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        try { await AccessTokenAsync(ct); return ProviderHealth.Healthy("OAuth token obtained for the service account."); }
        catch (Exception ex) { return ProviderHealth.Unhealthy(ex.Message); }
    }
}

// ───────────────────────────── Apple Push Notification service ─────────────────────────────

public sealed class ApnsPushProviderFactory(IHttpClientFactory http) : IProviderFactory
{
    public string ProviderType => "apns";
    public IReadOnlyList<Channel> Channels => [Channel.Push];
    public string DisplayName => "Apple Push Notification service (token-based, HTTP/2)";
    public IReadOnlyList<string> SecretKeys => ["privateKeyP8"];
    public IReadOnlyList<string> SettingKeys => ["teamId", "keyId", "bundleId", "environment"];
    public INotificationProvider Create(ProviderAccountConfig config) => new ApnsPushProvider(config, http);
}

public sealed class ApnsPushProvider(ProviderAccountConfig config, IHttpClientFactory http) : INotificationProvider
{
    private static readonly Dictionary<Guid, (string Token, DateTime Issued)> Tokens = [];
    public ProviderCapabilities Capabilities => new();

    private string ProviderToken()
    {
        // APNs provider tokens are valid for up to an hour; refresh every 40 minutes.
        lock (Tokens) if (Tokens.TryGetValue(config.AccountId, out var t) && t.Issued > DateTime.UtcNow.AddMinutes(-40)) return t.Token;
        using var key = ECDsa.Create();
        key.ImportFromPem(config.RequireSecret("privateKeyP8"));
        var token = Jwt.Es256(key, new JsonObject { ["alg"] = "ES256", ["kid"] = config.RequireSetting("keyId") },
            new JsonObject { ["iss"] = config.RequireSetting("teamId"), ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        lock (Tokens) Tokens[config.AccountId] = (token, DateTime.UtcNow);
        return token;
    }

    public async Task<SendResult> SendAsync(NotificationMessage m, CancellationToken ct)
        => (await HttpOutcome.Guard(async () =>
        {
            if (m.Push is null) return SendResult.Permanent("no_device", "Push target missing.");
            var host = config.Setting("environment") == "sandbox" ? "api.sandbox.push.apple.com" : "api.push.apple.com";
            var app = m.Content.App;
            var payload = new JsonObject
            {
                ["aps"] = new JsonObject
                {
                    ["alert"] = new JsonObject { ["title"] = m.Content.Title, ["body"] = m.Content.Body },
                    ["sound"] = "default", ["mutable-content"] = app?.ImageUrl is null ? null : 1, ["category"] = app?.Category,
                },
                ["deliveryId"] = m.DeliveryId.ToString(), ["deepLink"] = app?.DeepLink, ["imageUrl"] = app?.ImageUrl,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{m.Push.Token}")
            {
                Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("bearer", ProviderToken());
            req.Headers.Add("apns-topic", config.RequireSetting("bundleId"));
            req.Headers.Add("apns-push-type", "alert");
            req.Headers.Add("apns-priority", m.Priority >= NotificationPriority.High ? "10" : "5");
            req.Headers.Add("apns-id", m.DeliveryId.ToString());
            var ttl = app?.TimeToLiveSeconds ?? 86400;
            req.Headers.Add("apns-expiration", DateTimeOffset.UtcNow.AddSeconds(ttl).ToUnixTimeSeconds().ToString());
            var resp = await http.CreateClient("providers").SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return SendResult.Accepted(resp.Headers.TryGetValues("apns-id", out var ids) ? ids.First() : m.DeliveryId.ToString(), "APNs accepted");
            var reason = JsonNode.Parse(string.IsNullOrEmpty(body) ? "{}" : body)?["reason"]?.ToString();
            return reason switch
            {
                "BadDeviceToken" or "Unregistered" or "DeviceTokenNotForTopic" => SendResult.Permanent($"apns_{reason}", reason!, invalidAddress: true, ((int)resp.StatusCode).ToString()),
                "TooManyRequests" => SendResult.Throttled(reason!, TimeSpan.FromSeconds(30)),
                "ExpiredProviderToken" or "InvalidProviderToken" => SendResult.Transient($"apns_{reason}", reason!),
                "ServiceUnavailable" or "InternalServerError" or "Shutdown" => SendResult.Transient($"apns_{reason}", reason!),
                _ => HttpOutcome.FromStatus(resp, body, "apns"),
            };
        }))!;

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        try { ProviderToken(); return Task.FromResult(ProviderHealth.Healthy("Provider token signed with the .p8 key.")); }
        catch (Exception ex) { return Task.FromResult(ProviderHealth.Unhealthy(ex.Message)); }
    }
}

// ───────────────────────────── Web Push (RFC 8030 / 8291 / 8292) ─────────────────────────────

public sealed class WebPushProviderFactory(IHttpClientFactory http) : IProviderFactory
{
    public string ProviderType => "webpush";
    public IReadOnlyList<Channel> Channels => [Channel.WebPush];
    public string DisplayName => "Standards Web Push (VAPID)";
    public IReadOnlyList<string> SecretKeys => ["vapidPrivateKey"];
    public IReadOnlyList<string> SettingKeys => ["vapidPublicKey", "subject"];
    public INotificationProvider Create(ProviderAccountConfig config) => new WebPushProvider(config, http);
}

/// <summary>Encrypts payloads with aes128gcm (RFC 8291) and authenticates with VAPID (RFC 8292).</summary>
public sealed class WebPushProvider(ProviderAccountConfig config, IHttpClientFactory http) : INotificationProvider
{
    public ProviderCapabilities Capabilities => new();

    public async Task<SendResult> SendAsync(NotificationMessage m, CancellationToken ct)
        => (await HttpOutcome.Guard(async () =>
        {
            if (m.Push is not { P256dh: { } p256, Auth: { } auth } target) return SendResult.Permanent("no_subscription", "Web Push subscription keys missing.", invalidAddress: true);
            var payload = new JsonObject
            {
                ["title"] = m.Content.Title, ["body"] = m.Content.Body, ["icon"] = m.Content.App?.Icon, ["image"] = m.Content.App?.ImageUrl,
                ["url"] = m.Content.App?.DeepLink, ["deliveryId"] = m.DeliveryId.ToString(),
            };
            var body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(payload.ToJsonString()), Hashing.FromBase64Url(p256), Hashing.FromBase64Url(auth));
            var endpoint = new Uri(target.Token);
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Content.Headers.ContentEncoding.Add("aes128gcm");
            req.Headers.Add("TTL", (m.Content.App?.TimeToLiveSeconds ?? 86400).ToString());
            req.Headers.Add("Urgency", m.Priority >= NotificationPriority.High ? "high" : "normal");
            req.Headers.TryAddWithoutValidation("Authorization", WebPushCrypto.VapidHeader(endpoint, config.RequireSetting("vapidPublicKey"), config.RequireSecret("vapidPrivateKey"), config.Setting("subject") ?? "mailto:notifications@wings.local"));
            var resp = await http.CreateClient("providers").SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return SendResult.Accepted(resp.Headers.Location?.ToString() ?? m.DeliveryId.ToString(), $"push service accepted ({(int)resp.StatusCode})");
            return (int)resp.StatusCode switch
            {
                404 or 410 => SendResult.Permanent("webpush_gone", "Subscription expired or unsubscribed.", invalidAddress: true, ((int)resp.StatusCode).ToString()),
                413 => SendResult.Permanent("webpush_too_large", "Payload too large."),
                _ => HttpOutcome.FromStatus(resp, text, "webpush"),
            };
        }))!;

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var key = WebPushCrypto.ImportVapidKey(config.RequireSetting("vapidPublicKey"), config.RequireSecret("vapidPrivateKey"));
            return Task.FromResult(ProviderHealth.Healthy("VAPID key pair is valid."));
        }
        catch (Exception ex) { return Task.FromResult(ProviderHealth.Unhealthy(ex.Message)); }
    }
}

public static class WebPushCrypto
{
    private const int RecordSize = 4096;

    /// <summary>aes128gcm content coding (RFC 8188) with Web Push key derivation (RFC 8291).</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman? ephemeral = null, byte[]? salt = null)
    {
        using var asKey = ephemeral ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var asPublic = PublicKeyBytes(asKey.ExportParameters(false));
        using var ua = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var ecdhSecret = asKey.DeriveRawSecretAgreement(ua.PublicKey);
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = Hkdf(authSecret, ecdhSecret, keyInfo, 32);
        salt ??= RandomNumberGenerator.GetBytes(16);
        var cek = Hkdf(salt, ikm, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"), 16);
        var nonce = Hkdf(salt, ikm, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"), 12);
        var padded = Concat(plaintext, [0x02]);
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, padded, cipher, tag);
        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return Concat(header, cipher, tag);
    }

    /// <summary>Receiver-side decryption (used by tests to prove interoperability of <see cref="Encrypt"/>).</summary>
    public static byte[] Decrypt(byte[] body, ECDiffieHellman uaKey, byte[] authSecret)
    {
        var salt = body[..16];
        int idLen = body[20];
        var asPublic = body[21..(21 + idLen)];
        var cipher = body[(21 + idLen)..^16];
        var tag = body[^16..];
        var uaPublic = PublicKeyBytes(uaKey.ExportParameters(false));
        using var asKey = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] } });
        var ecdhSecret = uaKey.DeriveRawSecretAgreement(asKey.PublicKey);
        var ikm = Hkdf(authSecret, ecdhSecret, Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic), 32);
        var cek = Hkdf(salt, ikm, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"), 16);
        var nonce = Hkdf(salt, ikm, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"), 12);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, cipher, tag, plain);
        var end = Array.LastIndexOf(plain, (byte)0x02);
        return plain[..end];
    }

    public static string VapidHeader(Uri endpoint, string publicKeyB64Url, string privateKeyB64Url, string subject)
    {
        using var key = ImportVapidKey(publicKeyB64Url, privateKeyB64Url);
        var jwt = Jwt.Es256(key, new JsonObject { ["typ"] = "JWT", ["alg"] = "ES256" },
            new JsonObject { ["aud"] = endpoint.GetLeftPart(UriPartial.Authority), ["exp"] = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(), ["sub"] = subject });
        return $"vapid t={jwt}, k={publicKeyB64Url}";
    }

    public static ECDsa ImportVapidKey(string publicKeyB64Url, string privateKeyB64Url)
    {
        var pub = Hashing.FromBase64Url(publicKeyB64Url);
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, D = Hashing.FromBase64Url(privateKeyB64Url),
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        });
    }

    /// <summary>Generates a VAPID key pair (base64url public key in uncompressed point form, private scalar).</summary>
    public static (string PublicKey, string PrivateKey) GenerateVapidKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        return (Hashing.Base64Url(PublicKeyBytes(p)), Hashing.Base64Url(p.D!));
    }

    public static byte[] PublicKeyBytes(ECParameters p) => Concat([0x04], p.Q.X!, p.Q.Y!);

    private static byte[] Hkdf(byte[] salt, byte[] ikm, byte[] info, int length) => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, info);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var o = 0;
        foreach (var p in parts) { p.CopyTo(result, o); o += p.Length; }
        return result;
    }
}
