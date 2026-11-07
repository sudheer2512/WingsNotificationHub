using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Api.Infrastructure;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Rules;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Api.Endpoints;

public sealed record CampaignRequest(Guid CompanyId, Guid ApplicationId, string Name, string? Description, List<Channel> Channels, string TemplateCode, string? NotificationTypeCode,
    MessageClass MessageClass, List<Guid>? GroupIds, ConditionNode? Segment, Dictionary<string, string>? Variables, DateTimeOffset? ScheduledAt, int ThrottlePerMinute = 600);
public sealed record CampaignTestSend(RecipientDto Recipient);
public sealed record ScheduleRequest(Guid CompanyId, Guid? ApplicationId, string Name, string? CronExpression, List<int>? SequenceOffsetsMinutes, string TimeZone,
    DateTimeOffset? StartAt, DateTimeOffset? EndAt, int? MaxRuns, bool WorkingDaysOnly, Guid? HolidayCalendarId, SendNotificationRequest Notification);
public sealed record HolidayCalendarRequest(Guid? TenantId, string Name, string? Country, List<HolidayDateRequest> Dates);
public sealed record HolidayDateRequest(DateOnly Date, string Name);

public static class CampaignEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/campaigns").WithTags("Campaigns").RequireAuthorization("admin");

        g.MapGet("", async (HttpContext ctx, NotificationHubDbContext db, CampaignStatus? status) =>
        {
            var caller = await ctx.CallerAsync();
            var q = db.Campaigns.AsNoTracking().Visible(caller.FilterFor(Permission.ViewCampaigns));
            if (status is { } s) q = q.Where(c => c.Status == s);
            var rows = await q.OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync(ctx.RequestAborted);
            var ids = rows.Select(r => r.Id).ToList();
            var progress = await db.CampaignRecipients.AsNoTracking().Where(r => ids.Contains(r.CampaignId)).GroupBy(r => new { r.CampaignId, r.Status })
                .Select(x => new { x.Key.CampaignId, x.Key.Status, n = x.Count() }).ToListAsync(ctx.RequestAborted);
            var delivery = await db.NotificationDeliveries.AsNoTracking().Where(d => d.CampaignId != null && ids.Contains(d.CampaignId.Value)).GroupBy(d => new { d.CampaignId, d.Status })
                .Select(x => new { x.Key.CampaignId, x.Key.Status, n = x.Count(), cost = x.Sum(d => d.Cost ?? 0) }).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Select(c => new
            {
                c.Id, c.Name, c.Description, c.Status, c.Suspended, c.CompanyId, c.ApplicationId, channels = c.Channels.Select(ChannelNames.ToWire), c.TemplateCode, c.MessageClass,
                c.ScheduledAt, c.ThrottlePerMinute, c.TotalRecipients, c.CreatedBy, c.CreatedByName, c.ApprovedBy, c.ApprovedAt, c.StartedAt, c.CompletedAt, c.CreatedAt,
                recipients = progress.Where(p => p.CampaignId == c.Id).ToDictionary(p => p.Status.ToString(), p => p.n),
                deliveries = delivery.Where(p => p.CampaignId == c.Id).ToDictionary(p => p.Status.ToString(), p => p.n),
                cost = delivery.Where(p => p.CampaignId == c.Id).Sum(p => p.cost),
            }));
        });

        g.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var c = await Load(ctx, db, id, Permission.ManageCampaigns);
            return Results.Ok(new
            {
                c.Id, c.CompanyId, c.ApplicationId, c.Name, c.Description, channels = c.Channels.Select(ChannelNames.ToWire), c.TemplateCode, c.NotificationTypeCode, c.MessageClass,
                c.GroupIds, segment = Json.Deserialize<ConditionNode>(c.SegmentJson), variables = Json.Deserialize<Dictionary<string, string>>(c.VariablesJson), c.ScheduledAt, c.ThrottlePerMinute,
            });
        }).WithSummary("The editable fields of a campaign, in the shape PUT accepts");

        g.MapPost("", async (CampaignRequest req, HttpContext ctx, NotificationHubDbContext db, TenantContextResolver resolver, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var tc = await resolver.ResolveAsync(caller, req.ApplicationId, req.CompanyId, Permission.ManageCampaigns, null, ctx.RequestAborted, allowSuspended: true);
            var c = new Campaign { TenantId = tc.Tenant.Id, CompanyId = tc.Company.Id, ApplicationId = tc.Application.Id, CreatedBy = caller.ActorId, CreatedByName = caller.ActorName };
            Apply(c, req);
            db.Campaigns.Add(c);
            audit.Record(caller, "campaign.created", nameof(Campaign), c.Id.ToString(), c.Name, c.TenantId, c.CompanyId, c.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/campaigns/{c.Id}", new { c.Id });
        });
        g.MapPut("/{id:guid}", async (Guid id, CampaignRequest req, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var c = await Load(ctx, db, id, Permission.ManageCampaigns);
            if (c.Status != CampaignStatus.Draft) throw HubException.Conflict("campaign_locked", "Only draft campaigns can be edited.");
            Apply(c, req);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { c.Id });
        });
        g.MapGet("/{id:guid}/recipients", async (Guid id, HttpContext ctx, NotificationHubDbContext db, CampaignRecipientStatus? status, int page = 1) =>
        {
            var caller = await ctx.CallerAsync();
            var c = await Load(ctx, db, id, Permission.ViewCampaigns);
            var mask = !caller.Can(Permission.ViewMessageContent, c.TenantId, c.CompanyId, c.ApplicationId);
            var q = db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == id);
            if (status is { } s) q = q.Where(r => r.Status == s);
            var rows = await q.OrderBy(r => r.RowNumber).Skip((page - 1) * 100).Take(100).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Select(r => new
            {
                r.Id, r.RowNumber, r.ExternalId, r.Name, email = mask && r.Email is not null ? Core.Addressing.MaskEmail(r.Email) : r.Email,
                mobile = mask && r.Mobile is not null ? Core.Addressing.MaskPhone(r.Mobile) : r.Mobile, r.Status, r.Error, r.NotificationRequestId, r.ProcessedAt,
            }));
        }).WithSummary("Per-recipient outcomes");
        g.MapPost("/{id:guid}/import", async (Guid id, HttpContext ctx, NotificationHubDbContext db, CampaignService svc) =>
        {
            var c = await Load(ctx, db, id, Permission.ManageCampaigns);
            if (!ctx.Request.HasFormContentType) throw HubException.BadRequest("multipart_required", "Upload a CSV or .xlsx file as multipart/form-data field 'file'.");
            var file = (await ctx.Request.ReadFormAsync(ctx.RequestAborted)).Files["file"] ?? throw HubException.Validation("Missing 'file'.");
            if (file.Length > 20 * 1024 * 1024) throw HubException.Validation("Import files are limited to 20 MB.");
            await using var stream = file.OpenReadStream();
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ctx.RequestAborted);
            buffer.Position = 0;
            return Results.Ok(await svc.ImportAsync(c, buffer, file.FileName, ctx.RequestAborted));
        }).DisableAntiforgery().WithSummary("Import recipients from CSV/Excel (columns: email, mobile, name, externalId, plus template variables)");
        g.MapPost("/{id:guid}/test-send", async (Guid id, CampaignTestSend req, HttpContext ctx, NotificationHubDbContext db, NotificationService notifications) =>
        {
            var caller = await ctx.CallerAsync();
            var c = await Load(ctx, db, id, Permission.ManageCampaigns);
            var vars = (Json.Deserialize<Dictionary<string, string>>(c.VariablesJson) ?? []).ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
            var resp = await notifications.AcceptAsync(caller, new SendNotificationRequest
            {
                ApplicationId = c.ApplicationId, CompanyId = c.CompanyId, TemplateCode = c.TemplateCode, NotificationType = c.NotificationTypeCode, Channels = c.Channels,
                Recipient = req.Recipient, Variables = vars, Metadata = new() { ["campaignTest"] = c.Name },
            }, RequestSource.TestSend, new AcceptOptions { MessageClass = c.MessageClass, RequiredPermission = Permission.ManageCampaigns }, ctx.RequestAborted);
            return Results.Ok(resp);
        }).WithSummary("Send the campaign to a single test recipient");
        g.MapPost("/{id:guid}/submit", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var c = await Load(ctx, db, id, Permission.ManageCampaigns);
            if (c.Status != CampaignStatus.Draft) throw HubException.Conflict("invalid_state", "Only drafts can be submitted.");
            var hasAudience = c.GroupIds.Count > 0 || c.SegmentJson is not null || await db.CampaignRecipients.AnyAsync(r => r.CampaignId == id, ctx.RequestAborted);
            if (!hasAudience) throw HubException.Validation("Add recipients (import, groups or a segment) before submitting.");
            c.Status = CampaignStatus.PendingApproval;
            db.ApprovalRequests.Add(new ApprovalRequest { TenantId = c.TenantId, Kind = ApprovalKind.Campaign, EntityType = nameof(Campaign), EntityId = c.Id, Title = $"Campaign '{c.Name}' ({c.TotalRecipients} imported recipients)", RequestedBy = caller.ActorId, RequestedByName = caller.ActorName });
            audit.Record(caller, "campaign.submitted", nameof(Campaign), id.ToString(), c.Name, c.TenantId, c.CompanyId, c.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { c.Status });
        });
        g.MapPost("/{id:guid}/approve", async (Guid id, HttpContext ctx, NotificationHubDbContext db, CampaignService svc) =>
        {
            var caller = await ctx.CallerAsync();
            var c = await Load(ctx, db, id, Permission.ApproveCampaigns);
            await svc.ApproveAsync(caller, c, ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { c.Status });
        }).WithSummary("Approve (maker-checker); approved campaigns start at their scheduled time");
        foreach (var (action, from, to) in new[] { ("pause", CampaignStatus.Running, CampaignStatus.Paused), ("resume", CampaignStatus.Paused, CampaignStatus.Running) })
            g.MapPost($"/{{id:guid}}/{action}", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
            {
                var caller = await ctx.CallerAsync();
                var c = await Load(ctx, db, id, Permission.ManageCampaigns);
                if (c.Status != from) throw HubException.Conflict("invalid_state", $"Campaign must be {from} to {action}.");
                c.Status = to;
                audit.Record(caller, $"campaign.{action}d", nameof(Campaign), id.ToString(), c.Name, c.TenantId, c.CompanyId, c.ApplicationId);
                await db.SaveChangesAsync(ctx.RequestAborted);
                return Results.Ok(new { c.Status });
            });
        g.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var c = await Load(ctx, db, id, Permission.ManageCampaigns);
            if (c.Status is CampaignStatus.Completed or CampaignStatus.Cancelled) throw HubException.Conflict("invalid_state", "Campaign already finished.");
            c.Status = CampaignStatus.Cancelled;
            c.CompletedAt = DateTime.UtcNow;
            c.Suspended = false; // nothing is left to hold: every pending message is cancelled below
            // Queued campaign messages not yet sent are cancelled too.
            var pending = await db.NotificationDeliveries.Where(d => d.CampaignId == id && (d.Status == DeliveryStatus.Scheduled || d.Status == DeliveryStatus.Queued || d.Status == DeliveryStatus.RetryPending))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Cancelled).SetProperty(d => d.StatusReason, "campaign cancelled").SetProperty(d => d.CompletedAt, DateTime.UtcNow), ctx.RequestAborted);
            await db.CampaignRecipients.Where(r => r.CampaignId == id && r.Status == CampaignRecipientStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, CampaignRecipientStatus.Skipped).SetProperty(r => r.Error, "campaign cancelled"), ctx.RequestAborted);
            audit.Record(caller, "campaign.cancelled", nameof(Campaign), id.ToString(), $"{c.Name}; {pending} pending deliveries cancelled", c.TenantId, c.CompanyId, c.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { c.Status, cancelledDeliveries = pending });
        });

        // ── recurring schedules & calendars ──
        var s = api.MapGroup("/schedules").WithTags("Scheduling").RequireAuthorization("app-or-admin");
        s.MapGet("", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var rows = await db.NotificationSchedules.AsNoTracking().Visible(caller.FilterFor(Permission.ViewNotifications)).OrderBy(x => x.NextRunAt).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Select(x => new
            {
                x.Id, x.Name, x.CompanyId, x.ApplicationId, x.CronExpression, sequence = Json.Deserialize<List<int>>(x.SequenceOffsetsJson), x.SequenceIndex, x.TimeZone, x.StartAt, x.EndAt,
                x.NextRunAt, x.LastRunAt, x.RunCount, x.MaxRuns, x.WorkingDaysOnly, x.HolidayCalendarId, x.Enabled, request = Json.Deserialize<SendNotificationRequest>(x.RequestJson),
            }));
        });
        s.MapPost("", async (ScheduleRequest req, HttpContext ctx, NotificationHubDbContext db, TenantContextResolver resolver, ScheduleService schedules, NotificationService notifications) =>
        {
            var caller = await ctx.CallerAsync();
            var tc = await resolver.ResolveAsync(caller, req.ApplicationId ?? req.Notification.ApplicationId, req.CompanyId, Permission.SendNotifications, Scopes.NotificationsSend, ctx.RequestAborted);
            await ValidateScheduleAsync(caller, req, tc, notifications, ctx.RequestAborted);
            var sch = new NotificationSchedule
            {
                TenantId = tc.Tenant.Id, CompanyId = tc.Company.Id, ApplicationId = tc.Application.Id, CreatedBy = caller.ActorId,
                CreatedByCredential = caller.Kind == CallerKind.Application ? caller.ActorId : null, StartAt = DateTime.UtcNow,
            };
            ApplySchedule(sch, req);
            sch.NextRunAt = await schedules.ComputeNextRunAsync(sch, DateTime.UtcNow, ctx.RequestAborted);
            db.NotificationSchedules.Add(sch);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/schedules/{sch.Id}", new { sch.Id, sch.NextRunAt });
        }).WithSummary("Create a recurring notification or reminder sequence (time-zone, working-day and holiday aware)");
        s.MapPut("/{id:guid}", async (Guid id, ScheduleRequest req, HttpContext ctx, NotificationHubDbContext db, TenantContextResolver resolver, ScheduleService schedules, NotificationService notifications, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var x = await LoadSchedule(caller, db, id, ctx.RequestAborted);
            // A schedule stays with its company and application; only what it sends and when can change.
            var tc = await resolver.ResolveAsync(caller, x.ApplicationId, x.CompanyId, Permission.SendNotifications, Scopes.NotificationsSend, ctx.RequestAborted);
            await ValidateScheduleAsync(caller, req, tc, notifications, ctx.RequestAborted);
            var sequence = x.SequenceOffsetsJson;
            ApplySchedule(x, req);
            if (x.SequenceOffsetsJson != sequence) x.SequenceIndex = 0;
            if (x.Enabled) x.NextRunAt = await schedules.ComputeNextRunAsync(x, DateTime.UtcNow, ctx.RequestAborted);
            audit.Record(caller, "schedule.updated", nameof(NotificationSchedule), x.Id.ToString(), x.Name, x.TenantId, x.CompanyId, x.ApplicationId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { x.Id, x.NextRunAt });
        }).WithSummary("Change what a schedule sends and when");
        s.MapPost("/{id:guid}/enabled/{enabled:bool}", async (Guid id, bool enabled, HttpContext ctx, NotificationHubDbContext db, ScheduleService schedules) =>
        {
            var caller = await ctx.CallerAsync();
            var x = await LoadSchedule(caller, db, id, ctx.RequestAborted);
            x.Enabled = enabled;
            if (enabled) x.NextRunAt = await schedules.ComputeNextRunAsync(x, DateTime.UtcNow, ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { x.Enabled, x.NextRunAt });
        }).WithSummary("Pause/resume (cancel/reschedule) a schedule");
        s.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var x = await LoadSchedule(caller, db, id, ctx.RequestAborted);
            db.NotificationSchedules.Remove(x);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });

        var cal = api.MapGroup("/holiday-calendars").WithTags("Scheduling").RequireAuthorization("admin");
        cal.MapGet("", async (HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            var rows = await db.HolidayCalendars.AsNoTracking().Include(c => c.Dates).ToListAsync(ctx.RequestAborted);
            return Results.Ok(rows.Where(c => c.TenantId is null || caller.Can(Permission.ViewTenancy, c.TenantId)).Select(c => new { c.Id, c.TenantId, c.Name, c.Country, dates = c.Dates.OrderBy(d => d.Date).Select(d => new { d.Date, d.Name }) }));
        });
        cal.MapPost("", async (HolidayCalendarRequest req, HttpContext ctx, NotificationHubDbContext db) =>
        {
            var caller = await ctx.CallerAsync();
            if (req.TenantId is null ? !caller.IsPlatformAdmin : !caller.Can(Permission.ManageTenancy, req.TenantId)) throw new AccessDeniedException();
            var c = new HolidayCalendar { TenantId = req.TenantId, Name = req.Name, Country = req.Country };
            foreach (var d in req.Dates.DistinctBy(d => d.Date)) c.Dates.Add(new HolidayDate { CalendarId = c.Id, Date = d.Date, Name = d.Name });
            db.HolidayCalendars.Add(c);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/v1/holiday-calendars/{c.Id}", new { c.Id });
        });
        cal.MapPut("/{id:guid}", async (Guid id, HolidayCalendarRequest req, HttpContext ctx, NotificationHubDbContext db, ScheduleService schedules, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var c = await LoadCalendar(caller, db, id, ctx.RequestAborted);
            if (string.IsNullOrWhiteSpace(req.Name)) throw HubException.Validation("name is required.");
            c.Name = req.Name.Trim(); c.Country = req.Country;
            var wanted = req.Dates.DistinctBy(d => d.Date).ToDictionary(d => d.Date, d => d.Name);
            db.HolidayDates.RemoveRange(c.Dates.Where(d => !wanted.ContainsKey(d.Date)));
            foreach (var d in c.Dates.Where(d => wanted.ContainsKey(d.Date))) d.Name = wanted[d.Date];
            foreach (var (date, name) in wanted.Where(w => c.Dates.All(d => d.Date != w.Key))) db.HolidayDates.Add(new HolidayDate { CalendarId = c.Id, Date = date, Name = name });
            audit.Record(caller, "holiday_calendar.updated", nameof(HolidayCalendar), id.ToString(), $"{c.Name}: {wanted.Count} dates", c.TenantId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            // Next runs were worked out against the old dates.
            foreach (var x in await db.NotificationSchedules.Where(x => x.HolidayCalendarId == id && x.Enabled).ToListAsync(ctx.RequestAborted))
                x.NextRunAt = await schedules.ComputeNextRunAsync(x, DateTime.UtcNow, ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { c.Id });
        }).WithSummary("Change a calendar's name, country and dates; schedules using it are rescheduled");
        cal.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, NotificationHubDbContext db, AuditService audit) =>
        {
            var caller = await ctx.CallerAsync();
            var c = await LoadCalendar(caller, db, id, ctx.RequestAborted);
            var used = await db.NotificationSchedules.CountAsync(x => x.HolidayCalendarId == id, ctx.RequestAborted);
            if (used > 0) throw HubException.Conflict("calendar_in_use", $"{used} schedule(s) use this calendar. Choose another calendar for them first.");
            db.HolidayCalendars.Remove(c);
            audit.Record(caller, "holiday_calendar.deleted", nameof(HolidayCalendar), id.ToString(), c.Name, c.TenantId);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.NoContent();
        });
    }

    private static async Task ValidateScheduleAsync(CallerContext caller, ScheduleRequest req, TenantContext tc, NotificationService notifications, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw HubException.Validation("name is required.");
        if (string.IsNullOrWhiteSpace(req.CronExpression) == (req.SequenceOffsetsMinutes is not { Count: > 0 }))
            throw HubException.Validation("Provide either cronExpression (recurring) or sequenceOffsetsMinutes (reminder sequence).");
        if (req.CronExpression is { } cron)
        {
            try { Cronos.CronExpression.Parse(cron); } catch (Cronos.CronFormatException ex) { throw HubException.Validation("Invalid cron expression: " + ex.Message); }
        }
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(req.TimeZone, out _)) throw HubException.Validation($"Unknown time zone '{req.TimeZone}'.");
        req.Notification.ApplicationId = tc.Application.Id;
        req.Notification.CompanyId = tc.Company.Id;
        // Validate the notification up front with a dry run so a broken schedule is rejected now, not at 2 a.m.
        var probe = JsonSerializer.Deserialize<SendNotificationRequest>(JsonSerializer.Serialize(req.Notification))!;
        probe.DryRun = true; probe.ScheduleAt = null; probe.IdempotencyKey = null;
        await notifications.AcceptAsync(caller, probe, RequestSource.Schedule, null, ct);
    }

    private static void ApplySchedule(NotificationSchedule x, ScheduleRequest req)
    {
        x.Name = req.Name; x.CronExpression = req.CronExpression; x.TimeZone = req.TimeZone; x.EndAt = req.EndAt?.UtcDateTime; x.MaxRuns = req.MaxRuns;
        x.SequenceOffsetsJson = req.SequenceOffsetsMinutes is { Count: > 0 } o ? Json.Serialize(o.OrderBy(v => v).ToList()) : null;
        if (req.StartAt is { } start) x.StartAt = start.UtcDateTime;
        x.WorkingDaysOnly = req.WorkingDaysOnly; x.HolidayCalendarId = req.HolidayCalendarId; x.RequestJson = Json.Serialize(req.Notification);
    }

    private static async Task<NotificationSchedule> LoadSchedule(CallerContext caller, NotificationHubDbContext db, Guid id, CancellationToken ct)
    {
        var x = await db.NotificationSchedules.FirstOrDefaultAsync(z => z.Id == id, ct);
        if (x is null || !caller.Can(Permission.SendNotifications, x.TenantId, x.CompanyId, x.ApplicationId) || (caller.Kind == CallerKind.Application && caller.ApplicationId != x.ApplicationId))
            throw HubException.NotFound("Schedule");
        return x;
    }

    private static async Task<HolidayCalendar> LoadCalendar(CallerContext caller, NotificationHubDbContext db, Guid id, CancellationToken ct)
    {
        var c = await db.HolidayCalendars.Include(x => x.Dates).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw HubException.NotFound("Holiday calendar");
        if (c.TenantId is null ? !caller.IsPlatformAdmin : !caller.Can(Permission.ManageTenancy, c.TenantId)) throw new AccessDeniedException();
        return c;
    }

    private static void Apply(Campaign c, CampaignRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Channels.Count == 0 || string.IsNullOrWhiteSpace(req.TemplateCode))
            throw HubException.Validation("name, channels and templateCode are required.");
        if (ConditionEvaluator.Validate(req.Segment) is { Count: > 0 } errs) throw HubException.Validation("Invalid segment.", new() { ["segment"] = errs.ToArray() });
        c.Name = req.Name; c.Description = req.Description; c.Channels = req.Channels; c.TemplateCode = req.TemplateCode.ToUpperInvariant(); c.NotificationTypeCode = req.NotificationTypeCode;
        c.MessageClass = req.MessageClass; c.GroupIds = req.GroupIds ?? []; c.SegmentJson = req.Segment is null ? null : Json.Serialize(req.Segment);
        c.VariablesJson = req.Variables is null ? null : Json.Serialize(req.Variables); c.ScheduledAt = req.ScheduledAt?.UtcDateTime; c.ThrottlePerMinute = Math.Clamp(req.ThrottlePerMinute, 1, 100_000);
    }

    private static async Task<Campaign> Load(HttpContext ctx, NotificationHubDbContext db, Guid id, Permission p)
    {
        var caller = await ctx.CallerAsync();
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
        if (c is null || !caller.Can(p, c.TenantId, c.CompanyId, c.ApplicationId)) throw HubException.NotFound("Campaign");
        return c;
    }
}
