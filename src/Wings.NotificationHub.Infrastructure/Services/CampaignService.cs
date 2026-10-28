using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Policies;
using Wings.NotificationHub.Core.Rules;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Messaging;

namespace Wings.NotificationHub.Infrastructure.Services;

public sealed class ImportResult
{
    public int Imported { get; set; }
    public int Rejected { get; set; }
    public List<string> Errors { get; set; } = [];
}

/// <summary>
/// Bulk campaigns: drafts, recipient import (CSV/Excel) with validation, segmentation, maker-checker approval,
/// scheduled launch and throttled batch processing. Campaign messages are Promotional unless explicitly classed otherwise,
/// so consent, unsubscribe and quiet-hour policy apply per recipient.
/// </summary>
public sealed class CampaignService(
    NotificationHubDbContext db,
    NotificationService notifications,
    RecipientResolver recipients,
    AuditService audit,
    ILogger<CampaignService> logger) : IMessageHandler
{
    public const int MaxImportRows = 100_000;
    public string Type => MessageTypes.CampaignLaunch;

    public async Task<ImportResult> ImportAsync(Campaign campaign, Stream file, string fileName, CancellationToken ct)
    {
        if (campaign.Status is not CampaignStatus.Draft) throw HubException.Conflict("campaign_locked", "Recipients can only be imported into draft campaigns.");
        var rows = fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? ReadExcel(file) : await ReadCsvAsync(file, ct);
        var result = new ImportResult();
        var existing = await db.CampaignRecipients.CountAsync(r => r.CampaignId == campaign.Id, ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowNo = 1;
        foreach (var row in rows)
        {
            rowNo++;
            if (existing + result.Imported >= MaxImportRows) { result.Errors.Add($"Row {rowNo}: import limit of {MaxImportRows} reached."); result.Rejected++; continue; }
            string? Get(string key) => row.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v ? v.Trim() : null;
            var email = Get("email");
            var mobile = Get("mobile") ?? Get("phone");
            var normalizedMobile = Addressing.NormalizePhone(mobile);
            var errors = new List<string>();
            if (email is not null && !Addressing.IsValidEmail(email)) errors.Add($"invalid email '{email}'");
            if (mobile is not null && normalizedMobile is null) errors.Add($"invalid mobile '{mobile}'");
            if (email is null && normalizedMobile is null && Get("externalId") is null) errors.Add("needs email, mobile or externalId");
            var key = email ?? normalizedMobile ?? Get("externalId")!;
            if (errors.Count == 0 && !seen.Add(key ?? "")) errors.Add("duplicate recipient");
            if (errors.Count > 0)
            {
                result.Rejected++;
                if (result.Errors.Count < 200) result.Errors.Add($"Row {rowNo}: {string.Join(", ", errors)}");
                continue;
            }
            var vars = row.Where(kv => kv.Key is not ("email" or "mobile" or "phone" or "name" or "externalId")).ToDictionary(kv => kv.Key, kv => kv.Value);
            db.CampaignRecipients.Add(new CampaignRecipient
            {
                CampaignId = campaign.Id, ExternalId = Get("externalId"), Name = Get("name"), Email = email, Mobile = normalizedMobile,
                VariablesJson = vars.Count > 0 ? JsonSerializer.Serialize(vars) : null, RowNumber = rowNo,
            });
            result.Imported++;
        }
        campaign.TotalRecipients = existing + result.Imported;
        await db.SaveChangesAsync(ct);
        return result;
    }

    private static async Task<List<Dictionary<string, string>>> ReadCsvAsync(Stream file, CancellationToken ct)
    {
        using var reader = new StreamReader(file, Encoding.UTF8);
        var header = ParseCsvLine(await reader.ReadLineAsync(ct) ?? "");
        var rows = new List<Dictionary<string, string>>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null && rows.Count <= MaxImportRows)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = ParseCsvLine(line);
            rows.Add(header.Select((h, i) => (h, v: i < cells.Count ? cells[i] : "")).ToDictionary(x => x.h.Trim(), x => x.v, StringComparer.OrdinalIgnoreCase));
        }
        return rows;
    }

    public static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    private static List<Dictionary<string, string>> ReadExcel(Stream file)
    {
        using var wb = new XLWorkbook(file);
        var ws = wb.Worksheets.First();
        var used = ws.RangeUsed();
        if (used is null) return [];
        var headers = used.FirstRow().Cells().Select(c => c.GetString().Trim()).ToList();
        return used.RowsUsed().Skip(1).Take(MaxImportRows + 1)
            .Select(r => headers.Select((h, i) => (h, v: r.Cell(i + 1).GetFormattedString())).ToDictionary(x => x.h, x => x.v, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Maker-checker: the approver must be a different person from the creator.</summary>
    public async Task ApproveAsync(CallerContext caller, Campaign c, CancellationToken ct)
    {
        if (c.Status != CampaignStatus.PendingApproval) throw HubException.Conflict("invalid_state", "Only campaigns pending approval can be approved.");
        if (c.CreatedBy == caller.ActorId) throw new HubException(403, "maker_checker", "A campaign cannot be approved by the person who created it.");
        c.Status = c.ScheduledAt is { } at && at > DateTime.UtcNow ? CampaignStatus.Scheduled : CampaignStatus.Approved;
        c.ApprovedBy = caller.ActorId;
        c.ApprovedAt = DateTime.UtcNow;
        var approval = await db.ApprovalRequests.FirstOrDefaultAsync(a => a.EntityId == c.Id && a.Status == ApprovalStatus.Pending, ct);
        if (approval is not null) { approval.Status = ApprovalStatus.Approved; approval.DecidedBy = caller.ActorId; approval.DecidedAt = DateTime.UtcNow; }
        audit.Record(caller, "campaign.approved", nameof(Campaign), c.Id.ToString(), c.Name, c.TenantId, c.CompanyId, c.ApplicationId);
    }

    /// <summary>Starts an approved campaign: expands group/segment audiences into recipients and begins batch processing.</summary>
    public async Task LaunchAsync(Campaign c, CancellationToken ct)
    {
        if (c.Status is not (CampaignStatus.Approved or CampaignStatus.Scheduled)) return;
        var existing = await db.CampaignRecipients.Where(r => r.CampaignId == c.Id && r.ContactId != null).Select(r => r.ContactId!.Value).ToListAsync(ct);
        var have = existing.ToHashSet();
        var contacts = new List<RecipientContact>();
        foreach (var g in c.GroupIds) contacts.AddRange(await recipients.GroupMembersAsync(c.CompanyId, g, ct));
        if (Json.Deserialize<ConditionNode>(c.SegmentJson) is { } segment)
        {
            var all = await db.RecipientContacts.AsNoTracking().Where(x => x.CompanyId == c.CompanyId && x.ErasedAt == null).Take(MaxImportRows).ToListAsync(ct);
            contacts.AddRange(all.Where(x => ConditionEvaluator.Evaluate(segment, RecipientResolver.ContactDocument(x))));
        }
        foreach (var contact in contacts.DistinctBy(x => x.Id).Where(x => have.Add(x.Id)))
            db.CampaignRecipients.Add(new CampaignRecipient { CampaignId = c.Id, ContactId = contact.Id, ExternalId = contact.ExternalId, Name = contact.Name, Email = contact.Email, Mobile = contact.Mobile });
        c.Status = CampaignStatus.Running;
        c.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        c.TotalRecipients = await db.CampaignRecipients.CountAsync(r => r.CampaignId == c.Id, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task HandleAsync(MessageEnvelope message, CancellationToken ct)
    {
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == message.Payload<CampaignLaunchMessage>().CampaignId, ct);
        if (c is not null) await LaunchAsync(c, ct);
    }

    /// <summary>Processes the next batch of a running campaign within its per-minute throttle. Returns recipients processed.</summary>
    public async Task<int> ProcessBatchAsync(Campaign c, CancellationToken ct)
    {
        if (c.Status != CampaignStatus.Running || c.Suspended) return 0;
        var since = DateTime.UtcNow.AddMinutes(-1);
        var sentLastMinute = await db.CampaignRecipients.CountAsync(r => r.CampaignId == c.Id && r.ProcessedAt >= since, ct);
        var budget = Math.Min(200, c.ThrottlePerMinute - sentLastMinute);
        if (budget <= 0) return 0;
        var batch = await db.CampaignRecipients.Where(r => r.CampaignId == c.Id && r.Status == CampaignRecipientStatus.Pending).OrderBy(r => r.RowNumber).Take(budget).ToListAsync(ct);
        if (batch.Count == 0)
        {
            c.Status = CampaignStatus.Completed;
            c.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return 0;
        }
        var caller = new CallerContext { Kind = CallerKind.System, ActorId = $"campaign:{c.Id:N}", ActorName = $"Campaign {c.Name}", Grants = [new ScopeGrant(Roles.PlatformAdmin, null, null, null, null)] };
        var baseVars = Json.Deserialize<Dictionary<string, JsonElement>>(c.VariablesJson) ?? [];
        foreach (var r in batch)
        {
            var vars = new Dictionary<string, JsonElement>(baseVars, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in Json.Deserialize<Dictionary<string, string>>(r.VariablesJson) ?? []) vars[k] = JsonSerializer.SerializeToElement(v);
            try
            {
                var resp = await notifications.AcceptAsync(caller, new SendNotificationRequest
                {
                    ApplicationId = c.ApplicationId, CompanyId = c.CompanyId, NotificationType = c.NotificationTypeCode, TemplateCode = c.TemplateCode,
                    Recipient = new RecipientDto { ContactId = r.ContactId, Name = r.Name, Email = r.Email, Mobile = r.Mobile },
                    Channels = c.Channels, Variables = vars, Priority = NotificationPriority.Low, IdempotencyKey = $"campaign:{c.Id:N}:{r.Id:N}",
                    Metadata = new Dictionary<string, string> { ["campaign"] = c.Name },
                }, RequestSource.Campaign, new AcceptOptions { CampaignId = c.Id, MessageClass = c.MessageClass }, ct);
                r.NotificationRequestId = resp.NotificationId;
                r.Status = resp.Status == "suppressed" ? CampaignRecipientStatus.Skipped : CampaignRecipientStatus.Queued;
                if (resp.Status == "suppressed") r.Error = string.Join("; ", resp.Deliveries.Select(d => d.Reason).Distinct());
            }
            catch (HubException ex)
            {
                r.Status = ex.Code == "no_deliverable_channels" ? CampaignRecipientStatus.Skipped : CampaignRecipientStatus.Failed;
                r.Error = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
                db.ChangeTracker.Entries().Where(e => e.Entity is NotificationRequest or NotificationDelivery or NotificationRecipient or DeliveryEvent or OutboxMessage && e.State == EntityState.Added)
                    .ToList().ForEach(e => e.State = EntityState.Detached);
                if (ex.Code is "quota_exceeded" or "insufficient_credits")
                {
                    c.Status = CampaignStatus.Paused;
                    db.SystemAlerts.Add(new SystemAlert { TenantId = c.TenantId, Severity = "warning", Source = "campaign", Message = $"Campaign '{c.Name}' paused: {ex.Message}" });
                    r.Status = CampaignRecipientStatus.Pending;
                    r.Error = null;
                    break;
                }
            }
            r.ProcessedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Campaign {Campaign}: processed {Count} recipients", c.Id, batch.Count);
        return batch.Count;
    }
}

/// <summary>Recurring notifications and reminder sequences with time-zone, working-day and holiday awareness.</summary>
public sealed class ScheduleService(NotificationHubDbContext db, NotificationService notifications, ILogger<ScheduleService> logger)
{
    public async Task<DateTime?> ComputeNextRunAsync(NotificationSchedule s, DateTime fromUtc, CancellationToken ct)
    {
        var tz = TimeZones.Resolve(s.TimeZone);
        DateTime? next;
        if (!string.IsNullOrWhiteSpace(s.CronExpression))
        {
            var cron = CronExpression.Parse(s.CronExpression, CronFormat.Standard);
            next = cron.GetNextOccurrence(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), tz);
        }
        else
        {
            var offsets = Json.Deserialize<List<int>>(s.SequenceOffsetsJson) ?? [];
            next = s.SequenceIndex < offsets.Count ? s.StartAt.AddMinutes(offsets[s.SequenceIndex]) : null;
            if (next < fromUtc) next = fromUtc;
        }
        if (next is null) return null;
        if (next < s.StartAt) next = s.StartAt;
        if (s.WorkingDaysOnly)
        {
            var holidays = s.HolidayCalendarId is { } cal
                ? (await db.HolidayDates.AsNoTracking().Where(h => h.CalendarId == cal).Select(h => h.Date).ToListAsync(ct)).ToHashSet()
                : [];
            next = WorkingDays.NextWorkingInstant(next.Value, tz, holidays);
        }
        if (s.EndAt is { } end && next > end) return null;
        if (s.MaxRuns is { } max && s.RunCount >= max) return null;
        return next;
    }

    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.NotificationSchedules.Where(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= now).OrderBy(s => s.NextRunAt).Take(20).ToListAsync(ct);
        var ran = 0;
        foreach (var s in due)
        {
            // Claim the run with optimistic concurrency so two scheduler instances never fire the same occurrence.
            var occurrence = s.NextRunAt!.Value;
            s.LastRunAt = now;
            s.RunCount++;
            if (string.IsNullOrWhiteSpace(s.CronExpression)) s.SequenceIndex++;
            s.NextRunAt = await ComputeNextRunAsync(s, now.AddSeconds(1), ct);
            if (s.NextRunAt is null) s.Enabled = false;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); continue; }

            var req = Json.Deserialize<SendNotificationRequest>(s.RequestJson)!;
            req.ApplicationId = s.ApplicationId;
            req.CompanyId = s.CompanyId;
            req.ScheduleAt = null;
            req.IdempotencyKey = $"schedule:{s.Id:N}:{occurrence:yyyyMMddHHmm}";
            var caller = new CallerContext { Kind = CallerKind.System, ActorId = s.CreatedByCredential ?? s.CreatedBy, ActorName = $"Schedule {s.Name}", Grants = [new ScopeGrant(Roles.PlatformAdmin, null, null, null, null)] };
            try
            {
                await notifications.AcceptAsync(caller, req, RequestSource.Schedule, new AcceptOptions { ScheduleId = s.Id }, ct);
                ran++;
            }
            catch (HubException ex)
            {
                logger.LogWarning("Schedule {Schedule} run failed: {Error}", s.Id, ex.Message);
                db.ChangeTracker.Clear();
                db.SystemAlerts.Add(new SystemAlert { TenantId = s.TenantId, Severity = "warning", Source = "scheduler", Message = $"Schedule '{s.Name}' could not create its notification: {ex.Message}" });
                await db.SaveChangesAsync(ct);
            }
        }
        return ran;
    }
}
