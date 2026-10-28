using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.Infrastructure.Services;

/// <summary>Quotas, cost estimation, customer billing and prepaid credits.</summary>
public sealed class UsageService(NotificationHubDbContext db, ILogger<UsageService> logger)
{
    /// <summary>
    /// Checks monthly quotas for the deliveries about to be created. Counts deliveries already created this month
    /// (excluding suppressed/cancelled) so queued-but-unsent messages also consume quota.
    /// </summary>
    public async Task CheckQuotaAsync(Guid tenantId, Guid companyId, Guid applicationId, IReadOnlyDictionary<Channel, int> adding, CancellationToken ct)
    {
        var quotas = await db.TenantQuotas.AsNoTracking()
            .Where(q => q.TenantId == tenantId && (q.CompanyId == null || q.CompanyId == companyId) && (q.ApplicationId == null || q.ApplicationId == applicationId))
            .ToListAsync(ct);
        if (quotas.Count == 0) return;
        var since = Periods.StartOf(DateTime.UtcNow);
        foreach (var q in quotas)
        {
            var add = q.Channel is { } ch ? adding.GetValueOrDefault(ch) : adding.Values.Sum();
            if (add == 0) continue;
            var query = db.NotificationDeliveries.Where(d => d.TenantId == tenantId && d.CreatedAt >= since
                && d.Status != DeliveryStatus.Suppressed && d.Status != DeliveryStatus.Cancelled);
            if (q.CompanyId is { } c) query = query.Where(d => d.CompanyId == c);
            if (q.ApplicationId is { } a) query = query.Where(d => d.ApplicationId == a);
            if (q.Channel is { } qc) query = query.Where(d => d.Channel == qc);
            var used = await query.CountAsync(ct);
            if (used + add > q.MonthlyLimit)
            {
                var scope = q.ApplicationId is not null ? "application" : q.CompanyId is not null ? "company" : "tenant";
                throw new HubException(429, "quota_exceeded",
                    $"Monthly {(q.Channel is { } x ? ChannelNames.ToWire(x) + " " : "")}quota for this {scope} is exhausted ({used}/{q.MonthlyLimit}).");
            }
            if (q.AlertThresholdPercent > 0 && (used + add) * 100 >= q.MonthlyLimit * q.AlertThresholdPercent
                && used * 100 < q.MonthlyLimit * q.AlertThresholdPercent)
            {
                db.SystemAlerts.Add(new SystemAlert
                {
                    TenantId = tenantId, Severity = "warning", Source = "quota", Key = $"quota:{q.Id}:{Periods.Of(DateTime.UtcNow)}",
                    Message = $"Quota {q.Id} reached {q.AlertThresholdPercent}% ({used + add}/{q.MonthlyLimit}).",
                });
            }
        }
    }

    /// <summary>Estimated provider cost for one delivery. Most specific cost rate wins, then the provider's unit cost.</summary>
    public async Task<(decimal Cost, string Currency)> EstimateCostAsync(Guid? providerAccountId, Channel channel, string? address, MessageClass cls, int units, CancellationToken ct)
    {
        var rates = await db.CostRates.AsNoTracking()
            .Where(r => r.Channel == channel && (r.ProviderAccountId == null || r.ProviderAccountId == providerAccountId) && r.EffectiveFrom <= DateTime.UtcNow)
            .ToListAsync(ct);
        var best = rates
            .Where(r => r.CountryPrefix is null || (address?.StartsWith(r.CountryPrefix, StringComparison.Ordinal) ?? false))
            .Where(r => r.MessageClass is null || r.MessageClass == cls)
            .OrderByDescending(r => (r.ProviderAccountId is null ? 0 : 4) + (r.CountryPrefix?.Length ?? 0) + (r.MessageClass is null ? 0 : 1))
            .ThenByDescending(r => r.EffectiveFrom)
            .FirstOrDefault();
        if (best is not null) return (best.UnitCost * units, best.Currency);
        if (providerAccountId is { } pid && await db.ProviderAccounts.AsNoTracking().Where(p => p.Id == pid).Select(p => new { p.UnitCost, p.Currency }).FirstOrDefaultAsync(ct) is { UnitCost: { } uc } p)
            return (uc * units, p.Currency);
        return (0m, "INR");
    }

    public async Task<decimal> BilledAmountAsync(Guid tenantId, Channel channel, decimal cost, int units, CancellationToken ct)
    {
        var rate = await db.BillingRates.AsNoTracking()
            .Where(r => r.Channel == channel && (r.TenantId == null || r.TenantId == tenantId))
            .OrderByDescending(r => r.TenantId != null).FirstOrDefaultAsync(ct);
        if (rate is null) return cost;
        return rate.UnitPrice is { } price ? price * units : Math.Round(cost * (1 + rate.MarginPercent / 100m), 6);
    }

