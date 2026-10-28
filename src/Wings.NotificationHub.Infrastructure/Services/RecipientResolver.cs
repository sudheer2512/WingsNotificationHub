using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Policies;
using Wings.NotificationHub.Core.Rules;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.Infrastructure.Services;

public sealed class ResolvedRecipient
{
    public RecipientContact? Contact { get; set; }
    public string? UserId { get; set; }
    public string? CustomerId { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Mobile { get; set; }
    public string? WhatsApp { get; set; }
    public string? TimeZone { get; set; }
    public string? Locale { get; set; }
    public List<Guid>? DeviceIds { get; set; }
    public List<string>? Cc { get; set; }
    public List<string>? Bcc { get; set; }
    /// <summary>Set when the recipient referenced only addresses (no known contact).</summary>
    public bool AdHoc => Contact is null;
    public string Key => Contact?.Id.ToString() ?? $"{UserId}|{CustomerId}|{Email}|{Mobile}";
}

/// <summary>Resolves request recipients and audiences to contacts and addresses within one company.</summary>
public sealed class RecipientResolver(NotificationHubDbContext db)
{
    public const int MaxAudience = 5000;

    public async Task<List<ResolvedRecipient>> ResolveAsync(Guid companyId, IEnumerable<RecipientDto> recipients, AudienceDto? audience, string? companyCountry, CancellationToken ct)
    {
        var result = new List<ResolvedRecipient>();
        foreach (var r in recipients)
        {
            RecipientContact? contact = null;
            if (r.ContactId is { } cid)
                contact = await db.RecipientContacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.CompanyId == companyId && c.ErasedAt == null, ct)
                          ?? throw HubException.Validation($"Contact {cid} was not found in this company.");
            // A customer id identifies the person being messaged (consent, preferences, addresses); a user id alone
            // identifies an application user. When both are given, the user id is still used for in-app/push targeting.
            else if (!string.IsNullOrWhiteSpace(r.CustomerId))
                contact = await db.RecipientContacts.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Kind == ContactKind.Customer && c.ExternalId == r.CustomerId && c.ErasedAt == null, ct);
            if (contact is null && r.ContactId is null && !string.IsNullOrWhiteSpace(r.UserId))
                contact = await db.RecipientContacts.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Kind == ContactKind.User && c.ExternalId == r.UserId && c.ErasedAt == null, ct);
            result.Add(Merge(r, contact, companyCountry));
        }

        if (audience is not null)
        {
            var members = await ExpandAudienceAsync(companyId, audience, ct);
            foreach (var c in members)
                if (result.All(x => x.Contact?.Id != c.Id)) result.Add(Merge(new RecipientDto(), c, companyCountry));
        }
        return result;
    }

    /// <summary>Explicit request values win over the stored contact (callers may target a specific address).</summary>
    private static ResolvedRecipient Merge(RecipientDto r, RecipientContact? c, string? country) => new()
    {
        Contact = c,
        UserId = r.UserId ?? (c?.Kind == ContactKind.User ? c.ExternalId : null),
        CustomerId = r.CustomerId ?? (c?.Kind == ContactKind.Customer ? c.ExternalId : null),
        Name = r.Name ?? c?.Name,
        Email = (r.Email ?? c?.Email)?.Trim(),
        Mobile = Addressing.NormalizePhone(r.Mobile ?? c?.Mobile, c?.Country ?? country),
        // Explicit request addresses always win over stored ones.
        WhatsApp = Addressing.NormalizePhone(r.WhatsApp ?? r.Mobile ?? c?.WhatsApp ?? c?.Mobile, c?.Country ?? country),
        TimeZone = r.TimeZone ?? c?.TimeZone,
        Locale = r.Locale ?? c?.Locale,
        DeviceIds = r.DeviceIds,
        Cc = r.Cc,
        Bcc = r.Bcc,
    };

    public async Task<List<RecipientContact>> ExpandAudienceAsync(Guid companyId, AudienceDto audience, CancellationToken ct)
    {
        var users = db.RecipientContacts.AsNoTracking().Where(c => c.CompanyId == companyId && c.ErasedAt == null);
        var found = new Dictionary<Guid, RecipientContact>();
        if (audience.AllCompanyUsers)
            foreach (var c in await users.Where(c => c.Kind == ContactKind.User).Take(MaxAudience + 1).ToListAsync(ct)) found[c.Id] = c;
        if (audience.UserIds is { Count: > 0 } ids)
            foreach (var c in await users.Where(c => c.Kind == ContactKind.User && ids.Contains(c.ExternalId)).ToListAsync(ct)) found[c.Id] = c;
        if (audience.Roles is { Count: > 0 } roles)
        {
            // Roles are stored as a JSON list; filter in memory over the company's users.
            var all = await users.Where(c => c.Kind == ContactKind.User).Take(MaxAudience * 4).ToListAsync(ct);
            foreach (var c in all.Where(c => c.Roles.Any(r => roles.Contains(r, StringComparer.OrdinalIgnoreCase)))) found[c.Id] = c;
        }
        foreach (var gid in audience.GroupIds ?? [])
            foreach (var c in await GroupMembersAsync(companyId, gid, ct)) found[c.Id] = c;
        if (found.Count > MaxAudience)
            throw HubException.Validation($"Audience resolves to more than {MaxAudience} recipients; use a campaign for bulk messaging.");
        return found.Values.ToList();
    }

    public async Task<List<RecipientContact>> GroupMembersAsync(Guid companyId, Guid groupId, CancellationToken ct)
    {
        var group = await db.RecipientGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId, ct)
                    ?? throw HubException.Validation($"Recipient group {groupId} was not found in this company.");
        if (!group.Dynamic)
        {
            var ids = db.RecipientGroupMembers.Where(m => m.GroupId == groupId).Select(m => m.ContactId);
            return await db.RecipientContacts.AsNoTracking().Where(c => ids.Contains(c.Id) && c.ErasedAt == null).ToListAsync(ct);
        }
        var filter = Json.Deserialize<ConditionNode>(group.FilterJson);
        var candidates = await db.RecipientContacts.AsNoTracking().Where(c => c.CompanyId == companyId && c.ErasedAt == null).Take(100_000).ToListAsync(ct);
        return candidates.Where(c => ConditionEvaluator.Evaluate(filter, ContactDocument(c))).ToList();
    }

    /// <summary>JSON view of a contact used by dynamic groups and campaign segments.</summary>
    public static JsonObject ContactDocument(RecipientContact c)
    {
        var o = new JsonObject
        {
            ["kind"] = c.Kind.ToString(), ["externalId"] = c.ExternalId, ["name"] = c.Name, ["email"] = c.Email, ["mobile"] = c.Mobile,
            ["whatsapp"] = c.WhatsApp, ["country"] = c.Country, ["locale"] = c.Locale, ["timeZone"] = c.TimeZone,
            ["roles"] = new JsonArray(c.Roles.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        };
        if (Json.Deserialize<JsonObject>(c.AttributesJson) is { } attrs) o["attributes"] = attrs;
        return o;
    }

    /// <summary>Loads preferences, latest consents and suppressions for a set of recipients in three queries.</summary>
    public async Task<Dictionary<string, RecipientPolicyState>> LoadPolicyStateAsync(Guid tenantId, Guid companyId, IReadOnlyList<ResolvedRecipient> recipients, CancellationToken ct)
    {
        var contactIds = recipients.Where(r => r.Contact is not null).Select(r => r.Contact!.Id).Distinct().ToList();
        var prefs = contactIds.Count == 0 ? [] : await db.RecipientPreferences.AsNoTracking().Where(p => contactIds.Contains(p.ContactId)).ToListAsync(ct);
        var consents = contactIds.Count == 0 ? [] : await db.RecipientConsents.AsNoTracking().Where(c => contactIds.Contains(c.ContactId)).ToListAsync(ct);
        var addresses = recipients.SelectMany(r => new[] { r.Email?.ToLowerInvariant(), r.Mobile, r.WhatsApp }).Where(a => a is not null).Distinct().ToList();
        var suppressions = addresses.Count == 0 ? [] : await db.Suppressions.AsNoTracking()
            .Where(s => s.TenantId == tenantId && (s.CompanyId == null || s.CompanyId == companyId) && addresses.Contains(s.Address)).ToListAsync(ct);

        var map = new Dictionary<string, RecipientPolicyState>();
        foreach (var r in recipients)
        {
            var cid = r.Contact?.Id;
            var mine = new HashSet<string?>([r.Email?.ToLowerInvariant(), r.Mobile, r.WhatsApp]);
            map[r.Key] = new RecipientPolicyState
            {
                Preferences = cid is null ? [] : prefs.Where(p => p.ContactId == cid).ToList(),
                Consents = cid is null ? [] : consents.Where(c => c.ContactId == cid)
                    .GroupBy(c => (c.Channel, c.Purpose)).Select(g => g.OrderByDescending(x => x.CapturedAt).ThenByDescending(x => x.RecordedAt).First()).ToList(),
                Suppressions = suppressions.Where(s => mine.Contains(s.Address)).ToList(),
            };
        }
        return map;
    }
}
