using System.Diagnostics;
using System.Globalization;
using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.WinForms;

/// <summary>Raised when the user opens a message or presses an action whose deep link should be followed.</summary>
public sealed class DeepLinkEventArgs(InAppMessageDto message, string url, AppActionDto? action) : EventArgs
{
    public InAppMessageDto Message { get; } = message;
    public string Url { get; } = url;
    public AppActionDto? Action { get; } = action;
    /// <summary>Set to true when your handler navigated; otherwise http(s) links open in the default browser.</summary>
    public bool Handled { get; set; }
}

/// <summary>Raised when an action button is pressed.</summary>
public sealed class InboxActionEventArgs(InAppMessageDto message, AppActionDto action) : EventArgs
{
    public InAppMessageDto Message { get; } = message;
    public AppActionDto Action { get; } = action;
}

/// <summary>Colours used by the inbox controls; built from the hub's white-label branding.</summary>
public sealed class InboxTheme
{
    public static readonly Color DefaultPrimary = Color.FromArgb(0x15, 0x65, 0xC0);

    public Color Primary { get; init; } = DefaultPrimary;
    public Color OnPrimary { get; init; } = Color.White;
    public Color Surface { get; init; } = Color.White;
    public Color Text { get; init; } = Color.FromArgb(0x1F, 0x1F, 0x1F);
    public Color Muted { get; init; } = Color.FromArgb(0x5F, 0x63, 0x68);
    public Color Border { get; init; } = Color.FromArgb(0xE3, 0xE3, 0xE3);
    public Color Critical { get; init; } = Color.FromArgb(0xC6, 0x28, 0x28);
    public Color High { get; init; } = Color.FromArgb(0xEF, 0x6C, 0x00);
    public Color UnreadBackground { get; init; } = Color.FromArgb(0xF0, 0xF5, 0xFC);

    public static InboxTheme From(InboxBranding? branding)
    {
        var primary = ParseColor(branding?.PrimaryColor) ?? DefaultPrimary;
        return new InboxTheme
        {
            Primary = primary,
            OnPrimary = Luminance(primary) > 0.179 ? Color.FromArgb(0x1F, 0x1F, 0x1F) : Color.White,
            UnreadBackground = Blend(primary, Color.White, 0.06),
        };
    }

    public Color PriorityColor(NotificationPriority p) => p switch
    {
        NotificationPriority.Critical => Critical,
        NotificationPriority.High => High,
        _ => Color.Transparent,
    };

    /// <summary>Parses #rgb / #rrggbb / #aarrggbb branding colours; anything else returns null.</summary>
    public static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] != '#') return null;
        var hex = value[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length is not (6 or 8) || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return null;
        return hex.Length == 6 ? Color.FromArgb(255, (n >> 16) & 0xFF, (n >> 8) & 0xFF, n & 0xFF) : Color.FromArgb(n);
    }

    private static double Luminance(Color c)
    {
        static double Ch(int v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static Color Blend(Color a, Color b, double amountOfA)
        => Color.FromArgb((int)(a.R * amountOfA + b.R * (1 - amountOfA)), (int)(a.G * amountOfA + b.G * (1 - amountOfA)), (int)(a.B * amountOfA + b.B * (1 - amountOfA)));
}

public static class InboxFormatting
{
    /// <summary>"just now", "5 min ago", "yesterday", or a short date.</summary>
    public static string RelativeTime(DateTimeOffset at, DateTimeOffset? now = null)
    {
        var d = (now ?? DateTimeOffset.UtcNow) - at;
        if (d < TimeSpan.FromSeconds(45)) return "just now";
        if (d < TimeSpan.FromMinutes(60)) return $"{(int)Math.Round(d.TotalMinutes)} min ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)Math.Round(d.TotalHours)} h ago";
        if (d < TimeSpan.FromHours(48)) return "yesterday";
        return at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    public static string Badge(int count) => count <= 0 ? "" : count > 99 ? "99+" : count.ToString(CultureInfo.CurrentCulture);
}

/// <summary>Default deep-link behaviour for desktop apps.</summary>
public static class DeepLinks
{
    /// <summary>
    /// Resolves a deep link: absolute http(s) URLs are used as-is; relative paths ("/sales/orders/42") are combined with
    /// <paramref name="webBase"/> when given. Any other scheme (javascript:, file:, custom) returns null.
    /// </summary>
    public static Uri? Resolve(string? url, Uri? webBase)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "#") return null;
        var u = url.Trim();
        if (u.StartsWith('/') && !u.StartsWith("//", StringComparison.Ordinal) && !u.Contains('\\'))
            return webBase is null ? null : new Uri(webBase, u.TrimStart('/'));
        return Uri.TryCreate(u, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp) ? abs : null;
    }

    /// <summary>Opens an http(s) URL in the user's default browser.</summary>
    public static bool OpenInBrowser(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return true;
    }
}
