using System.Text;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Rules;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Delivery;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Api.Endpoints;

public sealed record PreferencesUpdate(Guid CompanyId, string ExternalId, ContactKind? Kind, List<PreferenceItemDto> Preferences);
public sealed record SuppressionRequest(Guid TenantId, Guid? CompanyId, Channel Channel, string Address, SuppressionReason Reason, string? Note);
public sealed record GroupRequest(Guid CompanyId, string Name, string? Description, bool Dynamic, ConditionNode? Filter, List<Guid>? ContactIds);

public static class RecipientEndpoints
{
    public static void Map(WebApplication app, RouteGroupBuilder api)
    {
        var contacts = api.MapGroup("/contacts").WithTags("Recipients").RequireAuthorization("app-or-admin");

        contacts.MapPut("", async (ContactUpsertRequest req, HttpContext ctx, NotificationHubDbContext db, TenantContextResolver resolver) =>
        {
            var caller = await ctx.CallerAsync();
            var tc = await ResolveForContacts(caller, resolver, db, req.CompanyId, ctx.RequestAborted);
            if (string.IsNullOrWhiteSpace(req.ExternalId)) throw HubException.Validation("externalId is required.");
            if (req.Email is not null && !Addressing.IsValidEmail(req.Email)) throw HubException.Validation("Invalid email.");
            var c = await db.RecipientContacts.FirstOrDefaultAsync(x => x.CompanyId == req.CompanyId && x.Kind == req.Kind && x.ExternalId == req.ExternalId, ctx.RequestAborted);
            var created = c is null;
            c ??= new RecipientContact { TenantId = tc, CompanyId = req.CompanyId, Kind = req.Kind, ExternalId = req.ExternalId.Trim() };
            if (created) db.RecipientContacts.Add(c);
            c.Name = req.Name ?? c.Name; c.Email = req.Email?.Trim().ToLowerInvariant() ?? c.Email;
            c.Mobile = req.Mobile is null ? c.Mobile : Addressing.NormalizePhone(req.Mobile) ?? throw HubException.Validation("Invalid mobile.");
            c.WhatsApp = req.WhatsApp is null ? c.WhatsApp : Addressing.NormalizePhone(req.WhatsApp) ?? throw HubException.Validation("Invalid WhatsApp number.");
            c.TimeZone = req.TimeZone ?? c.TimeZone; c.Locale = req.Locale ?? c.Locale; c.Country = req.Country ?? c.Country;
            if (req.Roles is not null) c.Roles = req.Roles;
            if (req.Attributes is not null) c.AttributesJson = Json.Serialize(req.Attributes);
            c.UpdatedAt = DateTime.UtcNow;
            c.ErasedAt = null;
            await db.SaveChangesAsync(ctx.RequestAborted);
            return created ? Results.Created($"/api/v1/contacts/{c.Id}", Dto(c, true)) : Results.Ok(Dto(c, true));
        }).WithSummary("Register or update a user/customer contact");

        contacts.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, Guid? companyId, ContactKind? kind, string? q, int page = 1, int pageSize = 50) =>
        {
            var caller = await ctx.CallerAsync();
            var query = db.RecipientContacts.AsNoTracking().Where(c => c.ErasedAt == null).Visible(caller.FilterFor(Permission.ViewContacts));
            if (companyId is { } cid) query = query.Where(c => c.CompanyId == cid);
            if (kind is { } k) query = query.Where(c => c.Kind == k);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(c => c.ExternalId.Contains(q) || (c.Name != null && c.Name.Contains(q)) || (c.Email != null && c.Email.Contains(q.ToLower())) || (c.Mobile != null && c.Mobile.Contains(q)));
            pageSize = Math.Clamp(pageSize, 1, 200);
            var total = await query.CountAsync(ctx.RequestAborted);
            var rows = await query.OrderBy(c => c.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ctx.RequestAborted);
            return Results.Ok(new { items = rows.Select(c => Dto(c, caller.Can(Permission.ViewMessageContent, c.TenantId, c.CompanyId))), total, page, pageSize });
        });

        contacts.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            // Data-protection erasure: identifiers are scrubbed; delivery history keeps only masked metadata.
            var caller = await ctx.CallerAsync();
            var c = await db.RecipientContacts.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
            if (c is null || !caller.Can(Permission.ManageContacts, c.TenantId, c.CompanyId)) throw HubException.NotFound("Contact");
            var addresses = new[] { c.Email, c.Mobile, c.WhatsApp }.Where(a => a is not null).ToList();
            c.Name = "[erased]"; c.Email = null; c.Mobile = null; c.WhatsApp = null; c.AttributesJson = null; c.ErasedAt = DateTime.UtcNow;
            await db.NotificationRecipients.Where(r => r.ContactId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Email, (string?)null).SetProperty(r => r.Mobile, (string?)null)
                .SetProperty(r => r.WhatsApp, (string?)null).SetProperty(r => r.Name, "[erased]"), ctx.RequestAborted);
            foreach (var a in addresses)
                await db.NotificationDeliveries.Where(d => d.TenantId == c.TenantId && d.Address == a)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.Address, "erased:" + Addressing.Sha256Hex(a!)[..16]).SetProperty(d => d.ContentCipher, (string?)null).SetProperty(d => d.ContentPurgedAt, DateTime.UtcNow), ctx.RequestAborted);
            await db.DeviceRegistrations.Where(d => d.ContactId == id).ExecuteDeleteAsync(ctx.RequestAborted);
            audit.Record(caller, "contact.erased", nameof(RecipientContact), id.ToString(), "personal data erased", c.TenantId, c.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        }).WithSummary("Erase a contact's personal data (right to erasure)");

        // ── preferences & consent ──
        var prefs = api.MapGroup("/preferences").WithTags("Preferences & consent").RequireAuthorization("app-or-admin");
        prefs.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, TenantContextResolver resolver, Guid companyId, string externalId, ContactKind? kind) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.HasScope(Scopes.PreferencesRead) && !caller.HasScope(Scopes.PreferencesWrite)) throw new AccessDeniedException();
            await ResolveForContacts(caller, resolver, db, companyId, ctx.RequestAborted, write: false);
            var c = await FindContact(db, companyId, externalId, kind, ctx.RequestAborted);
            return Results.Ok(await PreferencesDto(db, c, ctx.RequestAborted));
        }).WithSummary("Get a recipient's channel/category preferences and consent state");

        prefs.MapPut("", async (PreferencesUpdate req, HttpContext ctx, NotificationHubDbContext db, TenantContextResolver resolver, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.HasScope(Scopes.PreferencesWrite)) throw new AccessDeniedException();
            await ResolveForContacts(caller, resolver, db, req.CompanyId, ctx.RequestAborted);
            var c = await FindContact(db, req.CompanyId, req.ExternalId, req.Kind, ctx.RequestAborted);
            foreach (var p in req.Preferences)
            {
                var cat = string.IsNullOrWhiteSpace(p.Category) ? "*" : p.Category.Trim();
                var row = await db.RecipientPreferences.FirstOrDefaultAsync(x => x.ContactId == c.Id && x.Category == cat && x.Channel == p.Channel, ctx.RequestAborted);
                if (row is null) db.RecipientPreferences.Add(row = new RecipientPreference { TenantId = c.TenantId, CompanyId = c.CompanyId, ContactId = c.Id, Category = cat, Channel = p.Channel });
                row.Enabled = p.Enabled; row.UpdatedAt = DateTime.UtcNow; row.Source = caller.ActorId;
            }
            audit.Record(caller, "preferences.updated", nameof(RecipientContact), c.Id.ToString(), $"{req.Preferences.Count} preference(s)", c.TenantId, c.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(await PreferencesDto(db, c, ctx.RequestAborted));
        }).WithSummary("Update preferences (per category and channel; '*' = all categories)");

        api.MapPost("/consents", async (ConsentRequest req, HttpContext ctx, NotificationHubDbContext db, TenantContextResolver resolver, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.HasScope(Scopes.PreferencesWrite)) throw new AccessDeniedException();
            await ResolveForContacts(caller, resolver, db, req.CompanyId, ctx.RequestAborted);
            if (string.IsNullOrWhiteSpace(req.Source)) throw HubException.Validation("source is required (where consent was captured).");
            var c = await FindContact(db, req.CompanyId, req.ExternalId, null, ctx.RequestAborted);
            var row = new RecipientConsent
            {
                TenantId = c.TenantId, CompanyId = c.CompanyId, ContactId = c.Id, Channel = req.Channel, Purpose = req.Purpose, Status = req.Status, Source = req.Source,
                Evidence = req.Evidence, Scope = req.Scope, IpAddress = caller.IpAddress, RecordedBy = caller.ActorId, CapturedAt = req.CapturedAt?.UtcDateTime ?? DateTime.UtcNow,
            };
            db.RecipientConsents.Add(row);
            audit.Record(caller, $"consent.{req.Status.ToString().ToLowerInvariant()}", nameof(RecipientConsent), row.Id.ToString(), $"{ChannelNames.ToWire(req.Channel)}/{req.Purpose}", c.TenantId, c.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/consents/{row.Id}", new { row.Id });
        }).WithTags("Preferences & consent").RequireAuthorization("app-or-admin").WithSummary("Record consent or its withdrawal (append-only evidence)");

        api.MapGet("/consents", async (HttpContext ctx, NotificationHubDbContext db, Guid contactId) =>
        {
            var caller = await ctx.CallerAsync();
            var c = await db.RecipientContacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contactId, ctx.RequestAborted);
            if (c is null || !caller.Can(Permission.ViewContacts, c.TenantId, c.CompanyId)) throw HubException.NotFound("Contact");
            return Results.Ok(await db.RecipientConsents.AsNoTracking().Where(x => x.ContactId == contactId).OrderByDescending(x => x.CapturedAt).ToListAsync(ctx.RequestAborted));
        }).WithTags("Preferences & consent").RequireAuthorization("app-or-admin").WithSummary("Consent history with evidence");

        // ── suppressions ──
        var sup = api.MapGroup("/suppressions").WithTags("Preferences & consent").RequireAuthorization("admin");
        sup.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, Guid? tenantId, Channel? channel, string? address, int page = 1) =>
        {
            var caller = await ctx.CallerAsync();
            var q = db.Suppressions.AsNoTracking().Visible(caller.FilterFor(Permission.ViewContacts), includeBroader: true);
            if (tenantId is { } t) q = q.Where(s => s.TenantId == t);
            if (channel is { } ch) q = q.Where(s => s.Channel == ch);
            if (!string.IsNullOrWhiteSpace(address)) q = q.Where(s => s.Address.Contains(address.ToLower()));
            var rows = await q.OrderByDescending(s => s.CreatedAt).Skip((page - 1) * 100).Take(100).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Select(s => new { s.Id, s.TenantId, s.CompanyId, channel = ChannelNames.ToWire(s.Channel), address = caller.Can(Permission.ViewMessageContent, s.TenantId, s.CompanyId) ? s.Address : Addressing.Mask(s.Channel, s.Address), s.Reason, s.PromotionalOnly, s.Source, s.CreatedAt }));
        });
        sup.MapPost("", async (SuppressionRequest req, HttpContext ctx, NotificationHubDbContext db, DeliveryStatusUpdater status, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.Can(Permission.ManageContacts, req.TenantId, req.CompanyId)) throw new AccessDeniedException();
            await status.SuppressAsync(req.TenantId, req.CompanyId, req.Channel, req.Address, req.Reason, "manual:" + caller.ActorId, ctx.RequestAborted);
            audit.Record(caller, "suppression.added", nameof(Suppression), null, $"{ChannelNames.ToWire(req.Channel)} {Addressing.Mask(req.Channel, req.Address)} ({req.Reason})", req.TenantId, req.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });
        sup.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var s = await db.Suppressions.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
            if (s is null || !caller.Can(Permission.ManageContacts, s.TenantId, s.CompanyId)) throw HubException.NotFound("Suppression");
            db.Suppressions.Remove(s);
            audit.Record(caller, "suppression.removed", nameof(Suppression), id.ToString(), $"{ChannelNames.ToWire(s.Channel)} {Addressing.Mask(s.Channel, s.Address)}", s.TenantId, s.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });

        // ── recipient groups ──
        var groups = api.MapGroup("/groups").WithTags("Recipients").RequireAuthorization("admin");
        groups.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, Guid? companyId) =>
        {
            var caller = await ctx.CallerAsync();
            var q = db.RecipientGroups.AsNoTracking().Visible(caller.FilterFor(Permission.ViewContacts));
            if (companyId is { } c) q = q.Where(g => g.CompanyId == c);
            var rows = await q.OrderBy(g => g.Name).ToListAsync(ctx.RequestAborted);
            var ids = rows.Select(r => r.Id).ToList();
            var counts = await db.RecipientGroupMembers.Where(m => ids.Contains(m.GroupId)).GroupBy(m => m.GroupId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ctx.RequestAborted);
            return Results.Ok(rows.Select(g => new { g.Id, g.CompanyId, g.Name, g.Description, g.Dynamic, filter = Json.Deserialize<ConditionNode>(g.FilterJson), members = counts.GetValueOrDefault(g.Id) }));
        });
        groups.MapPost("", async (GroupRequest req, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CompanyId, ctx.RequestAborted) ?? throw HubException.NotFound("Company");
            if (!caller.Can(Permission.ManageContacts, company.TenantId, company.Id)) throw new AccessDeniedException();
            var g = new RecipientGroup { TenantId = company.TenantId, CompanyId = company.Id, Name = req.Name, Description = req.Description, Dynamic = req.Dynamic, FilterJson = req.Filter is null ? null : Json.Serialize(req.Filter) };
            db.RecipientGroups.Add(g);
            foreach (var cid in req.ContactIds ?? []) db.RecipientGroupMembers.Add(new RecipientGroupMember { GroupId = g.Id, ContactId = cid });
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/groups/{g.Id}", new { g.Id });
        });
        groups.MapGet("/{id:guid}/members", async (Guid id, HttpContext ctx, NotificationHubDbContext db, RecipientResolver resolver) =>
        {
            var caller = await ctx.CallerAsync();
            var g = await db.RecipientGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
            if (g is null || !caller.Can(Permission.ViewContacts, g.TenantId, g.CompanyId)) throw HubException.NotFound("Group");
            var members = await resolver.GroupMembersAsync(g.CompanyId, id, ctx.RequestAborted);
            return Results.Ok(members.Select(c => Dto(c, false)));
        });

        // ── devices ──
        var devices = api.MapGroup("/devices").WithTags("Devices").RequireAuthorization("app-or-admin");
        devices.MapPost("", async (RegisterDeviceRequest req, HttpContext ctx, NotificationHubDbContext db, TenantContextResolver resolver) =>
        {
            var caller = await ctx.CallerAsync();
            var tc = await resolver.ResolveAsync(caller, req.ApplicationId, req.CompanyId, Permission.ManageContacts, Scopes.DevicesWrite, ctx.RequestAborted);
            if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.Token) || req.Token.Length > 2000) throw HubException.Validation("userId and a token (max 2000 chars) are required.");
            if (req.Platform == DevicePlatform.Web && (string.IsNullOrEmpty(req.P256dh) || string.IsNullOrEmpty(req.Auth) || !req.Token.StartsWith("https://")))
                throw HubException.Validation("Web Push registrations need the subscription endpoint URL (token) and its p256dh/auth keys.");
            var hash = Addressing.Sha256Hex(req.Token);
            var d = await db.DeviceRegistrations.FirstOrDefaultAsync(x => x.ApplicationId == tc.Application.Id && x.TokenHash == hash, ctx.RequestAborted);
            var created = d is null;
            // A token moving to another user (shared device, re-login) is re-bound rather than duplicated.
            d ??= new DeviceRegistration { TenantId = tc.Tenant.Id, ApplicationId = tc.Application.Id, Token = req.Token, TokenHash = hash };
            if (created) db.DeviceRegistrations.Add(d);
            d.CompanyId = tc.Company.Id; d.UserId = req.UserId; d.Platform = req.Platform;
            d.Provider = req.Provider ?? req.Platform switch { DevicePlatform.Ios => "apns", DevicePlatform.Web => "webpush", _ => "fcm" };
            d.P256dh = req.P256dh; d.Auth = req.Auth; d.DeviceName = req.DeviceName; d.AppVersion = req.AppVersion; d.Locale = req.Locale;
            d.LastSeenAt = DateTime.UtcNow; d.InvalidatedAt = null; d.InvalidReason = null;
            d.ContactId = await db.RecipientContacts.Where(c => c.CompanyId == tc.Company.Id && c.Kind == ContactKind.User && c.ExternalId == req.UserId).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return created ? Results.Created($"/api/v1/devices/{d.Id}", DeviceDto(d)) : Results.Ok(DeviceDto(d));
        }).WithSummary("Register or refresh a push device token");
        devices.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, Guid companyId, string userId, Guid? applicationId) =>
        {
            var caller = await ctx.CallerAsync();
            var appId = caller.ApplicationId ?? applicationId;
            var rows = await db.DeviceRegistrations.AsNoTracking().Visible(caller.FilterFor(Permission.ViewContacts))
                .Where(d => d.CompanyId == companyId && d.UserId == userId && (appId == null || d.ApplicationId == appId)).OrderByDescending(d => d.LastSeenAt).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Select(DeviceDto));
        });
        devices.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var d = await db.DeviceRegistrations.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
            if (d is null || !caller.Can(Permission.ManageContacts, d.TenantId, d.CompanyId, d.ApplicationId) || (caller.Kind == CallerKind.Application && caller.ApplicationId != d.ApplicationId))
                throw HubException.NotFound("Device");
            d.InvalidatedAt = DateTime.UtcNow; d.InvalidReason = "unregistered by application";
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        }).WithSummary("Unregister a device (logout/uninstall)");

        // ── public unsubscribe (link in promotional messages) ──
        app.MapGet("/u/{token}", (string token) => Results.Content(UnsubscribePage(token, null), "text/html")).AllowAnonymous().ExcludeFromDescription();
        app.MapPost("/u/{token}", async (string token, HttpContext ctx, NotificationHubDbContext db, ISecretProtector protector, DeliveryStatusUpdater status) =>
        {
            UnsubscribeToken? t;
            try { t = Json.Deserialize<UnsubscribeToken>(protector.Unprotect(Encoding.UTF8.GetString(Hashing.FromBase64Url(token)), Purposes.Unsubscribe)); }
            catch (Exception) { return Results.Content(UnsubscribePage(token, "This unsubscribe link is invalid."), "text/html", statusCode: 400); }
            if (t is null) return Results.BadRequest();
            await status.SuppressAsync(t.TenantId, t.CompanyId, t.Channel, t.Address, SuppressionReason.Unsubscribe, "unsubscribe-link", ctx.RequestAborted);
            if (t.ContactId is { } cid)
                db.RecipientConsents.Add(new RecipientConsent { TenantId = t.TenantId, CompanyId = t.CompanyId, ContactId = cid, Channel = t.Channel, Purpose = ConsentPurpose.Marketing, Status = ConsentStatus.Withdrawn, Source = "unsubscribe-link", Evidence = $"one-click from {ctx.Connection.RemoteIpAddress}", RecordedBy = "recipient", IpAddress = ctx.Connection.RemoteIpAddress?.ToString() });
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Content(UnsubscribePage(token, "You have been unsubscribed from promotional messages. Essential service messages will continue."), "text/html");
        }).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();
    }

    private static string UnsubscribePage(string token, string? message) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Unsubscribe</title>
        <style>body{font-family:system-ui,sans-serif;max-width:480px;margin:10vh auto;padding:0 16px;color:#222}button{padding:10px 18px;font-size:16px}</style></head>
        <body><h1>Manage subscription</h1>{{(message is null
            ? $"<p>Stop receiving promotional messages at this address?</p><form method=\"post\" action=\"/u/{System.Net.WebUtility.HtmlEncode(token)}\"><button type=\"submit\">Unsubscribe</button></form>"
            : $"<p>{System.Net.WebUtility.HtmlEncode(message)}</p>")}}</body></html>
        """;

    private static async Task<Guid> ResolveForContacts(CallerContext caller, TenantContextResolver resolver, NotificationHubDbContext db, Guid companyId, CancellationToken ct, bool write = true)
    {
        if (caller.Kind == CallerKind.Application)
            return (await resolver.ResolveAsync(caller, null, companyId, write ? Permission.ManageContacts : Permission.ViewContacts, null, ct, allowSuspended: true)).Tenant.Id;
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct) ?? throw HubException.NotFound("Company");
        if (!caller.Can(write ? Permission.ManageContacts : Permission.ViewContacts, company.TenantId, company.Id)) throw new AccessDeniedException();
        return company.TenantId;
    }

    private static async Task<RecipientContact> FindContact(NotificationHubDbContext db, Guid companyId, string externalId, ContactKind? kind, CancellationToken ct)
        => await db.RecipientContacts.Where(c => c.CompanyId == companyId && c.ExternalId == externalId && c.ErasedAt == null && (kind == null || c.Kind == kind)).FirstOrDefaultAsync(ct)
           ?? throw HubException.NotFound("Contact");

    private static async Task<PreferencesDto> PreferencesDto(NotificationHubDbContext db, RecipientContact c, CancellationToken ct)
    {
        var prefs = await db.RecipientPreferences.AsNoTracking().Where(p => p.ContactId == c.Id).ToListAsync(ct);
        var consents = (await db.RecipientConsents.AsNoTracking().Where(x => x.ContactId == c.Id).ToListAsync(ct))
            .GroupBy(x => (x.Channel, x.Purpose)).Select(g => g.OrderByDescending(x => x.CapturedAt).First()).ToList();
        return new PreferencesDto
        {
            ContactId = c.Id, ExternalId = c.ExternalId,
            Preferences = prefs.Select(p => new PreferenceItemDto { Category = p.Category, Channel = p.Channel, Enabled = p.Enabled }).ToList(),
            Consents = consents.Select(x => new ConsentDto { Id = x.Id, Channel = x.Channel, Purpose = x.Purpose, Status = x.Status, Source = x.Source, Evidence = x.Evidence, Scope = x.Scope, CapturedAt = x.CapturedAt }).ToList(),
        };
    }

    private static object Dto(RecipientContact c, bool unmasked) => new
    {
        c.Id, c.TenantId, c.CompanyId, c.Kind, c.ExternalId, c.Name,
        email = unmasked ? c.Email : c.Email is null ? null : Addressing.MaskEmail(c.Email),
        mobile = unmasked ? c.Mobile : c.Mobile is null ? null : Addressing.MaskPhone(c.Mobile),
        whatsapp = unmasked ? c.WhatsApp : c.WhatsApp is null ? null : Addressing.MaskPhone(c.WhatsApp),
        c.TimeZone, c.Locale, c.Country, c.Roles, attributes = Json.Deserialize<Dictionary<string, string>>(c.AttributesJson), c.UpdatedAt,
    };

    private static DeviceDto DeviceDto(DeviceRegistration d) => new()
    {
        Id = d.Id, UserId = d.UserId, Platform = d.Platform, Provider = d.Provider, TokenPreview = d.Token.Length > 12 ? d.Token[..6] + "…" + d.Token[^4..] : "…",
        DeviceName = d.DeviceName, CreatedAt = d.CreatedAt, LastSeenAt = d.LastSeenAt, InvalidatedAt = d.InvalidatedAt, InvalidReason = d.InvalidReason,
    };
}
