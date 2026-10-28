using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Policies;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;
using Wings.NotificationHub.Infrastructure.Messaging;
using Wings.NotificationHub.Infrastructure.Providers;
using Wings.NotificationHub.Infrastructure.Services;

namespace Wings.NotificationHub.Infrastructure.Delivery;

/// <summary>
/// Processes one delivery: claims it (idempotent consumer), re-checks policy, selects a provider, submits,
/// records an immutable attempt, and decides retry / failover / reconciliation / operator hold.
/// </summary>
public sealed class DeliveryDispatcher(
    NotificationHubDbContext db,
    ProviderRouter router,
    ProviderRegistry registry,
    CircuitBreakerRegistry circuits,
    IProviderRateLimiter rateLimiter,
    DeliveryStatusUpdater status,
    UsageService usage,
    RecipientResolver recipients,
    IAttachmentStore attachments,
    ISecretProtector protector,
    IOptions<HubOptions> options,
    IOptions<WorkerOptions> workerOptions,
    ILogger<DeliveryDispatcher> logger) : IMessageHandler
{
    public string Type => MessageTypes.DispatchDelivery;

    public Task HandleAsync(MessageEnvelope message, CancellationToken ct) => DispatchAsync(message.Payload<DispatchDeliveryMessage>().DeliveryId, ct);

    public async Task DispatchAsync(Guid deliveryId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var owner = workerOptions.Value.InstanceId;
        var until = now.AddSeconds(options.Value.LeaseSeconds);
        // Claim: only one worker can move Queued/RetryPending → Processing. Duplicate queue messages become no-ops.
        var claimed = await db.NotificationDeliveries
            .Where(d => d.Id == deliveryId && (d.Status == DeliveryStatus.Queued || d.Status == DeliveryStatus.RetryPending)
                        && (d.NextAttemptAt == null || d.NextAttemptAt <= now) && (d.LeaseExpiresAt == null || d.LeaseExpiresAt < now))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Processing)
                .SetProperty(d => d.LeaseOwner, owner).SetProperty(d => d.LeaseExpiresAt, until).SetProperty(d => d.ConcurrencyStamp, Guid.NewGuid()), ct);
        if (claimed == 0) return;

        var d = await db.NotificationDeliveries.FirstAsync(x => x.Id == deliveryId, ct);
        using var activity = Telemetry.Source.StartActivity("delivery.dispatch");
        activity?.SetTag("wnh.delivery", d.Id).SetTag("wnh.channel", d.Channel.ToString());
        try
        {
            await ProcessAsync(d, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Dispatch of delivery {DeliveryId} failed unexpectedly", d.Id);
            db.ChangeTracker.Clear();
            var fresh = await db.NotificationDeliveries.FirstAsync(x => x.Id == deliveryId, CancellationToken.None);
            if (fresh.Status == DeliveryStatus.Processing)
            {
                // Nothing was submitted (Processing precedes Submitted): safe to retry later.
                fresh.Status = DeliveryStatus.RetryPending;
                fresh.NextAttemptAt = DateTime.UtcNow.AddSeconds(30);
                fresh.LeaseOwner = null; fresh.LeaseExpiresAt = null;
                fresh.StatusReason = "internal error: " + Redact.Sanitize(ex.Message, 300);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            throw;
        }
    }

    private async Task ProcessAsync(NotificationDelivery d, DateTime now, CancellationToken ct)
    {
        var request = await db.NotificationRequests.FirstAsync(r => r.Id == d.RequestId, ct);
        if (d.ExpiresAt is { } exp && exp <= now)
        {
            await status.SetAsync(d, DeliveryStatus.Expired, "dispatcher", "Expired before it could be sent.", ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        // Emergency suspension holds messages without consuming attempts.
        var hold = await HoldReasonAsync(d, ct);
        if (hold is not null)
        {
            Release(d, DeliveryStatus.RetryPending, now.AddMinutes(5), $"held: {hold}");
            await db.SaveChangesAsync(ct);
            return;
        }

        // Re-evaluate consent/preferences/suppression: an opt-out after acceptance must still be honoured.
        var recipient = await db.NotificationRecipients.AsNoTracking().FirstAsync(r => r.Id == d.RecipientId, ct);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == d.TenantId, ct);
        if (d.Channel is not Channel.Webhook)
        {
            var decision = await EvaluatePolicyAsync(d, recipient, request, tenant, ct);
            if (!decision.Allowed)
            {
                d.FailureCode = decision.ReasonCode;
                await status.SetAsync(d, DeliveryStatus.Suppressed, "dispatcher", decision.Reason, ct);
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        var content = Json.Deserialize<RenderedContent>(d.ContentCipher is null ? null : protector.Unprotect(d.ContentCipher, Purposes.MessageContent));
        if (content is null)
        {
            await Fail(d, "content_unavailable", "Message content was purged or is unavailable.", ct);
            return;
        }

        if (d.Channel == Channel.WhatsApp && content.WhatsAppTemplate is null)
        {
            var since = now.AddHours(-24);
            var windowOpen = await db.ConversationThreads.AnyAsync(t => t.TenantId == d.TenantId && t.Channel == Channel.WhatsApp
                && t.ExternalAddress == d.Address && t.LastInboundAt != null && t.LastInboundAt >= since, ct);
            if (!windowOpen)
            {
                await Fail(d, "outside_customer_service_window",
                    "Free-form WhatsApp messages are only allowed within 24 hours of the customer's last message; use an approved template.", ct);
                return;
            }
        }

        var app = await db.Applications.AsNoTracking().FirstAsync(a => a.Id == d.ApplicationId, ct);
        var attempts = await db.DeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == d.Id).OrderBy(a => a.AttemptNumber).ToListAsync(ct);
        var candidates = await router.CandidatesAsync(d, app.Sandbox, tenant.AllowSharedProviders, ct);
        var account = Choose(d, candidates, attempts);
        if (account is null)
        {
            var reasons = candidates.Count == 0 ? "no provider account configured for this channel" : string.Join("; ", candidates.Select(c => $"{c.Account.Name}: {c.SkipReason}"));
            await RecordAttemptlessFailureAsync(d, request, "no_provider", $"No usable provider ({reasons}).", ct);
            return;
        }

        if (account.RateLimitPerSecond is { } rps && await rateLimiter.TryAcquireAsync(account.Id, rps, ct) is { } wait)
        {
            // Backpressure: reschedule without consuming an attempt.
            Release(d, DeliveryStatus.RetryPending, now.Add(wait).AddMilliseconds(Random.Shared.Next(50, 250)), "throttled by provider rate limit");
            await db.SaveChangesAsync(ct);
            return;
        }

        INotificationProvider provider;
        try { provider = registry.Get(account); }
        catch (Exception ex) when (ex is ProviderConfigurationException or System.Security.Cryptography.CryptographicException)
        {
            await RecordAttemptlessFailureAsync(d, request, "provider_configuration", Redact.Sanitize(ex.Message, 500)!, ct);
            return;
        }

        var sender = await SenderAsync(d, account, ct);
        var message = new NotificationMessage
        {
            DeliveryId = d.Id, TenantId = d.TenantId, Channel = d.Channel, To = d.Address,
            From = sender?.Address, FromName = sender?.DisplayName, ReplyTo = sender?.ReplyTo,
            DltEntityId = sender?.DltEntityId, WhatsAppPhoneNumberId = sender?.WhatsAppPhoneNumberId,
            DltTemplateId = d.TemplateVersionId is { } tv ? await db.TemplateVersions.Where(v => v.Id == tv).Select(v => v.DltTemplateId).FirstOrDefaultAsync(ct) : null,
            MessageClass = d.MessageClass, Priority = d.Priority, Content = WithSignature(content, sender, d.Channel),
            Attachments = d.Channel == Channel.Email ? await attachments.LoadAsync(d.TenantId, content.Attachments, ct) : [],
            IdempotencyKey = $"wnh-{d.Id:N}", ExpiresAt = d.ExpiresAt,
            Push = d.DeviceId is { } deviceId ? await PushTargetAsync(deviceId, ct) : null,
            CallbackUrl = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/api/v1/callbacks/{account.Id}",
            Headers = content.UnsubscribeUrl is { } u ? new Dictionary<string, string> { ["List-Unsubscribe"] = $"<{u}>", ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click" } : new Dictionary<string, string>(),
        };
        if (d.DeviceId is not null && message.Push is null)
        {
            await Fail(d, "device_invalid", "The target device registration is no longer valid.", ct);
            return;
        }

        // Record the attempt and the Submitted state before calling out: a crash mid-call is then detectable as ambiguous.
        var attempt = new DeliveryAttempt
        {
            DeliveryId = d.Id, TenantId = d.TenantId, AttemptNumber = d.AttemptCount + 1, ProviderAccountId = account.Id,
            ProviderType = account.ProviderType, WorkerId = workerOptions.Value.InstanceId, IdempotencyToken = message.IdempotencyKey,
        };
        db.DeliveryAttempts.Add(attempt);
        d.AttemptCount++;
        d.ProviderAccountId = account.Id;
        d.ProviderType = account.ProviderType;
        d.SenderIdentityId = sender?.Id;
        await status.SetAsync(d, DeliveryStatus.Submitted, "dispatcher", $"attempt {attempt.AttemptNumber} via {account.Name}", ct);
        await db.SaveChangesAsync(ct);

        var sw = Stopwatch.StartNew();
        SendResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(account.TimeoutSeconds, 1, 300)));
            try
            {
                result = await provider.SendAsync(message, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = SendResult.Ambiguous("timeout", $"No response from provider within {account.TimeoutSeconds}s; acceptance is unknown.");
            }
            catch (ProviderConfigurationException ex)
            {
                result = SendResult.Transient("provider_configuration", ex.Message);
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.NetworkUnreachable })
            {
                result = SendResult.Transient("connection_failed", ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = SendResult.Ambiguous("transport_error", Redact.Sanitize(ex.GetType().Name + ": " + ex.Message, 500)!);
            }
        }
        sw.Stop();
        Telemetry.ProviderLatency.Record(sw.Elapsed.TotalMilliseconds, new("provider", account.ProviderType), new("channel", d.Channel.ToString()));
        Telemetry.DeliveryOutcomes.Add(1, new("provider", account.ProviderType), new("channel", d.Channel.ToString()), new("outcome", result.Outcome.ToString()));

        attempt.CompletedAt = DateTime.UtcNow;
        attempt.DurationMs = (int)sw.ElapsedMilliseconds;
        attempt.Outcome = result.Outcome;
        attempt.ProviderMessageId = result.ProviderMessageId;
        attempt.ResponseCode = result.ResponseCode;
        attempt.ResponseSummary = Redact.Sanitize(result.ResponseSummary);
        attempt.ErrorCode = result.ErrorCode;
        attempt.ErrorMessage = Redact.Sanitize(result.ErrorMessage, 1900);
        attempt.Cost = result.ReportedCost;
        if (result.Units > 1) d.Units = result.Units;

        await ApplyOutcomeAsync(d, request, account, provider, result, ct);
        d.LeaseOwner = null;
        d.LeaseExpiresAt = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyOutcomeAsync(NotificationDelivery d, NotificationRequest request, ProviderAccount account, INotificationProvider provider, SendResult result, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        switch (result.Outcome)
        {
            case AttemptOutcome.Accepted:
                circuits.RecordSuccess(account.Id);
                d.ProviderMessageId = result.ProviderMessageId;
                d.PinnedProviderAccountId = null;
                d.AwaitingOperator = false;
                d.FailureCode = null; d.FailureReason = null;
                await status.SetAsync(d, result.AcceptedStatus, "provider", Redact.Sanitize(result.ResponseSummary, 300), ct);
                // Providers that can be queried get a reconciliation check if no final receipt arrives in time.
                if (provider.Capabilities.SupportsStatusQuery && result.AcceptedStatus is DeliveryStatus.ProviderAccepted or DeliveryStatus.Sent)
                    d.NextAttemptAt = now.AddMinutes(options.Value.ReconcileAfterMinutes);
                await usage.RecordAcceptedAsync(d, result.ReportedCost, ct);
                await status.ApplyParkedCallbacksAsync(d, ct);
                break;

            case AttemptOutcome.PermanentFailure:
                if (result.InvalidAddress)
                {
                    if (d.DeviceId is { } dev) await status.InvalidateDeviceAsync(dev, result.ErrorCode ?? "invalid token", ct);
                    else if (d.Channel is Channel.Email or Channel.Sms or Channel.WhatsApp)
                        await status.SuppressAsync(d.TenantId, d.CompanyId, d.Channel, d.Address, SuppressionReason.InvalidAddress, $"provider:{account.ProviderType}", ct);
                }
                await Fail(d, result.ErrorCode ?? "rejected", result.ErrorMessage ?? "Provider rejected the message.", ct, save: false);
                break;

            case AttemptOutcome.Throttled:
            case AttemptOutcome.TransientFailure:
                if (result.Outcome == AttemptOutcome.TransientFailure) await RecordCircuitFailureAsync(account, ct);
                await RetryOrFailAsync(d, request, result.ErrorCode ?? "transient", result.ErrorMessage ?? "Temporary provider failure.", result.RetryAfter, ct);
                break;

            case AttemptOutcome.Ambiguous:
                await RecordCircuitFailureAsync(account, ct);
                d.FailureCode = result.ErrorCode;
                d.FailureReason = result.ErrorMessage;
                if (provider.Capabilities.SupportsIdempotency || account.DuplicateRiskPolicy == DuplicateRiskPolicy.SameProviderOnly)
                {
                    // Resend through the same provider only: it deduplicates by our idempotency key (or policy accepts the risk).
                    d.PinnedProviderAccountId = account.Id;
                    await RetryOrFailAsync(d, request, result.ErrorCode ?? "ambiguous", (result.ErrorMessage ?? "") + " Retrying via the same provider.", null, ct, ambiguous: true);
                }
                else if (account.DuplicateRiskPolicy == DuplicateRiskPolicy.AllowDuplicates)
                {
                    await RetryOrFailAsync(d, request, result.ErrorCode ?? "ambiguous", (result.ErrorMessage ?? "") + " Duplicates permitted by policy.", null, ct, ambiguous: true);
                }
                else if (provider is IStatusQueryProvider && provider.Capabilities.SupportsStatusQuery)
                {
                    d.NextAttemptAt = now.AddMinutes(1); // reconciliation will ask the provider whether it accepted the message
                    await status.SetAsync(d, DeliveryStatus.Unknown, "dispatcher", "Acceptance unknown; awaiting reconciliation with the provider.", ct);
                }
                else
                {
                    d.AwaitingOperator = true;
                    d.NextAttemptAt = null;
                    await status.SetAsync(d, DeliveryStatus.Unknown, "dispatcher",
                        "Acceptance unknown and the provider cannot deduplicate; held for an operator decision to avoid a duplicate message.", ct);
                    db.SystemAlerts.Add(new SystemAlert
                    {
                        TenantId = d.TenantId, Severity = "warning", Source = "dispatcher", Key = $"ambiguous:{d.Id}",
                        Message = $"Delivery {d.Id} ({ChannelNames.ToWire(d.Channel)} via {account.Name}) has an unknown outcome and needs an operator decision.",
                    });
                }
                break;
        }
    }

    private async Task RetryOrFailAsync(NotificationDelivery d, NotificationRequest request, string code, string message, TimeSpan? retryAfter, CancellationToken ct, bool ambiguous = false)
    {
        var policy = RetryPolicy.From(Json.Deserialize<RetryPolicyDto>(request.RetryPolicyJson), d.Priority);
        d.FailureCode = code;
        d.FailureReason = message;
        if (d.AttemptCount >= d.MaxAttempts)
        {
            d.DeadLettered = true;
            await Fail(d, code, $"{message} Gave up after {d.AttemptCount} attempts.", ct, save: false, terminalStatus: ambiguous ? DeliveryStatus.Unknown : DeliveryStatus.Failed);
            if (d.Priority == NotificationPriority.Critical)
                db.SystemAlerts.Add(new SystemAlert { TenantId = d.TenantId, Severity = "critical", Source = "dispatcher", Key = $"critical-failed:{d.Id}", Message = $"Critical notification delivery {d.Id} failed after {d.AttemptCount} attempts: {message}" });
            return;
        }
        var delay = policy.DelayFor(d.AttemptCount + 1);
        if (retryAfter is { } ra && ra > delay) delay = ra;
        d.NextAttemptAt = DateTime.UtcNow.Add(delay);
        d.LeaseOwner = null; d.LeaseExpiresAt = null;
        await status.SetAsync(d, DeliveryStatus.RetryPending, "dispatcher", $"{code}: {message} Next attempt in {delay.TotalSeconds:0}s.", ct);
    }

    private async Task Fail(NotificationDelivery d, string code, string reason, CancellationToken ct, bool save = true, DeliveryStatus terminalStatus = DeliveryStatus.Failed)
    {
        d.FailureCode = code;
        d.FailureReason = reason;
        d.LeaseOwner = null; d.LeaseExpiresAt = null; d.NextAttemptAt = null;
        if (terminalStatus == DeliveryStatus.Unknown) d.AwaitingOperator = true;
        await status.SetAsync(d, terminalStatus, "dispatcher", $"{code}: {reason}", ct);
        if (save) await db.SaveChangesAsync(ct);
    }

    /// <summary>No provider could be tried: counts toward max attempts without an outbound call.</summary>
    private async Task RecordAttemptlessFailureAsync(NotificationDelivery d, NotificationRequest request, string code, string message, CancellationToken ct)
    {
        d.AttemptCount++;
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            DeliveryId = d.Id, TenantId = d.TenantId, AttemptNumber = d.AttemptCount, Outcome = AttemptOutcome.TransientFailure,
            ErrorCode = code, ErrorMessage = Redact.Sanitize(message, 1900), CompletedAt = DateTime.UtcNow, WorkerId = workerOptions.Value.InstanceId,
        });
        await RetryOrFailAsync(d, request, code, message, null, ct);
        d.LeaseOwner = null; d.LeaseExpiresAt = null;
        await db.SaveChangesAsync(ct);
    }

    private ProviderAccount? Choose(NotificationDelivery d, List<ProviderRouter.Candidate> candidates, List<DeliveryAttempt> attempts)
    {
        if (d.PinnedProviderAccountId is { } pinned)
            return candidates.FirstOrDefault(c => c.Account.Id == pinned && c.SkipReason is null or "unhealthy")?.Account;
        // Fail over away from the provider that just failed transiently (it definitely did not accept the message).
        var last = attempts.LastOrDefault();
        var ordered = candidates.Where(c => c.SkipReason is null).Select(c => c.Account).ToList();
        if (last is { Outcome: AttemptOutcome.TransientFailure, ProviderAccountId: { } failed } && ordered.Count > 1)
        {
            var failedAccount = ordered.FirstOrDefault(a => a.Id == failed);
            var preferred = failedAccount?.FailoverProviderAccountId is { } fo ? ordered.FirstOrDefault(a => a.Id == fo) : null;
            ordered = ordered.Where(a => a.Id != failed).ToList();
            if (preferred is not null) { ordered.Remove(preferred); ordered.Insert(0, preferred); }
            if (failedAccount is not null) ordered.Add(failedAccount);
        }
        return ordered.FirstOrDefault() ?? candidates.FirstOrDefault(c => c.SkipReason == "unhealthy")?.Account;
    }

    private async Task RecordCircuitFailureAsync(ProviderAccount account, CancellationToken ct)
    {
        if (circuits.RecordFailure(account.Id) == CircuitTransition.Opened)
        {
            db.ProviderHealthEvents.Add(new ProviderHealthEvent { ProviderAccountId = account.Id, Status = ProviderHealthStatus.CircuitOpen, Detail = "Circuit opened after consecutive failures." });
            db.SystemAlerts.Add(new SystemAlert { TenantId = account.TenantId, Severity = "error", Source = "provider", Key = $"circuit:{account.Id}:{DateTime.UtcNow:yyyyMMddHHmm}", Message = $"Circuit breaker opened for provider '{account.Name}'." });
            await db.ProviderAccounts.Where(p => p.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.HealthStatus, ProviderHealthStatus.CircuitOpen), ct);
        }
    }

    private async Task<string?> HoldReasonAsync(NotificationDelivery d, CancellationToken ct)
    {
        var t = await db.Tenants.AsNoTracking().Where(x => x.Id == d.TenantId).Select(x => x.Status).FirstAsync(ct);
        if (t != EntityStatus.Active) return "tenant suspended";
        var c = await db.Companies.AsNoTracking().Where(x => x.Id == d.CompanyId).Select(x => x.Status).FirstAsync(ct);
        if (c != EntityStatus.Active) return "company suspended";
        var a = await db.Applications.AsNoTracking().Where(x => x.Id == d.ApplicationId).Select(x => x.Status).FirstAsync(ct);
        if (a != EntityStatus.Active) return "application suspended";
        if (d.CampaignId is { } cid && await db.Campaigns.AsNoTracking().Where(x => x.Id == cid).Select(x => new { x.Suspended, x.Status }).FirstOrDefaultAsync(ct) is { } camp
            && (camp.Suspended || camp.Status == CampaignStatus.Paused))
            return "campaign paused or suspended";
        return null;
    }

    private async Task<PolicyDecision> EvaluatePolicyAsync(NotificationDelivery d, NotificationRecipient r, NotificationRequest request, Tenant tenant, CancellationToken ct)
    {
        RecipientContact? contact = r.ContactId is { } cid ? await db.RecipientContacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        var resolved = new ResolvedRecipient { Contact = contact, Email = r.Email, Mobile = r.Mobile, WhatsApp = r.WhatsApp, UserId = r.UserId };
        var state = await recipients.LoadPolicyStateAsync(d.TenantId, d.CompanyId, [resolved], ct);
        var mandatory = request.NotificationTypeCode is { } code && await db.NotificationTypes.AsNoTracking()
            .AnyAsync(t => t.TenantId == d.TenantId && t.Code == code && t.Mandatory && (t.ApplicationId == null || t.ApplicationId == d.ApplicationId), ct);
        var address = d.Channel is Channel.Push or Channel.WebPush ? r.UserId ?? d.Address : d.Address;
        return CommunicationPolicy.Evaluate(d.Channel, address, d.MessageClass, d.Category, mandatory, tenant.RequireMarketingConsent, state[resolved.Key], DateTime.UtcNow);
    }

    private async Task<SenderIdentity?> SenderAsync(NotificationDelivery d, ProviderAccount account, CancellationToken ct)
    {
        var senders = await db.SenderIdentities.AsNoTracking().Where(s => s.ProviderAccountId == account.Id && !s.Suspended && s.Channel == d.Channel
            && (s.TenantId == null || s.TenantId == d.TenantId) && (s.CompanyId == null || s.CompanyId == d.CompanyId)).ToListAsync(ct);
        var link = await db.CompanyApplications.AsNoTracking().FirstOrDefaultAsync(l => l.CompanyId == d.CompanyId && l.ApplicationId == d.ApplicationId, ct);
        if (Json.Deserialize<Dictionary<string, Guid>>(link?.DefaultSendersJson) is { } defaults
            && defaults.TryGetValue(ChannelNames.ToWire(d.Channel), out var preferred) && senders.FirstOrDefault(s => s.Id == preferred) is { } p)
            return p;
        return senders.OrderByDescending(s => s.CompanyId != null).ThenByDescending(s => s.TenantId != null).ThenByDescending(s => s.IsDefault).FirstOrDefault();
    }

    private static RenderedContent WithSignature(RenderedContent content, SenderIdentity? sender, Channel channel)
    {
        if (channel != Channel.Email || string.IsNullOrEmpty(sender?.Signature) || content.Format != BodyFormat.Html) return content;
        content.Body += "<div class=\"wnh-signature\">" + sender.Signature + "</div>";
        return content;
    }

    private async Task<PushTarget?> PushTargetAsync(Guid deviceId, CancellationToken ct)
    {
        var dev = await db.DeviceRegistrations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId && x.InvalidatedAt == null, ct);
        return dev is null ? null : new PushTarget { DeviceId = dev.Id, Platform = dev.Platform, Token = dev.Token, P256dh = dev.P256dh, Auth = dev.Auth };
    }

    private static void Release(NotificationDelivery d, DeliveryStatus st, DateTime next, string reason)
    {
        d.Status = st;
        d.NextAttemptAt = next;
        d.StatusReason = reason;
        d.LeaseOwner = null;
        d.LeaseExpiresAt = null;
    }
}
