using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Core.Templates;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Providers;

namespace Wings.NotificationHub.Infrastructure.Services;

public sealed class TemplateUpsertRequest
{
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Category { get; set; }
    public Channel Channel { get; set; }
    public string Locale { get; set; } = "en";
}

public sealed class TemplateVersionRequest
{
    public string? Subject { get; set; }
    public string Body { get; set; } = "";
    public BodyFormat BodyFormat { get; set; }
    public string? TextBody { get; set; }
    public TemplateComponents? Components { get; set; }
    public string? ProviderTemplateName { get; set; }
    public string? ProviderTemplateLanguage { get; set; }
    public string? ProviderCategory { get; set; }
    public string? DltTemplateId { get; set; }
    public List<TemplateVariableDto>? Variables { get; set; }
    public string? ChangeNote { get; set; }
}

/// <summary>
/// Template repository with versioning and a Draft → InReview → Approved → Active → Retired workflow.
/// Submitted versions are immutable; edits create a new draft version. Approval is maker-checker.
/// </summary>
public sealed class TemplateService(NotificationHubDbContext db, AuditService audit, ProviderRegistry registry)
{
    public async Task<NotificationTemplate> CreateAsync(CallerContext caller, TemplateUpsertRequest req, TemplateVersionRequest version, CancellationToken ct)
    {
        await AuthorizeScopeAsync(caller, req.TenantId, req.CompanyId, req.ApplicationId, Permission.ManageTemplates, ct);
        var code = req.Code.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9_.-]{2,100}$")) throw HubException.Validation("Template code must be 2-100 characters: A-Z, 0-9, _ . -");
        if (string.IsNullOrWhiteSpace(req.Name)) throw HubException.Validation("Template name is required.");
        var locale = string.IsNullOrWhiteSpace(req.Locale) ? "en" : req.Locale.Trim();
        if (await db.NotificationTemplates.AnyAsync(t => t.TenantId == req.TenantId && t.CompanyId == req.CompanyId && t.ApplicationId == req.ApplicationId
                && t.Code == code && t.Channel == req.Channel && t.Locale == locale, ct))
            throw HubException.Conflict("template_exists", "A template with this code, channel and locale already exists at this scope.");
        var t = new NotificationTemplate
        {
            TenantId = req.TenantId, CompanyId = req.CompanyId, ApplicationId = req.ApplicationId, Code = code, Name = req.Name.Trim(),
            Description = req.Description, Category = req.Category, Channel = req.Channel, Locale = locale, Status = TemplateStatus.Draft, CreatedBy = caller.ActorId,
        };
        db.NotificationTemplates.Add(t);
        AddVersion(caller, t, version);
        audit.Record(caller, "template.created", nameof(NotificationTemplate), t.Id.ToString(), $"{t.Code} ({ChannelNames.ToWire(t.Channel)}/{t.Locale})", t.TenantId, t.CompanyId, t.ApplicationId);
        await db.SaveChangesAsync(ct);
        return t;
    }

    /// <summary>Edits the latest draft in place, or creates a new draft version when the latest is already submitted.</summary>
    public async Task<TemplateVersion> SaveDraftAsync(CallerContext caller, Guid templateId, TemplateVersionRequest version, CancellationToken ct)
    {
        var t = await LoadAsync(caller, templateId, Permission.ManageTemplates, ct);
        var latest = t.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
        TemplateVersion v;
        if (latest is { Status: TemplateStatus.Draft or TemplateStatus.Rejected })
        {
            Apply(latest, version, t.Channel);
            latest.Status = TemplateStatus.Draft;
            latest.RejectionReason = null;
            ReplaceVariables(latest, version);
            v = latest;
        }
        else v = AddVersion(caller, t, version);
        t.UpdatedAt = DateTime.UtcNow;
        audit.Record(caller, "template.draft_saved", nameof(NotificationTemplate), t.Id.ToString(), $"{t.Code} v{v.VersionNumber}", t.TenantId, t.CompanyId, t.ApplicationId);
        await db.SaveChangesAsync(ct);
        return v;
    }

    public async Task<TemplateVersion> SubmitAsync(CallerContext caller, Guid templateId, int versionNumber, CancellationToken ct)
    {
        var t = await LoadAsync(caller, templateId, Permission.ManageTemplates, ct);
        var v = Version(t, versionNumber);
        if (v.Status != TemplateStatus.Draft) throw HubException.Conflict("invalid_state", "Only drafts can be submitted for review.");
        Validate(t.Channel, v);
        v.Status = TemplateStatus.InReview;
        v.SubmittedBy = caller.ActorId;
        v.SubmittedAt = DateTime.UtcNow;
        v.Checksum = Checksum(v);
        if (t.ActiveVersionId is null) t.Status = TemplateStatus.InReview;
        db.ApprovalRequests.Add(new ApprovalRequest
        {
            TenantId = t.TenantId, Kind = ApprovalKind.TemplateVersion, EntityType = nameof(TemplateVersion), EntityId = v.Id,
            Title = $"Template {t.Code} ({ChannelNames.ToWire(t.Channel)}/{t.Locale}) v{v.VersionNumber}", RequestedBy = caller.ActorId, RequestedByName = caller.ActorName,
        });
        audit.Record(caller, "template.submitted", nameof(NotificationTemplate), t.Id.ToString(), $"{t.Code} v{v.VersionNumber}", t.TenantId, t.CompanyId, t.ApplicationId);
        await db.SaveChangesAsync(ct);
        return v;
    }

    /// <summary>Maker-checker: a version cannot be approved by its author or submitter.</summary>
    public async Task<TemplateVersion> ReviewAsync(CallerContext caller, Guid templateId, int versionNumber, bool approve, string? comment, bool activate, CancellationToken ct)
    {
        var t = await LoadAsync(caller, templateId, Permission.ApproveTemplates, ct);
        var v = Version(t, versionNumber);
        if (v.Status != TemplateStatus.InReview) throw HubException.Conflict("invalid_state", "Only versions in review can be approved or rejected.");
        if (caller.Kind != CallerKind.System && (v.CreatedBy == caller.ActorId || v.SubmittedBy == caller.ActorId))
            throw new HubException(403, "maker_checker", "A template version must be approved by someone other than its author.");
        if (v.Checksum != Checksum(v)) throw HubException.Conflict("tampered", "The version content changed after submission.");
        var approval = await db.ApprovalRequests.FirstOrDefaultAsync(a => a.EntityId == v.Id && a.Status == ApprovalStatus.Pending, ct);
        if (approval is not null) { approval.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected; approval.DecidedBy = caller.ActorId; approval.DecidedAt = DateTime.UtcNow; approval.Comment = comment; }
        if (!approve)
        {
            v.Status = TemplateStatus.Rejected;
            v.RejectionReason = comment;
            if (t.ActiveVersionId is null) t.Status = TemplateStatus.Rejected;
            audit.Record(caller, "template.rejected", nameof(NotificationTemplate), t.Id.ToString(), $"{t.Code} v{v.VersionNumber}: {comment}", t.TenantId, t.CompanyId, t.ApplicationId);
            await db.SaveChangesAsync(ct);
            return v;
        }
        v.Status = TemplateStatus.Approved;
        v.ApprovedBy = caller.ActorId;
        v.ApprovedAt = DateTime.UtcNow;
        t.ApprovedBy = caller.ActorId;
        audit.Record(caller, "template.approved", nameof(NotificationTemplate), t.Id.ToString(), $"{t.Code} v{v.VersionNumber}", t.TenantId, t.CompanyId, t.ApplicationId);
        if (activate) ActivateInternal(caller, t, v);
        else if (t.ActiveVersionId is null) t.Status = TemplateStatus.Approved;
        await db.SaveChangesAsync(ct);
        return v;
    }

    public async Task ActivateAsync(CallerContext caller, Guid templateId, int versionNumber, CancellationToken ct)
    {
        var t = await LoadAsync(caller, templateId, Permission.ApproveTemplates, ct);
        var v = Version(t, versionNumber);
        if (v.Status is not (TemplateStatus.Approved or TemplateStatus.Retired)) throw HubException.Conflict("invalid_state", "Only approved versions can be activated.");
        ActivateInternal(caller, t, v);
        await db.SaveChangesAsync(ct);
    }

    private void ActivateInternal(CallerContext caller, NotificationTemplate t, TemplateVersion v)
    {
        foreach (var other in t.Versions.Where(x => x.Status == TemplateStatus.Active && x.Id != v.Id)) other.Status = TemplateStatus.Retired;
        v.Status = TemplateStatus.Active;
        t.ActiveVersionId = v.Id;
        t.ActiveVersionNumber = v.VersionNumber;
        t.Status = TemplateStatus.Active;
        t.UpdatedAt = DateTime.UtcNow;
        audit.Record(caller, "template.activated", nameof(NotificationTemplate), t.Id.ToString(), $"{t.Code} v{v.VersionNumber}", t.TenantId, t.CompanyId, t.ApplicationId);
    }

    public async Task RetireAsync(CallerContext caller, Guid templateId, CancellationToken ct)
    {
        var t = await LoadAsync(caller, templateId, Permission.ApproveTemplates, ct);
        foreach (var v in t.Versions.Where(x => x.Status == TemplateStatus.Active)) v.Status = TemplateStatus.Retired;
        t.ActiveVersionId = null;
        t.ActiveVersionNumber = null;
        t.Status = TemplateStatus.Retired;
        audit.Record(caller, "template.retired", nameof(NotificationTemplate), t.Id.ToString(), t.Code, t.TenantId, t.CompanyId, t.ApplicationId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<NotificationTemplate> CloneAsync(CallerContext caller, Guid templateId, TemplateUpsertRequest target, CancellationToken ct)
    {
        var source = await LoadAsync(caller, templateId, Permission.ViewTemplates, ct);
        var v = source.Versions.FirstOrDefault(x => x.Id == source.ActiveVersionId) ?? source.Versions.OrderByDescending(x => x.VersionNumber).First();
        return await CreateAsync(caller, target, ToRequest(v, $"Cloned from {source.Code} v{v.VersionNumber}"), ct);
    }

    public static TemplateVersionRequest ToRequest(TemplateVersion v, string? note) => new()
    {
        Subject = v.Subject, Body = v.Body, BodyFormat = v.BodyFormat, TextBody = v.TextBody, Components = Json.Deserialize<TemplateComponents>(v.ComponentsJson),
        ProviderTemplateName = v.ProviderTemplateName, ProviderTemplateLanguage = v.ProviderTemplateLanguage, ProviderCategory = v.ProviderCategory, DltTemplateId = v.DltTemplateId,
        Variables = v.Variables.Select(x => new TemplateVariableDto { Name = x.Name, Required = x.Required, SampleValue = x.SampleValue, Description = x.Description }).ToList(),
        ChangeNote = note,
    };

    public async Task<TemplatePreviewResponse> PreviewAsync(CallerContext caller, TemplatePreviewRequest req, CancellationToken ct)
    {
        TemplateVersion version;
        Channel channel;
        Guid tenantId;
        if (req.Body is not null)
        {
            channel = req.Channel ?? Channel.Email;
            version = new TemplateVersion { Subject = req.Subject, Body = req.Body, BodyFormat = req.BodyFormat ?? BodyFormat.Text };
            tenantId = caller.TenantId ?? Guid.Empty;
        }
        else
        {
            NotificationTemplate t;
            if (req.TemplateId is { } id) t = await LoadAsync(caller, id, Permission.ViewTemplates, ct);
            else
            {
                if (req.TemplateCode is null || req.Channel is null) throw HubException.Validation("templateId, or templateCode and channel, are required.");
                var appId = caller.ApplicationId ?? req.ApplicationId;
                var found = await db.NotificationTemplates.Where(x => x.Code == req.TemplateCode && x.Channel == req.Channel && (caller.TenantId == null || x.TenantId == caller.TenantId)
                        && (x.ApplicationId == null || x.ApplicationId == appId) && (x.CompanyId == null || x.CompanyId == req.CompanyId))
                    .OrderByDescending(x => x.CompanyId != null).ThenByDescending(x => x.ApplicationId != null).FirstOrDefaultAsync(ct)
                    ?? throw HubException.NotFound("Template");
                t = await LoadAsync(caller, found.Id, Permission.ViewTemplates, ct);
            }
            channel = t.Channel;
            tenantId = t.TenantId;
            version = req.TemplateVersionId is { } vid ? t.Versions.FirstOrDefault(v => v.Id == vid) ?? throw HubException.NotFound("Template version")
                : t.Versions.FirstOrDefault(v => v.Id == t.ActiveVersionId) ?? t.Versions.OrderByDescending(v => v.VersionNumber).First();
        }
        var vars = TemplateEngine.FromJson(req.Variables);
        if (req.UseSampleData)
            foreach (var v in version.Variables.Where(v => v.SampleValue is not null)) vars.TryAdd(v.Name, v.SampleValue);
        vars.TryAdd("CompanyName", "Sample Company");
        var rendered = ChannelRenderer.Render(version, channel, vars);
        var referenced = TemplateEngine.ExtractVariables(version.Subject).Concat(TemplateEngine.ExtractVariables(version.Body)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new TemplatePreviewResponse
        {
            Subject = rendered.Content.Subject ?? rendered.Content.Title, Body = rendered.Content.Body, TextBody = rendered.Content.TextBody,
            MissingVariables = rendered.Missing, Errors = rendered.Errors,
            UnknownVariables = (req.Variables?.Keys ?? Enumerable.Empty<string>()).Where(k => !referenced.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList(),
            Sms = channel == Channel.Sms ? SmsSegmenter.Analyze(rendered.Content.Body) : null,
        };
    }

    /// <summary>Submits a WhatsApp template version to Meta for approval and records the provider status.</summary>
    public async Task<string> SubmitToWhatsAppAsync(CallerContext caller, Guid templateId, int versionNumber, Guid providerAccountId, CancellationToken ct)
    {
        var t = await LoadAsync(caller, templateId, Permission.ManageTemplates, ct);
        if (t.Channel != Channel.WhatsApp) throw HubException.Validation("Only WhatsApp templates can be submitted to Meta.");
        var v = Version(t, versionNumber);
        if (string.IsNullOrEmpty(v.ProviderTemplateName)) throw HubException.Validation("Set providerTemplateName (lowercase, underscores) before submitting.");
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == providerAccountId && a.Channel == Channel.WhatsApp, ct) ?? throw HubException.NotFound("WhatsApp provider account");
        if (account.TenantId is { } at && at != t.TenantId) throw new AccessDeniedException();
        if (registry.Get(account) is not WhatsAppCloudProvider wa) throw HubException.Validation("The provider account does not support template submission.");
        var wc = Json.Deserialize<TemplateComponents>(v.ComponentsJson)?.WhatsApp ?? new WhatsAppComponents();
        var components = new JsonArray();
        if (wc.HeaderFormat is { } hf)
        {
            var header = new JsonObject { ["type"] = "HEADER", ["format"] = hf.ToUpperInvariant() };
            if (hf.Equals("TEXT", StringComparison.OrdinalIgnoreCase)) header["text"] = ChannelRenderer.ToPositional(wc.HeaderText ?? "");
            components.Add(header);
        }
        components.Add(new JsonObject { ["type"] = "BODY", ["text"] = ChannelRenderer.ToPositional(v.Body) });
        if (!string.IsNullOrEmpty(wc.FooterText)) components.Add(new JsonObject { ["type"] = "FOOTER", ["text"] = wc.FooterText });
        if (wc.Buttons.Count > 0)
            components.Add(new JsonObject
            {
                ["type"] = "BUTTONS",
                ["buttons"] = new JsonArray(wc.Buttons.Select(b => (JsonNode)new JsonObject
                {
                    ["type"] = b.Type.ToUpperInvariant(), ["text"] = b.Text, ["url"] = b.Url, ["phone_number"] = b.PhoneNumber,
                }).ToArray()),
            });
        var (ok, id, status, detail) = await wa.SubmitTemplateAsync(new JsonObject
        {
            ["name"] = v.ProviderTemplateName, ["language"] = v.ProviderTemplateLanguage ?? t.Locale, ["category"] = (v.ProviderCategory ?? "UTILITY").ToUpperInvariant(),
            ["components"] = components,
        }, ct);
        v.ProviderApprovalStatus = ok ? status ?? "PENDING" : "SUBMIT_FAILED";
        audit.Record(caller, "template.whatsapp_submitted", nameof(NotificationTemplate), t.Id.ToString(), $"{v.ProviderTemplateName}: {v.ProviderApprovalStatus}", t.TenantId, t.CompanyId, t.ApplicationId);
        await db.SaveChangesAsync(ct);
        if (!ok) throw new HubException(502, "provider_rejected", "Meta rejected the template submission: " + detail);
        return v.ProviderApprovalStatus;
    }

    /// <summary>Pulls template approval statuses from the WhatsApp Business Account and records them on matching versions.</summary>
    public async Task<int> SyncWhatsAppStatusesAsync(CallerContext caller, Guid providerAccountId, CancellationToken ct)
    {
        var account = await db.ProviderAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == providerAccountId && a.Channel == Channel.WhatsApp, ct)
                      ?? throw HubException.NotFound("WhatsApp provider account");
        if (account.TenantId is { } t ? !caller.Can(Permission.ManageTemplates, t) : !caller.IsPlatformAdmin) throw new AccessDeniedException();
        if (registry.Get(account) is not WhatsAppCloudProvider wa) throw HubException.Validation("The provider account does not support template listing.");
        var remote = await wa.ListTemplatesAsync(ct);
        var versions = await db.TemplateVersions.Where(v => v.ProviderTemplateName != null
                && db.NotificationTemplates.Any(x => x.Id == v.TemplateId && x.Channel == Channel.WhatsApp && (account.TenantId == null || x.TenantId == account.TenantId)))
            .ToListAsync(ct);
        var updated = 0;
        foreach (var v in versions)
        {
            var match = remote.FirstOrDefault(r => r.Name == v.ProviderTemplateName && (v.ProviderTemplateLanguage is null || r.Language.Equals(v.ProviderTemplateLanguage, StringComparison.OrdinalIgnoreCase)));
            if (match.Name is null || match.Status == v.ProviderApprovalStatus) continue;
            v.ProviderApprovalStatus = match.Status;
            v.ProviderCategory = match.Category ?? v.ProviderCategory;
            updated++;
        }
        audit.Record(caller, "template.whatsapp_synced", nameof(ProviderAccount), account.Id.ToString(), $"{updated} version(s) updated from {remote.Count} remote templates", account.TenantId);
        await db.SaveChangesAsync(ct);
        return updated;
    }

    // ───────── helpers ─────────

    private TemplateVersion AddVersion(CallerContext caller, NotificationTemplate t, TemplateVersionRequest req)
    {
        var v = new TemplateVersion { TemplateId = t.Id, VersionNumber = t.LatestVersionNumber + 1, Status = TemplateStatus.Draft, CreatedBy = caller.ActorId, ChangeNote = req.ChangeNote };
        Apply(v, req, t.Channel);
        ReplaceVariables(v, req);
        t.LatestVersionNumber = v.VersionNumber;
        t.Versions.Add(v);
        db.TemplateVersions.Add(v);
        return v;
    }

    private static void Apply(TemplateVersion v, TemplateVersionRequest req, Channel channel)
    {
        v.Subject = req.Subject;
        v.Body = req.Body ?? "";
        v.BodyFormat = channel == Channel.Email ? req.BodyFormat : BodyFormat.Text;
        v.TextBody = req.TextBody;
        v.ComponentsJson = req.Components is null ? null : Json.Serialize(req.Components);
        v.ProviderTemplateName = req.ProviderTemplateName;
        v.ProviderTemplateLanguage = req.ProviderTemplateLanguage;
        v.ProviderCategory = req.ProviderCategory;
        v.DltTemplateId = req.DltTemplateId;
        v.ChangeNote = req.ChangeNote ?? v.ChangeNote;
        Validate(channel, v, draft: true);
    }

    private void ReplaceVariables(TemplateVersion v, TemplateVersionRequest req)
    {
        var declared = req.Variables ?? [];
        var usage = TemplateUsage(v.Subject, v.Body, v.TextBody);
        var names = declared.Select(d => d.Name).Concat(usage.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var old in v.Variables.ToList()) { v.Variables.Remove(old); db.TemplateVariables.Remove(old); }
        foreach (var name in names)
        {
            var d = declared.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            var tv = new TemplateVariable { TemplateVersionId = v.Id, Name = name, Required = d?.Required ?? usage.GetValueOrDefault(name, true), SampleValue = d?.SampleValue, Description = d?.Description };
            v.Variables.Add(tv);
            db.TemplateVariables.Add(tv);
        }
    }

    public static readonly HashSet<string> SystemVariables = new(StringComparer.OrdinalIgnoreCase) { "CompanyName", "TenantName", "RecipientName", "UnsubscribeUrl", "CurrentDate" };

    /// <summary>Variables referenced by a version's subject/body/text with their required flag (system variables excluded).</summary>
    public static Dictionary<string, bool> TemplateUsage(params string?[] parts)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        foreach (var (name, req) in TemplateEngine.ExtractVariableUsage(part))
        {
            if (SystemVariables.Contains(name)) continue;
            map[name] = map.TryGetValue(name, out var existing) ? existing || req : req;
        }
        return map;
    }

    public static void Validate(Channel channel, TemplateVersion v, bool draft = false)
    {
        var errors = new Dictionary<string, string[]>();
        if (TemplateEngine.Validate(v.Subject) is { } se) errors["subject"] = [se];
        if (TemplateEngine.Validate(v.Body) is { } be) errors["body"] = [be];
        if (TemplateEngine.Validate(v.TextBody) is { } te) errors["textBody"] = [te];
        if (v.BodyFormat == BodyFormat.Html && TemplateEngine.FindUnsafeHtml(v.Body) is { } unsafeHtml) errors["body"] = [unsafeHtml];
        if (!draft)
        {
            if (string.IsNullOrWhiteSpace(v.Body)) errors["body"] = ["Body is required."];
            if (channel == Channel.Email && string.IsNullOrWhiteSpace(v.Subject)) errors["subject"] = ["Email templates need a subject."];
            if (channel == Channel.WhatsApp && !string.IsNullOrEmpty(v.ProviderTemplateName) && v.Body.Contains("{{#"))
                errors["body"] = ["Approved WhatsApp templates support simple variables only (no #if/#each blocks)."];
            if (channel == Channel.Sms && v.Body.Length > 1600) errors["body"] = ["SMS body is too long (max 10 segments)."];
        }
        if (errors.Count > 0) throw HubException.Validation("The template is invalid.", errors);
    }

    private static string Checksum(TemplateVersion v)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v.Subject, v.Body, v.BodyFormat, v.TextBody, v.ComponentsJson, v.ProviderTemplateName, v.DltTemplateId }))));

    private static TemplateVersion Version(NotificationTemplate t, int n) => t.Versions.FirstOrDefault(v => v.VersionNumber == n) ?? throw HubException.NotFound($"Version {n}");

    public async Task<NotificationTemplate> LoadAsync(CallerContext caller, Guid id, Permission permission, CancellationToken ct)
    {
        var t = await db.NotificationTemplates.Include(x => x.Versions).ThenInclude(v => v.Variables).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw HubException.NotFound("Template");
        if (!caller.Can(permission, t.TenantId, t.CompanyId, t.ApplicationId, t.Channel)) throw HubException.NotFound("Template");
        if (caller.Kind == CallerKind.Application && t.ApplicationId is { } app && app != caller.ApplicationId) throw HubException.NotFound("Template");
        return t;
    }

    private async Task AuthorizeScopeAsync(CallerContext caller, Guid tenantId, Guid? companyId, Guid? applicationId, Permission p, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) throw HubException.Validation("Unknown tenant.");
        if (companyId is { } c && !await db.Companies.AnyAsync(x => x.Id == c && x.TenantId == tenantId, ct)) throw HubException.Validation("Unknown company for this tenant.");
        if (applicationId is { } a && !await db.Applications.AnyAsync(x => x.Id == a && x.TenantId == tenantId, ct)) throw HubException.Validation("Unknown application for this tenant.");
        if (caller.Kind == CallerKind.Application && (caller.ApplicationId != applicationId || caller.TenantId != tenantId)) throw new AccessDeniedException("Applications can only manage their own templates.");
        if (!caller.Can(p, tenantId, companyId, applicationId)) throw new AccessDeniedException();
    }
}
