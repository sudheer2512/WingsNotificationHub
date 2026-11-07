using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.Infrastructure.Messaging;

public static class MessageTypes
{
    public const string DispatchDelivery = "delivery.dispatch";
    public const string DeliveryStatusChanged = "delivery.status_changed";
    public const string ProcessEvent = "event.process";
    public const string WebhookDeliver = "webhook.deliver";
    public const string InboundReceived = "inbound.received";
    public const string CampaignLaunch = "campaign.launch";
    public const string ProcessCallback = "callback.process";

    /// <summary>Queue per workload so slow fan-out never starves message dispatch.</summary>
    public static string QueueFor(string type) => type switch
    {
        DispatchDelivery => Queues.Dispatch,
        WebhookDeliver => Queues.Webhooks,
        _ => Queues.Events,
    };
}

public static class Queues
{
    public const string Dispatch = "wnh.dispatch";
    public const string Events = "wnh.events";
    public const string Webhooks = "wnh.webhooks";
    public static readonly string[] All = [Dispatch, Events, Webhooks];
}

public sealed record DispatchDeliveryMessage(Guid DeliveryId);
public sealed record DeliveryStatusChangedMessage(Guid DeliveryId, Guid RequestId, string Status, string? PreviousStatus, DateTime OccurredAt)
{
    public string? RequestStatus { get; init; }
    /// <summary>The request reached a final aggregate status with this change (emit notification.completed).</summary>
    public bool RequestCompleted { get; init; }
}
public sealed record ProcessEventMessage(Guid EventId);
public sealed record WebhookDeliverMessage(Guid SubscriptionId, Guid EventId, string EventType, string PayloadJson, int Attempt);
public sealed record InboundReceivedMessage(Guid InboundMessageId);
public sealed record CampaignLaunchMessage(Guid CampaignId);

public sealed class MessageEnvelope
{
    public Guid MessageId { get; init; }
    public string Type { get; init; } = "";
    public string PayloadJson { get; init; } = "{}";
    public Guid? TenantId { get; init; }
    public string? CorrelationId { get; init; }
    public int DeliveryCount { get; init; }

    public T Payload<T>() => Json.Deserialize<T>(PayloadJson) ?? throw new InvalidOperationException($"Empty payload for {Type}.");
}

/// <summary>Transport abstraction: database queue (default) or RabbitMQ. Publishing happens only from the outbox relay.</summary>
public interface IMessageBus
{
    string Name { get; }
    Task PublishAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken ct);
    /// <summary>Approximate queue depths for monitoring.</summary>
    Task<IReadOnlyDictionary<string, QueueStats>> GetStatsAsync(CancellationToken ct);
}

public sealed record QueueStats(long Ready, long InFlight, long DeadLettered);

public interface IMessageHandler
{
    string Type { get; }
    Task HandleAsync(MessageEnvelope message, CancellationToken ct);
}

/// <summary>Adds outbox rows to the caller's DbContext so they commit atomically with the business change.</summary>
public static class Outbox
{
    public static void Add<T>(NotificationHubDbContext db, string type, T payload, Guid? tenantId = null, DateTime? availableAt = null, string? correlationId = null)
        => db.OutboxMessages.Add(new OutboxMessage
        {
            Type = type,
            PayloadJson = Json.Serialize(payload),
            TenantId = tenantId,
            AvailableAt = availableAt ?? DateTime.UtcNow,
            CorrelationId = correlationId ?? System.Diagnostics.Activity.Current?.TraceId.ToString(),
        });
}

/// <summary>Routes envelopes to their handler in a fresh DI scope.</summary>
public sealed class MessageDispatcher(IServiceScopeFactory scopes, ILogger<MessageDispatcher> logger)
{
    public async Task DispatchAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity($"consume {envelope.Type}", System.Diagnostics.ActivityKind.Consumer);
        activity?.SetTag("messaging.message.id", envelope.MessageId);
        using var scope = scopes.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IMessageHandler>().FirstOrDefault(h => h.Type == envelope.Type);
        if (handler is null)
        {
            logger.LogWarning("No handler for message type {Type}; dropping {MessageId}", envelope.Type, envelope.MessageId);
            return;
        }
        using (logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = envelope.CorrelationId, ["MessageType"] = envelope.Type }))
            await handler.HandleAsync(envelope, ct);
    }
}

