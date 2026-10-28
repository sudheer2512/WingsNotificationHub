using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Policies;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Messaging;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Infrastructure.Delivery;

/// <summary>
/// Creates fallback deliveries (channel A failed/suppressed → channel B) and escalations (not read within N minutes →
/// another channel and/or recipient) inside the original request, so the whole story stays on one timeline.
/// </summary>
public sealed class FollowUpService(
    NotificationHubDbContext db,
    TemplateResolver templates,
    RecipientResolver recipients,
    DeliveryStatusUpdater status,
    ISecretProtector protector,
    ILogger<FollowUpService> logger)
{
    private static readonly DeliveryStatus[] DefaultFallbackStatuses = [DeliveryStatus.Failed, DeliveryStatus.Suppressed, DeliveryStatus.Expired];

    /// <summary>Processes due fallbacks and escalations. Returns the number of follow-up deliveries created.</summary>
    public async Task<int> ProcessDueAsync(int batch, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var created = 0;
        var fallbacks = await db.NotificationDeliveries
            .Where(d => d.FallbackJson != null && !d.FollowUpProcessed
                        && (d.Status == DeliveryStatus.Failed || d.Status == DeliveryStatus.Suppressed || d.Status == DeliveryStatus.Expired
                            || d.Status == DeliveryStatus.Delivered || d.Status == DeliveryStatus.Read || d.Status == DeliveryStatus.Cancelled))
            .OrderBy(d => d.UpdatedAt).Take(batch).ToListAsync(ct);
        foreach (var d in fallbacks)
        {
            d.FollowUpProcessed = true;
            var fb = Json.Deserialize<FallbackDto>(d.FallbackJson)!;
            var triggers = fb.OnStatuses is { Count: > 0 } s ? s : [.. DefaultFallbackStatuses];
            if (!triggers.Contains(d.Status)) continue;
            if (await CreateAsync(d, fb.To, fb.TemplateCode, null, isEscalation: false, ct)) created++;
        }

        var escalations = await db.NotificationDeliveries
            .Where(d => d.EscalationJson != null && d.EscalateAt != null && d.EscalateAt <= now)
            .OrderBy(d => d.EscalateAt).Take(batch).ToListAsync(ct);
        foreach (var d in escalations)
        {
            d.EscalateAt = null;
            var esc = Json.Deserialize<EscalationDto>(d.EscalationJson)!;
            if (DeliveryStatusRules.Rank(d.Status) >= DeliveryStatusRules.Rank(esc.UnlessStatus) && !DeliveryStatusRules.IsFailure(d.Status)
                && d.Status is not DeliveryStatus.Suppressed and not DeliveryStatus.Cancelled)
                continue; // reached the required status in time
            if (d.Status == DeliveryStatus.Cancelled) continue;
            if (await CreateAsync(d, esc.Channel, esc.TemplateCode, esc.Recipient, isEscalation: true, ct)) created++;
        }
        if (fallbacks.Count + escalations.Count > 0) await db.SaveChangesAsync(ct);
        return created;
    }

    private async Task<bool> CreateAsync(NotificationDelivery original, Channel channel, string? templateCode, RecipientDto? overrideRecipient, bool isEscalation, CancellationToken ct)
    {
        var request = await db.NotificationRequests.FirstAsync(r => r.Id == original.RequestId, ct);
        if (request.Status is RequestStatus.Cancelled) return false;
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == request.TenantId, ct);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == request.CompanyId, ct);
        var app = await db.Applications.AsNoTracking().FirstAsync(a => a.Id == request.ApplicationId, ct);
        if (!ChannelSupport.Allowed(app, channel))
        {
            Note(original, $"{(isEscalation ? "Escalation" : "Fallback")} to {ChannelNames.ToWire(channel)} skipped: channel not permitted for the application.");
            return false;
        }

        NotificationRecipient recipientRow;
        ResolvedRecipient rr;
        if (overrideRecipient is not null)
        {
            var list = await recipients.ResolveAsync(request.CompanyId, [overrideRecipient], null, company.Country, ct);
            rr = list[0];
            recipientRow = new NotificationRecipient
            {
                RequestId = request.Id, ContactId = rr.Contact?.Id, UserId = rr.UserId, CustomerId = rr.CustomerId, Name = rr.Name,
                Email = rr.Email, Mobile = rr.Mobile, WhatsApp = rr.WhatsApp, TimeZone = rr.TimeZone, Locale = rr.Locale,
            };
            db.NotificationRecipients.Add(recipientRow);
        }
        else
        {
            recipientRow = await db.NotificationRecipients.AsNoTracking().FirstAsync(r => r.Id == original.RecipientId, ct);
            var contact = recipientRow.ContactId is { } cid ? await db.RecipientContacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
            rr = new ResolvedRecipient
            {
                Contact = contact, UserId = recipientRow.UserId, CustomerId = recipientRow.CustomerId, Name = recipientRow.Name, Email = recipientRow.Email,
                Mobile = recipientRow.Mobile, WhatsApp = recipientRow.WhatsApp, TimeZone = recipientRow.TimeZone, Locale = recipientRow.Locale,
            };
        }

        string? address = channel switch
        {
            Channel.Email => Addressing.IsValidEmail(rr.Email) ? rr.Email!.ToLowerInvariant() : null,
            Channel.Sms => rr.Mobile,
            Channel.WhatsApp => rr.WhatsApp,
            Channel.InApp => rr.UserId,
            _ => null, // push/webpush/webhook follow-ups are not supported (device fan-out is a primary-send concern)
        };
        if (address is null)
        {
            Note(original, $"{(isEscalation ? "Escalation" : "Fallback")} to {ChannelNames.ToWire(channel)} skipped: recipient has no address for that channel.");
            return false;
        }

        var code = templateCode ?? request.TemplateCode!;
        var resolved = await templates.ResolveAsync(request.TenantId, request.CompanyId, request.ApplicationId, code, channel, rr.Locale, app.DefaultLocale, ct);
        if (resolved is null)
        {
            Note(original, $"{(isEscalation ? "Escalation" : "Fallback")} skipped: no active '{code}' template for {ChannelNames.ToWire(channel)}.");
            return false;
        }
        var (tpl, version) = resolved.Value;
        var varsJson = request.VariablesCipher is null ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(protector.Unprotect(request.VariablesCipher, Purposes.Variables));
        var rendered = ChannelRenderer.Render(version, channel, RenderVariables.Build(varsJson, tenant, company, rr.Name, null));
        if (rendered.Errors.Count > 0 || rendered.Missing.Count > 0)
        {
            Note(original, $"Follow-up rendering failed: {string.Join("; ", rendered.Errors.Concat(rendered.Missing.Select(m => "missing " + m)))}");
            return false;
        }

        var mandatory = request.NotificationTypeCode is { } t && await db.NotificationTypes.AsNoTracking()
            .AnyAsync(x => x.TenantId == request.TenantId && x.Code == t && x.Mandatory, ct);
        var policy = await recipients.LoadPolicyStateAsync(request.TenantId, request.CompanyId, [rr], ct);
        var decision = CommunicationPolicy.Evaluate(channel, address, request.MessageClass, request.Category, mandatory, tenant.RequireMarketingConsent, policy[rr.Key], DateTime.UtcNow);

        var follow = new NotificationDelivery
        {
            RequestId = request.Id, RecipientId = recipientRow.Id, TenantId = request.TenantId, CompanyId = request.CompanyId, ApplicationId = request.ApplicationId,
            CampaignId = original.CampaignId, Channel = channel, Address = address, TemplateId = tpl.Id, TemplateVersionId = version.Id,
            TemplateVersionNumber = version.VersionNumber, MessageClass = request.MessageClass, Priority = isEscalation && request.Priority < NotificationPriority.High ? NotificationPriority.High : request.Priority,
            Category = request.Category, ContentCipher = protector.Protect(Json.Serialize(rendered.Content), Purposes.MessageContent),
            Status = decision.Allowed ? DeliveryStatus.Queued : DeliveryStatus.Suppressed, StatusReason = decision.Allowed ? null : decision.Reason,
            FailureCode = decision.Allowed ? null : decision.ReasonCode, NextAttemptAt = decision.Allowed ? DateTime.UtcNow : null,
            MaxAttempts = original.MaxAttempts, ExpiresAt = request.ExpiresAt, Units = rendered.Units,
            FallbackOfDeliveryId = isEscalation ? null : original.Id, EscalationOfDeliveryId = isEscalation ? original.Id : null,
            CompletedAt = decision.Allowed ? null : DateTime.UtcNow,
        };
        db.NotificationDeliveries.Add(follow);
        db.DeliveryEvents.Add(new DeliveryEvent
        {
            DeliveryId = follow.Id, TenantId = follow.TenantId, Type = "created", Status = follow.Status, Source = isEscalation ? "escalation" : "fallback", Applied = true,
            Detail = $"{(isEscalation ? "escalation of" : "fallback for")} {ChannelNames.ToWire(original.Channel)} delivery {original.Id} ({original.Status})",
        });
        Note(original, $"{(isEscalation ? "Escalated" : "Fell back")} to {ChannelNames.ToWire(channel)}: delivery {follow.Id}.");
        if (follow.Status == DeliveryStatus.Queued)
            Outbox.Add(db, MessageTypes.DispatchDelivery, new DispatchDeliveryMessage(follow.Id), follow.TenantId, correlationId: request.CorrelationId);
        else
            await status.SetAsync(follow, DeliveryStatus.Suppressed, isEscalation ? "escalation" : "fallback", decision.Reason, ct);
        logger.LogInformation("Created {Kind} delivery {Follow} for {Original}", isEscalation ? "escalation" : "fallback", follow.Id, original.Id);
        return true;
    }

    private void Note(NotificationDelivery d, string detail)
        => db.DeliveryEvents.Add(new DeliveryEvent { DeliveryId = d.Id, TenantId = d.TenantId, Type = "follow_up", Source = "system", Detail = detail, Applied = false });
}
