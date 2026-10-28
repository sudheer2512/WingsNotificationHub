using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Messaging;
using Wings.NotificationHub.Infrastructure.Providers;

namespace Wings.NotificationHub.Infrastructure.Delivery;

/// <summary>Optional analytics destination (e.g. ClickHouse). Never on the transactional path: failures are logged and dropped.</summary>
public interface IAnalyticsSink
{
    Task WriteDeliveryEventAsync(NotificationDelivery d, string previousStatus, CancellationToken ct);
}

public sealed class NullAnalyticsSink : IAnalyticsSink
{
    public Task WriteDeliveryEventAsync(NotificationDelivery d, string previousStatus, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Inserts delivery status events into ClickHouse over its HTTP interface (JSONEachRow).</summary>
public sealed class ClickHouseAnalyticsSink(IHttpClientFactory http, IConfiguration config, ILogger<ClickHouseAnalyticsSink> logger) : IAnalyticsSink
{
    public async Task WriteDeliveryEventAsync(NotificationDelivery d, string previousStatus, CancellationToken ct)
    {
        try
        {
            var table = config["Analytics:ClickHouse:Table"] ?? "wnh_delivery_events";
            var row = new JsonObject
            {
                ["event_time"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"), ["delivery_id"] = d.Id, ["request_id"] = d.RequestId,
                ["tenant_id"] = d.TenantId, ["company_id"] = d.CompanyId, ["application_id"] = d.ApplicationId, ["channel"] = ChannelNames.ToWire(d.Channel),
                ["provider_type"] = d.ProviderType, ["provider_account_id"] = d.ProviderAccountId, ["status"] = d.Status.ToString(), ["previous_status"] = previousStatus,
                ["attempts"] = d.AttemptCount, ["units"] = d.Units, ["cost"] = d.Cost, ["message_class"] = d.MessageClass.ToString(), ["campaign_id"] = d.CampaignId,
                ["latency_ms"] = d.AcceptedAt is { } a ? (long)(a - d.CreatedAt).TotalMilliseconds : null,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{config["Analytics:ClickHouse:Url"]!.TrimEnd('/')}/?query={Uri.EscapeDataString($"INSERT INTO {table} FORMAT JSONEachRow")}")
            { Content = new StringContent(row.ToJsonString()) };
            if (config["Analytics:ClickHouse:User"] is { } user) req.Headers.Add("X-ClickHouse-User", user);
            if (config["Analytics:ClickHouse:Password"] is { } pw) req.Headers.Add("X-ClickHouse-Key", pw);
            using var resp = await http.CreateClient("analytics").SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) logger.LogWarning("ClickHouse insert failed: {Status}", resp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ClickHouse analytics write failed (ignored)");
        }
    }
}

/// <summary>Fans delivery status changes out to subscribed webhooks and the analytics sink.</summary>
public sealed class DeliveryStatusFanoutHandler(NotificationHubDbContext db, IAnalyticsSink analytics) : IMessageHandler
{
    public string Type => MessageTypes.DeliveryStatusChanged;

    public async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var m = message.Payload<DeliveryStatusChangedMessage>();
        var d = await db.NotificationDeliveries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == m.DeliveryId, ct);
        if (d is null) return;
        await analytics.WriteDeliveryEventAsync(d, m.PreviousStatus ?? "", ct);
        var request = await db.NotificationRequests.AsNoTracking().FirstAsync(r => r.Id == d.RequestId, ct);
        var subs = await WebhookFanout.SubscriptionsAsync(db, d.ApplicationId, d.CompanyId, ct);
        var data = new JsonObject
        {
            ["notificationId"] = d.RequestId, ["deliveryId"] = d.Id, ["channel"] = ChannelNames.ToWire(d.Channel), ["status"] = m.Status,
            ["previousStatus"] = m.PreviousStatus, ["provider"] = d.ProviderType, ["providerMessageId"] = d.ProviderMessageId,
            ["recipient"] = Addressing.Mask(d.Channel, d.Address), ["attempts"] = d.AttemptCount, ["failureCode"] = d.FailureCode,
            ["notificationType"] = request.NotificationTypeCode, ["correlationId"] = request.CorrelationId,
            ["metadata"] = request.MetadataJson is null ? null : JsonNode.Parse(request.MetadataJson),
        };
        WebhookFanout.Enqueue(db, subs, WebhookEventTypes.DeliveryStatusChanged, message.MessageId, d.TenantId, d.CompanyId, d.ApplicationId, data);
        if (m.RequestCompleted)
            WebhookFanout.Enqueue(db, subs, WebhookEventTypes.NotificationCompleted, GuidFrom(message.MessageId, 1), d.TenantId, d.CompanyId, d.ApplicationId,
                new JsonObject { ["notificationId"] = d.RequestId, ["status"] = m.RequestStatus, ["correlationId"] = request.CorrelationId, ["notificationType"] = request.NotificationTypeCode });
        await db.SaveChangesAsync(ct);
    }

    private static Guid GuidFrom(Guid g, byte salt)
    {
        var b = g.ToByteArray();
        b[0] ^= salt;
        return new Guid(b);
    }
}

public static class WebhookFanout
{
    public static async Task<List<WebhookSubscription>> SubscriptionsAsync(NotificationHubDbContext db, Guid? applicationId, Guid? companyId, CancellationToken ct)
    {
        if (applicationId is null) return [];
        return await db.WebhookSubscriptions.AsNoTracking()
            .Where(s => s.ApplicationId == applicationId && s.Enabled && (s.CompanyId == null || s.CompanyId == companyId)).ToListAsync(ct);
    }

    /// <summary>Queues one webhook delivery per matching subscription. The event id is stable so receivers can deduplicate.</summary>
    public static int Enqueue(NotificationHubDbContext db, IEnumerable<WebhookSubscription> subs, string eventType, Guid eventId, Guid tenantId, Guid? companyId, Guid? applicationId, JsonObject data)
    {
        var envelope = new WebhookEventEnvelope { Id = eventId, Type = eventType, OccurredAt = DateTimeOffset.UtcNow, TenantId = tenantId, CompanyId = companyId, ApplicationId = applicationId, Data = data };
        var json = Json.Serialize(envelope);
        var n = 0;
        foreach (var s in subs.Where(s => s.EventTypes.Contains(eventType) || s.EventTypes.Contains("*")))
        {
            Outbox.Add(db, MessageTypes.WebhookDeliver, new WebhookDeliverMessage(s.Id, eventId, eventType, json, 1), tenantId);
            n++;
        }
        return n;
    }
}

/// <summary>Delivers application webhooks with signature, retries with backoff and auto-disables persistently failing endpoints.</summary>
public sealed class WebhookDeliveryHandler(NotificationHubDbContext db, IHttpClientFactory http, ISecretProtector protector, UrlGuard guard, ILogger<WebhookDeliveryHandler> logger) : IMessageHandler
{
    public const int MaxAttempts = 8;
    public const int DisableAfterConsecutiveFailures = 100;
    public string Type => MessageTypes.WebhookDeliver;

    public async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var m = message.Payload<WebhookDeliverMessage>();
        var sub = await db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == m.SubscriptionId, ct);
        if (sub is null || !sub.Enabled) return;
        // Idempotent consumer: the same (subscription, event, attempt) is posted at most once even if redelivered.
        if (!await InboxDedup.TryBeginAsync(db, "webhook", $"{m.SubscriptionId:N}:{m.EventId:N}:{m.Attempt}", ct)) return;
        var result = await WebhookPoster.PostAsync(http, guard, sub.Url, protector.Unprotect(sub.SecretCipher, Purposes.WebhookSecret), m.PayloadJson, m.EventId.ToString(), ct);
        var attempt = new WebhookDeliveryAttempt
        {
            SubscriptionId = sub.Id, TenantId = sub.TenantId, EventId = m.EventId, EventType = m.EventType, AttemptNumber = m.Attempt,
            Success = result.Success, ResponseCode = result.Status, Error = result.Error, DurationMs = result.DurationMs,
        };
        db.WebhookDeliveryAttempts.Add(attempt);
        if (result.Success) sub.ConsecutiveFailures = 0;
        else
        {
            sub.ConsecutiveFailures++;
            var retryable = result.Status is null or >= 500 or 408 or 429 && !(result.Error?.StartsWith("blocked") ?? false);
            if (retryable && m.Attempt < MaxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(3600, 15 * Math.Pow(3, m.Attempt - 1)) * (0.75 + Random.Shared.NextDouble() / 2));
                attempt.NextRetryAt = DateTime.UtcNow.Add(delay);
                Outbox.Add(db, MessageTypes.WebhookDeliver, m with { Attempt = m.Attempt + 1 }, sub.TenantId, attempt.NextRetryAt);
            }
            if (sub.ConsecutiveFailures >= DisableAfterConsecutiveFailures)
            {
                sub.Enabled = false;
                sub.DisabledAt = DateTime.UtcNow;
                db.SystemAlerts.Add(new SystemAlert { TenantId = sub.TenantId, Severity = "error", Source = "webhooks", Key = $"webhook-disabled:{sub.Id}", Message = $"Webhook '{sub.Name}' was disabled after {sub.ConsecutiveFailures} consecutive failures." });
            }
            logger.LogInformation("Webhook {Subscription} attempt {Attempt} for {EventType} failed: {Status} {Error}", sub.Id, m.Attempt, m.EventType, result.Status, result.Error);
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Routes inbound messages to the originating application (webhook) and raises a business event for rules.</summary>
public sealed class InboundRoutingHandler(NotificationHubDbContext db) : IMessageHandler
{
    public string Type => MessageTypes.InboundReceived;

    public async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var id = message.Payload<InboundReceivedMessage>().InboundMessageId;
        var msg = await db.InboundMessages.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (msg is null || msg.RoutingStatus != InboundRoutingStatus.Pending) return;
        var subs = await WebhookFanout.SubscriptionsAsync(db, msg.ApplicationId, msg.CompanyId, ct);
        var data = new JsonObject
        {
            ["inboundMessageId"] = msg.Id, ["threadId"] = msg.ThreadId, ["channel"] = ChannelNames.ToWire(msg.Channel), ["from"] = msg.From,
            ["to"] = msg.To, ["body"] = msg.Body, ["messageType"] = msg.MessageType, ["contactId"] = msg.ContactId,
            ["inReplyToNotificationId"] = msg.CorrelatedRequestId, ["inReplyToDeliveryId"] = msg.CorrelatedDeliveryId, ["receivedAt"] = msg.ReceivedAt,
        };
        var n = WebhookFanout.Enqueue(db, subs, WebhookEventTypes.InboundMessageReceived, msg.Id, msg.TenantId, msg.CompanyId, msg.ApplicationId, data);
        msg.RoutingStatus = n > 0 ? InboundRoutingStatus.Routed : InboundRoutingStatus.NoSubscriber;
        msg.RoutingDetail ??= n > 0 ? $"queued to {n} webhook(s)" : msg.ApplicationId is null ? "no originating application" : "application has no inbound webhook subscription";

        // Workflow trigger: rules may react to "inbound.message_received" (e.g. auto-acknowledge or escalate to staff).
        if (msg.ApplicationId is { } app && msg.CompanyId is { } company
            && await db.NotificationRules.AnyAsync(r => r.TenantId == msg.TenantId && r.Enabled && r.EventType == "inbound.message_received", ct))
        {
            var ev = new BusinessEvent
            {
                TenantId = msg.TenantId, CompanyId = company, ApplicationId = app, EventType = "inbound.message_received", ActorId = "system:inbound",
                PayloadJson = data.ToJsonString(), IdempotencyKey = $"inbound:{msg.Id:N}",
            };
            db.BusinessEvents.Add(ev);
            Outbox.Add(db, MessageTypes.ProcessEvent, new ProcessEventMessage(ev.Id), msg.TenantId);
        }
        await db.SaveChangesAsync(ct);
    }
}
