using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.Core.Security;

/// <summary>A role grant narrowed to a scope. Null levels are wildcards beneath the levels that are set.</summary>
public sealed record ScopeGrant(string Role, Guid? TenantId, Guid? CompanyId, Guid? ApplicationId, Channel? Channel);

public enum CallerKind { Anonymous, AdminUser, Application, InboxUser, System }

/// <summary>
/// The authenticated caller. Tenant, company and application context always comes from here (the validated
/// credential), never from request payloads; payload ids are checked against it.
/// </summary>
public sealed class CallerContext
{
    public CallerKind Kind { get; init; }
    public string ActorId { get; init; } = "";
    public string? ActorName { get; init; }
    public IReadOnlyList<ScopeGrant> Grants { get; init; } = [];
    /// <summary>Application credentials: the token's scopes.</summary>
    public IReadOnlySet<string> Scopes { get; init; } = new HashSet<string>();
    /// <summary>Application credentials: the bound tenant/application and permitted companies (empty = all linked companies).</summary>
    public Guid? TenantId { get; init; }
    public Guid? ApplicationId { get; init; }
    public IReadOnlyList<Guid> AllowedCompanyIds { get; init; } = [];
    public Guid? CredentialId { get; init; }
    public bool IsAiAgent { get; init; }
    /// <summary>Inbox users: the end user id in the application.</summary>
    public string? UserId { get; init; }
    public Guid? CompanyId { get; init; }
    public string? IpAddress { get; init; }
    public string? CorrelationId { get; init; }
    public string? TraceId { get; init; }

    public static readonly CallerContext SystemCaller = new()
    {
        Kind = CallerKind.System, ActorId = "system", ActorName = "Notification Hub",
        Grants = [new ScopeGrant(Roles.PlatformAdmin, null, null, null, null)],
    };

    public bool IsPlatformAdmin => Grants.Any(g => g.Role == Roles.PlatformAdmin && g.TenantId is null);

    public bool HasScope(string scope) => Kind != CallerKind.Application || Scopes.Contains(scope);

    /// <summary>
    /// Whether the caller holds <paramref name="permission"/> on a resource at the given scope. A grant matches when every
    /// level it pins equals the resource's. For read permissions a narrower grant also sees broader resources of its
    /// tenant (e.g. a company administrator can read tenant-wide templates but not edit them).
    /// </summary>
    public bool Can(Permission permission, Guid? tenantId, Guid? companyId = null, Guid? applicationId = null, Channel? channel = null)
    {
        if (Kind == CallerKind.System) return true;
        var read = RolePermissions.IsRead(permission);
        foreach (var g in Grants)
        {
            if (!PermissionsFor(g).Contains(permission)) continue;
            if (g.TenantId is { } gt && gt != tenantId) continue;
            if (!LevelMatches(g.CompanyId, companyId, read)) continue;
            if (!LevelMatches(g.ApplicationId, applicationId, read)) continue;
            if (g.Channel is { } gc && channel is { } c && gc != c) continue;
            if (g.Channel is not null && channel is null && !read) continue;
            if (Kind == CallerKind.Application && companyId is { } cid && AllowedCompanyIds.Count > 0 && !AllowedCompanyIds.Contains(cid)) continue;
            return true;
        }
        return false;
    }

    private static bool LevelMatches(Guid? grant, Guid? resource, bool read)
        => grant is null || grant == resource || (read && resource is null);

    private HashSet<Permission> PermissionsFor(ScopeGrant g)
        => g.Role == Roles.ApiClient ? [.. RolePermissions.ForScopes(Scopes)] : [.. Enum.GetValues<Permission>().Where(p => RolePermissions.Has(g.Role, p))];

    /// <summary>The data the caller may see for a permission, used to filter list queries.</summary>
    public AccessFilter FilterFor(Permission permission)
    {
        if (Kind == CallerKind.System || Grants.Any(g => g.TenantId is null && PermissionsFor(g).Contains(permission)))
            return AccessFilter.Unrestricted;
        var clauses = new List<AccessClause>();
        foreach (var g in Grants.Where(g => g.TenantId is not null && PermissionsFor(g).Contains(permission)))
        {
            var companies = g.CompanyId is { } c ? [c] : Kind == CallerKind.Application && AllowedCompanyIds.Count > 0 ? AllowedCompanyIds : null;
            clauses.Add(new AccessClause(g.TenantId!.Value, companies, g.ApplicationId));
        }
        return new AccessFilter { Clauses = clauses };
    }
}

/// <summary>One visibility clause: rows of the tenant, optionally narrowed to companies and an application.</summary>
public sealed record AccessClause(Guid TenantId, IReadOnlyList<Guid>? CompanyIds, Guid? ApplicationId);

/// <summary>Resolved visibility for list queries (OR of clauses). Unrestricted for platform-level grants.</summary>
public sealed class AccessFilter
{
    public static readonly AccessFilter Unrestricted = new() { IsUnrestricted = true };
    public bool IsUnrestricted { get; init; }
    public IReadOnlyList<AccessClause> Clauses { get; init; } = [];
    public bool IsEmpty => !IsUnrestricted && Clauses.Count == 0;
    public IEnumerable<Guid> TenantIds => Clauses.Select(c => c.TenantId).Distinct();
}

public sealed class AccessDeniedException(string message = "You do not have access to this resource.") : Exception(message);

/// <summary>Business-rule violation surfaced to API callers as 4xx with a stable code.</summary>
public sealed class HubException(int status, string code, string message, Dictionary<string, string[]>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public Dictionary<string, string[]>? Errors { get; } = errors;

    public static HubException NotFound(string what) => new(404, "not_found", $"{what} was not found.");
    public static HubException Validation(string message, Dictionary<string, string[]>? errors = null) => new(422, "validation_failed", message, errors);
    public static HubException Conflict(string code, string message) => new(409, code, message);
    public static HubException BadRequest(string code, string message) => new(400, code, message);
}
