using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Wings.NotificationHub.Infrastructure;
using Wings.NotificationHub.Infrastructure.Common;

// Standalone background processing host: run one or many instances next to the API (Workers:RunInApi=false there)
// to scale dispatch independently. Instances compete for work through leases, so no job is owned twice.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "WingsNotificationHubWorker");
builder.Services.AddSerilog((services, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Wings.NotificationHub.Worker")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {CorrelationId}{NewLine}{Exception}"));
builder.Services.AddNotificationHubInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddNotificationHubWorkers(builder.Configuration);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var otlp = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("wings-notification-hub-worker"))
    .WithTracing(t => { t.AddSource(Telemetry.Name).AddHttpClientInstrumentation(); if (!string.IsNullOrEmpty(otlp)) t.AddOtlpExporter(o => o.Endpoint = new Uri(otlp)); })
    .WithMetrics(m => { m.AddMeter(Telemetry.Name).AddHttpClientInstrumentation(); if (!string.IsNullOrEmpty(otlp)) m.AddOtlpExporter(o => o.Endpoint = new Uri(otlp)); });

var host = builder.Build();
using (var scope = host.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
await host.RunAsync();
