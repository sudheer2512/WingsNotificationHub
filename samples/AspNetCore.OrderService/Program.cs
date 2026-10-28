using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Wings.NotificationHub.Client;
using Wings.NotificationHub.Contracts;
using Wings.Samples.OrderService;

// Sample "order service" (an application backend) integrating with Wings Notification Hub through the C# SDK:
//  • sends a notification directly                     POST /orders/{orderNumber}/notify
//  • raises a business event for the rule engine        POST /orders/{orderNumber}/approve  (salesorder.approved)
//  • mints inbox tokens for its signed-in users         GET  /me/inbox-token
//  • registers its users' push devices                  POST /me/devices
//  • receives hub webhooks with signature verification  POST /webhooks/notification-hub
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<HubSettings>().Bind(builder.Configuration.GetSection("NotificationHub"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId) && !string.IsNullOrWhiteSpace(o.ClientSecret), "NotificationHub:ClientId and NotificationHub:ClientSecret are required (user-secrets or environment variables).")
    .Validate(o => o.CompanyId != Guid.Empty, "NotificationHub:CompanyId is required.")
    .ValidateOnStart();

// One long-lived client: it caches the access token, so do not create it per request.
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<HubSettings>>().Value;
    var baseUri = new Uri(o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/");
    var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
    return new NotificationHubClient(http, new NotificationHubClientOptions { BaseAddress = baseUri, ClientId = o.ClientId, ClientSecret = o.ClientSecret, DefaultCompanyId = o.CompanyId });
});
builder.Services.AddSingleton<WebhookInbox>();
builder.Services.AddHostedService<WebhookRegistrar>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? []).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseCors();
app.UseMiddleware<HubErrorMiddleware>();

app.MapGet("/", () => Results.Ok(new { service = "Wings sample order service", endpoints = new[] { "POST /orders/{n}/notify", "POST /orders/{n}/approve", "GET /me/inbox-token", "POST /me/devices", "POST /webhooks/notification-hub", "GET /webhooks/events" } }));

// ── 1. Direct send: an in-app + push notification to one user, with a deep link and action buttons ──
app.MapPost("/orders/{orderNumber}/notify", async (string orderNumber, NotifyRequest body, NotificationHubClient hub, CancellationToken ct) =>
{
    var response = await hub.SendAsync(new SendNotificationRequest
    {
        NotificationType = "ORDER_APPROVED",
        Recipient = new RecipientDto { UserId = body.UserId },
        Channels = body.Channels?.Select(c => ChannelNames.TryParse(c, out var ch) ? ch : throw new BadHttpRequestException($"Unknown channel '{c}'.")).ToList() ?? [Channel.InApp],
        Variables = new()
        {
            ["OrderNumber"] = JsonSerializer.SerializeToElement(orderNumber),
            ["OrderAmount"] = JsonSerializer.SerializeToElement(body.Amount ?? 0m),
            ["ApprovedBy"] = JsonSerializer.SerializeToElement(body.ApprovedBy ?? "Order service"),
        },
        App = new AppNotificationOptionsDto
        {
            DeepLink = $"/sales/orders/{Uri.EscapeDataString(orderNumber)}",
            Actions = [new AppActionDto { Id = "view", Label = "View order", DeepLink = $"/sales/orders/{Uri.EscapeDataString(orderNumber)}" }, new AppActionDto { Id = "ship", Label = "Mark shipped" }],
        },
        // A stable key makes this call safe to retry: the same order + user never produces two notifications.
        IdempotencyKey = body.IdempotencyKey ?? $"order-notify:{orderNumber}:{body.UserId}",
        CorrelationId = orderNumber,
        Metadata = new() { ["orderNumber"] = orderNumber },
    }, ct);
    return Results.Accepted(response.TrackingUrl, new { response.NotificationId, response.Status, response.Duplicate, response.Deliveries, response.Warnings });
});

