using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.Api.Infrastructure;

/// <summary>Accepts or creates X-Correlation-Id and pushes it into the logging scope and response.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string Header = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var id = ctx.Request.Headers[Header].ToString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100) id = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        ctx.Items[Header] = id;
        ctx.Response.Headers[Header] = id;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        using (Serilog.Context.LogContext.PushProperty("CorrelationId", id))
            await next(ctx);
    }
}

/// <summary>
/// Minimal APIs bind enum query parameters case-sensitively. This normalizes well-known enum query keys so clients can
/// use the same lower-case values as in JSON (?channel=whatsapp, ?status=delivered).
/// </summary>
public sealed class QueryEnumNormalizationMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase) { "channel", "status", "kind", "platform", "messageClass" };
    private static readonly Dictionary<string, string> Names = typeof(Channel).Assembly.GetTypes()
        .Where(t => t.IsEnum && t.Namespace == typeof(Channel).Namespace)
        .SelectMany(t => Enum.GetNames(t)).Distinct()
        .ToDictionary(n => n.ToLowerInvariant(), n => n);

    public Task InvokeAsync(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        if (q.Count > 0 && q.Keys.Any(Keys.Contains))
        {
            var rewritten = q.ToDictionary(kv => kv.Key, kv =>
            {
                if (!Keys.Contains(kv.Key)) return kv.Value;
                var v = kv.Value.ToString();
                if (kv.Key.Equals("channel", StringComparison.OrdinalIgnoreCase) && ChannelNames.TryParse(v, out var c)) return new Microsoft.Extensions.Primitives.StringValues(c.ToString());
                return Names.TryGetValue(v.Replace("_", "").Replace("-", "").ToLowerInvariant(), out var canonical) ? new Microsoft.Extensions.Primitives.StringValues(canonical) : kv.Value;
            });
            ctx.Request.QueryString = Microsoft.AspNetCore.Http.QueryString.Create(rewritten);
        }
        return next(ctx);
    }
}

/// <summary>Maps domain exceptions to RFC-style JSON errors without leaking internals.</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (HubException ex) { await WriteAsync(ctx, ex.Status, ex.Code, ex.Message, ex.Errors); }
        catch (AccessDeniedException ex) { await WriteAsync(ctx, 403, "forbidden", ex.Message, null); }
        catch (Core.Templates.TemplateSyntaxException ex) { await WriteAsync(ctx, 422, "template_syntax", ex.Message, null); }
        catch (DbUpdateConcurrencyException) { await WriteAsync(ctx, 409, "concurrency_conflict", "The resource was modified by someone else. Reload and try again.", null); }
        catch (BadHttpRequestException ex) { await WriteAsync(ctx, 400, "bad_request", ex.Message, null); }
        catch (JsonException ex) { await WriteAsync(ctx, 400, "invalid_json", "The request body is not valid JSON: " + ex.Message, null); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            await WriteAsync(ctx, 500, "internal_error", "An unexpected error occurred.", null);
        }
    }

    public static async Task WriteAsync(HttpContext ctx, int status, string code, string message, Dictionary<string, string[]>? errors, bool clear = true)
    {
        if (ctx.Response.HasStarted) return;
        if (clear) ctx.Response.Clear();
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ApiError
        {
            Code = code, Message = message, Errors = errors, CorrelationId = ctx.Items[CorrelationIdMiddleware.Header] as string,
        }, Json.Options));
    }
}

public static class RateLimiting
{
    public const string Api = "api";
    public const string Auth = "auth";
    public const string Callbacks = "callbacks";

    /// <summary>
    /// Per-credential fixed windows (limit = the application's RateLimitPerMinute), per-user for admins,
    /// per-IP for anonymous token/login calls. Rejections return 429 with Retry-After.
    /// </summary>
    public static IServiceCollection AddHubRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var enabled = config.GetValue("RateLimiting:Enabled", true);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.OnRejected = async (ctx, ct) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)) ctx.HttpContext.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString();
                await ErrorHandlingMiddleware.WriteAsync(ctx.HttpContext, 429, "rate_limited", "Too many requests. Slow down and retry after the indicated delay.", null, clear: false);
            };
            o.AddPolicy(Api, ctx =>
            {
                if (!enabled) return RateLimitPartition.GetNoLimiter("off");
                var user = ctx.User;
                var kind = user.FindFirst(HubClaims.Kind)?.Value;
                if (kind == "app")
                {
                    var appId = user.FindFirst(HubClaims.Application)?.Value;
                    var limit = LimitFor(ctx, appId);
                    return RateLimitPartition.GetFixedWindowLimiter("cred:" + user.FindFirst("sub")?.Value,
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
                }
                if (user.Identity?.IsAuthenticated == true)
                    return RateLimitPartition.GetFixedWindowLimiter("user:" + user.FindFirst("sub")?.Value,
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimiting:UserPerMinute", 3000), Window = TimeSpan.FromMinutes(1) });
                return RateLimitPartition.GetFixedWindowLimiter("ip:" + ctx.Connection.RemoteIpAddress,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimiting:AnonymousPerMinute", 300), Window = TimeSpan.FromMinutes(1) });
            });
            o.AddPolicy(Auth, ctx => !enabled ? RateLimitPartition.GetNoLimiter("off") : RateLimitPartition.GetFixedWindowLimiter("auth:" + ctx.Connection.RemoteIpAddress,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimiting:AuthPerMinute", 60), Window = TimeSpan.FromMinutes(1) }));
            o.AddPolicy(Callbacks, ctx => !enabled ? RateLimitPartition.GetNoLimiter("off") : RateLimitPartition.GetTokenBucketLimiter("cb:" + ctx.Request.RouteValues["accountId"],
                _ => new TokenBucketRateLimiterOptions { TokenLimit = 2000, TokensPerPeriod = 500, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 0 }));
        });
        return services;
    }

    private static int LimitFor(HttpContext ctx, string? appId)
    {
        if (!Guid.TryParse(appId, out var id)) return 600;
        var cache = ctx.RequestServices.GetRequiredService<IMemoryCache>();
        return cache.GetOrCreate(("app-rate", id), e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            using var scope = ctx.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
            return db.Applications.AsNoTracking().Where(a => a.Id == id).Select(a => a.RateLimitPerMinute).FirstOrDefault() is var v && v > 0 ? v : 600;
        });
    }
}

public static class HttpContextExtensions
{
    public static Task<CallerContext> CallerAsync(this HttpContext ctx) => ctx.RequestServices.GetRequiredService<CallerAccessor>().GetAsync(ctx.RequestAborted);
}
