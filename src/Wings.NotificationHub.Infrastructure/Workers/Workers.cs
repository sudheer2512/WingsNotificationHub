using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Delivery;
using Wings.NotificationHub.Infrastructure.Messaging;
using Wings.NotificationHub.Infrastructure.Providers;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Infrastructure.Workers;

/// <summary>Base loop: runs <see cref="TickAsync"/> on an interval in a fresh scope, recording heartbeat statistics.</summary>
public abstract class PeriodicWorker(IServiceScopeFactory scopes, WorkerStatus status, ILogger logger) : BackgroundService
{
    protected abstract string Name { get; }
    protected abstract TimeSpan Interval { get; }
    protected abstract Task<int> TickAsync(IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        status.Started(Name);
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(100, 1000)), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var n = await TickAsync(scope.ServiceProvider, stoppingToken);
                if (n > 0) status.Processed(Name, n);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                status.Failed(Name, ex.Message);
                logger.LogError(ex, "{Worker} tick failed", Name);
            }
            await Task.Delay(Interval, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
        status.Stopped(Name);
    }
}

/// <summary>
/// Promotes due scheduled/retry deliveries to the dispatch queue, expires stale ones, runs recurring schedules,
/// launches/advances campaigns and creates fallback/escalation follow-ups.
/// </summary>
public sealed class SchedulerWorker(IServiceScopeFactory scopes, WorkerStatus status, ILogger<SchedulerWorker> logger)
    : PeriodicWorker(scopes, status, logger)
{
    protected override string Name => "scheduler";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(1);

    protected override async Task<int> TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<NotificationHubDbContext>();
        var now = DateTime.UtcNow;
        var count = 0;

        // Due deliveries → Queued + dispatch message. Conditional per-row update: only one scheduler instance wins each row.
        var due = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => (d.Status == DeliveryStatus.Scheduled || d.Status == DeliveryStatus.RetryPending) && d.NextAttemptAt <= now
                        && (d.LeaseExpiresAt == null || d.LeaseExpiresAt < now) && (d.ExpiresAt == null || d.ExpiresAt > now))
            .OrderBy(d => d.Priority == NotificationPriority.Critical ? 0 : d.Priority == NotificationPriority.High ? 1 : 2).ThenBy(d => d.NextAttemptAt)
            .Select(d => new { d.Id, d.TenantId, d.Status }).Take(500).ToListAsync(ct);
        foreach (var d in due)
        {
            var won = await db.NotificationDeliveries.Where(x => x.Id == d.Id && x.Status == d.Status)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeliveryStatus.Queued).SetProperty(x => x.ConcurrencyStamp, Guid.NewGuid()), ct);
            if (won == 0) continue;
            Outbox.Add(db, MessageTypes.DispatchDelivery, new DispatchDeliveryMessage(d.Id), d.TenantId);
            count++;
        }
        if (count > 0) await db.SaveChangesAsync(ct);

        // Expiry of messages that never went out in time.
        var updater = sp.GetRequiredService<DeliveryStatusUpdater>();
        var expired = await db.NotificationDeliveries
            .Where(d => (d.Status == DeliveryStatus.Scheduled || d.Status == DeliveryStatus.Queued || d.Status == DeliveryStatus.RetryPending)
                        && d.ExpiresAt != null && d.ExpiresAt <= now && (d.LeaseExpiresAt == null || d.LeaseExpiresAt < now))
            .OrderBy(d => d.ExpiresAt).Take(100).ToListAsync(ct);
        foreach (var d in expired) await updater.SetAsync(d, DeliveryStatus.Expired, "scheduler", "Expired before it could be sent.", ct);
        if (expired.Count > 0) await db.SaveChangesAsync(ct);

        count += await sp.GetRequiredService<FollowUpService>().ProcessDueAsync(100, ct);
        count += await sp.GetRequiredService<ScheduleService>().RunDueAsync(ct);

        // Campaigns: launch scheduled ones that are due, then advance running ones within their throttle.
        var campaigns = sp.GetRequiredService<CampaignService>();
        var launch = await db.Campaigns.Where(c => (c.Status == CampaignStatus.Scheduled && c.ScheduledAt <= now) || (c.Status == CampaignStatus.Approved && (c.ScheduledAt == null || c.ScheduledAt <= now)))
            .OrderBy(c => c.ScheduledAt).Select(c => c.Id).Take(5).ToListAsync(ct);
        foreach (var id in launch)
        {
            var claimed = await db.Campaigns.Where(c => c.Id == id && (c.Status == CampaignStatus.Scheduled || c.Status == CampaignStatus.Approved))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CampaignStatus.Approved).SetProperty(c => c.ConcurrencyStamp, Guid.NewGuid()), ct);
            if (claimed == 0) continue;
            var c = await db.Campaigns.FirstAsync(x => x.Id == id, ct);
            await campaigns.LaunchAsync(c, ct);
        }
        foreach (var c in await db.Campaigns.Where(c => c.Status == CampaignStatus.Running && !c.Suspended).OrderBy(c => c.StartedAt).Take(10).ToListAsync(ct))
            count += await campaigns.ProcessBatchAsync(c, ct);
        return count;
    }
}

