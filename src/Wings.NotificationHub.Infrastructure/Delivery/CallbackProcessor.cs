using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Messaging;
using Wings.NotificationHub.Infrastructure.Providers;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Infrastructure.Delivery;

public sealed record CallbackOutcome(int StatusCode, string? Body, int Accepted, int Duplicates);

/// <summary>
/// Authenticates and applies provider callbacks: delivery/read receipts, bounces/complaints and inbound messages.
/// Idempotent per provider event id (replays are acknowledged without effect); out-of-order receipts never regress state.
/// </summary>
public sealed class CallbackProcessor(
    NotificationHubDbContext db,
    IEnumerable<ICallbackHandler> handlers,
    ProviderRegistry registry,
    DeliveryStatusUpdater status,
    InboundService inbound,
    ILogger<CallbackProcessor> logger)
{
    public async Task<CallbackOutcome> ProcessHttpAsync(Guid accountId, CallbackRequest request, CancellationToken ct)
    {
        var account = await db.ProviderAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null) return new(404, null, 0, 0);
        var handler = handlers.FirstOrDefault(h => h.ProviderType.Equals(account.ProviderType, StringComparison.OrdinalIgnoreCase));
        if (handler is null) return new(404, null, 0, 0);
        var parsed = await handler.ParseAsync(request, registry.ConfigFor(account), ct);
        Telemetry.Callbacks.Add(1, new("provider", account.ProviderType), new("authentic", parsed.Authentic));
        if (!parsed.Authentic)
        {
            logger.LogWarning("Rejected callback for provider account {Account}: {Reason}", account.Id, parsed.RejectReason);
            return new(401, null, 0, 0);
        }
        if (parsed.HandshakeResponse is not null && parsed.Events.Count == 0) return new(200, parsed.HandshakeResponse, 0, 0);
        var (accepted, dupes) = await ApplyAsync(account, parsed.Events, "provider_callback", ct);
        return new(200, parsed.HandshakeResponse, accepted, dupes);
    }

    /// <summary>Applies already-authenticated events (HTTP callbacks, mock provider receipts, reconciliation).</summary>
    public async Task<(int Accepted, int Duplicates)> ApplyAsync(ProviderAccount account, IReadOnlyList<ProviderCallbackEvent> events, string source, CancellationToken ct)
    {
        int accepted = 0, duplicates = 0;
        foreach (var e in events)
        {
            var key = e.EventId ?? Addressing.Sha256Hex($"{e.ProviderMessageId}|{e.Status}|{e.Interaction}|{e.OccurredAt:O}|{e.Inbound?.ProviderMessageId}");
            if (!await InboxDedup.TryBeginAsync(db, $"callback:{account.Id:N}", key, ct)) { duplicates++; continue; }
            accepted++;

            if (e.Inbound is not null)
            {
                await inbound.ReceiveAsync(account, e, ct);
                continue;
            }

            NotificationDelivery? d = null;
            if (e.ProviderMessageId is not null)
                d = db.NotificationDeliveries.Local.FirstOrDefault(x => x.ProviderAccountId == account.Id && x.ProviderMessageId == e.ProviderMessageId)
                    ?? await db.NotificationDeliveries.FirstOrDefaultAsync(x => x.ProviderAccountId == account.Id && x.ProviderMessageId == e.ProviderMessageId, ct);

            if (e.Suppress is { } reason)
            {
                var address = e.SuppressAddress ?? d?.Address;
                var tenantId = d?.TenantId ?? account.TenantId;
                if (address is not null && tenantId is { } t)
                    await status.SuppressAsync(t, d?.CompanyId ?? account.CompanyId, account.Channel, address, reason, $"{account.ProviderType}:{source}", ct);
            }

            if (d is null)
            {
                // Receipt arrived before the dispatcher recorded the provider message id: park it for later application.
                db.DeliveryEvents.Add(new DeliveryEvent
                {
                    ProviderAccountId = account.Id, ProviderMessageId = e.ProviderMessageId, ProviderEventId = e.EventId, TenantId = account.TenantId,
                    Type = "provider_status", Status = e.Status, Source = source, Detail = e.ErrorCode ?? e.Interaction, OccurredAt = e.OccurredAt,
                });
                continue;
            }
            if (e.Status is null && e.Interaction is null && e.Cost is null) continue;
            await status.ApplyProviderStatusAsync(d, e, source, ct);
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent duplicate callback won the dedup insert; the effects are already recorded.
            logger.LogInformation(ex, "Duplicate callback batch for provider account {Account} ignored", account.Id);
            db.ChangeTracker.Clear();
            return (0, events.Count);
        }
        return (accepted, duplicates);
    }
}

