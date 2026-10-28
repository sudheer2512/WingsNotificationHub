using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Data;
using App = Wings.NotificationHub.Core.Domain.Application;

namespace Wings.NotificationHub.Infrastructure.Services;

public sealed record TenantContext(Tenant Tenant, Company Company, App Application, CompanyApplication Link);

/// <summary>
/// Resolves and authorizes the tenant/company/application context of an operation from the authenticated caller.
/// Payload ids are only accepted when they match what the credential permits.
/// </summary>
public sealed class TenantContextResolver(NotificationHubDbContext db)
{
    public async Task<TenantContext> ResolveAsync(CallerContext caller, Guid? applicationId, Guid companyId, Permission permission, string? requiredScope, CancellationToken ct, bool allowSuspended = false)
    {
        Guid appId;
        if (caller.Kind == CallerKind.Application)
        {
            if (caller.ApplicationId is not { } bound)
                throw new AccessDeniedException("This credential is not bound to an application.");
            if (applicationId is { } requested && requested != Guid.Empty && requested != bound)
                throw new AccessDeniedException("The applicationId does not match the authenticated application.");
            if (requiredScope is not null && !caller.HasScope(requiredScope))
                throw new AccessDeniedException($"The credential lacks the '{requiredScope}' scope.");
            appId = bound;
        }
        else
        {
            appId = applicationId is { } a && a != Guid.Empty ? a : throw HubException.Validation("applicationId is required.");
        }
        if (companyId == Guid.Empty) throw HubException.Validation("companyId is required.");

        var app = await db.Applications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == appId, ct) ?? throw HubException.NotFound("Application");
        if (caller.Kind == CallerKind.Application && caller.TenantId != app.TenantId) throw new AccessDeniedException();
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == companyId, ct);
        // Unknown company and a company of another tenant look the same to the caller (no cross-tenant probing).
        if (company is null || company.TenantId != app.TenantId) throw HubException.NotFound("Company");
        if (!caller.Can(permission, app.TenantId, company.Id, app.Id)) throw new AccessDeniedException();

        var link = await db.CompanyApplications.AsNoTracking().FirstOrDefaultAsync(l => l.CompanyId == company.Id && l.ApplicationId == app.Id, ct);
        if (link is null || !link.Enabled) throw new AccessDeniedException("The application is not enabled for this company.");
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == app.TenantId, ct);
        if (!allowSuspended)
        {
            if (tenant.Status != EntityStatus.Active) throw new HubException(403, "tenant_suspended", "The tenant is suspended.");
            if (company.Status != EntityStatus.Active) throw new HubException(403, "company_suspended", "The company is suspended.");
            if (app.Status != EntityStatus.Active) throw new HubException(403, "application_suspended", "The application is suspended.");
        }
        return new TenantContext(tenant, company, app, link);
    }

    /// <summary>Notification types visible to an application: application-specific first, then tenant-wide.</summary>
    public async Task<NotificationType?> FindNotificationTypeAsync(Guid tenantId, Guid applicationId, string code, CancellationToken ct)
        => await db.NotificationTypes.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.Code == code && (t.ApplicationId == applicationId || t.ApplicationId == null))
            .OrderByDescending(t => t.ApplicationId != null).FirstOrDefaultAsync(ct);
}

public static class ChannelSupport
{
    public static bool Allowed(App app, Channel c) => app.AllowedChannels.Count == 0 || app.AllowedChannels.Contains(c);
}