/// <summary>
/// Reconciliation: recovers abandoned leases (worker crash), resolves ambiguous submissions by asking the provider,
/// checks accepted messages whose receipts never arrived, and re-enqueues deliveries whose queue message was lost.
/// </summary>
public sealed class ReconciliationWorker(IServiceScopeFactory scopes, WorkerStatus status, ILogger<ReconciliationWorker> logger)
    : PeriodicWorker(scopes, status, logger)
{
    protected override string Name => "reconciliation";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(15);

    protected override async Task<int> TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<NotificationHubDbContext>();
        var registry = sp.GetRequiredService<ProviderRegistry>();
        var updater = sp.GetRequiredService<DeliveryStatusUpdater>();
        var usage = sp.GetRequiredService<UsageService>();
        var now = DateTime.UtcNow;
        var n = 0;

        // 1. Worker died while Processing (nothing submitted): safe to retry.
        n += await db.NotificationDeliveries.Where(d => d.Status == DeliveryStatus.Processing && d.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.RetryPending).SetProperty(d => d.NextAttemptAt, now)
                .SetProperty(d => d.LeaseOwner, (string?)null).SetProperty(d => d.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(d => d.StatusReason, "recovered after worker interruption (not submitted)").SetProperty(d => d.ConcurrencyStamp, Guid.NewGuid()), ct);

        // 2. Worker died after Submitted: the provider may have the message → treat as ambiguous.
        var lost = await db.NotificationDeliveries.Where(d => d.Status == DeliveryStatus.Submitted && d.LeaseExpiresAt < now).OrderBy(d => d.LeaseExpiresAt).Take(50).ToListAsync(ct);
        foreach (var d in lost)
        {
            var attempt = await db.DeliveryAttempts.Where(a => a.DeliveryId == d.Id).OrderByDescending(a => a.AttemptNumber).FirstOrDefaultAsync(ct);
            if (attempt is { Outcome: null })
            {
                attempt.Outcome = AttemptOutcome.Ambiguous;
                attempt.ErrorCode = "worker_lost";
                attempt.ErrorMessage = "The worker stopped while waiting for the provider response.";
                attempt.CompletedAt = now;
            }
            d.LeaseOwner = null; d.LeaseExpiresAt = null; d.NextAttemptAt = now;
            await updater.SetAsync(d, DeliveryStatus.Unknown, "reconciliation", "Worker interrupted after submission; outcome unknown.", ct);
            n++;
        }

        // 3. Ambiguous deliveries awaiting reconciliation, and accepted deliveries whose receipts are overdue.
        var toCheck = await db.NotificationDeliveries
            .Where(d => d.NextAttemptAt != null && d.NextAttemptAt <= now && d.ProviderAccountId != null && !d.AwaitingOperator
                        && (d.Status == DeliveryStatus.Unknown || d.Status == DeliveryStatus.ProviderAccepted || d.Status == DeliveryStatus.Sent))
            .OrderBy(d => d.NextAttemptAt).Take(50).ToListAsync(ct);
        foreach (var d in toCheck)
        {
            var account = await db.ProviderAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == d.ProviderAccountId, ct);
            INotificationProvider? provider = null;
            try { if (account is not null) provider = registry.Get(account); } catch (Exception ex) { logger.LogWarning(ex, "Cannot build provider {Account}", d.ProviderAccountId); }
            ProviderStatusReport? report = null;
            if (provider is IStatusQueryProvider q && provider.Capabilities.SupportsStatusQuery)
            {
                try { report = await q.QueryStatusAsync(d.ProviderMessageId, $"wnh-{d.Id:N}", ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Status query failed for delivery {Delivery}", d.Id); d.NextAttemptAt = now.AddMinutes(5); continue; }
            }
            if (d.Status == DeliveryStatus.Unknown)
            {
                if (report is { Found: true })
                {
                    d.ProviderMessageId ??= report.ProviderMessageId;
                    d.AwaitingOperator = false;
                    await updater.SetAsync(d, report.Status is DeliveryStatus.Delivered or DeliveryStatus.Read or DeliveryStatus.Sent ? report.Status : DeliveryStatus.ProviderAccepted,
                        "reconciliation", "Provider confirms it accepted the message; no resend needed.", ct);
                    await usage.RecordAcceptedAsync(d, null, ct);
                    await updater.ApplyParkedCallbacksAsync(d, ct);
                }
                else if (report is { Found: false })
                {
                    // Provider never received it: resending cannot duplicate.
                    d.NextAttemptAt = now;
                    await updater.SetAsync(d, DeliveryStatus.RetryPending, "reconciliation", "Provider has no record of the message; safe to resend.", ct);
                }
                else
                {
                    d.AwaitingOperator = true;
                    d.NextAttemptAt = null;
                    await updater.SetAsync(d, DeliveryStatus.Unknown, "reconciliation", "Provider cannot be queried; awaiting an operator decision.", ct);
                }
            }
            else
            {
                if (report is { Found: true } && report.Status != d.Status)
                    await updater.ApplyProviderStatusAsync(d, new ProviderCallbackEvent { EventId = $"reconcile:{d.Id:N}:{report.Status}", ProviderMessageId = d.ProviderMessageId, Status = report.Status }, "reconciliation", ct);
                var age = now - (d.AcceptedAt ?? now);
                d.NextAttemptAt = d.Status is DeliveryStatus.ProviderAccepted or DeliveryStatus.Sent && age < TimeSpan.FromHours(48) ? now.Add(TimeSpan.FromMinutes(Math.Max(15, age.TotalMinutes))) : null;
            }
            n++;
        }

        // 4. Parked receipts whose delivery now has a provider message id (callback raced the acceptance save).
        var parkedCutoff = now.AddSeconds(-3);
        var parked = await db.DeliveryEvents.AsNoTracking()
            .Where(e => e.DeliveryId == null && e.ProviderMessageId != null && e.RecordedAt < parkedCutoff)
            .Select(e => new { e.ProviderAccountId, e.ProviderMessageId }).Distinct().Take(100).ToListAsync(ct);
        foreach (var p in parked)
        {
            var d = await db.NotificationDeliveries.FirstOrDefaultAsync(x => x.ProviderAccountId == p.ProviderAccountId && x.ProviderMessageId == p.ProviderMessageId, ct);
            if (d is not null) n += await updater.ApplyParkedCallbacksAsync(d, ct);
        }
        await db.SaveChangesAsync(ct);

        // 5. Queued deliveries whose dispatch message was lost or dead-lettered.
        var stale = now.AddMinutes(-10);
        var orphans = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.Status == DeliveryStatus.Queued && d.UpdatedAt < stale && (d.LeaseExpiresAt == null || d.LeaseExpiresAt < now))
            .OrderBy(d => d.UpdatedAt).Select(d => new { d.Id, d.TenantId }).Take(100).ToListAsync(ct);
        foreach (var o in orphans)
        {
            Outbox.Add(db, MessageTypes.DispatchDelivery, new DispatchDeliveryMessage(o.Id), o.TenantId);
            await db.NotificationDeliveries.Where(d => d.Id == o.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdatedAt, now), ct);
        }
        n += orphans.Count;
        await db.SaveChangesAsync(ct);
        return n;
    }
}

