using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Endpoints;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.Api.Infrastructure;

/// <summary>End-user in-app inbox hub. Clients connect with an inbox token (?access_token=…) and receive "notification" events.</summary>
[Authorize(Policy = "inbox")]
public sealed class InAppHub : Hub
{
    public static string Group(Guid app, Guid company, string user) => $"inbox:{app:N}:{company:N}:{user}";

    public override async Task OnConnectedAsync()
    {
        var u = Context.User!;
        var group = Group(Guid.Parse(u.FindFirst(HubClaims.Application)!.Value), Guid.Parse(u.FindFirst(HubClaims.Company)!.Value), u.FindFirst("sub")!.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        await base.OnConnectedAsync();
    }
}

/// <summary>Admin console hub: live delivery events, inbound messages and alerts, scoped to the user's tenants.</summary>
[Authorize(Policy = "admin")]
public sealed class AdminHub(CallerAccessor callers) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var caller = await callers.GetAsync();
        if (caller.IsPlatformAdmin) await Groups.AddToGroupAsync(Context.ConnectionId, "platform");
        foreach (var t in caller.Grants.Where(g => g.TenantId is not null).Select(g => g.TenantId!.Value).Distinct())
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{t:N}");
        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Pushes new inbox items, delivery events and inbound messages to connected SignalR clients. Each API instance relays
/// to its own connections by polling committed rows, so workers in separate processes need no SignalR backplane.
/// </summary>
public sealed class RealtimeRelay(IServiceScopeFactory scopes, IHubContext<InAppHub> inapp, IHubContext<AdminHub> admin, ILogger<RealtimeRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var inboxCursor = DateTime.UtcNow;
        var eventCursor = DateTime.UtcNow;
        var inboundCursor = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
                var messages = await db.InAppMessages.AsNoTracking().Where(m => m.CreatedAt > inboxCursor).OrderBy(m => m.CreatedAt).Take(500).ToListAsync(stoppingToken);
                foreach (var m in messages)
                {
                    await inapp.Clients.Group(InAppHub.Group(m.ApplicationId, m.CompanyId, m.UserId)).SendAsync("notification", CommunicationEndpoints.InAppDto(m), stoppingToken);
                    inboxCursor = m.CreatedAt;
                }

                var events = await db.DeliveryEvents.AsNoTracking().Where(e => e.RecordedAt > eventCursor && e.DeliveryId != null && e.Applied).OrderBy(e => e.RecordedAt).Take(300)
                    .Join(db.NotificationDeliveries.AsNoTracking(), e => e.DeliveryId, d => d.Id, (e, d) => new { e.RecordedAt, d.TenantId, d.Id, d.RequestId, d.Channel, e.Status, e.Source, d.CompanyId, d.ApplicationId })
                    .ToListAsync(stoppingToken);
                foreach (var e in events)
                {
                    var payload = new { deliveryId = e.Id, notificationId = e.RequestId, channel = ChannelNames.ToWire(e.Channel), status = e.Status?.ToString(), e.Source, at = e.RecordedAt, e.CompanyId, e.ApplicationId };
                    await admin.Clients.Groups("platform", $"tenant:{e.TenantId:N}").SendAsync("delivery", payload, stoppingToken);
                    eventCursor = e.RecordedAt;
                }

                var inbound = await db.InboundMessages.AsNoTracking().Where(m => m.ReceivedAt > inboundCursor).OrderBy(m => m.ReceivedAt).Take(100).ToListAsync(stoppingToken);
                foreach (var m in inbound)
                {
                    await admin.Clients.Groups("platform", $"tenant:{m.TenantId:N}").SendAsync("inbound", new { m.Id, m.ThreadId, channel = ChannelNames.ToWire(m.Channel), m.From, m.Body, m.ReceivedAt }, stoppingToken);
                    inboundCursor = m.ReceivedAt;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Realtime relay iteration failed"); }
            await Task.Delay(TimeSpan.FromMilliseconds(750), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