// ── 2. Business event: the hub's rules decide channels/recipients ("Sales order approved → in-app + push") ──
app.MapPost("/orders/{orderNumber}/approve", async (string orderNumber, ApproveRequest body, NotificationHubClient hub, CancellationToken ct) =>
{
    var response = await hub.PublishEventAsync(new BusinessEventRequest
    {
        EventType = "salesorder.approved",
        Payload = new JsonObject
        {
            ["OrderNumber"] = orderNumber,
            ["OrderAmount"] = body.Amount,
            ["ApprovedBy"] = body.ApprovedBy,
            ["requester"] = new JsonObject { ["userId"] = body.RequesterUserId },
        },
        IdempotencyKey = $"salesorder.approved:{orderNumber}",
        CorrelationId = orderNumber,
        OccurredAt = DateTimeOffset.UtcNow,
    }, ct);
    return Results.Accepted($"/api/v1/events/{response.EventId}", response);
});

app.MapGet("/notifications/{id:guid}", async (Guid id, NotificationHubClient hub, CancellationToken ct) => Results.Ok(await hub.GetStatusAsync(id, ct)));

// ── 3. Inbox tokens for the signed-in user (browser / desktop / mobile inbox clients call this) ──
app.MapGet("/me/inbox-token", async (HttpContext ctx, NotificationHubClient hub, IOptions<HubSettings> o, CancellationToken ct) =>
{
    var user = DemoAuth.UserId(ctx);
    if (user is null) return Results.Unauthorized();
    var token = await hub.CreateInboxTokenAsync(new InboxTokenRequest { CompanyId = o.Value.CompanyId, UserId = user, LifetimeMinutes = 60 }, ct);
    return Results.Ok(token);
});

// ── 4. Push device registration on behalf of the user (the app never sees the client secret) ──
app.MapPost("/me/devices", async (HttpContext ctx, DeviceRequest body, NotificationHubClient hub, IOptions<HubSettings> o, CancellationToken ct) =>
{
    var user = DemoAuth.UserId(ctx);
    if (user is null) return Results.Unauthorized();
    if (!Enum.TryParse<DevicePlatform>(body.Platform, ignoreCase: true, out var platform)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["platform"] = ["android, ios or web"] });
    var device = await hub.RegisterDeviceAsync(new RegisterDeviceRequest
    {
        CompanyId = o.Value.CompanyId, UserId = user, Platform = platform, Token = body.Token, P256dh = body.P256dh, Auth = body.Auth,
        DeviceName = body.DeviceName, AppVersion = body.AppVersion, Locale = body.Locale,
    }, ct);
    return Results.Ok(device);
});

// ── 5. Webhooks from the hub: verify the signature over the raw body, de-duplicate, then handle ──
app.MapPost("/webhooks/notification-hub", async (HttpContext ctx, WebhookInbox inbox, ILogger<WebhookInbox> log) =>
{
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync(ctx.RequestAborted);
    var signature = ctx.Request.Headers[WebhookVerifier.Header].ToString();
    var secret = inbox.SigningSecret;
    if (secret is null)
    {
        log.LogWarning("Webhook received but no signing secret is configured; rejecting.");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    if (!WebhookVerifier.Verify(secret, body, signature, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)))
    {
        log.LogWarning("Rejected webhook with an invalid or expired signature.");
        return Results.Unauthorized();
    }
    var evt = JsonSerializer.Deserialize<WebhookEventEnvelope>(body, NotificationHubClient.JsonOptions)!;
    // Deliveries are at-least-once: acknowledge duplicates without processing them twice.
    if (!inbox.TryAdd(evt, ctx.Request.Headers["Idempotency-Key"].ToString())) return Results.Ok(new { duplicate = true });
    switch (evt.Type)
    {
        case WebhookEventTypes.DeliveryStatusChanged:
            log.LogInformation("Delivery {Delivery} of notification {Notification} → {Status}", evt.Data["deliveryId"], evt.Data["notificationId"], evt.Data["status"]);
            break;
        case WebhookEventTypes.InAppAction:
            // e.g. "ship" pressed in the inbox: update the order here.
            log.LogInformation("User {User} pressed '{Action}' on in-app message {Message}", evt.Data["userId"], evt.Data["actionId"], evt.Data["inAppMessageId"]);
            break;
        default:
            log.LogInformation("Webhook {Type} {Id}", evt.Type, evt.Id);
            break;
    }
    return Results.Ok();
}).DisableAntiforgery();

app.MapGet("/webhooks/events", (WebhookInbox inbox, string? type) => Results.Ok(inbox.Recent(type)));

app.Run();

public partial class Program;
