using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Delivery;
using Wings.NotificationHub.Infrastructure.Providers;

namespace Wings.NotificationHub.Api.Endpoints;

public sealed record SimulateInboundRequest(Guid ProviderAccountId, string From, string Body, string? InReplyToProviderMessageId);
public sealed record WebhookSinkEntry(DateTime ReceivedAt, string? Signature, string? IdempotencyKey, string Body);

/// <summary>
/// Development/demo helpers (enabled by Hub:EnableDevEndpoints, on by default only in Development):
/// mock provider outbox, a local webhook receiver, provider outage simulation and simulated customer replies.
/// </summary>
public static class DevEndpoints
{
    private static readonly ConcurrentQueue<WebhookSinkEntry> Sink = new();

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/dev").WithTags("Development").ExcludeFromDescription();

        g.MapGet("/mock-messages", async (HttpContext ctx, MockProviderState state, string? to, int take = 100) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.IsPlatformAdmin && caller.Kind != CallerKind.Application) throw new AccessDeniedException();
            var rows = state.Sent.Reverse().Where(m => to is null || m.To.Contains(to, StringComparison.OrdinalIgnoreCase)).Take(Math.Clamp(take, 1, 1000));
            return Results.Ok(rows);
        }).RequireAuthorization("app-or-admin");

        g.MapPost("/mock-outage/{accountId:guid}/{down:bool}", async (Guid accountId, bool down, HttpContext ctx, MockProviderState state) =>
        {
            if (!(await ctx.CallerAsync()).IsPlatformAdmin) throw new AccessDeniedException();
            state.Outages[accountId] = down;
            return Results.Ok(new { accountId, down });
        }).RequireAuthorization("admin");

        g.MapPost("/webhook-sink", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            Sink.Enqueue(new WebhookSinkEntry(DateTime.UtcNow, ctx.Request.Headers[WebhookSignature.Header], ctx.Request.Headers["Idempotency-Key"], body.Length > 20000 ? body[..20000] : body));
            while (Sink.Count > 500) Sink.TryDequeue(out _);
            return Results.Ok();
        }).AllowAnonymous().DisableAntiforgery();

        g.MapGet("/webhook-sink", async (HttpContext ctx, string? contains) =>
        {
            var caller = await ctx.CallerAsync();
            if (!caller.IsPlatformAdmin && caller.Kind != CallerKind.Application) throw new AccessDeniedException();
            return Results.Ok(Sink.Reverse().Where(e => contains is null || e.Body.Contains(contains, StringComparison.OrdinalIgnoreCase)).Take(200));
        }).RequireAuthorization("app-or-admin");

        g.MapPost("/simulate-inbound", async (SimulateInboundRequest req, HttpContext ctx, NotificationHubDbContext db, CallbackProcessor processor) =>
        {
            if (!(await ctx.CallerAsync()).IsPlatformAdmin) throw new AccessDeniedException();
            if (string.IsNullOrWhiteSpace(req.From) || string.IsNullOrWhiteSpace(req.Body)) throw HubException.Validation("from and body are required.");
            var account = await db.ProviderAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == req.ProviderAccountId && a.ProviderType == "mock", ctx.RequestAborted)
                          ?? throw HubException.NotFound("Mock provider account");
            var id = $"mock-in-{Guid.NewGuid():N}";
            var stop = req.Body.Trim().ToUpperInvariant() is "STOP" or "UNSUBSCRIBE";
            var (accepted, _) = await processor.ApplyAsync(account, [new ProviderCallbackEvent
            {
                EventId = id,
                Inbound = new InboundMessageData { From = req.From, Body = req.Body, ProviderMessageId = id, InReplyToProviderMessageId = req.InReplyToProviderMessageId },
                Suppress = stop ? SuppressionReason.OptOut : null, SuppressAddress = stop ? req.From : null,
            }], "simulated", ctx.RequestAborted);
            return Results.Ok(new { providerMessageId = id, accepted });
        }).RequireAuthorization("admin");
    }
}
