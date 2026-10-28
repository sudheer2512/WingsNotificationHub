using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.Infrastructure.Providers;

/// <summary>Builds provider adapters from accounts (decrypting secrets in memory) and caches them briefly.</summary>
public sealed class ProviderRegistry(IEnumerable<IProviderFactory> factories, ISecretProtector protector, IMemoryCache cache)
{
    private readonly Dictionary<string, IProviderFactory> _factories = factories.ToDictionary(f => f.ProviderType, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IProviderFactory> Factories => _factories.Values;

    public IProviderFactory? Factory(string providerType) => _factories.GetValueOrDefault(providerType);

    public ProviderAccountConfig ConfigFor(ProviderAccount account)
    {
        var secrets = string.IsNullOrEmpty(account.SecretsCipher)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(account.SecretsCipher, Purposes.ProviderSecrets)) ?? [];
        if (!string.IsNullOrEmpty(account.CallbackSecretCipher))
            secrets["__callbackSecret"] = protector.Unprotect(account.CallbackSecretCipher, Purposes.CallbackSecret);
        return new ProviderAccountConfig
        {
            AccountId = account.Id, Name = account.Name, ProviderType = account.ProviderType, Channel = account.Channel,
            Settings = Json.Deserialize<JsonObject>(account.SettingsJson) ?? new JsonObject(),
            Secrets = secrets, Timeout = TimeSpan.FromSeconds(Math.Clamp(account.TimeoutSeconds, 1, 300)),
        };
    }

    /// <summary>Adapter instance for an account; rebuilt whenever the account's concurrency stamp changes.</summary>
    public INotificationProvider Get(ProviderAccount account)
        => cache.GetOrCreate(("provider", account.Id, account.ConcurrencyStamp), e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            var factory = Factory(account.ProviderType) ?? throw new ProviderConfigurationException($"No adapter registered for provider type '{account.ProviderType}'.");
            return factory.Create(ConfigFor(account));
        })!;
}

/// <summary>Per-provider-account circuit breaker. Opens after consecutive failures; half-opens after a cool-down.</summary>
public sealed class CircuitBreakerRegistry(ILogger<CircuitBreakerRegistry> logger)
{
    public int FailureThreshold { get; set; } = 5;
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<Guid, State> _states = new();

    private sealed class State
    {
        public int Failures;
        public DateTime? OpenUntil;
        public bool HalfOpenProbe;
    }

    public bool IsOpen(Guid accountId)
    {
        if (!_states.TryGetValue(accountId, out var s) || s.OpenUntil is null) return false;
        if (s.OpenUntil > DateTime.UtcNow) return true;
        // Half-open: allow a single probe through.
        lock (s)
        {
            if (s.HalfOpenProbe) return true;
            s.HalfOpenProbe = true;
            return false;
        }
    }

    public CircuitTransition RecordSuccess(Guid accountId)
    {
        var s = _states.GetOrAdd(accountId, _ => new State());
        lock (s)
        {
            var wasOpen = s.OpenUntil is not null;
            s.Failures = 0; s.OpenUntil = null; s.HalfOpenProbe = false;
            return wasOpen ? CircuitTransition.Closed : CircuitTransition.None;
        }
    }

    public CircuitTransition RecordFailure(Guid accountId)
    {
        var s = _states.GetOrAdd(accountId, _ => new State());
        lock (s)
        {
            s.Failures++;
            if (s.HalfOpenProbe || (s.OpenUntil is null && s.Failures >= FailureThreshold))
            {
                s.OpenUntil = DateTime.UtcNow.Add(OpenDuration);
                s.HalfOpenProbe = false;
                logger.LogWarning("Circuit opened for provider account {Account} after {Failures} failures", accountId, s.Failures);
                return CircuitTransition.Opened;
            }
            return CircuitTransition.None;
        }
    }

    public void Reset(Guid accountId) => _states.TryRemove(accountId, out _);

    public IReadOnlyDictionary<Guid, (int Failures, DateTime? OpenUntil)> Snapshot()
        => _states.ToDictionary(kv => kv.Key, kv => (kv.Value.Failures, kv.Value.OpenUntil));
}

public enum CircuitTransition { None, Opened, Closed }

/// <summary>Provider rate limiting. In-memory token buckets by default; Redis-backed (shared across instances) when configured.</summary>
public interface IProviderRateLimiter
{
    /// <summary>Takes one token; returns the wait time when the provider is saturated.</summary>
    Task<TimeSpan?> TryAcquireAsync(Guid accountId, double perSecond, CancellationToken ct);
}

public sealed class InMemoryProviderRateLimiter : IProviderRateLimiter
{
    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();

    private sealed class Bucket(double rate)
    {
        public double Rate = rate;
        public double Tokens = Math.Max(1, rate);
        public DateTime Last = DateTime.UtcNow;
    }

    public Task<TimeSpan?> TryAcquireAsync(Guid accountId, double perSecond, CancellationToken ct)
    {
        if (perSecond <= 0) return Task.FromResult<TimeSpan?>(null);
        var b = _buckets.GetOrAdd(accountId, _ => new Bucket(perSecond));
        lock (b)
        {
            b.Rate = perSecond;
            var now = DateTime.UtcNow;
            b.Tokens = Math.Min(Math.Max(1, perSecond), b.Tokens + (now - b.Last).TotalSeconds * perSecond);
            b.Last = now;
            if (b.Tokens >= 1) { b.Tokens -= 1; return Task.FromResult<TimeSpan?>(null); }
            return Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds((1 - b.Tokens) / perSecond));
        }
    }
}

