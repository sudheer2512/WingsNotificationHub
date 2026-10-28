using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Api.Endpoints;

public sealed record LoginRequest(string Email, string Password);

public static class AuthEndpoints
{
    public static void Map(WebApplication app)
    {
        // OAuth 2.0 client-credentials token endpoint for application credentials.
        app.MapPost("/connect/token", async (HttpContext ctx, NotificationHubDbContext db, TokenService tokens, AuditService audit) =>
        {
            var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ctx.RequestAborted) : null;
            string? clientId = form?["client_id"], clientSecret = form?["client_secret"];
            var header = ctx.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())).Split(':', 2);
                if (decoded.Length == 2) { clientId = Uri.UnescapeDataString(decoded[0]); clientSecret = Uri.UnescapeDataString(decoded[1]); }
            }
            if (form?["grant_type"].ToString() != "client_credentials") return OAuthError("unsupported_grant_type", "Only client_credentials is supported.");
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)) return OAuthError("invalid_client", "Client authentication failed.", 401);
            var cred = await db.ApplicationCredentials.FirstOrDefaultAsync(c => c.ClientId == clientId, ctx.RequestAborted);
            if (cred is null || cred.RevokedAt is not null || cred.ExpiresAt < DateTime.UtcNow || !Hashing.VerifySecret(clientSecret, cred.SecretHash))
                return OAuthError("invalid_client", "Client authentication failed.", 401);
            if (cred.ApplicationId is { } appId && await db.Applications.AsNoTracking().Where(a => a.Id == appId).Select(a => a.Status).FirstAsync(ctx.RequestAborted) != EntityStatus.Active)
                return OAuthError("unauthorized_client", "The application is suspended.", 401);
            var requested = form?["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var granted = requested.Length == 0 ? cred.Scopes : requested.Where(cred.Scopes.Contains).ToList();
            if (requested.Length > 0 && granted.Count != requested.Length) return OAuthError("invalid_scope", "One or more requested scopes are not granted to this client.");
            var claims = new List<Claim>
            {
                new("sub", cred.Id.ToString()), new(HubClaims.Kind, "app"), new(HubClaims.Tenant, cred.TenantId.ToString()),
                new(HubClaims.Scope, string.Join(' ', granted)), new(HubClaims.Name, cred.Name), new("client_id", cred.ClientId),
                new(HubClaims.Companies, JsonSerializer.Serialize(cred.AllowedCompanyIds)),
            };
            if (cred.ApplicationId is { } a) claims.Add(new(HubClaims.Application, a.ToString()));
            if (cred.IsAiAgent) claims.Add(new(HubClaims.Ai, "true"));
            var lifetime = TimeSpan.FromMinutes(60);
            var (token, _) = tokens.Issue(claims, lifetime);
            cred.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Json(new TokenResponse { Access_token = token, Expires_in = (int)lifetime.TotalSeconds, Scope = string.Join(' ', granted) },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        }).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(RateLimiting.Auth).WithTags("Authentication")
          .WithSummary("OAuth 2.0 client-credentials token for application credentials");

        var g = app.MapGroup("/api/v1/auth").WithTags("Authentication");
        g.MapPost("/login", async (LoginRequest req, NotificationHubDbContext db, TokenService tokens, HttpContext ctx) =>
        {
            var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Email == req.Email.Trim().ToLowerInvariant(), ctx.RequestAborted);
            if (user is null || !user.Enabled) return Results.Json(new ApiError { Code = "invalid_credentials", Message = "Invalid email or password." }, statusCode: 401);
            if (user.LockedUntil > DateTime.UtcNow) return Results.Json(new ApiError { Code = "locked", Message = "Too many failed attempts; try again later." }, statusCode: 423);
            if (!Hashing.VerifyPassword(req.Password, user.PasswordHash))
            {
                user.FailedLogins++;
                if (user.FailedLogins >= 5) { user.LockedUntil = DateTime.UtcNow.AddMinutes(15); user.FailedLogins = 0; }
                await db.SaveChangesAsync(ctx.RequestAborted);
                return Results.Json(new ApiError { Code = "invalid_credentials", Message = "Invalid email or password." }, statusCode: 401);
            }
            user.FailedLogins = 0;
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ctx.RequestAborted);
            var (token, expires) = tokens.Issue([new("sub", user.Id.ToString()), new(HubClaims.Kind, "admin"), new(HubClaims.Name, user.DisplayName), new(HubClaims.Email, user.Email)], TimeSpan.FromHours(8));
            return Results.Ok(new { accessToken = token, expiresAt = expires, user = new { user.Id, user.Email, user.DisplayName } });
        }).AllowAnonymous().RequireRateLimiting(RateLimiting.Auth);

        g.MapGet("/me", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var tenants = await db.Tenants.AsNoTracking().Select(t => new { t.Id, t.Name, t.Code }).ToListAsync(ctx.RequestAborted);
            return Results.Ok(new
            {
                kind = caller.Kind.ToString(), caller.ActorId, caller.ActorName, caller.TenantId, caller.ApplicationId, caller.IsAiAgent,
                scopes = caller.Scopes, grants = caller.Grants,
                permissions = Enum.GetValues<Permission>().Where(p => caller.Grants.Any(gr => caller.Can(p, gr.TenantId, gr.CompanyId, gr.ApplicationId))).Select(p => p.ToString()),
                tenants = tenants.Where(t => caller.IsPlatformAdmin || caller.Grants.Any(gr => gr.TenantId == t.Id)),
            });
        }).RequireAuthorization("app-or-admin");
    }

    private static IResult OAuthError(string error, string description, int status = 400)
        => Results.Json(new { error, error_description = description }, statusCode: status);
}