/// <summary>Durable queue in the application database. No extra infrastructure; competing consumers use row leases.</summary>
public sealed class DatabaseMessageBus(IServiceScopeFactory scopes) : IMessageBus
{
    public string Name => "database";

    public async Task PublishAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
        var ids = messages.Select(m => m.MessageId).ToList();
        // Publishing is at-least-once; skip envelopes a previous (crashed) relay run already enqueued.
        var existing = await db.QueueMessages.Where(q => ids.Contains(q.MessageId)).Select(q => q.MessageId).ToListAsync(ct);
        foreach (var m in messages.Where(m => !existing.Contains(m.MessageId)))
            db.QueueMessages.Add(new QueueMessage
            {
                MessageId = m.MessageId, Queue = MessageTypes.QueueFor(m.Type), Type = m.Type,
                PayloadJson = m.PayloadJson, TenantId = m.TenantId, CorrelationId = m.CorrelationId,
            });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, QueueStats>> GetStatsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
        var now = DateTime.UtcNow;
        var rows = await db.QueueMessages.GroupBy(q => q.Queue).Select(g => new
        {
            Queue = g.Key,
            Ready = g.LongCount(q => q.DeadLetteredAt == null && (q.LeaseExpiresAt == null || q.LeaseExpiresAt < now)),
            InFlight = g.LongCount(q => q.DeadLetteredAt == null && q.LeaseExpiresAt != null && q.LeaseExpiresAt >= now),
            Dead = g.LongCount(q => q.DeadLetteredAt != null),
        }).ToListAsync(ct);
        return Queues.All.ToDictionary(q => q, q => rows.FirstOrDefault(r => r.Queue == q) is { } r ? new QueueStats(r.Ready, r.InFlight, r.Dead) : new QueueStats(0, 0, 0));
    }
}

