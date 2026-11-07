using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Policies;
using Wings.NotificationHub.Core.Security;
using Wings.NotificationHub.Core.Templates;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Messaging;

namespace Wings.NotificationHub.Infrastructure.Services;

public sealed class AcceptOptions
{
    public Guid? BusinessEventId { get; init; }
    public Guid? RuleId { get; init; }
    public Guid? CampaignId { get; init; }
    public Guid? ScheduleId { get; init; }
    public MessageClass? MessageClass { get; init; }
    public QuietHoursPolicy? QuietHours { get; init; }
    public int DelaySeconds { get; init; }
    /// <summary>Set when a human approved a pending AI-agent request; the request row already exists.</summary>
    public Guid? ApprovedRequestId { get; init; }
    /// <summary>Skip saving (caller batches several acceptances into one unit of work).</summary>
    public bool Defer { get; init; }
    /// <summary>What the caller must hold. Test sends go to one address and need the right to manage what is being tested, not general send rights.</summary>
    public Permission RequiredPermission { get; init; } = Permission.SendNotifications;
}

/// <summary>
/// Accepts notification requests: authorizes the tenant context, enforces idempotency, quotas, consent and
/// preferences, renders templates and durably records request + deliveries + outbox messages in one transaction.
/// Acceptance means "durably recorded", never "delivered".
/// </summary>
public sealed class NotificationService(
    NotificationHubDbContext db,
    TenantContextResolver contexts,
    RecipientResolver recipients,
    TemplateResolver templates,
    UsageService usage,
    ISecretProtector protector,
    IOptions<HubOptions> options,
    ILogger<NotificationService> logger)
{
    public async Task<SendNotificationResponse> AcceptAsync(CallerContext caller, SendNotificationRequest req, RequestSource source, AcceptOptions? opts, CancellationToken ct)
    {
        opts ??= new AcceptOptions();
        using var activity = Telemetry.Source.StartActivity("notification.accept");
        var o = options.Value;
        var ctx = await contexts.ResolveAsync(caller, req.ApplicationId, req.CompanyId, opts.RequiredPermission, Scopes.NotificationsSend, ct);
        activity?.SetTag("wnh.tenant", ctx.Tenant.Id).SetTag("wnh.application", ctx.Application.Code);

        // ── notification type, template, channels ──
        NotificationType? type = null;
        if (!string.IsNullOrWhiteSpace(req.NotificationType))
            type = await contexts.FindNotificationTypeAsync(ctx.Tenant.Id, ctx.Application.Id, req.NotificationType.Trim(), ct)
                   ?? throw HubException.Validation($"Notification type '{req.NotificationType}' is not registered for this application.");
        var templateCode = (req.TemplateCode ?? type?.DefaultTemplateCode)?.Trim();
        if (string.IsNullOrEmpty(templateCode)) throw HubException.Validation("templateCode is required (or a notificationType with a default template).");
        var channels = (req.Channels is { Count: > 0 } ? req.Channels : type?.DefaultChannels ?? []).Distinct().ToList();
        if (channels.Count == 0) throw HubException.Validation("At least one channel is required.");
        var notAllowed = channels.Where(c => !ChannelSupport.Allowed(ctx.Application, c)).ToList();
        if (notAllowed.Count > 0) throw new HubException(403, "channel_not_permitted", $"Channel(s) not permitted for this application: {string.Join(", ", notAllowed.Select(ChannelNames.ToWire))}.");
        var priority = req.Priority ?? type?.DefaultPriority ?? NotificationPriority.Normal;
        var messageClass = opts.MessageClass ?? type?.MessageClass ?? MessageClass.Transactional;
        var category = req.App?.Category ?? type?.Category;

        ValidateRequest(req, o);

        // ── AI agent restrictions ──
        if (caller.IsAiAgent && opts.ApprovedRequestId is null)
        {
            if (type is null) throw new HubException(403, "ai_type_required", "AI agents must use a registered notification type.");
            if (!type.AiAllowed || type.IsSensitive) throw new HubException(403, "ai_not_permitted", $"Notification type '{type.Code}' is not permitted for AI agents.");
            if (req.Audience is not null || (req.Recipients?.Count ?? 0) + (req.Recipient is null ? 0 : 1) != 1)
                throw new HubException(403, "ai_single_recipient", "AI agents may notify exactly one known recipient per request.");
            var r = req.Recipient ?? req.Recipients![0];
            if (r.ContactId is null && r.UserId is null && r.CustomerId is null || r.Email is not null || r.Mobile is not null || r.WhatsApp is not null || r.Cc is not null || r.Bcc is not null)
                throw new HubException(403, "ai_known_recipient", "AI agents must reference a registered contact (contactId/userId/customerId) and may not supply addresses.");
            if (messageClass == MessageClass.Promotional) throw new HubException(403, "ai_not_permitted", "AI agents may not send promotional messages.");
        }

        // ── idempotency ──
        var requestHash = Hash(req);
        if (!string.IsNullOrWhiteSpace(req.IdempotencyKey) && opts.ApprovedRequestId is null && !req.DryRun)
        {
            var existing = await db.NotificationRequests.AsNoTracking().Include(r => r.Deliveries)
                .FirstOrDefaultAsync(r => r.ApplicationId == ctx.Application.Id && r.IdempotencyKey == req.IdempotencyKey, ct);
            if (existing is not null) return Duplicate(existing, requestHash);
        }

        if (caller.IsAiAgent && opts.ApprovedRequestId is null && type!.RequiresHumanApprovalForAi && !req.DryRun)
            return await CreatePendingApprovalAsync(caller, ctx, req, type, priority, messageClass, requestHash, ct);

        // ── recipients & policy state ──
        var list = new List<RecipientDto>();
        if (req.Recipient is not null) list.Add(req.Recipient);
        if (req.Recipients is not null) list.AddRange(req.Recipients);
        var resolved = await recipients.ResolveAsync(ctx.Company.Id, list, req.Audience, ctx.Company.Country, ct);
        if (resolved.Count == 0) throw HubException.Validation("No recipients were supplied or resolved.");
        if (resolved.Count > (req.Audience is null ? o.MaxRecipientsPerRequest : RecipientResolver.MaxAudience))
            throw HubException.Validation($"Too many recipients ({resolved.Count}); use the batch API or a campaign.");
        if (caller.IsAiAgent && resolved.Any(r => r.AdHoc)) throw new HubException(403, "ai_known_recipient", "AI agents may only notify registered contacts.");
        var policy = await recipients.LoadPolicyStateAsync(ctx.Tenant.Id, ctx.Company.Id, resolved, ct);
        var quiet = opts.QuietHours ?? Json.Deserialize<QuietHoursPolicy>(ctx.Company.QuietHoursJson);
        var now = DateTime.UtcNow;
        var baseSendAt = req.ScheduleAt?.UtcDateTime is { } s && s > now ? s : now;
        if (opts.DelaySeconds > 0) baseSendAt = baseSendAt.AddSeconds(opts.DelaySeconds);
        var expiresAt = req.ExpiresAt?.UtcDateTime;
        var retry = RetryPolicy.From(req.RetryPolicy, priority);

        var request = new NotificationRequest
        {
            Id = opts.ApprovedRequestId ?? Guid.NewGuid(),
            TenantId = ctx.Tenant.Id, CompanyId = ctx.Company.Id, ApplicationId = ctx.Application.Id,
            Source = source, ActorId = caller.ActorId, ActorName = caller.ActorName,
            BusinessEventId = opts.BusinessEventId, RuleId = opts.RuleId, CampaignId = opts.CampaignId, ScheduleId = opts.ScheduleId,
            NotificationTypeCode = type?.Code ?? req.NotificationType, TemplateCode = templateCode, Category = category,
            MessageClass = messageClass, Priority = priority,
            ScheduleAt = req.ScheduleAt?.UtcDateTime, ExpiresAt = expiresAt,
            IdempotencyKey = string.IsNullOrWhiteSpace(req.IdempotencyKey) ? null : req.IdempotencyKey.Trim(),
            RequestHash = requestHash,
            CorrelationId = req.CorrelationId ?? caller.CorrelationId,
            TraceId = Activity.Current?.TraceId.ToString() ?? caller.TraceId,
            MetadataJson = req.Metadata is { Count: > 0 } ? Json.Serialize(req.Metadata) : null,
            VariablesCipher = req.Variables is { Count: > 0 } ? protector.Protect(JsonSerializer.Serialize(req.Variables), Purposes.Variables) : null,
            RetryPolicyJson = req.RetryPolicy is null ? null : Json.Serialize(req.RetryPolicy),
        };

        var response = new SendNotificationResponse { NotificationId = request.Id, TrackingUrl = $"/api/v1/notifications/{request.Id}/status" };
        var previews = new List<RenderedPreviewDto>();
        var renderErrors = new Dictionary<string, string[]>();
        var perChannelCount = new Dictionary<Channel, int>();
        decimal reserve = 0;

        foreach (var rr in resolved)
        {
            var recipient = new NotificationRecipient
            {
                RequestId = request.Id, ContactId = rr.Contact?.Id, UserId = rr.UserId, CustomerId = rr.CustomerId, Name = rr.Name,
                Email = rr.Email, Mobile = rr.Mobile, WhatsApp = rr.WhatsApp, TimeZone = rr.TimeZone, Locale = rr.Locale,
            };
            request.Recipients.Add(recipient);
            var tz = TimeZones.Resolve(rr.TimeZone, ctx.Company.TimeZone, ctx.Tenant.DefaultTimeZone);

            foreach (var channel in channels)
            {
                var targets = await TargetsAsync(ctx, rr, channel, ct);
                if (targets.Count == 0)
                {
                    response.Warnings.Add($"{ChannelNames.ToWire(channel)}: no {AddressName(channel)} for recipient {Mask(rr)}; skipped.");
                    continue;
                }
                var resolvedTemplate = await templates.ResolveAsync(ctx.Tenant.Id, ctx.Company.Id, ctx.Application.Id, templateCode, channel, rr.Locale, ctx.Application.DefaultLocale, ct);
                if (resolvedTemplate is null)
                {
                    renderErrors[$"template.{ChannelNames.ToWire(channel)}"] = [$"No active '{templateCode}' template for channel {ChannelNames.ToWire(channel)}."];
                    continue;
                }
                var (tpl, version) = resolvedTemplate.Value;

                foreach (var target in targets)
                {
                    var unsubscribe = messageClass == MessageClass.Promotional && channel is Channel.Email or Channel.Sms
                        ? UnsubscribeUrl(ctx, channel, target.Address, rr.Contact?.Id) : null;
                    var vars = RenderVariables.Build(req.Variables, ctx.Tenant, ctx.Company, rr.Name, unsubscribe);
                    var footer = channel == Channel.Email && version.BodyFormat == BodyFormat.Html
                        ? RenderVariables.EmailFooter(ctx.Tenant, ctx.Company, unsubscribe, version.Body.Contains("UnsubscribeUrl", StringComparison.OrdinalIgnoreCase))
                        : null;
                    var rendered = ChannelRenderer.Render(version, channel, vars, footer);
                    if (rendered.Errors.Count > 0) renderErrors[$"template.{tpl.Code}.{ChannelNames.ToWire(channel)}"] = rendered.Errors.ToArray();
                    if (rendered.Missing.Count > 0) renderErrors[$"variables.{ChannelNames.ToWire(channel)}"] = rendered.Missing.Select(m => $"Missing variable '{m}'.").ToArray();
                    var content = rendered.Content;
                    content.App = MergeApp(content.App, req.App);
                    content.Attachments = channel == Channel.Email ? req.Attachments : null;
                    content.Cc = channel == Channel.Email ? rr.Cc : null;
                    content.Bcc = channel == Channel.Email ? rr.Bcc : null;
                    content.UnsubscribeUrl = unsubscribe;
                    if (channel == Channel.Webhook)
                        content.WebhookPayload = new JsonObject
                        {
                            ["notificationId"] = request.Id, ["notificationType"] = request.NotificationTypeCode, ["templateCode"] = templateCode,
                            ["subject"] = content.Subject, ["body"] = content.Body, ["priority"] = priority.ToString(),
                            ["variables"] = req.Variables is null ? null : JsonSerializer.SerializeToNode(req.Variables),
                            ["metadata"] = req.Metadata is null ? null : JsonSerializer.SerializeToNode(req.Metadata),
                        };

                    var decision = channel is Channel.Webhook
                        ? PolicyDecision.Allow
                        : CommunicationPolicy.Evaluate(channel, target.PolicyAddress, messageClass, category, type?.Mandatory ?? false,
                            ctx.Tenant.RequireMarketingConsent, policy[rr.Key], now);

                    var sendAt = baseSendAt;
                    if (decision.Allowed && quiet?.AppliesToMessage(messageClass, channel, priority) == true) sendAt = quiet.NextAllowed(sendAt, tz);
                    var scheduled = sendAt > now.AddSeconds(1);
                    var delivery = new NotificationDelivery
                    {
                        RequestId = request.Id, RecipientId = recipient.Id, TenantId = ctx.Tenant.Id, CompanyId = ctx.Company.Id,
                        ApplicationId = ctx.Application.Id, CampaignId = opts.CampaignId, Channel = channel, Address = target.Address,
                        DeviceId = target.DeviceId, TemplateId = tpl.Id, TemplateVersionId = version.Id, TemplateVersionNumber = version.VersionNumber,
                        MessageClass = messageClass, Priority = priority, Category = category,
                        ContentCipher = protector.Protect(Json.Serialize(content), Purposes.MessageContent),
                        Status = !decision.Allowed ? DeliveryStatus.Suppressed : scheduled ? DeliveryStatus.Scheduled : DeliveryStatus.Queued,
                        StatusReason = decision.Allowed ? (scheduled && sendAt != baseSendAt ? "deferred by quiet hours" : null) : decision.Reason,
                        FailureCode = decision.Allowed ? null : decision.ReasonCode,
                        NextAttemptAt = decision.Allowed ? sendAt : null,
                        MaxAttempts = retry.MaxAttempts,
                        ExpiresAt = expiresAt,
                        Units = rendered.Units,
                        CompletedAt = decision.Allowed ? null : now,
                    };
                    var fallback = req.Fallbacks?.FirstOrDefault(f => f.From == channel);
                    if (fallback is not null && fallback.To != channel) delivery.FallbackJson = Json.Serialize(fallback);
                    // Escalation watches the primary (first) channel's delivery.
                    if (req.Escalation is { } esc && channel == channels[0] && decision.Allowed)
                    {
                        delivery.EscalationJson = Json.Serialize(esc);
                        delivery.EscalateAt = sendAt.AddMinutes(Math.Clamp(esc.AfterMinutes, 1, 60 * 24 * 7));
                    }
                    if (decision.Allowed)
                    {
                        var (est, _) = await usage.EstimateCostAsync(null, channel, target.Address, messageClass, rendered.Units, ct);
                        delivery.EstimatedCost = est;
                        reserve += est;
                        perChannelCount[channel] = perChannelCount.GetValueOrDefault(channel) + 1;
                    }
                    request.Deliveries.Add(delivery);
                    response.Deliveries.Add(new DeliverySummaryDto
                    {
                        DeliveryId = delivery.Id, Channel = channel, Recipient = Addressing.Mask(channel, target.Address),
                        Status = delivery.Status, Reason = decision.Allowed ? delivery.StatusReason : decision.Reason,
                    });
                    if (req.DryRun)
                        previews.Add(new RenderedPreviewDto
                        {
                            Channel = channel, Recipient = Addressing.Mask(channel, target.Address), Subject = content.Subject ?? content.Title,
                            Body = content.Body, TextBody = content.TextBody, TemplateVersionId = version.Id,
                            PolicyDecision = decision.Allowed ? "allowed" : decision.ReasonCode, SendAt = new DateTimeOffset(sendAt, TimeSpan.Zero),
                        });
                }
            }
        }

        if (renderErrors.Count > 0)
            throw HubException.Validation("The notification could not be rendered.", renderErrors);
        if (request.Deliveries.Count == 0)
            throw new HubException(422, "no_deliverable_channels", "None of the requested channels could be addressed for the recipient(s). " + string.Join(" ", response.Warnings));

        if (req.DryRun)
        {
            response.Status = "dry_run";
            response.Preview = previews;
            return response;
        }

        await usage.CheckQuotaAsync(ctx.Tenant.Id, ctx.Company.Id, ctx.Application.Id, perChannelCount, ct);
        await usage.ReserveCreditsAsync(ctx.Tenant.Id, ctx.Company.Id, reserve, ct);

        request.Status = DeliveryStatusRules.Aggregate(request.Deliveries.Select(d => d.Status).ToList(), request.Deliveries.Any(d => d.Status == DeliveryStatus.Scheduled));
        if (request.Status is RequestStatus.Suppressed) request.CompletedAt = now;
        foreach (var d in request.Deliveries)
        {
            db.DeliveryEvents.Add(new DeliveryEvent
            {
                DeliveryId = d.Id, TenantId = d.TenantId, Type = "created", Status = d.Status, Source = source.ToString(), Applied = true,
                Detail = d.Status == DeliveryStatus.Suppressed ? d.StatusReason : d.StatusReason ?? (d.Status == DeliveryStatus.Scheduled ? $"scheduled for {d.NextAttemptAt:O}" : null),
            });
            if (d.Status == DeliveryStatus.Queued)
                Outbox.Add(db, MessageTypes.DispatchDelivery, new DispatchDeliveryMessage(d.Id), d.TenantId, correlationId: request.CorrelationId);
        }

        if (opts.ApprovedRequestId is { } approvedId)
        {
            var pending = await db.NotificationRequests.AsNoTracking().FirstAsync(r => r.Id == approvedId, ct);
            if (pending.Status != RequestStatus.PendingApproval) throw HubException.Conflict("not_pending", "The request is no longer pending approval.");
            // Keep the original row identity: creation time, idempotency key and the concurrency stamp it was read with.
            request.CreatedAt = pending.CreatedAt;
            request.IdempotencyKey = pending.IdempotencyKey;
            request.ConcurrencyStamp = pending.ConcurrencyStamp;
            db.NotificationRequests.Update(request);
            foreach (var r in request.Recipients) db.Entry(r).State = EntityState.Added;
            foreach (var d in request.Deliveries) db.Entry(d).State = EntityState.Added;
        }
        else db.NotificationRequests.Add(request);
        if (opts.Defer) return Finish(response, request);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (request.IdempotencyKey is not null)
        {
            // Lost an idempotency race with a concurrent identical request: return the winner.
            db.ChangeTracker.Clear();
            var winner = await db.NotificationRequests.AsNoTracking().Include(r => r.Deliveries)
                .FirstOrDefaultAsync(r => r.ApplicationId == ctx.Application.Id && r.IdempotencyKey == request.IdempotencyKey, ct);
            if (winner is null) throw;
            return Duplicate(winner, requestHash);
        }
        Telemetry.Accepted.Add(1, new KeyValuePair<string, object?>("source", source.ToString()));
        logger.LogInformation("Accepted notification {NotificationId} ({Type}) with {Count} deliveries from {Actor}", request.Id, request.NotificationTypeCode, request.Deliveries.Count, caller.ActorId);
        return Finish(response, request);
    }

    private static SendNotificationResponse Finish(SendNotificationResponse response, NotificationRequest request)
    {
        response.Status = request.Status switch
        {
            RequestStatus.Scheduled => "scheduled",
            RequestStatus.Suppressed => "suppressed",
            _ => "accepted",
        };
        return response;
    }

    private async Task<SendNotificationResponse> CreatePendingApprovalAsync(CallerContext caller, TenantContext ctx, SendNotificationRequest req, NotificationType type,
        NotificationPriority priority, MessageClass cls, string hash, CancellationToken ct)
    {
        var request = new NotificationRequest
        {
            TenantId = ctx.Tenant.Id, CompanyId = ctx.Company.Id, ApplicationId = ctx.Application.Id, Source = RequestSource.Mcp,
            ActorId = caller.ActorId, ActorName = caller.ActorName, NotificationTypeCode = type.Code, TemplateCode = req.TemplateCode ?? type.DefaultTemplateCode,
            Category = type.Category, MessageClass = cls, Priority = priority, Status = RequestStatus.PendingApproval,
            IdempotencyKey = string.IsNullOrWhiteSpace(req.IdempotencyKey) ? null : req.IdempotencyKey, RequestHash = hash,
            CorrelationId = req.CorrelationId ?? caller.CorrelationId, TraceId = Activity.Current?.TraceId.ToString(),
        };
        db.NotificationRequests.Add(request);
        db.ApprovalRequests.Add(new ApprovalRequest
        {
            TenantId = ctx.Tenant.Id, Kind = ApprovalKind.AiNotification, EntityType = nameof(NotificationRequest), EntityId = request.Id,
            Title = $"AI agent '{caller.ActorName}' requests {type.Code} notification",
            PayloadJson = protector.Protect(JsonSerializer.Serialize(new PendingAiNotification(req, caller.CredentialId ?? Guid.Empty)), Purposes.Approval),
            PayloadEncrypted = true, RequestedBy = caller.ActorId, RequestedByName = caller.ActorName,
        });
        await db.SaveChangesAsync(ct);
        return new SendNotificationResponse
        {
            NotificationId = request.Id, Status = "pending_approval", TrackingUrl = $"/api/v1/notifications/{request.Id}/status",
            Warnings = ["This notification type requires human approval for AI-initiated sends. It will be delivered once approved."],
        };
    }

    private SendNotificationResponse Duplicate(NotificationRequest existing, string hash)
    {
        if (existing.RequestHash is not null && existing.RequestHash != hash)
            throw HubException.Conflict("idempotency_key_reused", "The idempotency key was already used for a different request.");
        return new SendNotificationResponse
        {
            NotificationId = existing.Id, Duplicate = true, TrackingUrl = $"/api/v1/notifications/{existing.Id}/status",
            Status = existing.Status switch { RequestStatus.PendingApproval => "pending_approval", RequestStatus.Scheduled => "scheduled", _ => "accepted" },
            Deliveries = existing.Deliveries.Select(d => new DeliverySummaryDto
            {
                DeliveryId = d.Id, Channel = d.Channel, Recipient = Addressing.Mask(d.Channel, d.Address), Status = d.Status, Reason = d.StatusReason,
            }).ToList(),
        };
    }

    private sealed record Target(string Address, string PolicyAddress, Guid? DeviceId = null);

    private async Task<List<Target>> TargetsAsync(TenantContext ctx, ResolvedRecipient r, Channel channel, CancellationToken ct)
    {
        switch (channel)
        {
            case Channel.Email:
                return Addressing.IsValidEmail(r.Email) ? [new Target(r.Email!.ToLowerInvariant(), r.Email!.ToLowerInvariant())] : [];
            case Channel.Sms:
                return r.Mobile is not null ? [new Target(r.Mobile, r.Mobile)] : [];
            case Channel.WhatsApp:
                return r.WhatsApp is not null ? [new Target(r.WhatsApp, r.WhatsApp)] : [];
            case Channel.InApp:
                return string.IsNullOrWhiteSpace(r.UserId) ? [] : [new Target(r.UserId!, r.UserId!)];
            case Channel.Push:
            case Channel.WebPush:
                if (string.IsNullOrWhiteSpace(r.UserId)) return [];
                var platforms = channel == Channel.WebPush ? new[] { DevicePlatform.Web } : [DevicePlatform.Android, DevicePlatform.Ios];
                var q = db.DeviceRegistrations.AsNoTracking().Where(d => d.ApplicationId == ctx.Application.Id && d.CompanyId == ctx.Company.Id
                    && d.UserId == r.UserId && d.InvalidatedAt == null && platforms.Contains(d.Platform));
                if (r.DeviceIds is { Count: > 0 } ids) q = q.Where(d => ids.Contains(d.Id));
                return (await q.ToListAsync(ct)).Select(d => new Target(d.Id.ToString(), r.UserId!, d.Id)).ToList();
            case Channel.Webhook:
                var subs = await db.WebhookSubscriptions.AsNoTracking()
                    .Where(s => s.ApplicationId == ctx.Application.Id && s.Enabled && (s.CompanyId == null || s.CompanyId == ctx.Company.Id)).ToListAsync(ct);
                return subs.Where(s => s.EventTypes.Contains(WebhookEventTypes.NotificationWebhook)).Select(s => new Target(s.Id.ToString(), s.Id.ToString())).ToList();
            default:
                return [];
        }
    }

    private static string AddressName(Channel c) => c switch
    {
        Channel.Email => "valid email address",
        Channel.Sms or Channel.WhatsApp => "valid mobile number",
        Channel.InApp => "userId",
        Channel.Push or Channel.WebPush => "registered device",
        Channel.Webhook => "webhook subscription for notification.webhook",
        _ => "address",
    };

    private static string Mask(ResolvedRecipient r) => r.UserId ?? r.CustomerId ?? (r.Email is not null ? Addressing.MaskEmail(r.Email) : r.Mobile is not null ? Addressing.MaskPhone(r.Mobile) : r.Name ?? "with no contact details");

    private static AppNotificationOptionsDto? MergeApp(AppNotificationOptionsDto? fromTemplate, AppNotificationOptionsDto? fromRequest)
    {
        if (fromRequest is null) return fromTemplate;
        return new AppNotificationOptionsDto
        {
            DeepLink = fromRequest.DeepLink is null ? fromTemplate?.DeepLink : TemplateEngine.SafeUrl(fromRequest.DeepLink),
            ImageUrl = fromRequest.ImageUrl ?? fromTemplate?.ImageUrl,
            Icon = fromRequest.Icon ?? fromTemplate?.Icon,
            Category = fromRequest.Category,
            Actions = fromRequest.Actions?.Select(a => new AppActionDto { Id = a.Id, Label = a.Label, DeepLink = a.DeepLink is null ? null : TemplateEngine.SafeUrl(a.DeepLink) }).ToList(),
            TimeToLiveSeconds = fromRequest.TimeToLiveSeconds,
            Data = fromRequest.Data,
        };
    }

    private string UnsubscribeUrl(TenantContext ctx, Channel channel, string address, Guid? contactId)
    {
        var token = protector.Protect(Json.Serialize(new UnsubscribeToken(ctx.Tenant.Id, ctx.Company.Id, channel, address, contactId)), Purposes.Unsubscribe);
        return $"{options.Value.PublicBaseUrl.TrimEnd('/')}/u/{Hashing.Base64Url(Encoding.UTF8.GetBytes(token))}";
    }

    private static void ValidateRequest(SendNotificationRequest req, HubOptions o)
    {
        var errors = new Dictionary<string, string[]>();
        if (req.IdempotencyKey is { Length: > 200 }) errors["idempotencyKey"] = ["Must be at most 200 characters."];
        if (req.CorrelationId is { Length: > 200 }) errors["correlationId"] = ["Must be at most 200 characters."];
        if (req.Variables is not null && JsonSerializer.SerializeToUtf8Bytes(req.Variables).Length > o.MaxVariablesBytes)
            errors["variables"] = [$"Variables exceed {o.MaxVariablesBytes} bytes."];
        if (req.ScheduleAt is { } s && s < DateTimeOffset.UtcNow.AddMinutes(-5)) errors["scheduleAt"] = ["Must not be in the past."];
        if (req.ScheduleAt is { } s2 && s2 > DateTimeOffset.UtcNow.AddYears(1)) errors["scheduleAt"] = ["Must be within one year."];
        if (req.ExpiresAt is { } e && e <= DateTimeOffset.UtcNow) errors["expiresAt"] = ["Must be in the future."];
        if (req.ExpiresAt is { } e2 && req.ScheduleAt is { } s3 && e2 <= s3) errors["expiresAt"] = ["Must be after scheduleAt."];
        foreach (var (r, i) in (req.Recipients ?? []).Prepend(req.Recipient).Where(r => r is not null).Select((r, i) => (r!, i)))
        {
            if (r.Email is not null && !Addressing.IsValidEmail(r.Email)) errors[$"recipients[{i}].email"] = ["Invalid email address."];
            if (r.Mobile is not null && Addressing.NormalizePhone(r.Mobile) is null) errors[$"recipients[{i}].mobile"] = ["Invalid mobile number; use E.164 (+919876543210)."];
            if (r.WhatsApp is not null && Addressing.NormalizePhone(r.WhatsApp) is null) errors[$"recipients[{i}].whatsapp"] = ["Invalid WhatsApp number; use E.164."];
            foreach (var cc in (r.Cc ?? []).Concat(r.Bcc ?? []))
                if (!Addressing.IsValidEmail(cc)) errors[$"recipients[{i}].cc"] = [$"Invalid address '{cc}'."];
        }
        if (errors.Count > 0) throw HubException.Validation("The request is invalid.", errors);
    }

    /// <summary>Canonical hash of the request (idempotency payload check). DryRun and correlation id are excluded.</summary>
    public static string Hash(SendNotificationRequest req)
    {
        var node = JsonSerializer.SerializeToNode(req, Json.Options)!.AsObject();
        node.Remove("dryRun");
        node.Remove("correlationId");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }
}

public sealed record UnsubscribeToken(Guid TenantId, Guid CompanyId, Channel Channel, string Address, Guid? ContactId);
public sealed record PendingAiNotification(SendNotificationRequest Request, Guid CredentialId);
