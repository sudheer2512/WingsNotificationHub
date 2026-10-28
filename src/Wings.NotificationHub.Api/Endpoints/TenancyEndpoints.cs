using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Policies;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Services;
using App = Wings.NotificationHub.Core.Domain.Application;

namespace Wings.NotificationHub.Api.Endpoints;

public sealed record TenantRequest(string Code, string Name, bool AllowSharedProviders = true, bool RequireMarketingConsent = true, string DefaultTimeZone = "Asia/Kolkata",
    JsonObject? Branding = null, int BodyRetentionDays = 90, int DataRetentionDays = 730);
public sealed record CompanyRequest(Guid TenantId, string Code, string Name, string? Country, string? TimeZone, JsonObject? Branding, QuietHoursPolicy? QuietHours);
public sealed record ApplicationRequest(Guid TenantId, string Code, string Name, string? Description, bool Sandbox, List<Channel>? AllowedChannels, string DefaultLocale = "en", int RateLimitPerMinute = 600);
public sealed record LinkRequest(Guid CompanyId, bool Enabled = true, Dictionary<string, Guid>? DefaultSenders = null);
public sealed record CredentialRequest(string Name, List<string>? Scopes, List<Guid>? AllowedCompanyIds, bool IsAiAgent, DateTime? ExpiresAt);
public sealed record UserRequest(string Email, string DisplayName, string? Password, List<RoleAssignmentRequest> Roles);
public sealed record RoleAssignmentRequest(string Role, Guid? TenantId, Guid? CompanyId, Guid? ApplicationId, Channel? Channel);
public sealed record QuotaRequest(Guid TenantId, Guid? CompanyId, Guid? ApplicationId, Channel? Channel, int MonthlyLimit, int AlertThresholdPercent = 80);
public sealed record CostRateRequest(Guid? ProviderAccountId, Channel Channel, string? CountryPrefix, MessageClass? MessageClass, decimal UnitCost, string Currency = "INR");
public sealed record BillingRateRequest(Guid? TenantId, Channel Channel, decimal? UnitPrice, decimal MarginPercent, string Currency = "INR");
public sealed record CreditTopUp(Guid TenantId, Guid? CompanyId, decimal Amount, decimal? LowBalanceThreshold, string? Note);
public sealed record DecisionRequest(bool Approve, string? Comment);

