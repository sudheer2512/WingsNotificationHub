using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Security;

namespace Wings.NotificationHub.Infrastructure.Providers;

/// <summary>A message captured by a mock provider (demo inbox and test assertions).</summary>
public sealed record MockSentMessage(string ProviderMessageId, Guid AccountId, string AccountName, Channel Channel, string To, string? From,
    string? Subject, string Body, string IdempotencyKey, DateTime SentAt, Guid DeliveryId, string? WhatsAppTemplate);

/// <summary>
/// Shared state for mock providers: captured messages, idempotency memory and scheduled delivery receipts.
/// Mock behaviour is driven by the destination address so demos and tests can provoke every path:
/// <list type="bullet">
/// <item><c>fail-permanent</c> / numbers ending 0000 → permanent rejection (invalid address).</item>
/// <item><c>fail-transient</c> / ending 1111 → transient failure on the first <c>transientFailures</c> attempts.</item>
/// <item><c>timeout</c> / ending 2222 → ambiguous: the message IS accepted but the response is lost (first attempt).</item>
/// <item><c>undeliverable</c> / ending 3333 → accepted, then a "failed" delivery receipt.</item>
/// <item><c>slow</c> → sleeps past the provider timeout.</item>
/// </list>
/// </summary>
public sealed class MockProviderState
{
    private readonly ConcurrentQueue<MockSentMessage> _sent = new();
    private readonly ConcurrentDictionary<string, string> _byIdempotencyKey = new();
    private readonly ConcurrentDictionary<string, int> _attempts = new();
    public ConcurrentQueue<PendingMockCallback> PendingCallbacks { get; } = new();
    /// <summary>Global switch used by tests/demos to simulate a provider outage (connection refused).</summary>
    public ConcurrentDictionary<Guid, bool> Outages { get; } = new();

    public IReadOnlyList<MockSentMessage> Sent => _sent.ToArray();
    public void Clear() { _sent.Clear(); _byIdempotencyKey.Clear(); _attempts.Clear(); }

    internal int NextAttempt(string key) => _attempts.AddOrUpdate(key, 1, (_, n) => n + 1);
    internal bool TryGetExisting(string idempotencyKey, out string messageId) => _byIdempotencyKey.TryGetValue(idempotencyKey, out messageId!);
    internal void Record(MockSentMessage m)
    {
        _byIdempotencyKey[m.IdempotencyKey] = m.ProviderMessageId;
        _sent.Enqueue(m);
    }
    public MockSentMessage? Find(string providerMessageId) => _sent.FirstOrDefault(m => m.ProviderMessageId == providerMessageId);
    public MockSentMessage? FindByIdempotencyKey(string key) => _byIdempotencyKey.TryGetValue(key, out var id) ? Find(id) : null;
}

public sealed record PendingMockCallback(Guid AccountId, ProviderCallbackEvent Event, DateTime DueAt);

public sealed class MockProviderFactory(MockProviderState state) : IProviderFactory
{
    public string ProviderType => "mock";
    public IReadOnlyList<Channel> Channels => Enum.GetValues<Channel>();
    public string DisplayName => "Mock provider (demo/testing)";
    public IReadOnlyList<string> SecretKeys => [];
    public IReadOnlyList<string> SettingKeys => ["latencyMs", "transientFailures", "autoDeliver", "autoRead", "deliverAfterMs", "supportsIdempotency", "supportsStatusQuery", "unitCost"];
    public INotificationProvider Create(ProviderAccountConfig config) => new MockProvider(config, state);
}

public sealed class MockProvider(ProviderAccountConfig config, MockProviderState state) : INotificationProvider, IStatusQueryProvider
{
    private int LatencyMs => int.TryParse(config.Setting("latencyMs"), out var v) ? v : 20;
    private int TransientFailures => int.TryParse(config.Setting("transientFailures"), out var v) ? v : 1;
    private bool AutoDeliver => config.Setting("autoDeliver") != "false";
    private bool AutoRead => config.Setting("autoRead") == "true";
    private int DeliverAfterMs => int.TryParse(config.Setting("deliverAfterMs"), out var v) ? v : 300;
    private bool Idempotent => config.Setting("supportsIdempotency") != "false";

