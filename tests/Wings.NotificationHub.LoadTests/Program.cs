using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Wings.NotificationHub.Client;
using Wings.NotificationHub.Contracts;

// Load test for Wings Notification Hub.
//   dotnet run --project tests/Wings.NotificationHub.LoadTests -- --url http://localhost:5480 --company <guid> --count 2000 --concurrency 32 [--channel email] [--batch 0]
// Credentials come from WNH_CLIENT_ID / WNH_CLIENT_SECRET (or --env-file App_Data/dev-credentials.env, ERP_AUTO_DEMO_*).
// Measures API acceptance latency/throughput (internal SLO) separately from end-to-end provider acceptance (depends on providers).

var opts = Args.Parse(args);
var env = opts.EnvFile is { } file && File.Exists(file)
    ? File.ReadAllLines(file).Select(l => l.Split('#')[0].Trim()).Where(l => l.Contains('=')).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1])
    : new Dictionary<string, string>();
string Need(string envName, string fileKey) => Environment.GetEnvironmentVariable(envName) ?? env.GetValueOrDefault(fileKey) ?? throw new InvalidOperationException($"Set {envName} (or pass --env-file).");
var company = opts.Company ?? Guid.Parse(env.GetValueOrDefault("DEMO_COMPANY_ABC_ID") ?? throw new InvalidOperationException("--company is required"));
var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = opts.Concurrency * 2 }) { BaseAddress = new Uri(opts.Url), Timeout = TimeSpan.FromSeconds(60) };
var client = new NotificationHubClient(http, new NotificationHubClientOptions
{
    BaseAddress = new Uri(opts.Url), ClientId = Need("WNH_CLIENT_ID", "ERP_AUTO_DEMO_CLIENT_ID"), ClientSecret = Need("WNH_CLIENT_SECRET", "ERP_AUTO_DEMO_CLIENT_SECRET"),
    DefaultCompanyId = company, MaxRetries = 0,
});
if (!ChannelNames.TryParse(opts.Channel, out var channel)) throw new ArgumentException("Unknown channel " + opts.Channel);

SendNotificationRequest Make(int i) => new()
{
    CompanyId = company, NotificationType = "INVOICE_CREATED", Channels = [channel],
    Recipient = channel switch
    {
        Channel.Sms or Channel.WhatsApp => new RecipientDto { Mobile = "+9197" + (10000000 + i % 89999999) },
        Channel.InApp => new RecipientDto { UserId = "load-user-" + (i % 500) },
        _ => new RecipientDto { Email = $"load{i}@loadtest.example" },
    },
    Variables = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { CustomerName = "Load " + i, InvoiceNumber = "LT-" + i, InvoiceAmount = "100", InvoiceDate = "2026-10-28" })),
    IdempotencyKey = $"load-{opts.RunId}-{i}",
};

Console.WriteLine($"Warming up (token + one request)…");
await client.SendAsync(Make(-1));

