using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Providers;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Api.Endpoints;

public sealed class ProviderAccountRequest
{
    public string Name { get; set; } = "";
    public Channel Channel { get; set; }
    public string ProviderType { get; set; } = "";
    public ProviderScope Scope { get; set; } = ProviderScope.Tenant;
    public Guid? TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public JsonObject? Settings { get; set; }
    /// <summary>Write-only. Omitted keys keep their stored value; empty string removes a key.</summary>
    public Dictionary<string, string>? Secrets { get; set; }
    public int Priority { get; set; } = 100;
    public double? RateLimitPerSecond { get; set; }
    public int? MonthlyCapacity { get; set; }
    public List<string>? AllowedCountryPrefixes { get; set; }
    public decimal? UnitCost { get; set; }
    public string Currency { get; set; } = "INR";
    public Guid? FailoverProviderAccountId { get; set; }
    public DuplicateRiskPolicy DuplicateRiskPolicy { get; set; } = DuplicateRiskPolicy.Never;
    public int TimeoutSeconds { get; set; } = 30;
    public bool Enabled { get; set; } = true;
}

public sealed record SenderIdentityRequest(Guid? TenantId, Guid? CompanyId, Channel Channel, string Address, string? DisplayName, string? ReplyTo, string? Signature,
    string? DltEntityId, string? WhatsAppPhoneNumberId, string? WhatsAppBusinessAccountId, bool IsDefault);
public sealed record RoutingRuleRequest(Guid? TenantId, Guid? CompanyId, Guid? ApplicationId, Channel Channel, string? CountryPrefix, MessageClass? MessageClass, Guid ProviderAccountId, int Order, bool Enabled = true);
public sealed record SuspendRequest(bool Suspended, string? Reason);
public sealed record ProviderTestRequest(string To, Guid? CompanyId);