    /// <summary>Records billable usage when a provider accepts a message (one record per accepted delivery).</summary>
    public async Task RecordAcceptedAsync(NotificationDelivery d, decimal? reportedCost, CancellationToken ct)
    {
        if (await db.UsageRecords.AnyAsync(u => u.DeliveryId == d.Id, ct)) return;
        var (est, currency) = await EstimateCostAsync(d.ProviderAccountId, d.Channel, d.Address, d.MessageClass, d.Units, ct);
        var basis = reportedCost ?? est;
        var billed = await BilledAmountAsync(d.TenantId, d.Channel, basis, d.Units, ct);
        db.UsageRecords.Add(new UsageRecord
        {
            TenantId = d.TenantId, CompanyId = d.CompanyId, ApplicationId = d.ApplicationId, Channel = d.Channel,
            ProviderAccountId = d.ProviderAccountId, DeliveryId = d.Id, CampaignId = d.CampaignId, MessageClass = d.MessageClass,
            Period = Periods.Of(DateTime.UtcNow), Units = d.Units, EstimatedCost = est, ProviderReportedCost = reportedCost,
            BilledAmount = billed, Currency = currency,
        });
        d.EstimatedCost = est;
        d.Cost = reportedCost ?? est;
        d.Currency = currency;
        await ConsumeCreditAsync(d, billed, ct);
    }

    /// <summary>Applies a provider-reported cost that arrived later (callback pricing).</summary>
    public async Task ApplyReportedCostAsync(NotificationDelivery d, decimal cost, CancellationToken ct)
    {
        var u = await db.UsageRecords.FirstOrDefaultAsync(x => x.DeliveryId == d.Id, ct);
        if (u is null) return;
        u.ProviderReportedCost = cost;
        d.Cost = cost;
    }

    // ───────── credits ─────────

    private async Task<CreditAccount?> AccountForAsync(Guid tenantId, Guid companyId, CancellationToken ct)
        => await db.CreditAccounts.Where(a => a.TenantId == tenantId && a.Enabled && (a.CompanyId == companyId || a.CompanyId == null))
            .OrderByDescending(a => a.CompanyId != null).FirstOrDefaultAsync(ct);

    /// <summary>Reserves credit for deliveries at acceptance; throws 402 when the prepaid balance is insufficient.</summary>
    public async Task ReserveCreditsAsync(Guid tenantId, Guid companyId, decimal amount, CancellationToken ct)
    {
        if (amount <= 0) return;
        var acct = await AccountForAsync(tenantId, companyId, ct);
        if (acct is null) return;
        if (acct.Balance - acct.Reserved < amount)
            throw new HubException(402, "insufficient_credits", $"Prepaid credit balance is insufficient (available {acct.Balance - acct.Reserved:0.00} {acct.Currency}, required {amount:0.00}).");
        acct.Reserved += amount;
        db.CreditTransactions.Add(new CreditTransaction { AccountId = acct.Id, Kind = CreditTransactionKind.Reserve, Amount = amount, Note = "reservation at acceptance" });
    }

    private async Task ConsumeCreditAsync(NotificationDelivery d, decimal billed, CancellationToken ct)
    {
        var acct = await AccountForAsync(d.TenantId, d.CompanyId, ct);
        if (acct is null) return;
        var reserved = d.EstimatedCost ?? billed;
        acct.Reserved = Math.Max(0, acct.Reserved - reserved);
        acct.Balance -= billed;
        db.CreditTransactions.Add(new CreditTransaction { AccountId = acct.Id, Kind = CreditTransactionKind.Consume, Amount = billed, DeliveryId = d.Id });
        if (acct.Balance <= acct.LowBalanceThreshold && (acct.LowBalanceAlertedAt is null || acct.LowBalanceAlertedAt < DateTime.UtcNow.AddHours(-24)))
        {
            acct.LowBalanceAlertedAt = DateTime.UtcNow;
            db.SystemAlerts.Add(new SystemAlert { TenantId = d.TenantId, Severity = "warning", Source = "credits", Key = $"credits-low:{acct.Id}", Message = $"Credit balance is low: {acct.Balance:0.00} {acct.Currency}." });
            logger.LogWarning("Credit account {Account} is below its low-balance threshold", acct.Id);
        }
    }

    /// <summary>Releases a reservation for a delivery that will never be billed (failed, suppressed, cancelled).</summary>
    public async Task ReleaseCreditAsync(NotificationDelivery d, CancellationToken ct)
    {
        if (d.EstimatedCost is not { } est || est <= 0) return;
        var acct = await AccountForAsync(d.TenantId, d.CompanyId, ct);
        if (acct is null) return;
        acct.Reserved = Math.Max(0, acct.Reserved - est);
        db.CreditTransactions.Add(new CreditTransaction { AccountId = acct.Id, Kind = CreditTransactionKind.Release, Amount = est, DeliveryId = d.Id });
    }
}

public sealed class AuditService(NotificationHubDbContext db)
{
    /// <summary>Adds an audit row to the current unit of work (saved with the change it describes).</summary>
    public void Record(CallerContext caller, string action, string entityType, string? entityId, string? summary,
        Guid? tenantId = null, Guid? companyId = null, Guid? applicationId = null, object? detail = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId, CompanyId = companyId, ApplicationId = applicationId,
            ActorType = caller.Kind.ToString(), ActorId = caller.ActorId, ActorName = caller.ActorName,
            Action = action, EntityType = entityType, EntityId = entityId, Summary = summary,
            DetailJson = detail is null ? null : Redact.Sanitize(Json.Serialize(detail), 8000),
            IpAddress = caller.IpAddress, CorrelationId = caller.CorrelationId,
        });
    }
}