var latencies = new ConcurrentBag<double>();
var ids = new ConcurrentBag<Guid>();
var errors = new ConcurrentDictionary<string, int>();
var sw = Stopwatch.StartNew();
var next = -1;
await Parallel.ForEachAsync(Enumerable.Range(0, opts.Concurrency), async (_, ct) =>
{
    while (true)
    {
        var i = Interlocked.Increment(ref next);
        if (i >= opts.Count) break;
        var t = Stopwatch.StartNew();
        try
        {
            if (opts.Batch > 0)
            {
                var start = i;
                Interlocked.Add(ref next, opts.Batch - 1);
                var batch = Enumerable.Range(start, Math.Min(opts.Batch, opts.Count - start)).Select(Make).ToList();
                var r = await client.SendBatchAsync(batch, ct);
                foreach (var x in r.Results.Where(x => x.NotificationId is not null)) ids.Add(x.NotificationId!.Value);
                foreach (var x in r.Results.Where(x => x.Error is not null)) errors.AddOrUpdate(x.Error!.Code, 1, (_, n) => n + 1);
            }
            else ids.Add((await client.SendAsync(Make(i), ct)).NotificationId);
            latencies.Add(t.Elapsed.TotalMilliseconds);
        }
        catch (NotificationHubException ex) { errors.AddOrUpdate(ex.Error?.Code ?? ((int)ex.StatusCode).ToString(), 1, (_, n) => n + 1); }
        catch (Exception ex) { errors.AddOrUpdate(ex.GetType().Name, 1, (_, n) => n + 1); }
    }
});
sw.Stop();
var sorted = latencies.OrderBy(x => x).ToArray();
double P(double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
Console.WriteLine();
Console.WriteLine($"API acceptance: {ids.Count} notifications in {sw.Elapsed.TotalSeconds:0.0}s → {ids.Count / sw.Elapsed.TotalSeconds:0.0}/s (concurrency {opts.Concurrency}{(opts.Batch > 0 ? $", batch {opts.Batch}" : "")})");
Console.WriteLine($"  latency per call ms: p50 {P(0.50):0.0}  p95 {P(0.95):0.0}  p99 {P(0.99):0.0}  max {(sorted.Length > 0 ? sorted[^1] : 0):0.0}");
if (!errors.IsEmpty) Console.WriteLine("  errors: " + string.Join(", ", errors.Select(e => $"{e.Key}={e.Value}")));

if (opts.Wait)
{
    Console.WriteLine("Waiting for every notification to reach a final state (queue processing + provider acceptance)…");
    var e2e = Stopwatch.StartNew();
    var pending = ids.ToHashSet();
    var sample = ids.Take(Math.Min(ids.Count, opts.SampleForCompletion)).ToList();
    var done = 0;
    while (e2e.Elapsed < TimeSpan.FromMinutes(opts.WaitMinutes))
    {
        done = 0;
        foreach (var chunk in sample.Chunk(25))
        {
            var statuses = await Task.WhenAll(chunk.Select(id => client.GetStatusAsync(id)));
            done += statuses.Count(s => s.Status is RequestStatus.Completed or RequestStatus.Failed or RequestStatus.PartiallyFailed or RequestStatus.Suppressed);
        }
        Console.Write($"\r  {done}/{sample.Count} sampled notifications final after {e2e.Elapsed.TotalSeconds:0}s   ");
        if (done == sample.Count) break;
        await Task.Delay(1000);
    }
    Console.WriteLine();
    Console.WriteLine($"End-to-end: {done}/{sample.Count} sampled notifications final within {e2e.Elapsed.TotalSeconds + sw.Elapsed.TotalSeconds:0.0}s of the first send.");
}

sealed class Args
{
    public string Url { get; set; } = "http://localhost:5480";
    public Guid? Company { get; set; }
    public int Count { get; set; } = 1000;
    public int Concurrency { get; set; } = 16;
    public int Batch { get; set; }
    public string Channel { get; set; } = "email";
    public bool Wait { get; set; } = true;
    public int WaitMinutes { get; set; } = 5;
    public int SampleForCompletion { get; set; } = 200;
    public string? EnvFile { get; set; } = "App_Data/dev-credentials.env";
    public string RunId { get; } = Guid.NewGuid().ToString("N")[..8];

    public static Args Parse(string[] a)
    {
        var o = new Args();
        for (var i = 0; i < a.Length; i++)
        {
            string V() => a[++i];
            switch (a[i])
            {
                case "--url": o.Url = V(); break;
                case "--company": o.Company = Guid.Parse(V()); break;
                case "--count": o.Count = int.Parse(V()); break;
                case "--concurrency": o.Concurrency = int.Parse(V()); break;
                case "--batch": o.Batch = int.Parse(V()); break;
                case "--channel": o.Channel = V(); break;
                case "--no-wait": o.Wait = false; break;
                case "--wait-minutes": o.WaitMinutes = int.Parse(V()); break;
                case "--env-file": o.EnvFile = V(); break;
                default: throw new ArgumentException($"Unknown argument {a[i]}");
            }
        }
        return o;
    }
}
