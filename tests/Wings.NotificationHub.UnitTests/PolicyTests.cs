using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Policies;

namespace Wings.NotificationHub.UnitTests;

public class PolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc);

    private static RecipientPolicyState State(IEnumerable<RecipientConsent>? consents = null, IEnumerable<RecipientPreference>? prefs = null, IEnumerable<Suppression>? sups = null)
        => new() { Consents = consents?.ToList() ?? [], Preferences = prefs?.ToList() ?? [], Suppressions = sups?.ToList() ?? [] };

    private static RecipientConsent Consent(Channel c, ConsentPurpose p, ConsentStatus s, int daysAgo = 1)
        => new() { Channel = c, Purpose = p, Status = s, CapturedAt = Now.AddDays(-daysAgo) };

    private static PolicyDecision Eval(Channel ch, string addr, MessageClass cls, RecipientPolicyState st, string? cat = "Invoice", bool mandatory = false, bool requireMarketing = true)
        => CommunicationPolicy.Evaluate(ch, addr, cls, cat, mandatory, requireMarketing, st, Now);

    [Fact]
    public void Transactional_email_without_preferences_is_allowed()
        => Assert.True(Eval(Channel.Email, "a@b.com", MessageClass.Transactional, State()).Allowed);

    [Fact]
    public void Whatsapp_requires_opt_in_even_for_critical()
    {
        Assert.Equal("whatsapp_no_opt_in", Eval(Channel.WhatsApp, "+919800000001", MessageClass.Critical, State()).ReasonCode);
        Assert.True(Eval(Channel.WhatsApp, "+919800000001", MessageClass.Transactional, State([Consent(Channel.WhatsApp, ConsentPurpose.ChannelOptIn, ConsentStatus.Granted)])).Allowed);
    }

    [Fact]
    public void Latest_consent_record_wins()
    {
        var st = State([Consent(Channel.WhatsApp, ConsentPurpose.ChannelOptIn, ConsentStatus.Granted, 10), Consent(Channel.WhatsApp, ConsentPurpose.ChannelOptIn, ConsentStatus.Withdrawn, 1)]);
        Assert.False(Eval(Channel.WhatsApp, "+919800000001", MessageClass.Transactional, st).Allowed);
    }

    [Fact]
    public void Promotional_requires_marketing_consent_when_tenant_requires_it()
    {
        Assert.Equal("marketing_consent_missing", Eval(Channel.Email, "a@b.com", MessageClass.Promotional, State()).ReasonCode);
        Assert.True(Eval(Channel.Email, "a@b.com", MessageClass.Promotional, State(), requireMarketing: false).Allowed);
        Assert.True(Eval(Channel.Email, "a@b.com", MessageClass.Promotional, State([Consent(Channel.Email, ConsentPurpose.Marketing, ConsentStatus.Granted)])).Allowed);
        Assert.Equal("marketing_consent_withdrawn", Eval(Channel.Email, "a@b.com", MessageClass.Promotional, State([Consent(Channel.Email, ConsentPurpose.Marketing, ConsentStatus.Withdrawn)]), requireMarketing: false).ReasonCode);
    }

    [Fact]
    public void Category_preference_beats_wildcard_preference()
    {
        var prefs = new[]
        {
            new RecipientPreference { Channel = Channel.Sms, Category = "*", Enabled = false },
            new RecipientPreference { Channel = Channel.Sms, Category = "Invoice", Enabled = true },
        };
        Assert.True(Eval(Channel.Sms, "+919800000001", MessageClass.Transactional, State(prefs: prefs)).Allowed);
        Assert.False(Eval(Channel.Sms, "+919800000001", MessageClass.Transactional, State(prefs: prefs), cat: "Payment").Allowed);
    }

    [Fact]
    public void Mandatory_transactional_and_critical_ignore_preferences()
    {
        var prefs = new[] { new RecipientPreference { Channel = Channel.Email, Category = "*", Enabled = false } };
        Assert.False(Eval(Channel.Email, "a@b.com", MessageClass.Transactional, State(prefs: prefs)).Allowed);
        Assert.True(Eval(Channel.Email, "a@b.com", MessageClass.Transactional, State(prefs: prefs), mandatory: true).Allowed);
        Assert.True(Eval(Channel.Email, "a@b.com", MessageClass.Critical, State(prefs: prefs)).Allowed);
    }

    [Theory]
    [InlineData(SuppressionReason.HardBounce, MessageClass.Critical, false)]
    [InlineData(SuppressionReason.InvalidAddress, MessageClass.Transactional, false)]
    [InlineData(SuppressionReason.Complaint, MessageClass.Transactional, false)]
    [InlineData(SuppressionReason.Complaint, MessageClass.Critical, true)]
    [InlineData(SuppressionReason.Unsubscribe, MessageClass.Promotional, false)]
    [InlineData(SuppressionReason.Unsubscribe, MessageClass.Transactional, true)]
    [InlineData(SuppressionReason.OptOut, MessageClass.Transactional, false)]
    public void Suppression_matrix(SuppressionReason reason, MessageClass cls, bool allowed)
    {
        var sup = new Suppression { Channel = Channel.Email, Address = "a@b.com", Reason = reason, PromotionalOnly = reason == SuppressionReason.Unsubscribe };
        var st = State([Consent(Channel.Email, ConsentPurpose.Marketing, ConsentStatus.Granted)], sups: [sup]);
        Assert.Equal(allowed, Eval(Channel.Email, "A@B.com", cls, st).Allowed);
    }

    [Fact]
    public void Expired_suppression_does_not_block()
    {
        var sup = new Suppression { Channel = Channel.Sms, Address = "+919800000001", Reason = SuppressionReason.Manual, ExpiresAt = Now.AddMinutes(-1) };
        Assert.True(Eval(Channel.Sms, "+919800000001", MessageClass.Transactional, State(sups: [sup])).Allowed);
    }

    [Fact]
    public void Quiet_hours_across_midnight_defer_to_window_end()
    {
        var q = new QuietHoursPolicy { Start = "21:00", End = "08:00", AppliesTo = [MessageClass.Promotional] };
        var ist = TimeZones.Resolve("Asia/Kolkata");
        var at2300Ist = new DateTime(2026, 10, 28, 17, 30, 0, DateTimeKind.Utc); // 23:00 IST
        Assert.Equal(new DateTime(2026, 10, 29, 2, 30, 0, DateTimeKind.Utc), q.NextAllowed(at2300Ist, ist)); // 08:00 IST next day
        var at0700Ist = new DateTime(2026, 10, 28, 1, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, 28, 2, 30, 0, DateTimeKind.Utc), q.NextAllowed(at0700Ist, ist));
        var noon = new DateTime(2026, 10, 28, 6, 30, 0, DateTimeKind.Utc);
        Assert.Equal(noon, q.NextAllowed(noon, ist));
        Assert.False(q.AppliesToMessage(MessageClass.Transactional, Channel.Sms, NotificationPriority.Normal));
        Assert.False(q.AppliesToMessage(MessageClass.Promotional, Channel.Sms, NotificationPriority.Critical));
        Assert.False(q.AppliesToMessage(MessageClass.Promotional, Channel.InApp, NotificationPriority.Normal));
    }

    [Fact]
    public void Working_days_skip_weekends_and_holidays()
    {
        var utc = TimeZoneInfo.Utc;
        var saturday = new DateTime(2026, 10, 31, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc), WorkingDays.NextWorkingInstant(saturday, utc, new HashSet<DateOnly>()));
        Assert.Equal(new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc), WorkingDays.NextWorkingInstant(saturday, utc, new HashSet<DateOnly> { new(2026, 11, 2) }));
        var tuesday = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(tuesday, WorkingDays.NextWorkingInstant(tuesday, utc, new HashSet<DateOnly>()));
    }

    [Fact]
    public void Retry_backoff_grows_with_jitter_and_respects_cap()
    {
        var p = new RetryPolicy { InitialDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(60) };
        var rnd = new Random(1);
        var d2 = p.DelayFor(2, rnd);
        var d4 = p.DelayFor(4, rnd);
        var d10 = p.DelayFor(10, rnd);
        Assert.InRange(d2.TotalSeconds, 5, 10);
        Assert.InRange(d4.TotalSeconds, 20, 40);
        Assert.InRange(d10.TotalSeconds, 30, 60);
        Assert.Equal(8, RetryPolicy.ForPriority(NotificationPriority.Critical).MaxAttempts);
        Assert.Equal(20, RetryPolicy.From(new RetryPolicyDto { MaxAttempts = 99 }, NotificationPriority.Normal).MaxAttempts);
    }
}