/// <summary>Consumes the database queues with leases: safe with many API/worker instances.</summary>
public sealed class DatabaseQueueConsumer(
    IServiceScopeFactory scopes,
    MessageDispatcher dispatcher,
    WorkerStatus status,
    IOptions<WorkerOptions> options,
    IOptions<HubOptions> hubOptions,
    ILogger<DatabaseQueueConsumer> logger) : BackgroundService
{
    private const int MaxDeliveries = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        await Task.WhenAll(Queues.All.Select(q => RunQueueAsync(q, q == Queues.Dispatch ? o.DispatchConcurrency : Math.Max(2, o.DispatchConcurrency / 2), stoppingToken)));
    }

    private async Task RunQueueAsync(string queue, int concurrency, CancellationToken ct)
    {
        var worker = $"queue:{queue}";
        status.Started(worker);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batch = await ClaimAsync(queue, Math.Max(concurrency * 2, 10), ct);
                if (batch.Count == 0)
                {
                    await Task.Delay(options.Value.PollIntervalMs, ct);
                    continue;
                }
                await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct }, async (msg, token) =>
                {
                    try
                    {
                        await dispatcher.DispatchAsync(new MessageEnvelope
                        {
                            MessageId = msg.MessageId, Type = msg.Type, PayloadJson = msg.PayloadJson, CorrelationId = msg.CorrelationId, DeliveryCount = msg.DequeueCount,
                        }, token);
                        await CompleteAsync(msg.Id, token);
                        status.Processed(worker);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Shutting down: the lease expires and another instance picks the message up.
                    }
                    catch (Exception ex)
                    {
                        status.Failed(worker, ex.Message);
                        logger.LogError(ex, "Handler for {Type} failed (delivery {Count})", msg.Type, msg.DequeueCount);
                        await FailAsync(msg, ex, CancellationToken.None);
                    }
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                status.Failed(worker, ex.Message);
                logger.LogError(ex, "Queue consumer {Queue} loop error", queue);
                await Task.Delay(2000, ct).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
        status.Stopped(worker);
    }

    private async Task<List<QueueMessage>> ClaimAsync(string queue, int max, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
        var now = DateTime.UtcNow;
        var candidates = await db.QueueMessages
            .Where(q => q.Queue == queue && q.DeadLetteredAt == null && q.VisibleAt <= now && (q.LeaseExpiresAt == null || q.LeaseExpiresAt < now))
            .OrderBy(q => q.VisibleAt).Select(q => q.Id).Take(max).ToListAsync(ct);
        if (candidates.Count == 0) return [];
        var owner = options.Value.InstanceId + ":" + Guid.NewGuid().ToString("N")[..8];
        var until = now.AddSeconds(hubOptions.Value.LeaseSeconds);
        // Conditional update: only rows still unleased are taken, so concurrent consumers never share a message.
        await db.QueueMessages
            .Where(q => candidates.Contains(q.Id) && (q.LeaseExpiresAt == null || q.LeaseExpiresAt < now) && q.DeadLetteredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.LeaseOwner, owner).SetProperty(q => q.LeaseExpiresAt, until).SetProperty(q => q.DequeueCount, q => q.DequeueCount + 1), ct);
        return await db.QueueMessages.AsNoTracking().Where(q => candidates.Contains(q.Id) && q.LeaseOwner == owner).ToListAsync(ct);
    }

    private async Task CompleteAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
        await db.QueueMessages.Where(q => q.Id == id).ExecuteDeleteAsync(ct);
    }

    private async Task FailAsync(QueueMessage msg, Exception ex, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
            var dead = msg.DequeueCount >= MaxDeliveries;
            var visible = DateTime.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, msg.DequeueCount)));
            var error = Redact.Sanitize(ex.GetType().Name + ": " + ex.Message, 1900);
            await db.QueueMessages.Where(q => q.Id == msg.Id).ExecuteUpdateAsync(s => s
                .SetProperty(q => q.LeaseOwner, (string?)null)
                .SetProperty(q => q.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(q => q.VisibleAt, visible)
                .SetProperty(q => q.LastError, error)
                .SetProperty(q => q.DeadLetteredAt, dead ? DateTime.UtcNow : null), ct);
            if (dead)
            {
                db.SystemAlerts.Add(new SystemAlert { Severity = "error", Source = "queue", Key = $"dlq:{msg.Id}", Message = $"Message {msg.Type} {msg.MessageId} moved to the dead-letter queue: {error}" });
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception inner)
        {
            logger.LogError(inner, "Could not record failure for queue message {Id}", msg.Id);
        }
    }
}

/// <summary>Relays committed outbox rows to the message bus (transactional outbox pattern).</summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopes,
    IMessageBus bus,
    WorkerStatus status,
    IOptions<WorkerOptions> options,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const string worker = "outbox-relay";
        status.Started(worker);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var n = await RelayOnceAsync(stoppingToken);
                if (n == 0) await Task.Delay(options.Value.PollIntervalMs, stoppingToken);
                else status.Processed(worker, n);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                status.Failed(worker, ex.Message);
                logger.LogError(ex, "Outbox relay failed");
                await Task.Delay(2000, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
        status.Stopped(worker);
    }

    public async Task<int> RelayOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
        var now = DateTime.UtcNow;
        var candidates = await db.OutboxMessages
            .Where(o => o.PublishedAt == null && o.AvailableAt <= now && (o.LockedUntil == null || o.LockedUntil < now))
            .OrderBy(o => o.CreatedAt).Select(o => o.Id).Take(options.Value.BatchSize * 4).ToListAsync(ct);
        if (candidates.Count == 0) return 0;
        var owner = options.Value.InstanceId + ":" + Guid.NewGuid().ToString("N")[..8];
        await db.OutboxMessages
            .Where(o => candidates.Contains(o.Id) && o.PublishedAt == null && (o.LockedUntil == null || o.LockedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LockedBy, owner).SetProperty(o => o.LockedUntil, now.AddSeconds(60)), ct);
        var rows = await db.OutboxMessages.AsNoTracking().Where(o => candidates.Contains(o.Id) && o.LockedBy == owner).OrderBy(o => o.CreatedAt).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        try
        {
            await bus.PublishAsync(rows.Select(r => new MessageEnvelope { MessageId = r.Id, Type = r.Type, PayloadJson = r.PayloadJson, TenantId = r.TenantId, CorrelationId = r.CorrelationId }).ToList(), ct);
            var ids = rows.Select(r => r.Id).ToList();
            await db.OutboxMessages.Where(o => ids.Contains(o.Id)).ExecuteUpdateAsync(s => s.SetProperty(o => o.PublishedAt, DateTime.UtcNow), ct);
        }
        catch (Exception ex)
        {
            var ids = rows.Select(r => r.Id).ToList();
            var err = Redact.Sanitize(ex.Message, 1900);
            await db.OutboxMessages.Where(o => ids.Contains(o.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Attempts, o => o.Attempts + 1).SetProperty(o => o.LastError, err)
                .SetProperty(o => o.LockedUntil, DateTime.UtcNow.AddSeconds(10)), CancellationToken.None);
            throw;
        }
        return rows.Count;
    }
}

