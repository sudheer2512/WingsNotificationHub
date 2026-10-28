using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.Core;

public static partial class Addressing
{
    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164();

    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254) return false;
        try
        {
            var a = new MailAddress(email.Trim());
            return a.Address.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) && a.Host.Contains('.');
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Normalizes to E.164. Bare 10-digit Indian mobile numbers get +91 when <paramref name="defaultCountry"/> is IN.</summary>
    public static string? NormalizePhone(string? phone, string? defaultCountry = "IN")
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new StringBuilder();
        var s = phone.Trim();
        foreach (var c in s) if (char.IsDigit(c)) digits.Append(c);
        if (digits.Length == 0) return null;
        string candidate;
        if (s.StartsWith('+')) candidate = "+" + digits;
        else if (s.StartsWith("00")) candidate = "+" + digits.ToString()[2..];
        else if (digits.Length == 10 && (defaultCountry ?? "IN") == "IN") candidate = "+91" + digits;
        else if (digits.Length == 11 && digits[0] == '0' && (defaultCountry ?? "IN") == "IN") candidate = "+91" + digits.ToString()[1..];
        else candidate = "+" + digits;
        return E164().IsMatch(candidate) ? candidate : null;
    }

    public static bool IsValidPhone(string? phone) => phone is not null && E164().IsMatch(phone);

    public static string Normalize(Channel channel, string address) => channel switch
    {
        Channel.Email => address.Trim().ToLowerInvariant(),
        Channel.Sms or Channel.WhatsApp => NormalizePhone(address) ?? address.Trim(),
        _ => address.Trim(),
    };

    /// <summary>Masks an address for logs and restricted views: c*****r@example.com, +91******3210.</summary>
    public static string Mask(Channel channel, string? address)
    {
        if (string.IsNullOrEmpty(address)) return "";
        return channel switch
        {
            Channel.Email => MaskEmail(address),
            Channel.Sms or Channel.WhatsApp => MaskPhone(address),
            _ => address,
        };
    }

    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return new string('*', Math.Min(email.Length, 8));
        var local = email[..at];
        var masked = local.Length <= 2 ? local[0] + "*" : local[0] + new string('*', Math.Min(5, local.Length - 2)) + local[^1];
        return masked + email[at..];
    }

    public static string MaskPhone(string phone)
    {
        if (phone.Length <= 4) return "****";
        var keepStart = phone.StartsWith('+') ? Math.Min(3, phone.Length - 4) : 0;
        return phone[..keepStart] + new string('*', phone.Length - keepStart - 4) + phone[^4..];
    }

    public static string CountryPrefixMatch(string e164, IEnumerable<string> prefixes)
        => prefixes.Where(p => e164.StartsWith(p, StringComparison.Ordinal)).OrderByDescending(p => p.Length).FirstOrDefault() ?? "";

    public static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

/// <summary>SMS encoding and segmentation (GSM 03.38 7-bit vs UCS-2).</summary>
public static class SmsSegmenter
{
    private const string Gsm7Basic =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string Gsm7Extended = "^{}\\[~]|€\f";

    public static SmsInfoDto Analyze(string? text)
    {
        text ??= "";
        var gsm = true;
        var septets = 0;
        foreach (var c in text)
        {
            if (Gsm7Basic.Contains(c)) septets += 1;
            else if (Gsm7Extended.Contains(c)) septets += 2;
            else { gsm = false; break; }
        }
        if (gsm)
            return new SmsInfoDto { Encoding = "GSM-7", Characters = septets, Segments = septets == 0 ? 1 : septets <= 160 ? 1 : (int)Math.Ceiling(septets / 153.0) };
        // UCS-2 counts UTF-16 code units; surrogate pairs take two.
        var units = text.Length;
        return new SmsInfoDto { Encoding = "UCS-2", Characters = units, Segments = units <= 70 ? 1 : (int)Math.Ceiling(units / 67.0) };
    }
}

