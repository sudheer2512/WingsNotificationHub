using System.Security.Cryptography;
using System.Text;
using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.Core.Security;

public static class Roles
{
    public const string PlatformAdmin = "PlatformAdministrator";
    public const string TenantAdmin = "TenantAdministrator";
    public const string CompanyAdmin = "CompanyAdministrator";
    public const string ApplicationAdmin = "ApplicationAdministrator";
    public const string TemplateDesigner = "TemplateDesigner";
    public const string TemplateApprover = "TemplateApprover";
    public const string CampaignManager = "CampaignManager";
    public const string Operator = "NotificationOperator";
    public const string Auditor = "Auditor";
    /// <summary>Synthetic role for application credentials; capabilities come from token scopes.</summary>
    public const string ApiClient = "ApiClient";

    public static readonly string[] All = [PlatformAdmin, TenantAdmin, CompanyAdmin, ApplicationAdmin, TemplateDesigner, TemplateApprover, CampaignManager, Operator, Auditor];
}

public enum Permission
{
    // read
    ViewDashboard,
    ViewNotifications,
    ViewMessageContent,
    ViewTemplates,
    ViewProviders,
    ViewRules,
    ViewCampaigns,
    ViewInbox,
    ViewUsage,
    ViewMonitoring,
    ViewAudit,
    ViewContacts,
    ViewTenancy,
    // write
    SendNotifications,
    RetryDeliveries,
    CancelNotifications,
    ManageTemplates,
    ApproveTemplates,
    ManageProviders,
    ApproveProviderChanges,
    ManageRules,
    ManageCampaigns,
    ApproveCampaigns,
    ManageContacts,
    ManageInbox,
    ManageTenancy,
    ManageCredentials,
    ManageQuotas,
    ManageWebhooks,
    EmergencySuspend,
    ManageUsers,
}

public static class RolePermissions
{
    private static readonly Permission[] ReadAll =
    [
        Permission.ViewDashboard, Permission.ViewNotifications, Permission.ViewTemplates, Permission.ViewProviders, Permission.ViewRules,
        Permission.ViewCampaigns, Permission.ViewInbox, Permission.ViewUsage, Permission.ViewMonitoring, Permission.ViewContacts, Permission.ViewTenancy,
    ];

