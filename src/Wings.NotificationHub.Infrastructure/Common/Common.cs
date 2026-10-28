using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wings.NotificationHub.Infrastructure.Common;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string? json) => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}

public sealed class HubOptions
{
    /// <summary>Public base URL of the API (unsubscribe links, callback URLs, tracking URLs).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5480";
    public string DataDirectory { get; set; } = "App_Data";
    /// <summary>"database" (default, no extra infrastructure) or "rabbitmq".</summary>
    public string MessageTransport { get; set; } = "database";
    public int MaxRecipientsPerRequest { get; set; } = 500;
    public int MaxBatchSize { get; set; } = 1000;
    public int MaxVariablesBytes { get; set; } = 64 * 1024;
    public int MaxAttachmentBytes { get; set; } = 10 * 1024 * 1024;
    /// <summary>Seed demo tenants, providers, templates and credentials on first start.</summary>
    public bool SeedDemoData { get; set; }
    /// <summary>When a provider callback has not arrived this long after acceptance, reconciliation queries the provider.</summary>
    public int ReconcileAfterMinutes { get; set; } = 15;
    /// <summary>Processing leases older than this are considered abandoned (worker crash) and recovered.</summary>
    public int LeaseSeconds { get; set; } = 120;
    public int InboxRetentionDays { get; set; } = 14;
}

public sealed class WorkerOptions
{
    public int DispatchConcurrency { get; set; } = 8;
    public int PollIntervalMs { get; set; } = 500;
    public int BatchSize { get; set; } = 50;
    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";
}

/// <summary>Symmetric encryption for secrets and message bodies at rest.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext, string purpose);
    string Unprotect(string ciphertext, string purpose);
}

/// <summary>
/// AES-256-GCM with a versioned key ring. Ciphertext: <c>v1.{keyId}.{base64(nonce|tag|cipher)}</c>; the purpose string
/// is bound as associated data so a secret encrypted for one use cannot be replayed into another.
/// Keys come from configuration (<c>Security:EncryptionKeys</c>) — use a secret store / environment variables in production.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _activeKeyId;

    public AesGcmSecretProtector(IReadOnlyDictionary<string, byte[]> keys, string activeKeyId)
    {
        if (!keys.TryGetValue(activeKeyId, out var active) || active.Length != 32)
            throw new InvalidOperationException("The active encryption key must be 32 bytes (base64-encoded 256-bit key).");
        _keys = keys.ToDictionary(k => k.Key, k => k.Value);
        _activeKeyId = activeKeyId;
    }

    public string Protect(string plaintext, string purpose)
    {
        var key = _keys[_activeKeyId];
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        var blob = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, 12);
        cipher.CopyTo(blob, 28);
        return $"v1.{_activeKeyId}.{Convert.ToBase64String(blob)}";
    }

    public string Unprotect(string ciphertext, string purpose)
    {
        var parts = ciphertext.Split('.', 3);
        if (parts.Length != 3 || parts[0] != "v1" || !_keys.TryGetValue(parts[1], out var key))
            throw new CryptographicException("Unknown ciphertext format or key id.");
        var blob = Convert.FromBase64String(parts[2]);
        var plain = new byte[blob.Length - 28];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(purpose));
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Builds the protector from configuration; in Development a key file is generated under the data directory.</summary>
    public static AesGcmSecretProtector FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration config, string dataDirectory, bool isDevelopment, ILogger? logger = null)
    {
        var section = config.GetSection("Security:EncryptionKeys");
        var keys = new Dictionary<string, byte[]>();
        foreach (var child in section.GetChildren())
            if (!string.IsNullOrWhiteSpace(child.Value)) keys[child.Key] = Convert.FromBase64String(child.Value);
        var active = config["Security:ActiveKeyId"];
        if (keys.Count > 0) return new AesGcmSecretProtector(keys, active ?? keys.Keys.Last());

        if (!isDevelopment)
            throw new InvalidOperationException("Security:EncryptionKeys is not configured. Provide a 256-bit key (base64) via a secret store or environment variable Security__EncryptionKeys__k1.");

        var file = Path.Combine(Path.GetFullPath(dataDirectory), "dev-encryption-key.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file))
        {
            File.WriteAllText(file, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            logger?.LogWarning("Generated a development encryption key at {File}. Configure Security:EncryptionKeys for real deployments.", file);
        }
        return new AesGcmSecretProtector(new Dictionary<string, byte[]> { ["dev"] = Convert.FromBase64String(File.ReadAllText(file).Trim()) }, "dev");
    }
}

public static class Purposes
{
    public const string ProviderSecrets = "provider-secrets";
    public const string CallbackSecret = "provider-callback-secret";
    public const string WebhookSecret = "webhook-secret";
    public const string MessageContent = "message-content";
    public const string Variables = "request-variables";
    public const string Approval = "approval-payload";
    public const string Unsubscribe = "unsubscribe-token";
}

/// <summary>OpenTelemetry instrumentation sources (traces + metrics).</summary>
public static class Telemetry
{
    public const string Name = "Wings.NotificationHub";
    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Accepted = Meter.CreateCounter<long>("wnh.notifications.accepted", description: "Notification requests accepted");
    public static readonly Counter<long> DeliveryOutcomes = Meter.CreateCounter<long>("wnh.deliveries.outcomes", description: "Provider submission outcomes by channel/provider/outcome");
    public static readonly Histogram<double> ProviderLatency = Meter.CreateHistogram<double>("wnh.provider.latency", "ms", "Provider submission latency");
    public static readonly Counter<long> Callbacks = Meter.CreateCounter<long>("wnh.callbacks", description: "Provider callbacks received");
}

/// <summary>Redaction helpers for logs and sanitized provider responses.</summary>
public static class Redact
{
    private static readonly System.Text.RegularExpressions.Regex Secretish = new(
        "(\"?(?:access_token|token|secret|password|api[_-]?key|authorization|auth|client_secret|private_key)\"?\\s*[:=]\\s*\"?)([^\",\\s}]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex Otp = new(@"\b\d{4,8}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Strips secrets and truncates; safe to persist as a provider response summary.</summary>
    public static string? Sanitize(string? text, int max = 1000)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = Secretish.Replace(text, "$1***");
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>Masks digit runs that could be one-time codes (used on sensitive message previews).</summary>
    public static string MaskCodes(string text) => Otp.Replace(text, m => new string('•', m.Length));
}

public static class Periods
{
    public static int Of(DateTime utc) => utc.Year * 100 + utc.Month;
    public static DateTime StartOf(DateTime utc) => new(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}
