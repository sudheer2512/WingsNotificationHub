using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;

namespace Wings.NotificationHub.Core.Policies;

public sealed record PolicyDecision(bool Allowed, string? ReasonCode = null, string? Reason = null)
{
    public static readonly PolicyDecision Allow = new(true);
    public static PolicyDecision Deny(string code, string reason) => new(false, code, reason);
}

/// <summary>Inputs for <see cref="CommunicationPolicy.Evaluate"/>; loaded once per recipient.</summary>
public sealed class RecipientPolicyState
{
    public IReadOnlyList<RecipientPreference> Preferences { get; init; } = [];
    /// <summary>Latest consent row per (channel, purpose).</summary>
    public IReadOnlyList<RecipientConsent> Consents { get; init; } = [];
    public IReadOnlyList<Suppression> Suppressions { get; init; } = [];
}

/// <summary>
/// Consent, preference and suppression policy. Pure function so it can be unit tested and simulated.
/// <list type="bullet">
/// <item>Hard suppressions (bounce, invalid address) block every class, including Critical: the message cannot be delivered.</item>
/// <item>Complaints and opt-outs block Transactional and Promotional; Critical (security/regulatory) still goes out.</item>
/// <item>Unsubscribe suppressions block Promotional only.</item>
/// <item>WhatsApp business-initiated messages always need a channel opt-in (Meta policy), including Critical.</item>
/// <item>Promotional messages need marketing consent when the tenant requires it, and honour preferences.</item>
/// <item>Transactional messages honour preferences unless the notification type is mandatory. Critical ignores preferences.</item>
/// </list>
/// </summary>
public static class CommunicationPolicy
{
    public static PolicyDecision Evaluate(
        Channel channel,
        string address,
        MessageClass messageClass,
        string? category,
        bool mandatoryType,
        bool requireMarketingConsent,
        RecipientPolicyState state,
        DateTime nowUtc)
    {
        var normalized = Addressing.Normalize(channel, address);
        foreach (var s in state.Suppressions)
        {
            if (s.Channel != channel || !string.Equals(s.Address, normalized, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.ExpiresAt is { } exp && exp <= nowUtc) continue;
            var blocks = s.Reason switch
            {
                SuppressionReason.HardBounce or SuppressionReason.InvalidAddress => true,
                SuppressionReason.Unsubscribe => messageClass == MessageClass.Promotional,
                _ when s.PromotionalOnly => messageClass == MessageClass.Promotional,
                _ => messageClass != MessageClass.Critical,
            };
            if (blocks) return PolicyDecision.Deny($"suppressed_{s.Reason.ToString().ToLowerInvariant()}", $"Address is on the suppression list ({s.Reason}).");
        }

        if (channel == Channel.WhatsApp)
        {
            var optIn = Latest(state, Channel.WhatsApp, ConsentPurpose.ChannelOptIn);
            if (optIn is null || optIn.Status != ConsentStatus.Granted)
                return PolicyDecision.Deny("whatsapp_no_opt_in", optIn is null ? "No WhatsApp opt-in recorded for the recipient." : "Recipient withdrew WhatsApp opt-in.");
        }

        if (messageClass == MessageClass.Critical) return PolicyDecision.Allow;

        if (messageClass == MessageClass.Promotional)
        {
            var marketing = Latest(state, channel, ConsentPurpose.Marketing);
            if (marketing?.Status == ConsentStatus.Withdrawn)
                return PolicyDecision.Deny("marketing_consent_withdrawn", "Recipient withdrew marketing consent for this channel.");
            if (requireMarketingConsent && marketing is null)
                return PolicyDecision.Deny("marketing_consent_missing", "No marketing consent recorded for this channel.");
        }

        if (messageClass == MessageClass.Transactional && mandatoryType) return PolicyDecision.Allow;

        var pref = ResolvePreference(state.Preferences, channel, category);
        if (pref is false)
            return PolicyDecision.Deny("preference_opt_out", $"Recipient disabled {ChannelNames.ToWire(channel)} for {(string.IsNullOrEmpty(category) ? "this category" : category)}.");
        return PolicyDecision.Allow;
    }

    /// <summary>Most specific preference wins: exact category, then "*". Null = no preference (allowed).</summary>
    public static bool? ResolvePreference(IReadOnlyList<RecipientPreference> prefs, Channel channel, string? category)
    {
        RecipientPreference? exact = null, wildcard = null;
        foreach (var p in prefs)
        {
            if (p.Channel != channel) continue;
            if (!string.IsNullOrEmpty(category) && string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase)) exact = p;
            else if (p.Category == "*") wildcard = p;
        }
        return (exact ?? wildcard)?.Enabled;
    }

    private static RecipientConsent? Latest(RecipientPolicyState state, Channel channel, ConsentPurpose purpose)
        => state.Consents.Where(c => c.Channel == channel && c.Purpose == purpose)
            .OrderByDescending(c => c.CapturedAt).ThenByDescending(c => c.RecordedAt).FirstOrDefault();
}

