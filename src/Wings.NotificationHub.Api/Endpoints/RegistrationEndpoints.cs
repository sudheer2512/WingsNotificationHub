using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Providers;
using Wings.NotificationHub.Infrastructure.Services;
using App = Wings.NotificationHub.Core.Domain.Application;

namespace Wings.NotificationHub.Api.Endpoints;

/// <summary>
/// Wings Code integration: a generated application registers its notification manifest during deployment.
/// Idempotent upsert keyed by application code. Templates arrive as drafts and go through review unless the
/// application is a sandbox (auto-activated so pre-deployment tests can run against mock providers).
/// </summary>
public static class RegistrationEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/registrations/manifest", async (NotificationManifest manifest, HttpContext ctx, NotificationHubDbContext db, TemplateService templates,
            ISecretProtector protector, UrlGuard guard, AuditService audit, Guid? tenantId) =>
        {
            var caller = await ctx.CallerAsync();
            Guid tid;
            if (caller.Kind == CallerKind.Application)
            {
                if (!caller.HasScope(Scopes.ApplicationsRegister)) throw new AccessDeniedException("A provisioning credential with the applications.register scope is required.");
                tid = caller.TenantId!.Value;
            }
            else tid = tenantId ?? throw HubException.Validation("tenantId is required for administrator registrations.");
            if (caller.Kind != CallerKind.Application && !caller.Can(Permission.ManageTenancy, tid)) throw new AccessDeniedException();
            Validate(manifest);

            var result = new ManifestRegistrationResult();
            var code = manifest.Application.Code.Trim().ToUpperInvariant();
            var app = await db.Applications.FirstOrDefaultAsync(a => a.TenantId == tid && a.Code == code, ctx.RequestAborted);
            if (app is null)
            {
                app = new App { TenantId = tid, Code = code };
                db.Applications.Add(app);
                result.Created = true;
            }
            app.Name = manifest.Application.Name; app.Description = manifest.Application.Description; app.DefaultLocale = manifest.Application.DefaultLocale;
            app.Sandbox = manifest.Application.Sandbox; app.AllowedChannels = manifest.Channels;
            result.ApplicationId = app.Id;

            foreach (var companyCode in manifest.Companies)
            {
                var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tid && c.Code == companyCode.ToUpper(), ctx.RequestAborted);
                if (company is null) { result.Warnings.Add($"Company '{companyCode}' does not exist in the tenant; skipped."); continue; }
                if (!await db.CompanyApplications.AnyAsync(l => l.CompanyId == company.Id && l.ApplicationId == app.Id, ctx.RequestAborted))
                    db.CompanyApplications.Add(new CompanyApplication { TenantId = tid, CompanyId = company.Id, ApplicationId = app.Id });
            }

            foreach (var nt in manifest.NotificationTypes)
            {
                var c = nt.Code.Trim().ToUpperInvariant();
                var t = await db.NotificationTypes.FirstOrDefaultAsync(x => x.TenantId == tid && x.ApplicationId == app.Id && x.Code == c, ctx.RequestAborted);
                if (t is null) db.NotificationTypes.Add(t = new NotificationType { TenantId = tid, ApplicationId = app.Id, Code = c });
                t.Name = nt.Name; t.Description = nt.Description; t.Category = nt.Category; t.MessageClass = nt.MessageClass; t.DefaultChannels = nt.DefaultChannels;
                t.DefaultTemplateCode = nt.DefaultTemplateCode?.ToUpperInvariant(); t.DefaultPriority = nt.DefaultPriority; t.AiAllowed = nt.AiAllowed;
                t.RequiresHumanApprovalForAi = nt.RequiresHumanApprovalForAi;
                result.NotificationTypesUpserted++;
            }
            await db.SaveChangesAsync(ctx.RequestAborted);

            // Templates go through the normal workflow; the registering identity is the author.
            var system = new CallerContext { Kind = CallerKind.System, ActorId = caller.ActorId, ActorName = caller.ActorName ?? "Wings Code", Grants = [new ScopeGrant(Roles.PlatformAdmin, null, null, null, null)] };
            foreach (var mt in manifest.Templates)
            {
                var tcode = mt.Code.Trim().ToUpperInvariant();
                var version = new TemplateVersionRequest { Subject = mt.Subject, Body = mt.Body, BodyFormat = mt.BodyFormat, Variables = mt.Variables, ChangeNote = $"Registered by manifest {manifest.SchemaVersion}" };
                var existing = await db.NotificationTemplates.Include(x => x.Versions).FirstOrDefaultAsync(x => x.TenantId == tid && x.CompanyId == null && x.ApplicationId == app.Id
                    && x.Code == tcode && x.Channel == mt.Channel && x.Locale == mt.Locale, ctx.RequestAborted);
                TemplateVersion? draft = null;
                if (existing is null)
                {
                    var created = await templates.CreateAsync(system, new TemplateUpsertRequest { TenantId = tid, ApplicationId = app.Id, Code = tcode, Name = mt.Name, Channel = mt.Channel, Locale = mt.Locale, Category = mt.Category }, version, ctx.RequestAborted);
                    draft = created.Versions.Single();
                }
                else
                {
                    var current = existing.Versions.FirstOrDefault(v => v.Id == existing.ActiveVersionId) ?? existing.Versions.OrderByDescending(v => v.VersionNumber).First();
                    if (current.Body == mt.Body && current.Subject == mt.Subject) continue; // unchanged: idempotent re-registration
                    draft = await templates.SaveDraftAsync(system, existing.Id, version, ctx.RequestAborted);
                }
                result.TemplatesRegistered++;
                if (app.Sandbox)
                {
                    await templates.SubmitAsync(system, draft.TemplateId, draft.VersionNumber, ctx.RequestAborted);
                    await templates.ReviewAsync(CallerContext.SystemCaller, draft.TemplateId, draft.VersionNumber, true, "auto-approved for sandbox application", true, ctx.RequestAborted);
                }
            }
            if (!app.Sandbox && result.TemplatesRegistered > 0)
                result.Warnings.Add("Templates were registered as drafts. Submit and approve them in the Template Manager before production use.");

            foreach (var w in manifest.Webhooks)
            {
                if (guard.Validate(w.Url) is { } err) { result.Warnings.Add($"Webhook {w.Url} rejected: {err}"); continue; }
                var unknown = w.EventTypes.Where(e => e != "*" && !WebhookEventTypes.All.Contains(e)).ToList();
                if (unknown.Count > 0 || w.EventTypes.Count == 0)
                {
                    result.Warnings.Add($"Webhook {w.Url} rejected: unknown event type(s) {string.Join(", ", unknown)}; use {string.Join(", ", WebhookEventTypes.All)}.");
                    continue;
                }
                if (await db.WebhookSubscriptions.AnyAsync(s => s.ApplicationId == app.Id && s.Url == w.Url, ctx.RequestAborted)) continue;
                var secret = Hashing.NewSecret("whsec_");
                db.WebhookSubscriptions.Add(new WebhookSubscription { TenantId = tid, ApplicationId = app.Id, Name = "manifest", Url = w.Url, EventTypes = w.EventTypes, SecretCipher = protector.Protect(secret, Purposes.WebhookSecret) });
                result.WebhookSecrets[w.Url] = secret;
                result.WebhooksRegistered++;
            }

            if (manifest.Credential is { } credReq && !await db.ApplicationCredentials.AnyAsync(c => c.ApplicationId == app.Id && c.RevokedAt == null, ctx.RequestAborted))
            {
                var scopes = credReq.Scopes ?? [.. Scopes.ApplicationDefaults];
                if (scopes.Except(Scopes.All).Any() || scopes.Contains(Scopes.ApplicationsRegister)) throw HubException.Validation("Invalid credential scopes in manifest.");
                var secret = Hashing.NewSecret("wnhs_");
                var cred = new ApplicationCredential
                {
                    TenantId = tid, ApplicationId = app.Id, Name = credReq.Name, ClientId = $"{code.ToLowerInvariant()}-{Hashing.NewSecret("", 6).ToLowerInvariant()}",
                    SecretHash = Hashing.HashSecret(secret), Scopes = scopes, CreatedBy = caller.ActorId,
                };
                db.ApplicationCredentials.Add(cred);
                result.ClientId = cred.ClientId;
                result.ClientSecret = secret;
            }
            audit.Record(caller, "application.manifest_registered", nameof(App), app.Id.ToString(),
                $"{code}: {result.NotificationTypesUpserted} types, {result.TemplatesRegistered} templates, {result.WebhooksRegistered} webhooks", tid, null, app.Id);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(result);
        }).WithTags("Wings Code integration").RequireAuthorization("app-or-admin")
          .WithSummary("Register or update an application from its notification manifest (idempotent)");
    }

    private static void Validate(NotificationManifest m)
    {
        var errors = new List<string>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(m.Application.Code ?? "", "^[A-Za-z0-9_.-]{2,100}$")) errors.Add("application.code must be 2-100 characters [A-Za-z0-9_.-].");
        if (string.IsNullOrWhiteSpace(m.Application.Name)) errors.Add("application.name is required.");
        if (m.Templates.Count > 500) errors.Add("At most 500 templates per manifest.");
        foreach (var t in m.Templates.Where(t => !m.Channels.Contains(t.Channel) && m.Channels.Count > 0)) errors.Add($"Template {t.Code} uses channel {ChannelNames.ToWire(t.Channel)} which is not in channels.");
        if (errors.Count > 0) throw HubException.Validation("The manifest is invalid.", new() { ["manifest"] = errors.ToArray() });
    }
}