    private static readonly Dictionary<string, HashSet<Permission>> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [Roles.PlatformAdmin] = [.. Enum.GetValues<Permission>()],
        [Roles.TenantAdmin] = [.. Enum.GetValues<Permission>()],
        [Roles.CompanyAdmin] =
        [
            .. ReadAll, Permission.ViewMessageContent, Permission.ViewAudit, Permission.SendNotifications, Permission.RetryDeliveries,
            Permission.CancelNotifications, Permission.ManageTemplates, Permission.ApproveTemplates, Permission.ManageProviders,
            Permission.ApproveProviderChanges, Permission.ManageRules, Permission.ManageCampaigns, Permission.ApproveCampaigns,
            Permission.ManageContacts, Permission.ManageInbox, Permission.ManageWebhooks, Permission.EmergencySuspend,
        ],
        [Roles.ApplicationAdmin] =
        [
            .. ReadAll, Permission.ViewMessageContent, Permission.SendNotifications, Permission.RetryDeliveries, Permission.CancelNotifications,
            Permission.ManageTemplates, Permission.ManageRules, Permission.ManageContacts, Permission.ManageWebhooks, Permission.ManageCredentials,
        ],
        [Roles.TemplateDesigner] = [Permission.ViewDashboard, Permission.ViewTemplates, Permission.ManageTemplates, Permission.ViewTenancy, Permission.ViewRules],
        [Roles.TemplateApprover] = [Permission.ViewDashboard, Permission.ViewTemplates, Permission.ApproveTemplates, Permission.ViewTenancy],
        [Roles.CampaignManager] =
        [
            Permission.ViewDashboard, Permission.ViewCampaigns, Permission.ManageCampaigns, Permission.ViewTemplates, Permission.ViewContacts,
            Permission.ManageContacts, Permission.ViewUsage, Permission.ViewTenancy, Permission.ViewNotifications,
        ],
        [Roles.Operator] =
        [
            Permission.ViewDashboard, Permission.ViewNotifications, Permission.ViewMessageContent, Permission.RetryDeliveries, Permission.CancelNotifications,
            Permission.ViewProviders, Permission.ViewMonitoring, Permission.ViewInbox, Permission.ManageInbox, Permission.ViewTenancy, Permission.ViewCampaigns,
        ],
        // Auditors see metadata, audit trails and reports, but not message bodies or unmasked addresses.
        [Roles.Auditor] = [.. ReadAll, Permission.ViewAudit],
    };

    public static bool Has(string role, Permission p) => Map.TryGetValue(role, out var set) && set.Contains(p);

    /// <summary>Permissions where a narrower grant (company/app) also sees broader-scoped resources (tenant-wide templates).</summary>
    public static bool IsRead(Permission p) => p.ToString().StartsWith("View", StringComparison.Ordinal);

    /// <summary>Maps API scopes to the permissions an application credential holds.</summary>
    public static IEnumerable<Permission> ForScopes(IEnumerable<string> scopes)
    {
        foreach (var s in scopes)
        {
            switch (s)
            {
                case Scopes.NotificationsSend: case Scopes.NotificationsBatch: yield return Permission.SendNotifications; yield return Permission.CancelNotifications; break;
                case Scopes.NotificationsRead: yield return Permission.ViewNotifications; break;
                case Scopes.TemplatesRead: yield return Permission.ViewTemplates; break;
                case Scopes.TemplatesWrite: yield return Permission.ViewTemplates; yield return Permission.ManageTemplates; break;
                case Scopes.ContactsWrite: case Scopes.DevicesWrite: case Scopes.PreferencesWrite: yield return Permission.ManageContacts; yield return Permission.ViewContacts; break;
                case Scopes.PreferencesRead: yield return Permission.ViewContacts; break;
                case Scopes.UsageRead: yield return Permission.ViewUsage; break;
                case Scopes.ProvidersHealth: yield return Permission.ViewProviders; break;
                case Scopes.WebhooksManage: yield return Permission.ManageWebhooks; break;
            }
        }
    }
}

/// <summary>Password hashing (PBKDF2-SHA256, 210k iterations) and client-secret hashing.</summary>
public static class Hashing
{
    private const int Iterations = 210_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iter)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Client secrets are high-entropy random values, so a single SHA-256 is sufficient (and fast for token issuance).</summary>
    public static string HashSecret(string secret) => "sha256$" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool VerifySecret(string secret, string stored)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(HashSecret(secret)), Encoding.UTF8.GetBytes(stored));

    public static string NewSecret(string prefix, int bytes = 32) => prefix + Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        var p = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(p.PadRight(p.Length + (4 - p.Length % 4) % 4, '='));
    }
}

/// <summary>
/// Signs and verifies webhook payloads: header <c>X-Wings-Signature: t=&lt;unix&gt;,v1=&lt;hex hmac-sha256(secret, "t.body")&gt;</c>.
/// Receivers must reject signatures older than the tolerance (replay protection).
/// </summary>
public static class WebhookSignature
{
    public const string Header = "X-Wings-Signature";
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, string body, DateTimeOffset timestamp)
    {
        var t = timestamp.ToUnixTimeSeconds();
        return $"t={t},v1={Compute(secret, t, body)}";
    }

    public static bool Verify(string secret, string body, string? header, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrEmpty(header)) return false;
        long? t = null;
        var sigs = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t" && long.TryParse(kv[1], out var ts)) t = ts;
            else if (kv[0].Trim() == "v1") sigs.Add(kv[1].Trim());
        }
        if (t is null || sigs.Count == 0) return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - t.Value) > (tolerance ?? DefaultTolerance).TotalSeconds) return false;
        var expected = Encoding.ASCII.GetBytes(Compute(secret, t.Value, body));
        return sigs.Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), expected));
    }

    private static string Compute(string secret, long t, string body)
        => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")));
}
