using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Messaging;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Infrastructure.Delivery;

/// <summary>
/// The single place delivery statuses change. Records an immutable <see cref="DeliveryEvent"/>, stamps timestamps,
/// emits a status-changed outbox message, maintains the request aggregate and handles side effects
/// (suppression, device invalidation, credit release). Callers save the unit of work.
/// </summary>
public sealed class DeliveryStatusUpdater(NotificationHubDbContext db, UsageService usage)
{
    /// <summary>System transition (dispatcher, scheduler, operator): applied unconditionally.</summary>
    public async Task SetAsync(NotificationDelivery d, DeliveryStatus status, string source, string? detail, CancellationToken ct, DateTime? occurredAt = null)
    {
        var previous = d.Status;
        Stamp(d, status, occurredAt ?? DateTime.UtcNow);
        d.StatusReason = detail is { Length: > 1000 } ? detail[..1000] : detail;
        db.DeliveryEvents.Add(new DeliveryEvent
        {
            DeliveryId = d.Id, TenantId = d.TenantId, ProviderAccountId = d.ProviderAccountId, ProviderMessageId = d.ProviderMessageId,
            Type = "status", Status = status, Source = source, Detail = detail, Applied = true, OccurredAt = occurredAt ?? DateTime.UtcNow,
        });
        if (previous != status) await AfterChangeAsync(d, previous, ct);
    }

    /// <summary>
    /// Provider-reported status (callback/reconciliation). Always recorded; applied only when it moves the delivery
    /// forward (out-of-order callbacks never regress a delivery). Returns whether it was applied.
    /// </summary>
    public async Task<bool> ApplyProviderStatusAsync(NotificationDelivery d, ProviderCallbackEvent e, string source, CancellationToken ct)
    {
        var applied = e.Status is { } s && DeliveryStatusRules.CanApplyProviderStatus(d.Status, s);
        var detail = e.ErrorCode is null ? e.Interaction : $"{e.ErrorCode}: {e.ErrorMessage}";
        db.DeliveryEvents.Add(new DeliveryEvent
        {
            DeliveryId = d.Id, TenantId = d.TenantId, ProviderAccountId = d.ProviderAccountId, ProviderMessageId = e.ProviderMessageId ?? d.ProviderMessageId,
            ProviderEventId = e.EventId, Type = e.Interaction is not null ? "interaction" : "provider_status", Status = e.Status, Source = source,
            Detail = detail, Applied = applied, OccurredAt = e.OccurredAt,
        });
        if (e.Cost is { } cost) await usage.ApplyReportedCostAsync(d, cost, ct);
        if (!applied) return false;
        var previous = d.Status;
        Stamp(d, e.Status!.Value, e.OccurredAt);
        d.StatusReason = $"{source}: {e.Status}" + (detail is null ? "" : $" ({detail})");
        if (e.Status == DeliveryStatus.Failed)
        {
            d.FailureCode = e.ErrorCode ?? "provider_failed";
            d.FailureReason = e.ErrorMessage;
        }
        d.AwaitingOperator = false;
        await AfterChangeAsync(d, previous, ct);
        return true;
    }

    private static void Stamp(NotificationDelivery d, DeliveryStatus status, DateTime at)
    {
        d.Status = status;
        switch (status)
        {
            case DeliveryStatus.Submitted: d.SubmittedAt ??= at; break;
            case DeliveryStatus.ProviderAccepted: d.AcceptedAt ??= at; break;
            case DeliveryStatus.Sent: d.AcceptedAt ??= at; d.SentAt ??= at; break;
            case DeliveryStatus.Delivered: d.AcceptedAt ??= at; d.DeliveredAt ??= at; break;
            case DeliveryStatus.Read: d.AcceptedAt ??= at; d.DeliveredAt ??= at; d.ReadAt ??= at; break;
            case DeliveryStatus.Failed: d.FailedAt ??= at; break;
        }
        if (DeliveryStatusRules.IsTerminal(status) || status is DeliveryStatus.ProviderAccepted or DeliveryStatus.Sent)
        {
            d.CompletedAt ??= at;
            d.NextAttemptAt = null;
            d.LeaseOwner = null;
            d.LeaseExpiresAt = null;
        }
    }

    private async Task AfterChangeAsync(NotificationDelivery d, DeliveryStatus previous, CancellationToken ct)
    {
        if (d.Status is DeliveryStatus.Failed or DeliveryStatus.Expired or DeliveryStatus.Cancelled or DeliveryStatus.Suppressed && !DeliveryStatusRules.IsSuccess(previous))
            await usage.ReleaseCreditAsync(d, ct);
        var (requestStatus, completed) = await RefreshRequestAsync(d, ct);
        Outbox.Add(db, MessageTypes.DeliveryStatusChanged,
            new DeliveryStatusChangedMessage(d.Id, d.RequestId, d.Status.ToString(), previous.ToString(), DateTime.UtcNow) { RequestStatus = requestStatus.ToString(), RequestCompleted = completed },
            d.TenantId);
    }

