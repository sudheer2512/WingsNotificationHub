using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Delivery;
using Wings.NotificationHub.Infrastructure.Messaging;
using Wings.NotificationHub.Infrastructure.Providers;
using Wings.NotificationHub.Infrastructure.Services;
using Wings.NotificationHub.Infrastructure.Workers;

namespace Wings.NotificationHub.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Core services shared by the API and the worker host.</summary>
    public static IServiceCollection AddNotificationHubInfrastructure(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        services.Configure<HubOptions>(config.GetSection("Hub"));
        services.Configure<WorkerOptions>(config.GetSection("Workers"));

        var provider = config["Database:Provider"] ?? "sqlite";
        var cs = config.GetConnectionString("NotificationHub") ?? "Data Source=App_Data/notification-hub.db";
        services.AddDbContext<NotificationHubDbContext>(o =>
        {
            if (provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
                o.UseSqlServer(cs, sql => sql.EnableRetryOnFailure(5).CommandTimeout(60));
            else
                o.UseSqlite(cs.Contains("Default Timeout", StringComparison.OrdinalIgnoreCase) ? cs : cs + ";Default Timeout=30");
        });

        services.AddMemoryCache();
        services.AddSingleton<ISecretProtector>(sp => AesGcmSecretProtector.FromConfiguration(config,
            sp.GetRequiredService<IOptions<HubOptions>>().Value.DataDirectory, env.IsDevelopment() || env.IsEnvironment("Testing"),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Security")));

        services.AddHttpClient("providers", c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        services.AddHttpClient("analytics", c => c.Timeout = TimeSpan.FromSeconds(10));

        // Redis (optional): distributed cache, shared provider rate limits.
        var redis = config["Redis:Connection"];
        if (!string.IsNullOrWhiteSpace(redis))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
            services.AddStackExchangeRedisCache(o => { o.Configuration = redis; o.InstanceName = "wnh:"; });
            services.AddSingleton<IProviderRateLimiter, RedisProviderRateLimiter>();
        }
        else
        {
            services.AddDistributedMemoryCache();
            services.AddSingleton<IProviderRateLimiter, InMemoryProviderRateLimiter>();
        }

        // Providers (adapters are selected per provider account by ProviderType).
        services.AddSingleton<MockProviderState>();
        services.AddSingleton<CircuitBreakerRegistry>();
        services.AddSingleton<UrlGuard>();
        services.AddSingleton<IProviderFactory, MockProviderFactory>();
        services.AddSingleton<IProviderFactory, SmtpEmailProviderFactory>();
        services.AddSingleton<IProviderFactory, AmazonSesProviderFactory>();
        services.AddSingleton<IProviderFactory, Microsoft365ProviderFactory>();
        services.AddSingleton<IProviderFactory, TwilioSmsProviderFactory>();
        services.AddSingleton<IProviderFactory, Msg91SmsProviderFactory>();
        services.AddSingleton<IProviderFactory, HttpSmsProviderFactory>();
        services.AddSingleton<IProviderFactory, MetaWhatsAppProviderFactory>();
        services.AddSingleton<IProviderFactory, Dialog360WhatsAppProviderFactory>();
        services.AddSingleton<IProviderFactory, FcmPushProviderFactory>();
        services.AddSingleton<IProviderFactory, ApnsPushProviderFactory>();
        services.AddSingleton<IProviderFactory, WebPushProviderFactory>();
        services.AddSingleton<IProviderFactory, InAppProviderFactory>();
        services.AddSingleton<IProviderFactory, WebhookChannelProviderFactory>();
        services.AddSingleton<ICallbackHandler, MockCallbackHandler>();
        services.AddSingleton<ICallbackHandler, SesSnsCallbackHandler>();
        services.AddSingleton<ICallbackHandler, TwilioCallbackHandler>();
        services.AddSingleton<ICallbackHandler>(new WhatsAppCallbackHandler("meta-whatsapp"));
        services.AddSingleton<ICallbackHandler>(new WhatsAppCallbackHandler("360dialog"));
        services.AddSingleton<ProviderRegistry>();

        // Application services.
        services.AddScoped<TenantContextResolver>();
        services.AddScoped<RecipientResolver>();
        services.AddScoped<TemplateResolver>();
        services.AddScoped<UsageService>();
        services.AddScoped<AuditService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<TemplateService>();
        services.AddScoped<ProviderRouter>();
        services.AddScoped<DeliveryStatusUpdater>();
        services.AddScoped<FollowUpService>();
        services.AddScoped<InboundService>();
        services.AddScoped<CallbackProcessor>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<IAttachmentStore, FileAttachmentStore>();
        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<DemoSeeder>();

        // Message handlers (one per message type).
        services.AddScoped<DeliveryDispatcher>();
        services.AddScoped<IMessageHandler>(sp => sp.GetRequiredService<DeliveryDispatcher>());
        services.AddScoped<RuleEngine>();
        services.AddScoped<IMessageHandler>(sp => sp.GetRequiredService<RuleEngine>());
        services.AddScoped<CampaignService>();
        services.AddScoped<IMessageHandler>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<IMessageHandler, DeliveryStatusFanoutHandler>();
        services.AddScoped<IMessageHandler, WebhookDeliveryHandler>();
        services.AddScoped<IMessageHandler, InboundRoutingHandler>();

        if (!string.IsNullOrWhiteSpace(config["Analytics:ClickHouse:Url"])) services.AddSingleton<IAnalyticsSink, ClickHouseAnalyticsSink>();
        else services.AddSingleton<IAnalyticsSink, NullAnalyticsSink>();

        services.AddSingleton<WorkerStatus>();
        services.AddSingleton<MessageDispatcher>();
        if ((config["Hub:MessageTransport"] ?? "database").Equals("rabbitmq", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<RabbitMqConnection>();
            services.AddSingleton<IMessageBus, RabbitMqMessageBus>();
        }
        else services.AddSingleton<IMessageBus, DatabaseMessageBus>();
        return services;
    }

    /// <summary>Background processing: outbox relay, queue consumers, scheduler, reconciliation, health, retention.</summary>
    public static IServiceCollection AddNotificationHubWorkers(this IServiceCollection services, IConfiguration config)
    {
        services.AddHostedService<OutboxRelay>();
        if ((config["Hub:MessageTransport"] ?? "database").Equals("rabbitmq", StringComparison.OrdinalIgnoreCase))
            services.AddHostedService<RabbitMqConsumer>();
        else
            services.AddHostedService<DatabaseQueueConsumer>();
        services.AddHostedService<SchedulerWorker>();
        services.AddHostedService<ReconciliationWorker>();
        services.AddHostedService<ProviderHealthWorker>();
        services.AddHostedService<RetentionWorker>();
        services.AddHostedService<MockCallbackPump>();
        services.AddHostedService<HeartbeatWriter>();
        return services;
    }
}

public sealed class DatabaseInitializer(NotificationHubDbContext db, IConfiguration config, IOptions<HubOptions> options, DemoSeeder seeder, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (config.GetValue("Database:AutoCreate", true))
        {
            if (db.Database.IsSqlServer())
            {
                // SQL Server uses EF migrations (Data/Migrations); DBAs can instead run database/sqlserver/migrate.sql and set AutoCreate=false.
                await db.Database.MigrateAsync(ct);
            }
            else
            {
                var dbFile = db.Database.IsSqlite() ? new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource : null;
                if (dbFile is not null && Path.GetDirectoryName(Path.GetFullPath(dbFile)) is { } d) Directory.CreateDirectory(d);
                await db.Database.EnsureCreatedAsync(ct);
                if (db.Database.IsSqlite()) await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            }
        }
        await seeder.EnsureSystemDataAsync(ct);
        if (options.Value.SeedDemoData) await seeder.SeedDemoAsync(ct);
        logger.LogInformation("Database ready ({Provider})", db.Database.ProviderName);
    }
}