/// <summary>Quiet hours in the recipient's local time. Messages falling inside the window are deferred to its end.</summary>
public sealed class QuietHoursPolicy
{
    /// <summary>Local start time, "HH:mm".</summary>
    public string Start { get; set; } = "21:00";
    /// <summary>Local end time, "HH:mm". May be earlier than Start (window crosses midnight).</summary>
    public string End { get; set; } = "08:00";
    public List<MessageClass> AppliesTo { get; set; } = [MessageClass.Promotional];
    /// <summary>Channels the window applies to; empty = all channels except in-app.</summary>
    public List<Channel> Channels { get; set; } = [];
    public bool Enabled { get; set; } = true;

    public bool AppliesToMessage(MessageClass cls, Channel channel, NotificationPriority priority)
        => Enabled && priority != NotificationPriority.Critical && cls != MessageClass.Critical && AppliesTo.Contains(cls)
           && (Channels.Count == 0 ? channel != Channel.InApp : Channels.Contains(channel));

    /// <summary>Returns the earliest UTC instant ≥ <paramref name="utc"/> outside the quiet window.</summary>
    public DateTime NextAllowed(DateTime utc, TimeZoneInfo tz)
    {
        if (!TimeOnly.TryParse(Start, out var start) || !TimeOnly.TryParse(End, out var end) || start == end) return utc;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        var t = TimeOnly.FromDateTime(local);
        bool inside;
        DateTime endLocal;
        if (start < end)
        {
            inside = t >= start && t < end;
            endLocal = local.Date + end.ToTimeSpan();
        }
        else
        {
            inside = t >= start || t < end;
            endLocal = t >= start ? local.Date.AddDays(1) + end.ToTimeSpan() : local.Date + end.ToTimeSpan();
        }
        if (!inside) return utc;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified), tz);
    }
}

public static class TimeZones
{
    public static TimeZoneInfo Resolve(params string?[] candidates)
    {
        foreach (var id in candidates)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        }
        return TimeZoneInfo.Utc;
    }
}

/// <summary>Working-day/holiday adjustment for scheduled sends.</summary>
public static class WorkingDays
{
    public static DateTime NextWorkingInstant(DateTime utc, TimeZoneInfo tz, IReadOnlySet<DateOnly> holidays, int maxDays = 60)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        var shifted = false;
        for (var i = 0; i < maxDays; i++)
        {
            var d = DateOnly.FromDateTime(local);
            if (local.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && !holidays.Contains(d)) break;
            local = local.Date.AddDays(1).AddHours(9); // next day, 09:00 local
            shifted = true;
        }
        return shifted ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz) : utc;
    }
}

/// <summary>Exponential backoff with full jitter.</summary>
public sealed class RetryPolicy
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(30);

    public static RetryPolicy ForPriority(NotificationPriority priority) => priority switch
    {
        NotificationPriority.Critical => new() { MaxAttempts = 8, InitialDelay = TimeSpan.FromSeconds(5), MaxDelay = TimeSpan.FromMinutes(5) },
        NotificationPriority.High => new() { MaxAttempts = 6, InitialDelay = TimeSpan.FromSeconds(5), MaxDelay = TimeSpan.FromMinutes(15) },
        NotificationPriority.Low => new() { MaxAttempts = 4, InitialDelay = TimeSpan.FromSeconds(60), MaxDelay = TimeSpan.FromHours(2) },
        _ => new(),
    };

    public static RetryPolicy From(RetryPolicyDto? dto, NotificationPriority priority)
    {
        var p = ForPriority(priority);
        if (dto is null) return p;
        return new RetryPolicy
        {
            MaxAttempts = Math.Clamp(dto.MaxAttempts ?? p.MaxAttempts, 1, 20),
            InitialDelay = dto.InitialDelaySeconds is { } i ? TimeSpan.FromSeconds(Math.Clamp(i, 1, 3600)) : p.InitialDelay,
            MaxDelay = dto.MaxDelaySeconds is { } m ? TimeSpan.FromSeconds(Math.Clamp(m, 1, 86400)) : p.MaxDelay,
        };
    }

    /// <summary>Delay before attempt <paramref name="nextAttempt"/> (2 = first retry). Full jitter in [base/2, base].</summary>
    public TimeSpan DelayFor(int nextAttempt, Random? random = null)
    {
        var exp = Math.Pow(2, Math.Max(0, nextAttempt - 2));
        var baseMs = Math.Min(MaxDelay.TotalMilliseconds, InitialDelay.TotalMilliseconds * exp);
        var r = (random ?? Random.Shared).NextDouble();
        return TimeSpan.FromMilliseconds(baseMs / 2 + r * baseMs / 2);
    }
}