/// <summary>Periodic provider health checks; records health events on transitions and raises alerts.</summary>
public sealed class ProviderHealthWorker(IServiceScopeFactory scopes, WorkerStatus status, CircuitBreakerRegistry circuits, ILogger<ProviderHealthWorker> logger)
    : PeriodicWorker(scopes, status, logger)
{
    protected override string Name => "provider-health";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(60);

    protected override async Task<int> TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<NotificationHubDbContext>();
        var registry = sp.GetRequiredService<ProviderRegistry>();
        var accounts = await db.ProviderAccounts.Where(a => a.Enabled).ToListAsync(ct);
        foreach (var a in accounts)
        {
            ProviderHealth h;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                h = await registry.Get(a).CheckHealthAsync(cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                h = ProviderHealth.Unhealthy(Redact.Sanitize(ex.Message, 500)!);
            }
            var newStatus = circuits.IsOpen(a.Id) ? ProviderHealthStatus.CircuitOpen : h.Status;
            if (newStatus != a.HealthStatus)
            {
                db.ProviderHealthEvents.Add(new ProviderHealthEvent { ProviderAccountId = a.Id, Status = newStatus, LatencyMs = h.LatencyMs ?? (int)sw.ElapsedMilliseconds, Detail = h.Detail });
                if (newStatus == ProviderHealthStatus.Unhealthy)
                    db.SystemAlerts.Add(new SystemAlert { TenantId = a.TenantId, Severity = "error", Source = "provider", Key = $"unhealthy:{a.Id}", Message = $"Provider '{a.Name}' is unhealthy: {h.Detail}" });
            }
            a.HealthStatus = newStatus;
            a.LastHealthCheckAt = DateTime.UtcNow;
            a.LastHealthDetail = h.Detail;
        }
        await db.SaveChangesAsync(ct);
        return accounts.Count;
    }
}