public static class ProviderEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/providers/health", async (HttpContext ctx, NotificationHubDbContext db, CircuitBreakerRegistry circuits) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.HasScope(Scopes.ProvidersHealth)) throw new AccessDeniedException();
            var accounts = await VisibleAccounts(db, caller, Permission.ViewProviders).ToListAsync(ctx.RequestAborted);
            var snapshot = circuits.Snapshot();
            return Results.Ok(accounts.Select(a => new
            {
                a.Id, a.Name, channel = ChannelNames.ToWire(a.Channel), a.ProviderType, a.Scope, a.HealthStatus, a.LastHealthCheckAt, a.LastHealthDetail, a.Enabled, a.Suspended,
                circuit = snapshot.TryGetValue(a.Id, out var c) ? new { c.Failures, c.OpenUntil } : null,
            }));
        }).WithTags("Providers").RequireAuthorization("app-or-admin").WithSummary("Provider health (applications see shared and own-tenant providers)");

        var g = api.MapGroup("/providers").WithTags("Providers").RequireAuthorization("admin");
        g.MapGet("/types", (ProviderRegistry registry) => Results.Ok(registry.Factories.OrderBy(f => f.DisplayName).Select(f => new
        {
            f.ProviderType, f.DisplayName, channels = f.Channels.Select(ChannelNames.ToWire), f.SecretKeys, f.SettingKeys,
        }))).WithSummary("Available provider adapters");

        g.MapGet("", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            return Results.Ok((await VisibleAccounts(db, caller, Permission.ViewProviders).OrderBy(a => a.Channel).ThenBy(a => a.Priority).ToListAsync(ctx.RequestAborted)).Select(Dto));
        });

        g.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var a = await Load(ctx, db, id, Permission.ViewProviders);
            var senders = await db.SenderIdentities.AsNoTracking().Where(s => s.ProviderAccountId == id).ToListAsync(ctx.RequestAborted);
            var health = await db.ProviderHealthEvents.AsNoTracking().Where(h => h.ProviderAccountId == id).OrderByDescending(h => h.OccurredAt).Take(50).ToListAsync(ctx.RequestAborted);
            var since = DateTime.UtcNow.AddDays(-1);
            var stats = await db.DeliveryAttempts.AsNoTracking().Where(x => x.ProviderAccountId == id && x.StartedAt >= since).GroupBy(x => x.Outcome)
                .Select(x => new { outcome = x.Key, count = x.Count(), avgMs = x.Average(y => y.DurationMs) }).ToListAsync(ctx.RequestAborted);
            return Results.Ok(new { account = Dto(a), senders, health, last24h = stats, callbackUrl = $"/api/v1/callbacks/{a.Id}" });
        });

        g.MapPost("", async (ProviderAccountRequest req, HttpContext ctx, NotificationHubDbContext db, ISecretProtector protector, ProviderRegistry registry, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            Authorize(caller, req.Scope, req.TenantId, req.CompanyId);
            var factory = registry.Factory(req.ProviderType) ?? throw HubException.Validation($"Unknown provider type '{req.ProviderType}'.");
            if (!factory.Channels.Contains(req.Channel)) throw HubException.Validation($"{factory.DisplayName} does not support {ChannelNames.ToWire(req.Channel)}.");
            var a = new ProviderAccount { ProviderType = factory.ProviderType, CallbackSecretCipher = protector.Protect(Hashing.NewSecret("cb_"), Purposes.CallbackSecret) };
            Apply(a, req);
            ApplySecrets(a, req.Secrets, protector);
            db.ProviderAccounts.Add(a);
            audit.Record(caller, "provider.created", nameof(ProviderAccount), a.Id.ToString(), $"{a.Name} ({a.ProviderType}/{ChannelNames.ToWire(a.Channel)}); secrets: {string.Join(",", a.SecretKeys)}", a.TenantId, a.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/providers/{a.Id}", Dto(a));
        }).WithSummary("Add a provider account (secrets are encrypted and never returned)");

        g.MapPut("/{id:guid}", async (Guid id, ProviderAccountRequest req, HttpContext ctx, NotificationHubDbContext db, ISecretProtector protector, IConfiguration config, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var a = await Load(ctx, db, id, Permission.ManageProviders, tracked: true);
            Authorize(caller, req.Scope, req.TenantId, req.CompanyId);
            Apply(a, req);
            a.UpdatedAt = DateTime.UtcNow;
            object result = Dto(a);
            if (req.Secrets is { Count: > 0 })
            {
                if (config.GetValue("Security:RequireApprovalForCredentialChanges", true) && a.Id != Guid.Empty)
                {
                    // Maker-checker for credential changes: the new secrets wait (encrypted) for a second administrator.
                    var approval = new ApprovalRequest
                    {
                        TenantId = a.TenantId ?? Guid.Empty, Kind = ApprovalKind.ProviderCredentials, EntityType = nameof(ProviderAccount), EntityId = a.Id,
                        Title = $"Credential change for provider '{a.Name}' ({string.Join(", ", req.Secrets.Keys)})", RequestedBy = caller.ActorId, RequestedByName = caller.ActorName,
                        PayloadJson = protector.Protect(JsonSerializer.Serialize(req.Secrets), Purposes.Approval), PayloadEncrypted = true,
                    };
                    db.ApprovalRequests.Add(approval);
                    a.PendingApprovalId = approval.Id;
                    result = new { account = Dto(a), pendingApprovalId = approval.Id, note = "Settings saved. Credential changes take effect after approval by another administrator." };
                }
                else ApplySecrets(a, req.Secrets, protector);
            }
            audit.Record(caller, "provider.updated", nameof(ProviderAccount), a.Id.ToString(), a.Name + (req.Secrets is { Count: > 0 } ? $"; secret keys changed: {string.Join(",", req.Secrets.Keys)}" : ""), a.TenantId, a.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(result);
        }).WithSummary("Update a provider account (credential changes require approval)");

        g.MapPost("/{id:guid}/suspend", async (Guid id, SuspendRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var a = await Load(ctx, db, id, Permission.EmergencySuspend, tracked: true);
            a.Suspended = req.Suspended; a.SuspendedReason = req.Reason;
            audit.Record(caller, req.Suspended ? "provider.suspended" : "provider.resumed", nameof(ProviderAccount), id.ToString(), req.Reason, a.TenantId, a.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(Dto(a));
        }).WithSummary("Emergency suspend/resume a provider account");

        g.MapPost("/{id:guid}/test", async (Guid id, ProviderTestRequest? req, HttpContext ctx, NotificationHubDbContext db, ProviderRegistry registry, CircuitBreakerRegistry circuits) =>
        {
            var a = await Load(ctx, db, id, Permission.ManageProviders);
            var provider = registry.Get(a);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var health = await provider.CheckHealthAsync(cts.Token);
            object? send = null;
            if (!string.IsNullOrWhiteSpace(req?.To))
            {
                var r = await provider.SendAsync(new NotificationMessage
                {
                    DeliveryId = Guid.NewGuid(), TenantId = a.TenantId ?? Guid.Empty, Channel = a.Channel, To = req.To, IdempotencyKey = "test-" + Guid.NewGuid().ToString("N"),
                    Content = new RenderedContent { Subject = "Wings Notification Hub connectivity test", Title = "Connectivity test", Body = "This is a connectivity test from Wings Notification Hub." },
                }, cts.Token);
                send = new { r.Outcome, r.ProviderMessageId, r.ResponseCode, summary = Redact.Sanitize(r.ResponseSummary), r.ErrorCode, error = Redact.Sanitize(r.ErrorMessage) };
            }
            if (health.Status == ProviderHealthStatus.Healthy) circuits.Reset(a.Id);
            return Results.Ok(new { health, send });
        }).WithSummary("Test connectivity (and optionally send a test message)");

        g.MapPost("/{id:guid}/senders", async (Guid id, SenderIdentityRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var a = await Load(ctx, db, id, Permission.ManageProviders);
            if (req.TenantId is { } t && !caller.Can(Permission.ManageProviders, t, req.CompanyId)) throw new AccessDeniedException();
            if (req.Signature is not null && Core.Templates.TemplateEngine.FindUnsafeHtml(req.Signature) is { } bad) throw HubException.Validation(bad);
            var s = new SenderIdentity
            {
                ProviderAccountId = a.Id, TenantId = req.TenantId ?? a.TenantId, CompanyId = req.CompanyId ?? a.CompanyId, Channel = a.Channel, Address = req.Address.Trim(),
                DisplayName = req.DisplayName, ReplyTo = req.ReplyTo, Signature = req.Signature, DltEntityId = req.DltEntityId, WhatsAppPhoneNumberId = req.WhatsAppPhoneNumberId,
                WhatsAppBusinessAccountId = req.WhatsAppBusinessAccountId, IsDefault = req.IsDefault,
            };
            db.SenderIdentities.Add(s);
            audit.Record(caller, "sender.created", nameof(SenderIdentity), s.Id.ToString(), $"{s.Address} on {a.Name}", s.TenantId, s.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/providers/{id}/senders/{s.Id}", s);
        });

        g.MapPost("/senders/{senderId:guid}/suspend", async (Guid senderId, SuspendRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var s = await db.SenderIdentities.FirstOrDefaultAsync(x => x.Id == senderId, ctx.RequestAborted) ?? throw HubException.NotFound("Sender");
            var a = await Load(ctx, db, s.ProviderAccountId, Permission.EmergencySuspend);
            if (s.TenantId is { } t && !caller.Can(Permission.EmergencySuspend, t, s.CompanyId)) throw new AccessDeniedException();
            s.Suspended = req.Suspended;
            audit.Record(caller, req.Suspended ? "sender.suspended" : "sender.resumed", nameof(SenderIdentity), senderId.ToString(), $"{s.Address}: {req.Reason}", s.TenantId ?? a.TenantId, s.CompanyId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(s);
        }).WithSummary("Emergency suspend/resume a sender identity");

        g.MapGet("/sender-guidance", (string domain) => Results.Ok(new
        {
            domain,
            spf = new { type = "TXT", host = domain, value = "v=spf1 include:<your-provider-spf-domain> ~all", note = "Merge with any existing SPF record; only one SPF record per domain." },
            dkim = new { type = "CNAME/TXT", host = $"<selector>._domainkey.{domain}", note = "Publish the DKIM key(s) issued by your provider (SES: 3 CNAMEs; M365: selector1/selector2 CNAMEs)." },
            dmarc = new { type = "TXT", host = $"_dmarc.{domain}", value = $"v=DMARC1; p=quarantine; rua=mailto:dmarc@{domain}; adkim=s; aspf=s", note = "Start with p=none to monitor, then tighten to quarantine/reject." },
            returnPath = "Use a custom MAIL FROM / bounce domain aligned with the From domain so SPF aligns for DMARC.",
        })).WithSummary("SPF/DKIM/DMARC configuration guidance for a sender domain");

        var routing = api.MapGroup("/routing-rules").WithTags("Providers").RequireAuthorization("admin");
        routing.MapGet("", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var rows = await db.ProviderRoutingRules.AsNoTracking().ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Where(r => r.TenantId is null ? caller.IsPlatformAdmin : caller.Can(Permission.ViewProviders, r.TenantId, r.CompanyId, r.ApplicationId)).OrderBy(r => r.Channel).ThenBy(r => r.Order));
        });
        routing.MapPost("", async (RoutingRuleRequest req, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            if (req.TenantId is null ? !caller.IsPlatformAdmin : !caller.Can(Permission.ManageProviders, req.TenantId, req.CompanyId, req.ApplicationId)) throw new AccessDeniedException();
            await Load(ctx, db, req.ProviderAccountId, Permission.ViewProviders);
            var r = new ProviderRoutingRule { TenantId = req.TenantId, CompanyId = req.CompanyId, ApplicationId = req.ApplicationId, Channel = req.Channel, CountryPrefix = req.CountryPrefix, MessageClass = req.MessageClass, ProviderAccountId = req.ProviderAccountId, Order = req.Order, Enabled = req.Enabled };
            db.ProviderRoutingRules.Add(r);
            audit.Record(caller, "routing_rule.created", nameof(ProviderRoutingRule), r.Id.ToString(), $"{ChannelNames.ToWire(r.Channel)} → {r.ProviderAccountId}", r.TenantId, r.CompanyId, r.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/routing-rules/{r.Id}", r);
        });
        routing.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var r = await db.ProviderRoutingRules.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted) ?? throw HubException.NotFound("Routing rule");
            if (r.TenantId is null ? !caller.IsPlatformAdmin : !caller.Can(Permission.ManageProviders, r.TenantId, r.CompanyId, r.ApplicationId)) throw HubException.NotFound("Routing rule");
            db.ProviderRoutingRules.Remove(r);
            audit.Record(caller, "routing_rule.deleted", nameof(ProviderRoutingRule), id.ToString(), null, r.TenantId, r.CompanyId, r.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });
    }

    /// <summary>Shared accounts are visible to everyone with the permission; tenant/company accounts only within scope.</summary>
    public static IQueryable<ProviderAccount> VisibleAccounts(NotificationHubDbContext db, CallerContext caller, Permission p)
    {
        var filter = caller.FilterFor(p);
        if (filter.IsUnrestricted) return db.ProviderAccounts.AsNoTracking();
        var tenants = filter.TenantIds.ToList();
        var companies = filter.Clauses.Where(c => c.CompanyIds is not null).SelectMany(c => c.CompanyIds!).ToList();
        var tenantWide = filter.Clauses.Where(c => c.CompanyIds is null).Select(c => c.TenantId).ToList();
        return db.ProviderAccounts.AsNoTracking().Where(a => a.Scope == ProviderScope.Shared
            || (a.Scope == ProviderScope.Tenant && a.TenantId != null && tenants.Contains(a.TenantId.Value))
            || (a.Scope == ProviderScope.Company && a.CompanyId != null && (companies.Contains(a.CompanyId.Value) || (a.TenantId != null && tenantWide.Contains(a.TenantId.Value)))));
    }

    private static void Authorize(CallerContext caller, ProviderScope scope, Guid? tenantId, Guid? companyId)
    {
        switch (scope)
        {
            case ProviderScope.Shared when !caller.IsPlatformAdmin: throw new AccessDeniedException("Only platform administrators manage shared providers.");
            case ProviderScope.Tenant when tenantId is null || !caller.Can(Permission.ManageProviders, tenantId): throw new AccessDeniedException();
            case ProviderScope.Company when tenantId is null || companyId is null || !caller.Can(Permission.ManageProviders, tenantId, companyId): throw new AccessDeniedException();
        }
    }

    private static async Task<ProviderAccount> Load(HttpContext ctx, NotificationHubDbContext db, Guid id, Permission p, bool tracked = false)
    {
        var caller = await ctx.CallerAsync();
        var a = await (tracked ? db.ProviderAccounts : db.ProviderAccounts.AsNoTracking()).FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
        if (a is null) throw HubException.NotFound("Provider account");
        var allowed = a.Scope switch
        {
            ProviderScope.Shared => RolePermissions.IsRead(p) ? caller.Grants.Count > 0 : caller.IsPlatformAdmin,
            ProviderScope.Tenant => caller.Can(p, a.TenantId),
            _ => caller.Can(p, a.TenantId, a.CompanyId),
        };
        if (!allowed) throw HubException.NotFound("Provider account");
        return a;
    }

    private static void Apply(ProviderAccount a, ProviderAccountRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw HubException.Validation("Name is required.");
        a.Name = req.Name.Trim(); a.Channel = req.Channel; a.Scope = req.Scope;
        a.TenantId = req.Scope == ProviderScope.Shared ? null : req.TenantId; a.CompanyId = req.Scope == ProviderScope.Company ? req.CompanyId : null;
        a.SettingsJson = (req.Settings ?? new JsonObject()).ToJsonString(); a.Priority = req.Priority; a.RateLimitPerSecond = req.RateLimitPerSecond;
        a.MonthlyCapacity = req.MonthlyCapacity; a.AllowedCountryPrefixes = req.AllowedCountryPrefixes ?? []; a.UnitCost = req.UnitCost; a.Currency = req.Currency;
        a.FailoverProviderAccountId = req.FailoverProviderAccountId == a.Id ? null : req.FailoverProviderAccountId; a.DuplicateRiskPolicy = req.DuplicateRiskPolicy;
        a.TimeoutSeconds = Math.Clamp(req.TimeoutSeconds, 1, 300); a.Enabled = req.Enabled;
    }

    public static void ApplySecrets(ProviderAccount a, Dictionary<string, string>? updates, ISecretProtector protector)
    {
        if (updates is null || updates.Count == 0) return;
        var current = string.IsNullOrEmpty(a.SecretsCipher) ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(a.SecretsCipher, Purposes.ProviderSecrets)) ?? [];
        foreach (var (k, v) in updates)
        {
            if (string.IsNullOrEmpty(v)) current.Remove(k);
            else current[k] = v;
        }
        a.SecretsCipher = current.Count == 0 ? null : protector.Protect(JsonSerializer.Serialize(current), Purposes.ProviderSecrets);
        a.SecretKeys = current.Keys.OrderBy(k => k).ToList();
    }

    public static object Dto(ProviderAccount a) => new
    {
        a.Id, a.Name, channel = ChannelNames.ToWire(a.Channel), a.ProviderType, a.Scope, a.TenantId, a.CompanyId, settings = JsonNode.Parse(a.SettingsJson),
        // Secrets are write-only: only the key names and a mask are ever returned.
        secrets = a.SecretKeys.ToDictionary(k => k, _ => "••••••••"), a.Priority, a.RateLimitPerSecond, a.MonthlyCapacity, a.AllowedCountryPrefixes, a.UnitCost, a.Currency,
        a.FailoverProviderAccountId, a.DuplicateRiskPolicy, a.TimeoutSeconds, a.Enabled, a.Suspended, a.SuspendedReason, a.HealthStatus, a.LastHealthCheckAt, a.LastHealthDetail,
        a.PendingApprovalId, a.CreatedAt, a.UpdatedAt,
    };
}