    public ProviderCapabilities Capabilities => new()
    {
        SupportsIdempotency = Idempotent, SupportsStatusQuery = config.Setting("supportsStatusQuery") != "false", SupportsDeliveryReceipts = true,
        SupportsReadReceipts = config.Channel is Channel.WhatsApp or Channel.Email, SupportsInbound = config.Channel is Channel.WhatsApp or Channel.Sms or Channel.Email,
    };

    public async Task<SendResult> SendAsync(NotificationMessage message, CancellationToken ct)
    {
        if (state.Outages.TryGetValue(config.AccountId, out var down) && down)
            return SendResult.Transient("connection_refused", "Mock provider outage (connection refused).", "503");
        // Push deliveries are addressed to a device registration; behaviour keys off the device token.
        var to = (message.Push?.Token ?? message.To).ToLowerInvariant();
        // Counted per message across provider accounts, so a transient failure on one provider lets failover succeed.
        var attempt = state.NextAttempt(message.IdempotencyKey);
        if (to.Contains("slow")) await Task.Delay(TimeSpan.FromSeconds(600), ct);
        await Task.Delay(LatencyMs, ct);

        if (Idempotent && state.TryGetExisting(message.IdempotencyKey, out var existingId))
            return SendResult.Accepted(existingId, "duplicate submission ignored (idempotency key)");
        if (to.Contains("fail-permanent") || to.EndsWith("0000"))
            return SendResult.Permanent("invalid_recipient", "Mock: recipient address rejected.", invalidAddress: true, responseCode: "400");
        if ((to.Contains("fail-transient") || to.EndsWith("1111")) && attempt <= TransientFailures)
            return SendResult.Transient("upstream_unavailable", $"Mock: temporary failure (attempt {attempt}).", "503");

        var id = $"mock-{config.Channel.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}"[..36];
        state.Record(new MockSentMessage(id, config.AccountId, config.Name, message.Channel, message.To, message.From, message.Content.Subject ?? message.Content.Title,
            message.Content.Body, message.IdempotencyKey, DateTime.UtcNow, message.DeliveryId, message.Content.WhatsAppTemplate?.Name));

        if (AutoDeliver && message.Channel is not Channel.InApp and not Channel.Webhook)
        {
            var due = DateTime.UtcNow.AddMilliseconds(DeliverAfterMs);
            if (to.Contains("undeliverable") || to.EndsWith("3333"))
                state.PendingCallbacks.Enqueue(new(config.AccountId, new ProviderCallbackEvent
                {
                    EventId = $"evt-{Guid.NewGuid():N}", ProviderMessageId = id, Status = DeliveryStatus.Failed, ErrorCode = "undeliverable", ErrorMessage = "Mock: handset unreachable.", OccurredAt = due,
                }, due));
            else
            {
                if (message.Channel is Channel.Sms or Channel.WhatsApp or Channel.Email)
                    state.PendingCallbacks.Enqueue(new(config.AccountId, new ProviderCallbackEvent { EventId = $"evt-{Guid.NewGuid():N}", ProviderMessageId = id, Status = DeliveryStatus.Sent, OccurredAt = due.AddMilliseconds(-100) }, due.AddMilliseconds(-100)));
                state.PendingCallbacks.Enqueue(new(config.AccountId, new ProviderCallbackEvent { EventId = $"evt-{Guid.NewGuid():N}", ProviderMessageId = id, Status = DeliveryStatus.Delivered, OccurredAt = due }, due));
                if (AutoRead)
                    state.PendingCallbacks.Enqueue(new(config.AccountId, new ProviderCallbackEvent { EventId = $"evt-{Guid.NewGuid():N}", ProviderMessageId = id, Status = DeliveryStatus.Read, OccurredAt = due.AddMilliseconds(500) }, due.AddMilliseconds(500)));
            }
        }

        // Ambiguous simulation: the provider accepted the message but the response never reached us.
        if ((to.Contains("timeout") || to.EndsWith("2222")) && attempt == 1)
            return SendResult.Ambiguous("timeout", "Mock: connection reset after the request was sent; acceptance unknown.");

        var status = message.Channel is Channel.InApp or Channel.Webhook ? DeliveryStatus.Delivered : DeliveryStatus.ProviderAccepted;
        var units = message.Channel == Channel.Sms ? Core.SmsSegmenter.Analyze(message.Content.Body).Segments : 1;
        decimal? cost = decimal.TryParse(config.Setting("unitCost"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c * units : null;
        return SendResult.Accepted(id, $"mock accepted ({message.Channel})", status, units, cost);
    }

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(state.Outages.TryGetValue(config.AccountId, out var down) && down
            ? ProviderHealth.Unhealthy("Mock outage enabled.")
            : ProviderHealth.Healthy("Mock provider ready.", LatencyMs));

    public Task<ProviderStatusReport?> QueryStatusAsync(string? providerMessageId, string idempotencyKey, CancellationToken ct)
    {
        var m = providerMessageId is not null ? state.Find(providerMessageId) : state.FindByIdempotencyKey(idempotencyKey);
        return Task.FromResult<ProviderStatusReport?>(m is null
            ? new ProviderStatusReport { Found = false }
            : new ProviderStatusReport { Found = true, ProviderMessageId = m.ProviderMessageId, Status = AutoDeliver ? DeliveryStatus.Delivered : DeliveryStatus.ProviderAccepted });
    }
}

/// <summary>
/// Mock callback format (also usable by tests over HTTP): JSON {events:[{eventId, messageId, status, errorCode, occurredAt}],
/// inbound:{from,to,body,inReplyTo}} signed with X-Wings-Signature using the account's callback secret.
/// </summary>
public sealed class MockCallbackHandler : ICallbackHandler
{
    public string ProviderType => "mock";

    public Task<CallbackParseResult> ParseAsync(CallbackRequest request, ProviderAccountConfig config, CancellationToken ct)
    {
        var secret = config.Secret("__callbackSecret");
        if (string.IsNullOrEmpty(secret)) return Task.FromResult(CallbackParseResult.Reject("Callback secret not configured."));
        request.Headers.TryGetValue(WebhookSignature.Header, out var sig);
        if (!WebhookSignature.Verify(secret, request.Body, sig, DateTimeOffset.UtcNow))
            return Task.FromResult(CallbackParseResult.Reject("Invalid or expired signature."));
        var root = JsonNode.Parse(request.Body)?.AsObject();
        var events = new List<ProviderCallbackEvent>();
        foreach (var e in root?["events"]?.AsArray() ?? [])
        {
            if (e is null) continue;
            DeliveryStatus? st = Enum.TryParse<DeliveryStatus>(e["status"]?.GetValue<string>(), true, out var s) ? s : null;
            events.Add(new ProviderCallbackEvent
            {
                EventId = e["eventId"]?.GetValue<string>(), ProviderMessageId = e["messageId"]?.GetValue<string>(), Status = st,
                ErrorCode = e["errorCode"]?.GetValue<string>(), ErrorMessage = e["errorMessage"]?.GetValue<string>(),
                OccurredAt = e["occurredAt"] is { } at && DateTime.TryParse(at.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.UtcNow,
                Interaction = e["interaction"]?.GetValue<string>(),
                Suppress = e["bounce"]?.GetValue<bool>() == true ? Contracts.SuppressionReason.HardBounce : e["complaint"]?.GetValue<bool>() == true ? Contracts.SuppressionReason.Complaint : null,
                SuppressAddress = e["address"]?.GetValue<string>(),
            });
        }
        if (root?["inbound"] is JsonObject inb)
            events.Add(new ProviderCallbackEvent
            {
                EventId = inb["messageId"]?.GetValue<string>() ?? $"in-{Guid.NewGuid():N}",
                Inbound = new InboundMessageData
                {
                    From = inb["from"]?.GetValue<string>() ?? "", To = inb["to"]?.GetValue<string>(), Body = inb["body"]?.GetValue<string>(),
                    FromName = inb["name"]?.GetValue<string>(), ProviderMessageId = inb["messageId"]?.GetValue<string>(),
                    InReplyToProviderMessageId = inb["inReplyTo"]?.GetValue<string>(), ButtonPayload = inb["buttonPayload"]?.GetValue<string>(),
                    Subject = inb["subject"]?.GetValue<string>(),
                },
            });
        return Task.FromResult(new CallbackParseResult { Authentic = true, Events = events });
    }
}