/// <summary>Fixed one-second windows in Redis (INCR + EXPIRE): a shared limit for all API/worker instances.</summary>
public sealed class RedisProviderRateLimiter(IConnectionMultiplexer redis) : IProviderRateLimiter
{
    public async Task<TimeSpan?> TryAcquireAsync(Guid accountId, double perSecond, CancellationToken ct)
    {
        if (perSecond <= 0) return null;
        var now = DateTimeOffset.UtcNow;
        var key = $"wnh:rl:{accountId:N}:{now.ToUnixTimeSeconds()}";
        var db = redis.GetDatabase();
        var n = await db.StringIncrementAsync(key);
        if (n == 1) await db.KeyExpireAsync(key, TimeSpan.FromSeconds(2));
        return n <= Math.Max(1, (long)Math.Floor(perSecond)) ? null : TimeSpan.FromMilliseconds(1000 - now.Millisecond);
    }
}

/// <summary>
/// Chooses provider accounts for a delivery: visibility (company-dedicated → tenant → shared when permitted),
/// channel, enabled/suspended, circuit state, geography, capacity, routing rules, priority and cost.
/// </summary>
public sealed class ProviderRouter(NotificationHubDbContext db, CircuitBreakerRegistry circuits)
{
    public sealed record Candidate(ProviderAccount Account, string? SkipReason);

    public async Task<List<Candidate>> CandidatesAsync(NotificationDelivery d, bool sandbox, bool allowShared, CancellationToken ct)
    {
        var accounts = await db.ProviderAccounts.AsNoTracking()
            .Where(p => p.Channel == d.Channel && p.Enabled
                        && ((p.Scope == ProviderScope.Shared && (allowShared || sandbox))
                            || (p.Scope == ProviderScope.Tenant && p.TenantId == d.TenantId)
                            || (p.Scope == ProviderScope.Company && p.CompanyId == d.CompanyId)))
            .ToListAsync(ct);
        if (sandbox) accounts = accounts.Where(a => a.ProviderType == "mock").ToList();

        var rules = await db.ProviderRoutingRules.AsNoTracking()
            .Where(r => r.Enabled && r.Channel == d.Channel && (r.TenantId == null || r.TenantId == d.TenantId)
                        && (r.CompanyId == null || r.CompanyId == d.CompanyId) && (r.ApplicationId == null || r.ApplicationId == d.ApplicationId))
            .ToListAsync(ct);
        var applicable = rules
            .Where(r => r.CountryPrefix is null || d.Address.StartsWith(r.CountryPrefix, StringComparison.Ordinal))
            .Where(r => r.MessageClass is null || r.MessageClass == d.MessageClass)
            .OrderByDescending(r => (r.CompanyId is null ? 0 : 4) + (r.ApplicationId is null ? 0 : 2) + (r.CountryPrefix?.Length ?? 0))
            .ThenBy(r => r.Order).ToList();
        var ruleRank = new Dictionary<Guid, int>();
        for (var i = 0; i < applicable.Count; i++) ruleRank.TryAdd(applicable[i].ProviderAccountId, i);
        var restrictedByRule = applicable.Count > 0;

        var month = Periods.StartOf(DateTime.UtcNow);
        var capacityLimited = accounts.Where(a => a.MonthlyCapacity is not null).Select(a => a.Id).ToList();
        var used = capacityLimited.Count == 0 ? new Dictionary<Guid, int>() : await db.UsageRecords.AsNoTracking()
            .Where(u => u.ProviderAccountId != null && capacityLimited.Contains(u.ProviderAccountId.Value) && u.OccurredAt >= month)
            .GroupBy(u => u.ProviderAccountId!.Value).Select(g => new { g.Key, Count = g.Sum(x => x.Units) })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return accounts
            .Select(a => new Candidate(a, SkipReason(a, d, used, restrictedByRule && !ruleRank.ContainsKey(a.Id))))
            .OrderBy(c => c.SkipReason is null ? 0 : 1)
            .ThenBy(c => ruleRank.TryGetValue(c.Account.Id, out var r) ? r : int.MaxValue)
            .ThenBy(c => c.Account.Scope == ProviderScope.Company ? 0 : c.Account.Scope == ProviderScope.Tenant ? 1 : 2)
            .ThenBy(c => c.Account.Priority)
            .ThenBy(c => c.Account.UnitCost ?? decimal.MaxValue)
            .ToList();
    }

    private string? SkipReason(ProviderAccount a, NotificationDelivery d, Dictionary<Guid, int> used, bool excludedByRule)
    {
        if (a.Suspended) return "suspended";
        if (excludedByRule) return "not in routing rule";
        if (circuits.IsOpen(a.Id)) return "circuit open";
        if (a.HealthStatus == ProviderHealthStatus.Unhealthy) return "unhealthy";
        if (d.Channel is Channel.Sms or Channel.WhatsApp && a.AllowedCountryPrefixes.Count > 0 && Addressing.CountryPrefixMatch(d.Address, a.AllowedCountryPrefixes) == "")
            return "destination country not served";
        if (a.MonthlyCapacity is { } cap && used.GetValueOrDefault(a.Id) >= cap) return "monthly capacity reached";
        return null;
    }
}
