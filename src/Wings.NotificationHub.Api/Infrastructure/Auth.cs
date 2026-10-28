using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.Api.Infrastructure;

public static class HubClaims
{
    public const string Kind = "wnh_kind";
    public const string Tenant = "wnh_tenant";
    public const string Application = "wnh_app";
    public const string Companies = "wnh_companies";
    public const string Company = "wnh_company";
    public const string Scope = "scope";
    public const string Ai = "wnh_ai";
    public const string Name = "name";
    public const string Email = "email";
}

/// <summary>Issues and validates the hub's own JWTs (admin sessions, client-credential tokens, inbox tokens).</summary>
public sealed class TokenService
{
    public const string Issuer = "wings-notification-hub";
    public const string Audience = "wings-notification-hub";
    public SymmetricSecurityKey Key { get; }

    public TokenService(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Auth:SigningKey"];
        byte[] key;
        if (!string.IsNullOrWhiteSpace(configured)) key = Convert.FromBase64String(configured);
        else if (env.IsDevelopment() || env.IsEnvironment("Testing"))
        {
            var file = Path.Combine(Path.GetFullPath(config["Hub:DataDirectory"] ?? "App_Data"), "dev-signing-key.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!File.Exists(file)) File.WriteAllText(file, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            key = Convert.FromBase64String(File.ReadAllText(file).Trim());
        }
        else throw new InvalidOperationException("Auth:SigningKey (base64, 256-bit) must be configured outside Development.");
        if (key.Length < 32) throw new InvalidOperationException("Auth:SigningKey must be at least 256 bits.");
        Key = new SymmetricSecurityKey(key) { KeyId = "wnh-1" };
    }

    public (string Token, DateTime Expires) Issue(IEnumerable<Claim> claims, TimeSpan lifetime)
    {
        var expires = DateTime.UtcNow.Add(lifetime);
        var token = new JwtSecurityToken(Issuer, Audience, claims, DateTime.UtcNow.AddSeconds(-5), expires, new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = Key, ValidateIssuerSigningKey = true,
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "sub",
    };
}

public static class AuthSetup
{
    public const string ExternalScheme = "oidc";

    public static IServiceCollection AddHubAuthentication(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<TokenService>();
        JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
        var oidcAuthority = config["Authentication:Oidc:Authority"];
        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = "smart";
            o.DefaultChallengeScheme = "smart";
        });
        auth.AddPolicyScheme("smart", "Hub or external token", o => o.ForwardDefaultSelector = ctx =>
        {
            // Route to the external OIDC validator only when the token was not issued by the hub.
            if (oidcAuthority is null) return JwtBearerDefaults.AuthenticationScheme;
            var header = ctx.Request.Headers.Authorization.ToString();
            var raw = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : ctx.Request.Query["access_token"].ToString();
            if (string.IsNullOrEmpty(raw)) return JwtBearerDefaults.AuthenticationScheme;
            try { return new JwtSecurityTokenHandler().ReadJwtToken(raw).Issuer == TokenService.Issuer ? JwtBearerDefaults.AuthenticationScheme : ExternalScheme; }
            catch (ArgumentException) { return JwtBearerDefaults.AuthenticationScheme; }
        });
        auth.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TokenService>((o, tokens) =>
        {
            o.TokenValidationParameters = tokens.ValidationParameters();
            o.MapInboundClaims = false;
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    // SignalR sends the token on the query string for WebSockets/SSE.
                    var token = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
                    return Task.CompletedTask;
                },
            };
        });
        if (oidcAuthority is not null)
        {
            auth.AddJwtBearer(ExternalScheme, o =>
            {
                o.Authority = oidcAuthority;
                o.Audience = config["Authentication:Oidc:Audience"];
                o.MapInboundClaims = false;
                o.RequireHttpsMetadata = !oidcAuthority.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
            });
        }
        services.AddAuthorization(o =>
        {
            // Administrators: hub-issued admin sessions or external OIDC users (no hub kind claim); never app or inbox tokens.
            o.AddPolicy("admin", p => p.RequireAuthenticatedUser().RequireAssertion(c => !IsHubToken(c.User, "app") && !IsHubToken(c.User, "inbox")));
            o.AddPolicy("app-or-admin", p => p.RequireAuthenticatedUser().RequireAssertion(c => !IsHubToken(c.User, "inbox")));
            o.AddPolicy("inbox", p => p.RequireAuthenticatedUser().RequireClaim(HubClaims.Kind, "inbox"));
            o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        services.AddScoped<CallerAccessor>();
        return services;
    }

    private static bool IsHubToken(ClaimsPrincipal user, string kind) => user.FindFirst(HubClaims.Kind)?.Value == kind;
}