public static class TenancyEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Tenancy & administration").RequireAuthorization("admin");

        g.MapGet("/tenants", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var filter = caller.FilterFor(Permission.ViewTenancy);
            var rows = await db.Tenants.AsNoTracking().ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Where(t => filter.IsUnrestricted || filter.TenantIds.Contains(t.Id)).Select(t => new
            {
                t.Id, t.Code, t.Name, t.Status, t.SuspendedReason, t.AllowSharedProviders, t.RequireMarketingConsent, t.DefaultTimeZone,
                branding = Json.Deserialize<JsonObject>(t.BrandingJson), t.BodyRetentionDays, t.DataRetentionDays, t.CreatedAt,
            }));
        });
        g.MapPost("/tenants", async (TenantRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.IsPlatformAdmin) throw new AccessDeniedException("Only platform administrators create tenants.");
            if (await db.Tenants.AnyAsync(t => t.Code == req.Code.ToUpper(), ctx.RequestAborted)) throw HubException.Conflict("exists", "Tenant code already exists.");
            var t = new Tenant { Code = req.Code.Trim().ToUpperInvariant(), Name = req.Name };
            ApplyTenant(t, req);
            db.Tenants.Add(t);
            audit.Record(caller, "tenant.created", nameof(Tenant), t.Id.ToString(), t.Name, t.Id);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/tenants/{t.Id}", new { t.Id });
        });
        g.MapPut("/tenants/{id:guid}", async (Guid id, TenantRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.Can(Permission.ManageTenancy, id)) throw new AccessDeniedException();
            var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Tenant");
            t.Name = req.Name;
            ApplyTenant(t, req);
            audit.Record(caller, "tenant.updated", nameof(Tenant), id.ToString(), t.Name, id);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { t.Id });
        });

        g.MapGet("/companies", async (HttpContext ctx, NotificationHubDbContext db, Guid? tenantId) =>
        {
            var caller = await ctx.CallerAsync();
            var filter = caller.FilterFor(Permission.ViewTenancy);
            var q = db.Companies.AsNoTracking();
            if (tenantId is { } t) q = q.Where(c => c.TenantId == t);
            var rows = await q.OrderBy(c => c.Name).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Where(c => caller.Can(Permission.ViewTenancy, c.TenantId, c.Id)).Select(c => new
            {
                c.Id, c.TenantId, c.Code, c.Name, c.Country, c.TimeZone, c.Status, c.SuspendedReason, branding = Json.Deserialize<JsonObject>(c.BrandingJson),
                quietHours = Json.Deserialize<QuietHoursPolicy>(c.QuietHoursJson),
            }));
        });
        g.MapPost("/companies", async (CompanyRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.Can(Permission.ManageTenancy, req.TenantId)) throw new AccessDeniedException();
            var c = new Company { TenantId = req.TenantId, Code = req.Code.Trim().ToUpperInvariant(), Name = req.Name };
            ApplyCompany(c, req);
            db.Companies.Add(c);
            audit.Record(caller, "company.created", nameof(Company), c.Id.ToString(), c.Name, c.TenantId, c.Id);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/companies/{c.Id}", new { c.Id });
        });
        g.MapPut("/companies/{id:guid}", async (Guid id, CompanyRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var c = await db.Companies.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Company");
            if (!caller.Can(Permission.ManageTenancy, c.TenantId, c.Id)) throw HubException.NotFound("Company");
            c.Name = req.Name;
            ApplyCompany(c, req);
            audit.Record(caller, "company.updated", nameof(Company), id.ToString(), c.Name, c.TenantId, c.Id);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { c.Id });
        });

        g.MapGet("/applications", async (HttpContext ctx, NotificationHubDbContext db, Guid? tenantId) =>
        {
            var caller = await ctx.CallerAsync();
            var q = db.Applications.AsNoTracking();
            if (tenantId is { } t) q = q.Where(a => a.TenantId == t);
            var rows = (await q.OrderBy(a => a.Name).ToListAsync(ctx.RequestAborted)).Where(a => caller.Can(Permission.ViewTenancy, a.TenantId, null, a.Id)).ToList();
            var ids = rows.Select(a => a.Id).ToList();
            var links = await db.CompanyApplications.AsNoTracking().Where(l => ids.Contains(l.ApplicationId)).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Select(a => new
            {
                a.Id, a.TenantId, a.Code, a.Name, a.Description, a.Status, a.SuspendedReason, a.Sandbox, allowedChannels = a.AllowedChannels.Select(ChannelNames.ToWire), a.DefaultLocale, a.RateLimitPerMinute,
                companies = links.Where(l => l.ApplicationId == a.Id).Select(l => new { l.CompanyId, l.Enabled, defaultSenders = Json.Deserialize<Dictionary<string, Guid>>(l.DefaultSendersJson) }),
            }));
        });
        g.MapPost("/applications", async (ApplicationRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.Can(Permission.ManageTenancy, req.TenantId)) throw new AccessDeniedException();
            if (await db.Applications.AnyAsync(a => a.TenantId == req.TenantId && a.Code == req.Code.ToUpper(), ctx.RequestAborted)) throw HubException.Conflict("exists", "Application code already exists in the tenant.");
            var a = new App { TenantId = req.TenantId, Code = req.Code.Trim().ToUpperInvariant() };
            ApplyApp(a, req);
            db.Applications.Add(a);
            audit.Record(caller, "application.created", nameof(App), a.Id.ToString(), a.Name, a.TenantId, null, a.Id);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/applications/{a.Id}", new { a.Id });
        });
        g.MapPut("/applications/{id:guid}", async (Guid id, ApplicationRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Application");
            if (!caller.Can(Permission.ManageTenancy, a.TenantId, null, a.Id) && !caller.Can(Permission.ManageCredentials, a.TenantId, null, a.Id)) throw HubException.NotFound("Application");
            ApplyApp(a, req);
            audit.Record(caller, "application.updated", nameof(App), id.ToString(), a.Name, a.TenantId, null, a.Id);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { a.Id });
        });
        g.MapPut("/applications/{id:guid}/companies", async (Guid id, LinkRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var a = await db.Applications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Application");
            var c = await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.CompanyId && x.TenantId == a.TenantId, ctx.RequestAborted) ?? throw HubException.NotFound("Company");
            if (!caller.Can(Permission.ManageTenancy, a.TenantId, c.Id)) throw new AccessDeniedException();
            var link = await db.CompanyApplications.FirstOrDefaultAsync(l => l.CompanyId == c.Id && l.ApplicationId == id, ctx.RequestAborted);
            if (link is null) db.CompanyApplications.Add(link = new CompanyApplication { TenantId = a.TenantId, CompanyId = c.Id, ApplicationId = id });
            link.Enabled = req.Enabled;
            if (req.DefaultSenders is not null) link.DefaultSendersJson = Json.Serialize(req.DefaultSenders);
            audit.Record(caller, "application.company_linked", nameof(CompanyApplication), link.Id.ToString(), $"{a.Name} ↔ {c.Name} enabled={req.Enabled}", a.TenantId, c.Id, a.Id);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { link.Id, link.Enabled });
        }).WithSummary("Enable an application for a company (and choose default senders)");

        // ── credentials ──
        g.MapGet("/applications/{id:guid}/credentials", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var a = await db.Applications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Application");
            if (!caller.Can(Permission.ManageCredentials, a.TenantId, null, a.Id)) throw HubException.NotFound("Application");
            return Results.Ok(await db.ApplicationCredentials.AsNoTracking().Where(c => c.ApplicationId == id).OrderByDescending(c => c.CreatedAt)
                .Select(c => new { c.Id, c.Name, c.ClientId, c.Scopes, c.AllowedCompanyIds, c.IsAiAgent, c.ExpiresAt, c.RevokedAt, c.LastUsedAt, c.CreatedBy, c.CreatedAt }).ToListAsync(ctx.RequestAborted));
        });
        g.MapPost("/applications/{id:guid}/credentials", async (Guid id, CredentialRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var a = await db.Applications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Application");
            if (!caller.Can(Permission.ManageCredentials, a.TenantId, null, a.Id)) throw HubException.NotFound("Application");
            var scopes = req.Scopes ?? [.. Scopes.ApplicationDefaults];
            if (scopes.Except(Scopes.All).Any()) throw HubException.Validation("Unknown scope(s): " + string.Join(", ", scopes.Except(Scopes.All)));
            if (scopes.Contains(Scopes.ApplicationsRegister)) throw HubException.Validation("applications.register is only granted to tenant provisioning credentials.");
            var secret = Hashing.NewSecret("wnhs_");
            var cred = new ApplicationCredential
            {
                TenantId = a.TenantId, ApplicationId = a.Id, Name = req.Name, ClientId = $"{a.Code.ToLowerInvariant()}-{Hashing.NewSecret("", 6).ToLowerInvariant()}",
                SecretHash = Hashing.HashSecret(secret), Scopes = scopes, AllowedCompanyIds = req.AllowedCompanyIds ?? [], IsAiAgent = req.IsAiAgent, ExpiresAt = req.ExpiresAt, CreatedBy = caller.ActorId,
            };
            db.ApplicationCredentials.Add(cred);
            audit.Record(caller, "credential.created", nameof(ApplicationCredential), cred.Id.ToString(), $"{cred.ClientId} scopes={string.Join(' ', scopes)}", a.TenantId, null, a.Id);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { cred.Id, cred.ClientId, clientSecret = secret, note = "Store the secret now; it cannot be retrieved again." });
        }).WithSummary("Create a scoped client credential (secret shown once)");
        g.MapPost("/credentials/{id:guid}/revoke", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var c = await db.ApplicationCredentials.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Credential");
            if (!caller.Can(Permission.ManageCredentials, c.TenantId, null, c.ApplicationId)) throw HubException.NotFound("Credential");
            c.RevokedAt ??= DateTime.UtcNow;
            audit.Record(caller, "credential.revoked", nameof(ApplicationCredential), id.ToString(), c.ClientId, c.TenantId, null, c.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });

        // ── users & roles ──
        g.MapGet("/users", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var users = await db.AdminUsers.AsNoTracking().ToListAsync(ctx.RequestAborted);
            var roles = await db.RoleAssignments.AsNoTracking().ToListAsync(ctx.RequestAborted);
            return Results.Ok(users.Select(u => new { u.Id, u.Email, u.DisplayName, u.Enabled, u.LastLoginAt, external = u.ExternalSubject != null, roles = roles.Where(r => r.UserId == u.Id) })
                .Where(u => caller.IsPlatformAdmin || u.roles.Any(r => caller.Can(Permission.ManageUsers, r.TenantId, r.CompanyId))));
        });
        g.MapPost("/users", async (UserRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            foreach (var r in req.Roles)
            {
                if (!Roles.All.Contains(r.Role)) throw HubException.Validation($"Unknown role '{r.Role}'.");
                if (r.TenantId is null ? !caller.IsPlatformAdmin : !caller.Can(Permission.ManageUsers, r.TenantId, r.CompanyId)) throw new AccessDeniedException("You cannot grant a role outside your own scope.");
                if (r.Role == Roles.PlatformAdmin && !caller.IsPlatformAdmin) throw new AccessDeniedException();
            }
            var email = req.Email.Trim().ToLowerInvariant();
            var u = await db.AdminUsers.FirstOrDefaultAsync(x => x.Email == email, ctx.RequestAborted);
            if (u is null) db.AdminUsers.Add(u = new AdminUser { Email = email, DisplayName = req.DisplayName });
            u.DisplayName = req.DisplayName;
            if (!string.IsNullOrEmpty(req.Password))
            {
                if (req.Password.Length < 12) throw HubException.Validation("Passwords must be at least 12 characters.");
                u.PasswordHash = Hashing.HashPassword(req.Password);
            }
            foreach (var r in req.Roles)
                db.RoleAssignments.Add(new RoleAssignment { UserId = u.Id, Role = r.Role, TenantId = r.TenantId, CompanyId = r.CompanyId, ApplicationId = r.ApplicationId, Channel = r.Channel });
            audit.Record(caller, "user.upserted", nameof(AdminUser), u.Id.ToString(), $"{email}: {string.Join(", ", req.Roles.Select(r => r.Role))}", req.Roles.FirstOrDefault()?.TenantId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { u.Id });
        }).WithSummary("Create a user (local password or external OIDC by e-mail) and grant scoped roles");
        g.MapDelete("/users/roles/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var r = await db.RoleAssignments.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Role assignment");
            if (r.TenantId is null ? !caller.IsPlatformAdmin : !caller.Can(Permission.ManageUsers, r.TenantId, r.CompanyId)) throw new AccessDeniedException();
            db.RoleAssignments.Remove(r);
            audit.Record(caller, "user.role_removed", nameof(RoleAssignment), id.ToString(), r.Role, r.TenantId, r.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });

        // ── quotas, rates, credits ──
        g.MapGet("/quotas", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var since = Periods.StartOf(DateTime.UtcNow);
            var quotas = await db.TenantQuotas.AsNoTracking().Visible(caller.FilterFor(Permission.ViewUsage), includeBroader: true).ToListAsync(ctx.RequestAborted);
            var result = new List<object>();
            foreach (var q in quotas)
            {
                var used = db.NotificationDeliveries.Where(d => d.TenantId == q.TenantId && d.CreatedAt >= since && d.Status != DeliveryStatus.Suppressed && d.Status != DeliveryStatus.Cancelled);
                if (q.CompanyId is { } c) used = used.Where(d => d.CompanyId == c);
                if (q.ApplicationId is { } a) used = used.Where(d => d.ApplicationId == a);
                if (q.Channel is { } ch) used = used.Where(d => d.Channel == ch);
                result.Add(new { q.Id, q.TenantId, q.CompanyId, q.ApplicationId, channel = q.Channel is { } x ? ChannelNames.ToWire(x) : null, q.MonthlyLimit, q.AlertThresholdPercent, used = await used.CountAsync(ctx.RequestAborted) });
            }
            return Results.Ok(result);
        });
        g.MapPost("/quotas", async (QuotaRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.Can(Permission.ManageQuotas, req.TenantId, req.CompanyId, req.ApplicationId)) throw new AccessDeniedException();
            var q = await db.TenantQuotas.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.CompanyId == req.CompanyId && x.ApplicationId == req.ApplicationId && x.Channel == req.Channel, ctx.RequestAborted);
            if (q is null) db.TenantQuotas.Add(q = new TenantQuota { TenantId = req.TenantId, CompanyId = req.CompanyId, ApplicationId = req.ApplicationId, Channel = req.Channel });
            q.MonthlyLimit = Math.Max(0, req.MonthlyLimit); q.AlertThresholdPercent = Math.Clamp(req.AlertThresholdPercent, 0, 100);
            audit.Record(caller, "quota.set", nameof(TenantQuota), q.Id.ToString(), $"{q.MonthlyLimit}/month", req.TenantId, req.CompanyId, req.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(q);
        });
        g.MapDelete("/quotas/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var q = await db.TenantQuotas.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Quota");
            if (!caller.Can(Permission.ManageQuotas, q.TenantId, q.CompanyId, q.ApplicationId)) throw HubException.NotFound("Quota");
            db.TenantQuotas.Remove(q);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });
        g.MapGet("/cost-rates", async (NotificationHubDbContext db, HttpContext ctx) => Results.Ok(await db.CostRates.AsNoTracking().OrderBy(r => r.Channel).ToListAsync(ctx.RequestAborted)));
        g.MapPost("/cost-rates", async (CostRateRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.IsPlatformAdmin) throw new AccessDeniedException("Provider cost rates are managed by platform administrators.");
            var r = new CostRate { ProviderAccountId = req.ProviderAccountId, Channel = req.Channel, CountryPrefix = req.CountryPrefix, MessageClass = req.MessageClass, UnitCost = req.UnitCost, Currency = req.Currency };
            db.CostRates.Add(r);
            audit.Record(caller, "cost_rate.added", nameof(CostRate), r.Id.ToString(), $"{ChannelNames.ToWire(r.Channel)} {r.CountryPrefix} {r.UnitCost} {r.Currency}");
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(r);
        });
        g.MapGet("/billing-rates", async (NotificationHubDbContext db, HttpContext ctx) =>
        {
            var caller = await ctx.CallerAsync();
            var rows = await db.BillingRates.AsNoTracking().ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Where(r => r.TenantId is null || caller.Can(Permission.ViewUsage, r.TenantId)));
        });
        g.MapPost("/billing-rates", async (BillingRateRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.IsPlatformAdmin) throw new AccessDeniedException("Customer billing rates are managed by platform administrators.");
            var r = await db.BillingRates.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Channel == req.Channel, ctx.RequestAborted);
            if (r is null) db.BillingRates.Add(r = new BillingRate { TenantId = req.TenantId, Channel = req.Channel });
            r.UnitPrice = req.UnitPrice; r.MarginPercent = req.MarginPercent; r.Currency = req.Currency;
            audit.Record(caller, "billing_rate.set", nameof(BillingRate), r.Id.ToString(), $"{ChannelNames.ToWire(r.Channel)} price={r.UnitPrice} margin={r.MarginPercent}%", req.TenantId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(r);
        });
        g.MapGet("/credits", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var rows = await db.CreditAccounts.AsNoTracking().Visible(caller.FilterFor(Permission.ViewUsage), includeBroader: true).ToListAsync(ctx.RequestAborted);
            var ids = rows.Select(r => r.Id).ToList();
            var tx = await db.CreditTransactions.AsNoTracking().Where(t => ids.Contains(t.AccountId)).OrderByDescending(t => t.CreatedAt).Take(200).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Select(a => new { a.Id, a.TenantId, a.CompanyId, a.Balance, a.Reserved, available = a.Balance - a.Reserved, a.LowBalanceThreshold, a.Currency, a.Enabled, transactions = tx.Where(t => t.AccountId == a.Id).Take(20) }));
        });
        g.MapPost("/credits/top-up", async (CreditTopUp req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.IsPlatformAdmin) throw new AccessDeniedException("Credits are added by platform administrators (billing).");
            if (req.Amount <= 0) throw HubException.Validation("Amount must be positive.");
            var a = await db.CreditAccounts.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.CompanyId == req.CompanyId, ctx.RequestAborted);
            if (a is null) db.CreditAccounts.Add(a = new CreditAccount { TenantId = req.TenantId, CompanyId = req.CompanyId });
            a.Balance += req.Amount;
            if (req.LowBalanceThreshold is { } th) a.LowBalanceThreshold = th;
            db.CreditTransactions.Add(new CreditTransaction { AccountId = a.Id, Kind = CreditTransactionKind.TopUp, Amount = req.Amount, Note = req.Note, Actor = caller.ActorId });
            audit.Record(caller, "credits.top_up", nameof(CreditAccount), a.Id.ToString(), $"+{req.Amount}", req.TenantId, req.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { a.Id, a.Balance });
        });

        // ── emergency suspension ──
        g.MapPost("/suspend/{kind}/{id:guid}", async (string kind, Guid id, SuspendRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var status = req.Suspended ? EntityStatus.Suspended : EntityStatus.Active;
            Guid tenantId;
            switch (kind)
            {
                case "tenant":
                    var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Tenant");
                    if (!caller.IsPlatformAdmin) throw new AccessDeniedException("Only platform administrators suspend tenants.");
                    t.Status = status; t.SuspendedReason = req.Reason; tenantId = t.Id;
                    break;
                case "company":
                    var c = await db.Companies.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Company");
                    if (!caller.Can(Permission.EmergencySuspend, c.TenantId, c.Id)) throw HubException.NotFound("Company");
                    c.Status = status; c.SuspendedReason = req.Reason; tenantId = c.TenantId;
                    break;
                case "application":
                    var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Application");
                    if (!caller.Can(Permission.EmergencySuspend, a.TenantId, null, a.Id)) throw HubException.NotFound("Application");
                    a.Status = status; a.SuspendedReason = req.Reason; tenantId = a.TenantId;
                    break;
                case "campaign":
                    var cp = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Campaign");
                    if (!caller.Can(Permission.EmergencySuspend, cp.TenantId, cp.CompanyId, cp.ApplicationId) && !caller.Can(Permission.ApproveCampaigns, cp.TenantId, cp.CompanyId)) throw HubException.NotFound("Campaign");
                    cp.Suspended = req.Suspended; tenantId = cp.TenantId;
                    break;
                default: throw HubException.Validation("kind must be tenant, company, application or campaign (providers and senders have their own endpoints).");
            }
            audit.Record(caller, $"{kind}.{(req.Suspended ? "suspended" : "resumed")}", kind, id.ToString(), req.Reason, tenantId);
            if (req.Suspended)
                db.SystemAlerts.Add(new SystemAlert { TenantId = tenantId, Severity = "critical", Source = "emergency", Message = $"{kind} {id} suspended by {caller.ActorName}: {req.Reason}" });
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { kind, id, suspended = req.Suspended });
        }).WithSummary("Emergency suspension: pending messages are held (not lost) until resumed");

        // ── approvals inbox (maker-checker) ──
        g.MapGet("/approvals", async (HttpContext ctx, NotificationHubDbContext db, ApprovalStatus? status) =>
        {
            var caller = await ctx.CallerAsync();
            var rows = await db.ApprovalRequests.AsNoTracking().Where(a => a.Status == (status ?? ApprovalStatus.Pending)).OrderByDescending(a => a.RequestedAt).Take(200).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Where(a => caller.IsPlatformAdmin || caller.Can(ApprovalPermission(a.Kind), a.TenantId)).Select(a => new
            {
                a.Id, a.TenantId, a.Kind, a.EntityType, a.EntityId, a.Title, a.Status, a.RequestedBy, a.RequestedByName, a.RequestedAt, a.DecidedBy, a.DecidedAt, a.Comment,
                canDecide = a.RequestedBy != caller.ActorId,
            }));
        });
        g.MapPost("/approvals/{id:guid}/decide", async (Guid id, DecisionRequest req, HttpContext ctx, NotificationHubDbContext db, ISecretProtector protector,
            TemplateService templates, CampaignService campaigns, NotificationService notifications, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var a = await db.ApprovalRequests.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Approval");
            if (a.Status != ApprovalStatus.Pending) throw HubException.Conflict("decided", "This request was already decided.");
            if (!caller.IsPlatformAdmin && !caller.Can(ApprovalPermission(a.Kind), a.TenantId)) throw new AccessDeniedException();
            if (a.RequestedBy == caller.ActorId) throw new HubException(403, "maker_checker", "You cannot approve your own request.");
            switch (a.Kind)
            {
                case ApprovalKind.TemplateVersion:
                    var v = await db.TemplateVersions.AsNoTracking().FirstAsync(x => x.Id == a.EntityId, ctx.RequestAborted);
                    await templates.ReviewAsync(caller, v.TemplateId, v.VersionNumber, req.Approve, req.Comment, true, ctx.RequestAborted);
                    return Results.Ok(new { decided = true });
                case ApprovalKind.Campaign:
                    var c = await db.Campaigns.FirstAsync(x => x.Id == a.EntityId, ctx.RequestAborted);
                    if (req.Approve) await campaigns.ApproveAsync(caller, c, ctx.RequestAborted);
                    else { c.Status = CampaignStatus.Draft; a.Status = ApprovalStatus.Rejected; a.DecidedBy = caller.ActorId; a.DecidedAt = DateTime.UtcNow; a.Comment = req.Comment; }
                    break;
                case ApprovalKind.ProviderCredentials:
                    var p = await db.ProviderAccounts.FirstAsync(x => x.Id == a.EntityId, ctx.RequestAborted);
                    if (req.Approve)
                        ProviderEndpoints.ApplySecrets(p, JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(a.PayloadJson!, Purposes.Approval)), protector);
                    p.PendingApprovalId = null;
                    a.PayloadJson = null; // discard the pending secret material either way
                    break;
                case ApprovalKind.AiNotification:
                    if (req.Approve)
                    {
                        var pending = JsonSerializer.Deserialize<PendingAiNotification>(protector.Unprotect(a.PayloadJson!, Purposes.Approval))!;
                        var cred = await db.ApplicationCredentials.AsNoTracking().FirstAsync(x => x.Id == pending.CredentialId, ctx.RequestAborted);
                        var agent = new CallerContext
                        {
                            Kind = CallerKind.Application, ActorId = $"credential:{cred.Id}", ActorName = $"{cred.Name} (approved by {caller.ActorName})", CredentialId = cred.Id,
                            TenantId = cred.TenantId, ApplicationId = cred.ApplicationId, AllowedCompanyIds = cred.AllowedCompanyIds, Scopes = cred.Scopes.ToHashSet(), IsAiAgent = true,
                            Grants = [new ScopeGrant(Roles.ApiClient, cred.TenantId, null, cred.ApplicationId, null)],
                        };
                        await notifications.AcceptAsync(agent, pending.Request, RequestSource.Mcp, new AcceptOptions { ApprovedRequestId = a.EntityId }, ctx.RequestAborted);
                    }
                    else
                    {
                        var r = await db.NotificationRequests.FirstAsync(x => x.Id == a.EntityId, ctx.RequestAborted);
                        r.Status = RequestStatus.Rejected;
                        r.CompletedAt = DateTime.UtcNow;
                    }
                    a.PayloadJson = null;
                    break;
            }
            if (a.Status == ApprovalStatus.Pending)
            {
                a.Status = req.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
                a.DecidedBy = caller.ActorId; a.DecidedAt = DateTime.UtcNow; a.Comment = req.Comment;
            }
            audit.Record(caller, $"approval.{(req.Approve ? "approved" : "rejected")}", a.EntityType, a.EntityId.ToString(), a.Title, a.TenantId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { decided = true, a.Status });
        }).WithSummary("Approve or reject a pending maker-checker request");
    }

    private static Permission ApprovalPermission(ApprovalKind k) => k switch
    {
        ApprovalKind.TemplateVersion => Permission.ApproveTemplates,
        ApprovalKind.Campaign => Permission.ApproveCampaigns,
        ApprovalKind.ProviderCredentials => Permission.ApproveProviderChanges,
        _ => Permission.SendNotifications,
    };

    private static void ApplyTenant(Tenant t, TenantRequest r)
    {
        t.AllowSharedProviders = r.AllowSharedProviders; t.RequireMarketingConsent = r.RequireMarketingConsent; t.DefaultTimeZone = r.DefaultTimeZone;
        t.BrandingJson = r.Branding?.ToJsonString(); t.BodyRetentionDays = Math.Clamp(r.BodyRetentionDays, 1, 3650); t.DataRetentionDays = Math.Clamp(r.DataRetentionDays, 30, 3650);
        if (r.Branding?["emailFooterHtml"]?.ToString() is { } footer && Core.Templates.TemplateEngine.FindUnsafeHtml(footer) is { } bad) throw HubException.Validation(bad);
    }

    private static void ApplyCompany(Company c, CompanyRequest r)
    {
        c.Country = r.Country; c.TimeZone = r.TimeZone; c.BrandingJson = r.Branding?.ToJsonString(); c.QuietHoursJson = r.QuietHours is null ? null : Json.Serialize(r.QuietHours);
        if (r.TimeZone is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZone, out _)) throw HubException.Validation($"Unknown time zone '{r.TimeZone}'.");
    }

    private static void ApplyApp(App a, ApplicationRequest r)
    {
        a.Name = r.Name; a.Description = r.Description; a.Sandbox = r.Sandbox; a.AllowedChannels = r.AllowedChannels ?? [];
        a.DefaultLocale = r.DefaultLocale; a.RateLimitPerMinute = Math.Clamp(r.RateLimitPerMinute, 1, 1_000_000);
    }
}
