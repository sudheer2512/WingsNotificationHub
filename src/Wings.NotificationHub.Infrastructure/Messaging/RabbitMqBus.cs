using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Wings.NotificationHub.Infrastructure.Common;

namespace Wings.NotificationHub.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ transport (Messaging:Transport = "rabbitmq"). Durable queues with dead-letter exchanges, persistent messages,
/// publisher confirms and manual acks. Redelivery is bounded by an x-delivery-count style header; poison messages go to the DLQ.
/// The outbox relay remains the only publisher, so business changes and messages stay atomic.
/// </summary>
public sealed class RabbitMqConnection(IConfiguration config, ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private IConnection? _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public const string Exchange = "wnh";
    public const string DeadLetterExchange = "wnh.dlx";

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true }) return _connection;
        await _lock.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            var factory = new ConnectionFactory
            {
                Uri = new Uri(config["Messaging:RabbitMq:Uri"] ?? "amqp://guest:guest@localhost:5672/"),
                ClientProvidedName = "wings-notification-hub",
                AutomaticRecoveryEnabled = true,
            };
            _connection = await factory.CreateConnectionAsync(ct);
            await using var ch = await _connection.CreateChannelAsync(cancellationToken: ct);
            await ch.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
            await ch.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
            foreach (var q in Queues.All)
            {
                await ch.QueueDeclareAsync(q, durable: true, exclusive: false, autoDelete: false,
                    arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadLetterExchange, ["x-dead-letter-routing-key"] = q + ".dlq", ["x-queue-type"] = "quorum", ["x-delivery-limit"] = 10 },
                    cancellationToken: ct);
                await ch.QueueBindAsync(q, Exchange, q, cancellationToken: ct);
                await ch.QueueDeclareAsync(q + ".dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
                await ch.QueueBindAsync(q + ".dlq", DeadLetterExchange, q + ".dlq", cancellationToken: ct);
            }
            logger.LogInformation("Connected to RabbitMQ and declared topology");
            return _connection;
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}

public sealed class RabbitMqMessageBus(RabbitMqConnection connection) : IMessageBus
{
    public string Name => "rabbitmq";

    public async Task PublishAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken ct)
    {
        var conn = await connection.GetAsync(ct);
        await using var ch = await conn.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        foreach (var m in messages)
        {
            var props = new BasicProperties
            {
                Persistent = true, MessageId = m.MessageId.ToString(), Type = m.Type, CorrelationId = m.CorrelationId, ContentType = "application/json",
            };
            // With tracking enabled the call completes when the broker confirms (throws on nack), so the outbox marks rows only after confirmation.
            await ch.BasicPublishAsync(RabbitMqConnection.Exchange, MessageTypes.QueueFor(m.Type), mandatory: true, props, Encoding.UTF8.GetBytes(m.PayloadJson), ct);
        }
    }

    public async Task<IReadOnlyDictionary<string, QueueStats>> GetStatsAsync(CancellationToken ct)
    {
        var conn = await connection.GetAsync(ct);
        await using var ch = await conn.CreateChannelAsync(cancellationToken: ct);
        var result = new Dictionary<string, QueueStats>();
        foreach (var q in Queues.All)
        {
            var ok = await ch.QueueDeclarePassiveAsync(q, ct);
            var dlq = await ch.QueueDeclarePassiveAsync(q + ".dlq", ct);
            result[q] = new QueueStats(ok.MessageCount, ok.ConsumerCount, dlq.MessageCount);
        }
        return result;
    }
}

public sealed class RabbitMqConsumer(
    RabbitMqConnection connection,
    MessageDispatcher dispatcher,
    WorkerStatus status,
    IOptions<WorkerOptions> options,
    ILogger<RabbitMqConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var conn = await connection.GetAsync(stoppingToken);
        var channels = new List<IChannel>();
        foreach (var q in Queues.All)
        {
            var prefetch = (ushort)(q == Queues.Dispatch ? options.Value.DispatchConcurrency : Math.Max(2, options.Value.DispatchConcurrency / 2));
            var ch = await conn.CreateChannelAsync(new CreateChannelOptions(false, false, consumerDispatchConcurrency: prefetch), stoppingToken);
            await ch.BasicQosAsync(0, prefetch, false, stoppingToken);
            var worker = $"rabbit:{q}";
            status.Started(worker);
            var consumer = new AsyncEventingBasicConsumer(ch);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                try
                {
                    await dispatcher.DispatchAsync(new MessageEnvelope
                    {
                        MessageId = Guid.TryParse(ea.BasicProperties.MessageId, out var id) ? id : Guid.NewGuid(),
                        Type = ea.BasicProperties.Type ?? "", PayloadJson = Encoding.UTF8.GetString(ea.Body.Span), CorrelationId = ea.BasicProperties.CorrelationId,
                        DeliveryCount = ea.Redelivered ? 2 : 1,
                    }, stoppingToken);
                    await ch.BasicAckAsync(ea.DeliveryTag, false, CancellationToken.None);
                    status.Processed(worker);
                }
                catch (Exception ex)
                {
                    status.Failed(worker, ex.Message);
                    logger.LogError(ex, "RabbitMQ handler failed for {Type}", ea.BasicProperties.Type);
                    // Requeue; the quorum queue's delivery limit routes poison messages to the DLQ.
                    await ch.BasicNackAsync(ea.DeliveryTag, false, requeue: true, CancellationToken.None);
                }
            };
            await ch.BasicConsumeAsync(q, autoAck: false, consumer, stoppingToken);
            channels.Add(ch);
        }
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
        foreach (var ch in channels) await ch.CloseAsync();
    }
}
