using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Wings.NotificationHub.Api.Endpoints;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Infrastructure;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "WingsNotificationHub");
builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Wings.NotificationHub.Api")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {CorrelationId}{NewLine}{Exception}"), preserveStaticLogger: true);

builder.Services.AddNotificationHubInfrastructure(builder.Configuration, builder.Environment);
if (builder.Configuration.GetValue("Workers:RunInApi", true))
    builder.Services.AddNotificationHubWorkers(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddHubAuthentication(builder.Configuration);
builder.Services.AddHubRateLimiting(builder.Configuration);
var signalR = builder.Services.AddSignalR();
if (builder.Configuration["Redis:Connection"] is { Length: > 0 } redis) signalR.AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("wnh"));
builder.Services.AddHostedService<RealtimeRelay>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    // Channel keeps its lower-case wire names; options converters take precedence over type attributes.
    o.SerializerOptions.Converters.Add(new Wings.NotificationHub.Contracts.ChannelJsonConverter());
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();

// OpenTelemetry: traces and metrics, exported over OTLP when an endpoint is configured.
var otlp = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("wings-notification-hub-api"))
    .WithTracing(t =>
    {
        t.AddSource(Telemetry.Name).AddAspNetCoreInstrumentation(o => o.Filter = c => !c.Request.Path.StartsWithSegments("/health")).AddHttpClientInstrumentation();
        if (!string.IsNullOrEmpty(otlp)) t.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
    })
    .WithMetrics(m =>
    {
        m.AddMeter(Telemetry.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrEmpty(otlp)) m.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
    });

builder.Services.AddOpenApi("v1", o =>
{
    o.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info = new OpenApiInfo
        {
            Title = "Wings Notification Hub API",
            Version = "v1",
            Description = "Unified, multi-tenant notification platform: email, SMS, WhatsApp, in-app, push, web push and webhooks. " +
                          "Obtain a token with POST /connect/token (client_credentials) and send 'Authorization: Bearer …'. " +
                          "Send Idempotency-Key (or idempotencyKey in the body) on retried requests. 202 Accepted means durably recorded, not delivered.",
        };
        doc.Components ??= new OpenApiComponents();
        doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        doc.Components.SecuritySchemes["oauth2"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2, Description = "Client credentials for applications; admin sessions use the same bearer header.",
            Flows = new OpenApiOAuthFlows { ClientCredentials = new OpenApiOAuthFlow { TokenUrl = new Uri("/connect/token", UriKind.Relative), Scopes = Wings.NotificationHub.Contracts.Scopes.All.ToDictionary(s => s, s => s) } },
        };
        doc.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("oauth2", doc)] = [] }];
        return Task.CompletedTask;
    });
});
builder.Services.AddCors(o => o.AddPolicy("ui", p => p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials().WithExposedHeaders(CorrelationIdMiddleware.Header)));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Only explicitly configured proxies are trusted (loopback by default).
    foreach (var ip in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        o.KnownProxies.Add(IPAddress.Parse(ip));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment() && app.Configuration.GetValue("Https:Redirect", true))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) => ex is not null || ctx.Response.StatusCode >= 500
    ? Serilog.Events.LogEventLevel.Error
    : ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/hubs") ? Serilog.Events.LogEventLevel.Verbose : Serilog.Events.LogEventLevel.Information);
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<QueryEnumNormalizationMiddleware>();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("ui");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.MapScalarApiReference("/api-docs", o => o.WithTitle("Wings Notification Hub API").WithOpenApiRoutePattern("/openapi/{documentName}.json")).AllowAnonymous();

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous().ExcludeFromDescription();
app.MapGet("/health/ready", async (NotificationHubDbContext db, CancellationToken ct) =>
{
    try { await db.Database.ExecuteSqlRawAsync("SELECT 1", ct); return Results.Ok(new { status = "Ready" }); }
    catch (Exception ex) { return Results.Json(new { status = "NotReady", error = ex.GetType().Name }, statusCode: 503); }
}).AllowAnonymous().ExcludeFromDescription();

AuthEndpoints.Map(app);
var api = app.MapGroup("/api/v1").RequireRateLimiting(RateLimiting.Api);
NotificationEndpoints.Map(api);
TemplateEndpoints.Map(api);
RuleEndpoints.Map(api);
RecipientEndpoints.Map(app, api);
ProviderEndpoints.Map(api);
TenancyEndpoints.Map(api);
CampaignEndpoints.Map(api);
CommunicationEndpoints.Map(app, api);
ReportEndpoints.Map(api);
RegistrationEndpoints.Map(api);
if (app.Configuration.GetValue("Hub:EnableDevEndpoints", app.Environment.IsDevelopment())) DevEndpoints.Map(api);
McpEndpoints.Map(app);
app.MapHub<InAppHub>("/hubs/inapp");
app.MapHub<AdminHub>("/hubs/admin");
app.MapFallback("/api/{**path}", (HttpContext ctx) => ErrorHandlingMiddleware.WriteAsync(ctx, 404, "not_found", "Unknown API route.", null)).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