/// <summary>Receives inbound messages: resolves tenant/thread/contact, correlates replies and routes them to applications.</summary>
public sealed class InboundService(
    NotificationHubDbContext db,
    DeliveryStatusUpdater status,
    IAttachmentStore attachments,
    ILogger<InboundService> logger)
{
    public async Task<InboundMessage?> ReceiveAsync(ProviderAccount account, ProviderCallbackEvent e, CancellationToken ct)
    {
        var data = e.Inbound!;
        var channel = account.Channel;
        var from = channel == Channel.Email ? data.From.Trim().ToLowerInvariant() : Addressing.NormalizePhone(data.From) ?? data.From;

        // Correlate: explicit reply context first, then the latest outbound message to this address.
        NotificationDelivery? correlated = null;
        if (data.InReplyToProviderMessageId is { } ctx)
            correlated = await db.NotificationDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.ProviderMessageId == ctx && d.ProviderAccountId == account.Id, ct);
        if (correlated is null)
        {
            var q = db.NotificationDeliveries.AsNoTracking().Where(d => d.Channel == channel && d.Address == from && d.ProviderAccountId == account.Id);
            if (account.TenantId is { } at) q = q.Where(d => d.TenantId == at);
            correlated = await q.OrderByDescending(d => d.CreatedAt).FirstOrDefaultAsync(ct);
        }
        var tenantId = correlated?.TenantId ?? account.TenantId;
        if (tenantId is null)
        {
            // Shared provider and no prior conversation: the sender cannot be attributed to a tenant safely.
            db.SystemAlerts.Add(new SystemAlert { Severity = "warning", Source = "inbound", Message = $"Unattributable inbound {ChannelNames.ToWire(channel)} message on shared provider '{account.Name}' from {Addressing.Mask(channel, from)}." });
            logger.LogWarning("Inbound message on shared account {Account} could not be attributed to a tenant", account.Id);
            return null;
        }
        var companyId = correlated?.CompanyId ?? account.CompanyId;
        var applicationId = correlated?.ApplicationId;

        var contact = companyId is { } cid
            ? await db.RecipientContacts.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == cid && c.ErasedAt == null
                && (c.Mobile == from || c.WhatsApp == from || c.Email == from), ct)
            : null;

        var thread = await db.ConversationThreads.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Channel == channel && t.ExternalAddress == from
            && (t.ProviderAccountId == account.Id) && !t.Closed, ct);
        if (thread is null)
        {
            thread = new ConversationThread
            {
                TenantId = tenantId.Value, CompanyId = companyId, ApplicationId = applicationId, Channel = channel, ProviderAccountId = account.Id,
                ExternalAddress = from, SenderAddress = data.To, ContactId = contact?.Id, ContactName = contact?.Name ?? data.FromName,
            };
            db.ConversationThreads.Add(thread);
        }
        thread.CompanyId ??= companyId;
        thread.ApplicationId ??= applicationId;
        thread.ContactId ??= contact?.Id;
        thread.ContactName ??= data.FromName;
        thread.LastInboundAt = e.OccurredAt;
        thread.LastMessageAt = DateTime.UtcNow;
        thread.UnreadCount++;

        var msg = new InboundMessage
        {
            TenantId = tenantId.Value, CompanyId = companyId, ApplicationId = applicationId, Channel = channel, ProviderAccountId = account.Id,
            ThreadId = thread.Id, From = from, To = data.To, Subject = data.Subject, Body = data.Body, MessageType = data.MessageType,
            ProviderMessageId = data.ProviderMessageId, InReplyToProviderMessageId = data.InReplyToProviderMessageId,
            CorrelatedDeliveryId = correlated?.Id, CorrelatedRequestId = correlated?.RequestId, ContactId = contact?.Id, ReceivedAt = e.OccurredAt,
        };
        db.InboundMessages.Add(msg);

        foreach (var media in data.Media)
        {
            if (media.Content is { Length: > 0 } bytes)
            {
                try { await attachments.SaveAsync(tenantId.Value, companyId, "inbound", media.FileName ?? "attachment", media.ContentType, bytes, $"provider:{account.ProviderType}", msg.Id, ct); }
                catch (Core.Security.HubException ex) { msg.RoutingDetail = $"attachment rejected: {ex.Message}"; }
            }
            else msg.RoutingDetail = $"media {media.ProviderMediaId} ({media.ContentType}) available from provider";
        }

        // STOP / opt-out replies: withdraw consent and suppress further non-critical messages.
        if (e.Suppress is SuppressionReason.OptOut)
        {
            await status.SuppressAsync(tenantId.Value, companyId, channel, from, SuppressionReason.OptOut, $"inbound:{account.ProviderType}", ct);
            if (contact is not null)
                foreach (var purpose in new[] { ConsentPurpose.ChannelOptIn, ConsentPurpose.Marketing })
                    db.RecipientConsents.Add(new RecipientConsent
                    {
                        TenantId = tenantId.Value, CompanyId = contact.CompanyId, ContactId = contact.Id, Channel = channel, Purpose = purpose,
                        Status = ConsentStatus.Withdrawn, Source = "inbound-reply", Evidence = $"{account.ProviderType} message {data.ProviderMessageId}: \"{data.Body}\"",
                        RecordedBy = "system", CapturedAt = e.OccurredAt,
                    });
        }

        Outbox.Add(db, MessageTypes.InboundReceived, new InboundReceivedMessage(msg.Id), tenantId);
        return msg;
    }
}