/// <summary>Retention: purges message bodies and old records per tenant policy; trims dedup/queue/heartbeat tables.</summary>
public sealed class RetentionWorker(IServiceScopeFactory scopes, WorkerStatus status, IOptions<HubOptions> options, ILogger<RetentionWorker> logger)
    : PeriodicWorker(scopes, status, logger)
{
    protected override string Name => "retention";
    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task<int> TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<NotificationHubDbContext>();
        var now = DateTime.UtcNow;
        var n = 0;
        foreach (var t in await db.Tenants.AsNoTracking().ToListAsync(ct))
        {
            var bodyCutoff = now.AddDays(-Math.Max(1, t.BodyRetentionDays));
            n += await db.NotificationDeliveries.Where(d => d.TenantId == t.Id && d.CreatedAt < bodyCutoff && d.ContentCipher != null && d.CompletedAt != null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ContentCipher, (string?)null).SetProperty(d => d.ContentPurgedAt, now), ct);
            n += await db.NotificationRequests.Where(r => r.TenantId == t.Id && r.CreatedAt < bodyCutoff && r.VariablesCipher != null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.VariablesCipher, (string?)null), ct);
            n += await db.InboundMessages.Where(m => m.TenantId == t.Id && m.ReceivedAt < bodyCutoff && m.Body != null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Body, "[purged]"), ct);

            var dataCutoff = now.AddDays(-Math.Max(30, t.DataRetentionDays));
            var oldRequests = db.NotificationRequests.Where(r => r.TenantId == t.Id && r.CreatedAt < dataCutoff).Select(r => r.Id);
            var oldDeliveries = db.NotificationDeliveries.Where(d => oldRequests.Contains(d.RequestId)).Select(d => d.Id);
            await db.DeliveryAttempts.Where(a => oldDeliveries.Contains(a.DeliveryId)).ExecuteDeleteAsync(ct);
            await db.DeliveryEvents.Where(e => e.DeliveryId != null && oldDeliveries.Contains(e.DeliveryId.Value)).ExecuteDeleteAsync(ct);
            await db.InAppMessages.Where(m => oldDeliveries.Contains(m.DeliveryId)).ExecuteDeleteAsync(ct);
            await db.NotificationDeliveries.Where(d => oldRequests.Contains(d.RequestId)).ExecuteDeleteAsync(ct);
            await db.NotificationRecipients.Where(r => oldRequests.Contains(r.RequestId)).ExecuteDeleteAsync(ct);
            n += await db.NotificationRequests.Where(r => r.TenantId == t.Id && r.CreatedAt < dataCutoff).ExecuteDeleteAsync(ct);
        }
        var dedupCutoff = now.AddDays(-options.Value.InboxRetentionDays);
        n += await db.InboxDeduplication.Where(x => x.ProcessedAt < dedupCutoff).ExecuteDeleteAsync(ct);
        n += await db.OutboxMessages.Where(o => o.PublishedAt != null && o.PublishedAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        n += await db.QueueMessages.Where(q => q.DeadLetteredAt != null && q.DeadLetteredAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
        n += await db.WorkerHeartbeats.Where(h => h.LastHeartbeatAt < now.AddDays(-2)).ExecuteDeleteAsync(ct);
        n += await db.DeliveryEvents.Where(e => e.DeliveryId == null && e.RecordedAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        return n;
    }
}

/// <summary>Feeds simulated delivery receipts from mock providers through the real callback pipeline.</summary>
public sealed class MockCallbackPump(IServiceScopeFactory scopes, MockProviderState state, WorkerStatus status, ILogger<MockCallbackPump> logger)
    : PeriodicWorker(scopes, status, logger)
{
    protected override string Name => "mock-receipts";
    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(200);

    protected override async Task<int> TickAsync(IServiceProvider sp, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = new List<PendingMockCallback>();
        var later = new List<PendingMockCallback>();
        while (state.PendingCallbacks.TryDequeue(out var cb)) (cb.DueAt <= now ? due : later).Add(cb);
        foreach (var cb in later) state.PendingCallbacks.Enqueue(cb);
        if (due.Count == 0) return 0;
        var db = sp.GetRequiredService<NotificationHubDbContext>();
        var processor = sp.GetRequiredService<CallbackProcessor>();
        foreach (var group in due.GroupBy(c => c.AccountId))
        {
            var account = await db.ProviderAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == group.Key, ct);
            if (account is null) continue;
            try { await processor.ApplyAsync(account, group.OrderBy(c => c.DueAt).Select(c => c.Event).ToList(), "provider_callback", ct); }
            catch (Exception ex)
            {
                // Receipt raced the dispatcher's own save: retry shortly (a real provider would redeliver the callback).
                logger.LogDebug(ex, "Mock receipt application failed; requeueing");
                db.ChangeTracker.Clear();
                foreach (var cb in group) state.PendingCallbacks.Enqueue(cb with { DueAt = now.AddMilliseconds(500) });
            }
        }
        return due.Count;
    }
}