    /// <summary>Recomputes the request's aggregate status including tracked (unsaved) changes.</summary>
    private async Task<(RequestStatus, bool)> RefreshRequestAsync(NotificationDelivery changed, CancellationToken ct)
    {
        var request = db.NotificationRequests.Local.FirstOrDefault(r => r.Id == changed.RequestId)
                      ?? await db.NotificationRequests.FirstAsync(r => r.Id == changed.RequestId, ct);
        var stored = await db.NotificationDeliveries.AsNoTracking().Where(x => x.RequestId == changed.RequestId).Select(x => new { x.Id, x.Status }).ToListAsync(ct);
        var statuses = stored.Select(x =>
            db.NotificationDeliveries.Local.FirstOrDefault(l => l.Id == x.Id)?.Status ?? x.Status).ToList();
        foreach (var local in db.NotificationDeliveries.Local.Where(l => l.RequestId == changed.RequestId && stored.All(s => s.Id != l.Id)))
            statuses.Add(local.Status);
        var aggregate = DeliveryStatusRules.Aggregate(statuses, statuses.Contains(DeliveryStatus.Scheduled));
        var wasFinal = IsFinal(request.Status);
        if (request.Status != aggregate && request.Status != RequestStatus.PendingApproval && request.Status != RequestStatus.Rejected)
        {
            request.Status = aggregate;
            if (IsFinal(aggregate)) request.CompletedAt ??= DateTime.UtcNow;
            else request.CompletedAt = null;
        }
        return (request.Status, !wasFinal && IsFinal(request.Status));
    }

    private static bool IsFinal(RequestStatus s) => s is RequestStatus.Completed or RequestStatus.Failed or RequestStatus.PartiallyFailed
        or RequestStatus.Cancelled or RequestStatus.Suppressed or RequestStatus.Rejected;

    /// <summary>
    /// Receipts that arrived before the provider message id was recorded (out-of-order callbacks) are parked with no
    /// delivery id; this links and applies them once the id is known.
    /// </summary>
    public async Task<int> ApplyParkedCallbacksAsync(NotificationDelivery d, CancellationToken ct)
    {
        if (d.ProviderMessageId is null || d.ProviderAccountId is null) return 0;
        var parked = await db.DeliveryEvents.Where(e => e.DeliveryId == null && e.ProviderAccountId == d.ProviderAccountId && e.ProviderMessageId == d.ProviderMessageId)
            .OrderBy(e => e.OccurredAt).ToListAsync(ct);
        foreach (var o in parked)
        {
            o.DeliveryId = d.Id;
            o.TenantId = d.TenantId;
            if (o.Status is { } s && DeliveryStatusRules.CanApplyProviderStatus(d.Status, s))
            {
                o.Applied = true;
                var previous = d.Status;
                Stamp(d, s, o.OccurredAt);
                d.StatusReason = $"provider_callback: {s} (applied after acceptance)";
                await AfterChangeAsync(d, previous, ct);
            }
        }
        return parked.Count;
    }

    /// <summary>Adds an address to the tenant suppression list (bounces, complaints, invalid numbers, STOP replies).</summary>
    public async Task SuppressAsync(Guid tenantId, Guid? companyId, Channel channel, string address, SuppressionReason reason, string source, CancellationToken ct)
    {
        var normalized = Addressing.Normalize(channel, address);
        var exists = await db.Suppressions.AnyAsync(s => s.TenantId == tenantId && s.Channel == channel && s.Address == normalized && s.Reason == reason, ct)
                     || db.Suppressions.Local.Any(s => s.TenantId == tenantId && s.Channel == channel && s.Address == normalized && s.Reason == reason);
        if (exists) return;
        db.Suppressions.Add(new Suppression
        {
            TenantId = tenantId, CompanyId = reason is SuppressionReason.Unsubscribe or SuppressionReason.OptOut ? companyId : null,
            Channel = channel, Address = normalized, Reason = reason, PromotionalOnly = reason == SuppressionReason.Unsubscribe, Source = source,
        });
    }

    public async Task InvalidateDeviceAsync(Guid deviceId, string reason, CancellationToken ct)
    {
        var device = await db.DeviceRegistrations.FirstOrDefaultAsync(x => x.Id == deviceId, ct);
        if (device is null || device.InvalidatedAt is not null) return;
        device.InvalidatedAt = DateTime.UtcNow;
        device.InvalidReason = reason;
    }
}