public class StatusRuleTests
{
    [Theory]
    [InlineData(DeliveryStatus.ProviderAccepted, DeliveryStatus.Delivered, true)]
    [InlineData(DeliveryStatus.Delivered, DeliveryStatus.Sent, false)]      // out-of-order: never regress
    [InlineData(DeliveryStatus.Read, DeliveryStatus.Delivered, false)]
    [InlineData(DeliveryStatus.Sent, DeliveryStatus.Failed, true)]          // undeliverable after send
    [InlineData(DeliveryStatus.Delivered, DeliveryStatus.Failed, false)]
    [InlineData(DeliveryStatus.Failed, DeliveryStatus.Delivered, true)]     // late proof of delivery
    [InlineData(DeliveryStatus.Cancelled, DeliveryStatus.Delivered, false)]
    [InlineData(DeliveryStatus.Unknown, DeliveryStatus.Delivered, true)]
    [InlineData(DeliveryStatus.Delivered, DeliveryStatus.Delivered, false)]
    public void Provider_status_application(DeliveryStatus current, DeliveryStatus reported, bool expected)
        => Assert.Equal(expected, DeliveryStatusRules.CanApplyProviderStatus(current, reported));

    [Fact]
    public void Aggregate_status()
    {
        Assert.Equal(RequestStatus.Completed, DeliveryStatusRules.Aggregate([DeliveryStatus.Delivered, DeliveryStatus.ProviderAccepted, DeliveryStatus.Suppressed], false));
        Assert.Equal(RequestStatus.PartiallyFailed, DeliveryStatusRules.Aggregate([DeliveryStatus.Delivered, DeliveryStatus.Failed], false));
        Assert.Equal(RequestStatus.Failed, DeliveryStatusRules.Aggregate([DeliveryStatus.Failed, DeliveryStatus.Expired], false));
        Assert.Equal(RequestStatus.Processing, DeliveryStatusRules.Aggregate([DeliveryStatus.Delivered, DeliveryStatus.Queued], false));
        Assert.Equal(RequestStatus.Scheduled, DeliveryStatusRules.Aggregate([DeliveryStatus.Scheduled, DeliveryStatus.Scheduled], true));
        Assert.Equal(RequestStatus.Suppressed, DeliveryStatusRules.Aggregate([DeliveryStatus.Suppressed], false));
        Assert.Equal(RequestStatus.Processing, DeliveryStatusRules.Aggregate([DeliveryStatus.Unknown], false));
    }
}