/// <summary>Idempotent-consumer helper: records (consumer, key) once; returns false if it was already processed.</summary>
public static class InboxDedup
{
    public static async Task<bool> TryBeginAsync(NotificationHubDbContext db, string consumer, string key, CancellationToken ct)
    {
        if (await db.InboxDeduplication.AnyAsync(x => x.Consumer == consumer && x.MessageKey == key, ct)) return false;
        db.InboxDeduplication.Add(new InboxDeduplication { Consumer = consumer, MessageKey = key.Length > 390 ? Core.Addressing.Sha256Hex(key) : key });
        return true;
    }
}

/// <summary>In-process worker statistics, flushed to WorkerHeartbeats for the monitoring screen.</summary>
public sealed class WorkerStatus
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _entries = new();
    public string InstanceId { get; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    public sealed class Entry
    {
        public string Status = "running";
        public long Processed;
        public long Failed;
        public string? LastError;
        public DateTime StartedAt = DateTime.UtcNow;
        public DateTime LastActivity = DateTime.UtcNow;
    }

    public void Started(string worker) => _entries[worker] = new Entry();
    public void Stopped(string worker) { if (_entries.TryGetValue(worker, out var e)) e.Status = "stopped"; }
    public void Processed(string worker, int n = 1) { var e = _entries.GetOrAdd(worker, _ => new Entry()); Interlocked.Add(ref e.Processed, n); e.LastActivity = DateTime.UtcNow; }
    public void Failed(string worker, string error) { var e = _entries.GetOrAdd(worker, _ => new Entry()); Interlocked.Increment(ref e.Failed); e.LastError = error; e.LastActivity = DateTime.UtcNow; }
    public IReadOnlyDictionary<string, Entry> Snapshot() => _entries;
}

/// <summary>Writes worker heartbeats every 15 s so the monitoring UI sees every instance.</summary>
public sealed class HeartbeatWriter(IServiceScopeFactory scopes, WorkerStatus status, ILogger<HeartbeatWriter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await FlushAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogDebug(ex, "Heartbeat flush failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
        try
        {
            foreach (var (_, e) in status.Snapshot()) e.Status = "stopped";
            await FlushAsync(CancellationToken.None);
        }
        catch { /* best effort on shutdown */ }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>();
        foreach (var (name, e) in status.Snapshot())
        {
            var id = $"{status.InstanceId}/{name}";
            var row = await db.WorkerHeartbeats.FindAsync([id], ct);
            if (row is null) db.WorkerHeartbeats.Add(row = new WorkerHeartbeat { Id = id, Worker = name, Instance = status.InstanceId, StartedAt = e.StartedAt });
            row.Status = e.Status; row.Processed = e.Processed; row.Failed = e.Failed; row.LastError = e.LastError; row.LastHeartbeatAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