/// <summary>Builds the <see cref="CallerContext"/> for the current request from validated claims (+ role grants from the database).</summary>
public sealed class CallerAccessor(IHttpContextAccessor http, NotificationHubDbContext db, IMemoryCache cache)
{
    private CallerContext? _caller;

    public async Task<CallerContext> GetAsync(CancellationToken ct = default)
    {
        if (_caller is not null) return _caller;
        var ctx = http.HttpContext ?? throw new InvalidOperationException("No HTTP context.");
        var user = ctx.User;
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var correlation = ctx.Items[CorrelationIdMiddleware.Header] as string;
        var trace = System.Diagnostics.Activity.Current?.TraceId.ToString();
        if (user.Identity?.IsAuthenticated != true) return _caller = new CallerContext { Kind = CallerKind.Anonymous, IpAddress = ip, CorrelationId = correlation };
        var kind = user.FindFirst(HubClaims.Kind)?.Value;
        var sub = user.FindFirst("sub")?.Value ?? "";
        switch (kind)
        {
            case "app":
                var tenant = Guid.Parse(user.FindFirst(HubClaims.Tenant)!.Value);
                Guid? app = Guid.TryParse(user.FindFirst(HubClaims.Application)?.Value, out var a) ? a : null;
                var scopes = (user.FindFirst(HubClaims.Scope)?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var companies = JsonSerializer.Deserialize<List<Guid>>(user.FindFirst(HubClaims.Companies)?.Value ?? "[]") ?? [];
                // Revocation takes effect immediately even though the JWT is still unexpired.
                var credentialId = Guid.Parse(sub);
                var revoked = await cache.GetOrCreateAsync(("cred-revoked", credentialId), async e =>
                {
                    e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                    return await db.ApplicationCredentials.AsNoTracking().Where(c => c.Id == credentialId).Select(c => c.RevokedAt != null).FirstOrDefaultAsync(ct);
                });
                if (revoked) throw new AccessDeniedException("The credential has been revoked.");
                return _caller = new CallerContext
                {
                    Kind = CallerKind.Application, ActorId = $"credential:{sub}", ActorName = user.FindFirst(HubClaims.Name)?.Value, CredentialId = credentialId,
                    TenantId = tenant, ApplicationId = app, AllowedCompanyIds = companies, Scopes = scopes, IsAiAgent = user.FindFirst(HubClaims.Ai)?.Value == "true",
                    Grants = [new ScopeGrant(Roles.ApiClient, tenant, null, app, null)], IpAddress = ip, CorrelationId = correlation, TraceId = trace,
                };
            case "inbox":
                return _caller = new CallerContext
                {
                    Kind = CallerKind.InboxUser, ActorId = $"inbox:{sub}", UserId = sub, TenantId = Guid.Parse(user.FindFirst(HubClaims.Tenant)!.Value),
                    ApplicationId = Guid.Parse(user.FindFirst(HubClaims.Application)!.Value), CompanyId = Guid.Parse(user.FindFirst(HubClaims.Company)!.Value),
                    Scopes = new HashSet<string> { Scopes.InAppUser }, IpAddress = ip, CorrelationId = correlation, TraceId = trace,
                };
            default:
                var userRow = kind == "admin"
                    ? await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Guid.Parse(sub) && u.Enabled, ct)
                    : await ResolveExternalAsync(user, ct);
                if (userRow is null) throw new AccessDeniedException("Unknown or disabled user.");
                var grants = await cache.GetOrCreateAsync(("grants", userRow.Id), async e =>
                {
                    e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                    return (await db.RoleAssignments.AsNoTracking().Where(r => r.UserId == userRow.Id).ToListAsync(ct))
                        .Select(r => new ScopeGrant(r.Role, r.TenantId, r.CompanyId, r.ApplicationId, r.Channel)).ToList();
                }) ?? [];
                return _caller = new CallerContext
                {
                    Kind = CallerKind.AdminUser, ActorId = userRow.Id.ToString(), ActorName = userRow.DisplayName, Grants = grants,
                    IpAddress = ip, CorrelationId = correlation, TraceId = trace,
                };
        }
    }

    /// <summary>External OIDC users must be pre-provisioned by e-mail; the subject is linked on first sign-in.</summary>
    private async Task<AdminUser?> ResolveExternalAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var subject = $"{user.FindFirst("iss")?.Value}|{user.FindFirst("sub")?.Value}";
        var u = await db.AdminUsers.FirstOrDefaultAsync(x => x.ExternalSubject == subject && x.Enabled, ct);
        if (u is not null) return u;
        var email = user.FindFirst("email")?.Value ?? user.FindFirst("preferred_username")?.Value;
        if (email is null) return null;
        u = await db.AdminUsers.FirstOrDefaultAsync(x => x.Email == email.ToLowerInvariant() && x.Enabled && x.ExternalSubject == null, ct);
        if (u is null) return null;
        u.ExternalSubject = subject;
        u.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return u;
    }
}