public class AddressingTests
{
    [Theory]
    [InlineData("9876543210", "+919876543210")]
    [InlineData("+91 98765-43210", "+919876543210")]
    [InlineData("09876543210", "+919876543210")]
    [InlineData("0044 20 7946 0958", "+442079460958")]
    [InlineData("12", null)]
    [InlineData("abc", null)]
    public void Phone_normalization(string input, string? expected) => Assert.Equal(expected, Addressing.NormalizePhone(input));

    [Theory]
    [InlineData("customer@example.com", true)]
    [InlineData("bad@", false)]
    [InlineData("a@b", false)]
    [InlineData("\"x\" <a@b.com>", false)]
    public void Email_validation(string input, bool ok) => Assert.Equal(ok, Addressing.IsValidEmail(input));

    [Fact]
    public void Masking()
    {
        Assert.Equal("c*****r@example.com", Addressing.MaskEmail("customer@example.com"));
        Assert.Equal("+91******3210", Addressing.MaskPhone("+919876543210"));
        Assert.Equal("u-1001", Addressing.Mask(Channel.InApp, "u-1001"));
    }

    [Theory]
    [InlineData("Hello", "GSM-7", 1)]
    [InlineData("€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€", "GSM-7", 2)] // 81 € = 162 septets
    [InlineData("नमस्ते", "UCS-2", 1)]
    public void Sms_segments(string text, string encoding, int segments)
    {
        var info = SmsSegmenter.Analyze(text);
        Assert.Equal(encoding, info.Encoding);
        Assert.Equal(segments, info.Segments);
    }

    [Fact]
    public void Long_unicode_sms_uses_67_char_segments() => Assert.Equal(2, SmsSegmenter.Analyze(new string('न', 71)).Segments);
}