/// <summary>Delivery status ordering and transitions shared by workers, callbacks and reconciliation.</summary>
public static class DeliveryStatusRules
{
    /// <summary>Progress rank for forward-only statuses. Callbacks never move a delivery backwards.</summary>
    public static int Rank(DeliveryStatus s) => s switch
    {
        DeliveryStatus.Draft => 0,
        DeliveryStatus.Scheduled => 1,
        DeliveryStatus.Queued => 2,
        DeliveryStatus.RetryPending => 2,
        DeliveryStatus.Processing => 3,
        DeliveryStatus.Unknown => 4,
        DeliveryStatus.Submitted => 4,
        DeliveryStatus.ProviderAccepted => 5,
        DeliveryStatus.Sent => 6,
        DeliveryStatus.Delivered => 7,
        DeliveryStatus.Read => 8,
        _ => 100, // Failed, Cancelled, Expired, Suppressed: terminal
    };

    public static bool IsTerminal(DeliveryStatus s) => s is DeliveryStatus.Failed or DeliveryStatus.Cancelled or DeliveryStatus.Expired
        or DeliveryStatus.Suppressed or DeliveryStatus.Delivered or DeliveryStatus.Read;

    /// <summary>Final from the dispatcher's point of view: no further send attempts will be made.</summary>
    public static bool IsDispatchFinal(DeliveryStatus s) => IsTerminal(s) || s is DeliveryStatus.ProviderAccepted or DeliveryStatus.Sent;

    public static bool IsPending(DeliveryStatus s) => s is DeliveryStatus.Draft or DeliveryStatus.Scheduled or DeliveryStatus.Queued
        or DeliveryStatus.RetryPending or DeliveryStatus.Processing or DeliveryStatus.Submitted;

    public static bool IsSuccess(DeliveryStatus s) => s is DeliveryStatus.ProviderAccepted or DeliveryStatus.Sent or DeliveryStatus.Delivered or DeliveryStatus.Read;

    public static bool IsFailure(DeliveryStatus s) => s is DeliveryStatus.Failed or DeliveryStatus.Expired;

    /// <summary>
    /// Whether a provider-reported status may replace the current one. Forward progress only; a provider-reported
    /// failure is accepted while the message has not been confirmed delivered/read (e.g. Sent → Failed undeliverable).
    /// </summary>
    public static bool CanApplyProviderStatus(DeliveryStatus current, DeliveryStatus reported)
    {
        if (current == reported) return false;
        if (current is DeliveryStatus.Cancelled or DeliveryStatus.Expired or DeliveryStatus.Suppressed) return false;
        if (reported == DeliveryStatus.Failed)
            return current is DeliveryStatus.Submitted or DeliveryStatus.ProviderAccepted or DeliveryStatus.Sent or DeliveryStatus.Unknown;
        if (current == DeliveryStatus.Failed)
            // A late success after a provider-side failure report is evidence the message did arrive.
            return reported is DeliveryStatus.Delivered or DeliveryStatus.Read;
        return Rank(reported) > Rank(current);
    }

    public static RequestStatus Aggregate(IReadOnlyCollection<DeliveryStatus> statuses, bool anyScheduled)
    {
        if (statuses.Count == 0) return RequestStatus.Accepted;
        if (statuses.All(s => s == DeliveryStatus.Cancelled)) return RequestStatus.Cancelled;
        if (statuses.All(s => s == DeliveryStatus.Suppressed)) return RequestStatus.Suppressed;
        if (statuses.Any(s => IsPending(s) || s == DeliveryStatus.Unknown))
            return anyScheduled && statuses.All(s => s is DeliveryStatus.Scheduled or DeliveryStatus.Suppressed or DeliveryStatus.Cancelled)
                ? RequestStatus.Scheduled : RequestStatus.Processing;
        var ok = statuses.Count(IsSuccess);
        var failed = statuses.Count(IsFailure);
        if (failed == 0) return RequestStatus.Completed;
        return ok == 0 ? RequestStatus.Failed : RequestStatus.PartiallyFailed;
    }
}
