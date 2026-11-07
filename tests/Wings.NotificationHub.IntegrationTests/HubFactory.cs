using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wings.NotificationHub.Client;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.IntegrationTests;

/// <summary>
/// Full in-process hub: real API, real workers (outbox relay, queue consumers, scheduler, reconciliation) and mock
/// providers, on a private SQLite database seeded with the demo tenants.
/// </summary>
public sealed class HubFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "wnh-tests-" + Guid.NewGuid().ToString("N"));
    private Dictionary<string, string>? _credentials;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataDir);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:NotificationHub", $"Data Source={Path.Combine(DataDir, "hub.db")}");
        builder.UseSetting("Hub:DataDirectory", DataDir);
        builder.UseSetting("Hub:SeedDemoData", "true");
        builder.UseSetting("Hub:EnableDevEndpoints", "true");
        builder.UseSetting("Hub:PublicBaseUrl", "http://localhost");
        builder.UseSetting("Hub:ReconcileAfterMinutes", "15");
        builder.UseSetting("Workers:PollIntervalMs", "50");
        builder.UseSetting("Webhooks:AllowHttp", "true");
        builder.UseSetting("Webhooks:AllowPrivateNetworks", "true");
        builder.UseSetting("Https:Redirect", "false");
        builder.UseSetting("Security:RequireApprovalForCredentialChanges", "true");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("RateLimiting:AuthPerMinute", "100000");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<FakeWebhookReceiver>();
            // Outbound webhooks go to an in-memory receiver instead of the network.
            services.AddHttpClient("webhooks").ConfigurePrimaryHttpMessageHandler(sp => new FakeWebhookHandler(sp.GetRequiredService<FakeWebhookReceiver>()));
        });
    }

    /// <summary>The demo company's quiet hours would make promotional-mail tests pass only in the daytime.</summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>().Companies.ExecuteUpdate(u => u.SetProperty(c => c.QuietHoursJson, (string?)null));
        return host;
    }

    public IReadOnlyDictionary<string, string> Credentials
    {
        get
        {
            if (_credentials is not null) return _credentials;
            _ = Server; // ensure started (seeding writes the file)
            _credentials = File.ReadAllLines(Path.Combine(DataDir, "dev-credentials.env"))
                .Select(l => l.Split('#')[0].Trim()).Where(l => l.Contains('='))
                .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            return _credentials;
        }
    }

    public Guid AbcCompany => Guid.Parse(Credentials["DEMO_COMPANY_ABC_ID"]);
    public Guid SunriseCompany => Guid.Parse(Credentials["DEMO_COMPANY_SUNRISE_ID"]);
    public Guid AcmeCompany => Guid.Parse(Credentials["ACME_COMPANY_ID"]);
    public Guid ErpApp => Guid.Parse(Credentials["DEMO_APP_ERP_ID"]);
    public Guid TenantId => Guid.Parse(Credentials["DEMO_TENANT_ID"]);

    public NotificationHubClient Sdk(string clientKey = "ERP_AUTO_DEMO")
        => new(CreateClient(), new NotificationHubClientOptions
        {
            ClientId = Credentials[$"{clientKey}_CLIENT_ID"], ClientSecret = Credentials[$"{clientKey}_CLIENT_SECRET"], DefaultCompanyId = AbcCompany, MaxRetries = 0,
        });

    public async Task<HttpClient> AppHttpAsync(string clientKey = "ERP_AUTO_DEMO", string? scope = null)
    {
        var http = CreateClient();
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = Credentials[$"{clientKey}_CLIENT_ID"], ["client_secret"] = Credentials[$"{clientKey}_CLIENT_SECRET"] };
        if (scope is not null) form["scope"] = scope;
        var resp = await http.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public async Task<HttpClient> AdminHttpAsync(string userKey = "ADMIN")
    {
        var http = CreateClient();
        var (email, pw) = userKey == "ADMIN"
            ? (Credentials["ADMIN_EMAIL"], Credentials["ADMIN_PASSWORD"])
            : ($"{userKey.ToLowerInvariant().Replace('_', '.')}@wings.local", Credentials[$"{userKey}_PASSWORD"]);
        var resp = await http.PostAsJsonAsync("/api/v1/auth/login", new { email, password = pw });
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public async Task<T> DbAsync<T>(Func<NotificationHubDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationHubDbContext>());
    }

    /// <summary>Polls until the predicate holds (workers are asynchronous).</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> probe, Func<T, bool> done, int timeoutMs = 15000, string? because = null)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        T last;
        while (true)
        {
            last = await probe();
            if (done(last)) return last;
            if (DateTime.UtcNow > until) throw new Xunit.Sdk.XunitException($"Condition not met within {timeoutMs} ms{(because is null ? "" : ": " + because)}. Last value: {JsonSerializer.Serialize(last)}");
            await Task.Delay(100);
        }
    }

    public Task<NotificationStatusDto> WaitForStatusAsync(NotificationHubClient sdk, Guid id, Func<NotificationStatusDto, bool> done, int timeoutMs = 15000)
        => EventuallyAsync(() => sdk.GetStatusAsync(id), done, timeoutMs);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDir, true); } catch { /* best effort */ }
    }
}

/// <summary>Records webhook posts made through the "webhooks" HttpClient and can be told to fail.</summary>
public sealed class FakeWebhookReceiver
{
    public int FailNext;
    /// <summary>Only URLs with this prefix are failed (other subscribers keep succeeding).</summary>
    public string FailPrefix = "https://fail.invalid";
    public System.Collections.Concurrent.ConcurrentQueue<(string Url, string Body, string? Signature)> Received { get; } = new();
}

public sealed class FakeWebhookHandler(FakeWebhookReceiver receiver) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        receiver.Received.Enqueue((request.RequestUri!.ToString(), body, request.Headers.TryGetValues("X-Wings-Signature", out var v) ? v.First() : null));
        if (request.RequestUri.ToString().StartsWith(receiver.FailPrefix, StringComparison.Ordinal) && Interlocked.Decrement(ref receiver.FailNext) >= 0)
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
    }
}

[CollectionDefinition("hub")]
public sealed class HubCollection : ICollectionFixture<HubFactory>;

public static class Req
{
    public static Dictionary<string, JsonElement> Vars(object o) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    public static Dictionary<string, JsonElement> Invoice(string number = "INV-1") => Vars(new { CustomerName = "Test Customer", InvoiceNumber = number, InvoiceAmount = "1000", InvoiceDate = "2026-10-28" });

    public static SendNotificationRequest InvoiceTo(Guid company, RecipientDto r, params Channel[] channels) => new()
    {
        CompanyId = company, NotificationType = "INVOICE_CREATED", Recipient = r, Channels = channels.ToList(), Variables = Invoice($"INV-{Guid.NewGuid():N}"[..12]),
    };

    public static string Phone(string suffix = "") => "+9198" + Random.Shared.Next(10000000, 99999999).ToString()[..(8 - suffix.Length)] + suffix;
    public static string Email(string tag = "") => $"t{Guid.NewGuid():N}{tag}@example.com";
}
