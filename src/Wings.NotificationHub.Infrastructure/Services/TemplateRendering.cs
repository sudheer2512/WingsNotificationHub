using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Wings.NotificationHub.Contracts;
using Wings.NotificationHub.Core.Domain;
using Wings.NotificationHub.Core.Messaging;
using Wings.NotificationHub.Core.Templates;
using Wings.NotificationHub.Infrastructure.Common;
using Wings.NotificationHub.Infrastructure.Data;

namespace Wings.NotificationHub.Infrastructure.Services;

/// <summary>Channel-specific structure stored in <see cref="TemplateVersion.ComponentsJson"/>.</summary>
public sealed class TemplateComponents
{
    /// <summary>In-app / push title template.</summary>
    public string? Title { get; set; }
    public WhatsAppComponents? WhatsApp { get; set; }
    public PushComponents? Push { get; set; }
}

public sealed class WhatsAppComponents
{
    /// <summary>TEXT, IMAGE, DOCUMENT, VIDEO or null for no header.</summary>
    public string? HeaderFormat { get; set; }
    public string? HeaderText { get; set; }
    /// <summary>Variable holding the header media URL (IMAGE/DOCUMENT/VIDEO headers).</summary>
    public string? HeaderMediaVariable { get; set; }
    public string? FooterText { get; set; }
    public List<WhatsAppButtonDefinition> Buttons { get; set; } = [];
}

public sealed class WhatsAppButtonDefinition
{
    /// <summary>QUICK_REPLY, URL or PHONE_NUMBER.</summary>
    public string Type { get; set; } = "QUICK_REPLY";
    public string Text { get; set; } = "";
    /// <summary>URL buttons: base URL, may end with {{1}} for a dynamic suffix.</summary>
    public string? Url { get; set; }
    /// <summary>Variable supplying the dynamic URL suffix / quick-reply payload.</summary>
    public string? Variable { get; set; }
    public string? PhoneNumber { get; set; }
}

public sealed class PushComponents
{
    public string? Icon { get; set; }
    public string? ImageUrl { get; set; }
    public string? Sound { get; set; }
    public string? DeepLink { get; set; }
}

public sealed class RenderOutcome
{
    public RenderedContent Content { get; init; } = new();
    public List<string> Missing { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public int Units { get; init; } = 1;
}

/// <summary>Renders a template version for a channel. Escaping follows the destination format.</summary>
public static partial class ChannelRenderer
{
    [GeneratedRegex(@"\{\{\s*([^#/!\s{][^}]*?)\s*\}\}")]
    private static partial Regex SimpleTag();

    public static RenderOutcome Render(TemplateVersion version, Channel channel, IReadOnlyDictionary<string, object?> vars, string? brandingFooterHtml = null)
    {
        var missing = new List<string>();
        var errors = new List<string>();
        string R(string? template, OutputEncoding enc)
        {
            if (string.IsNullOrEmpty(template)) return "";
            try
            {
                var r = TemplateEngine.Render(template, vars, enc);
                missing.AddRange(r.MissingVariables);
                return r.Output;
            }
            catch (TemplateSyntaxException ex) { errors.Add(ex.Message); return ""; }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); return ""; }
        }

        var components = Json.Deserialize<TemplateComponents>(version.ComponentsJson) ?? new TemplateComponents();
        var content = new RenderedContent { Format = BodyFormat.Text };
        var units = 1;
        switch (channel)
        {
            case Channel.Email:
                content.Subject = R(version.Subject, OutputEncoding.Text).Replace("\r", "").Replace("\n", " ").Trim();
                if (version.BodyFormat == BodyFormat.Html)
                {
                    content.Format = BodyFormat.Html;
                    content.Body = R(version.Body, OutputEncoding.Html) + (brandingFooterHtml ?? "");
                    content.TextBody = string.IsNullOrEmpty(version.TextBody) ? TemplateEngine.HtmlToText(content.Body) : R(version.TextBody, OutputEncoding.Text);
                }
                else
                {
                    content.Body = R(version.Body, OutputEncoding.Text);
                    content.TextBody = content.Body;
                }
                break;
            case Channel.Sms:
                content.Body = R(version.Body, OutputEncoding.Text);
                units = Core.SmsSegmenter.Analyze(content.Body).Segments;
                break;
            case Channel.WhatsApp:
                content.Body = R(version.Body, OutputEncoding.Text);
                if (!string.IsNullOrEmpty(version.ProviderTemplateName))
                {
                    var wa = components.WhatsApp ?? new WhatsAppComponents();
                    var call = new WhatsAppTemplateCall
                    {
                        Name = version.ProviderTemplateName!,
                        Language = version.ProviderTemplateLanguage ?? "en",
                        BodyParameters = PositionalParameters(version.Body, vars, R),
                        HeaderParameters = wa.HeaderFormat?.Equals("TEXT", StringComparison.OrdinalIgnoreCase) == true ? PositionalParameters(wa.HeaderText, vars, R) : [],
                    };
                    if (wa.HeaderMediaVariable is { Length: > 0 } mediaVar)
                    {
                        call.HeaderMediaUrl = R("{{" + mediaVar + " | url}}", OutputEncoding.Text);
                        call.HeaderMediaType = wa.HeaderFormat?.ToLowerInvariant();
                    }
                    for (var i = 0; i < wa.Buttons.Count; i++)
                    {
                        var b = wa.Buttons[i];
                        if (string.IsNullOrEmpty(b.Variable)) continue;
                        call.Buttons.Add(new WhatsAppButtonParameter
                        {
                            Index = i,
                            SubType = b.Type.Equals("URL", StringComparison.OrdinalIgnoreCase) ? "url" : "quick_reply",
                            Value = R("{{" + b.Variable + "}}", OutputEncoding.Text),
                        });
                    }
                    content.WhatsAppTemplate = call;
                }
                break;
            case Channel.InApp:
            case Channel.Push:
            case Channel.WebPush:
                content.Title = R(components.Title ?? version.Subject, OutputEncoding.Text);
                content.Body = R(version.Body, OutputEncoding.Text);
                if (components.Push is { } push)
                    content.App = new AppNotificationOptionsDto
                    {
                        Icon = push.Icon,
                        ImageUrl = string.IsNullOrEmpty(push.ImageUrl) ? null : R(push.ImageUrl, OutputEncoding.Text),
                        DeepLink = string.IsNullOrEmpty(push.DeepLink) ? null : TemplateEngine.SafeUrl(R(push.DeepLink, OutputEncoding.Text)),
                    };
                break;
            case Channel.Webhook:
                content.Subject = R(version.Subject, OutputEncoding.Text);
                content.Body = R(version.Body, OutputEncoding.Text);
                break;
        }

        // Required variables declared on the version that the template did not reference still have to be supplied.
        foreach (var v in version.Variables.Where(v => v.Required))
            if (!vars.ContainsKey(v.Name) && !missing.Contains(v.Name, StringComparer.OrdinalIgnoreCase)) missing.Add(v.Name);
        return new RenderOutcome { Content = content, Missing = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Errors = errors, Units = units };
    }

    /// <summary>Values for Meta's positional {{1}}, {{2}} … parameters: one per distinct expression, in order of first appearance.</summary>
    public static List<string> PositionalParameters(string? text, IReadOnlyDictionary<string, object?> vars, Func<string?, OutputEncoding, string> render)
        => DistinctExpressions(text).Select(expr => render("{{" + expr + "}}", OutputEncoding.Text)).ToList();

    public static List<string> DistinctExpressions(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (Match m in SimpleTag().Matches(text))
        {
            var expr = m.Groups[1].Value.Trim();
            if (expr is "else") continue;
            if (!list.Contains(expr)) list.Add(expr);
        }
        return list;
    }

    /// <summary>Converts our {{Var}} syntax into Meta's positional {{n}} syntax for template submission.</summary>
    public static string ToPositional(string text)
    {
        var order = DistinctExpressions(text);
        return SimpleTag().Replace(text, m => "{{" + (order.IndexOf(m.Groups[1].Value.Trim()) + 1) + "}}");
    }
}

/// <summary>Finds the active template for a code/channel/locale with most-specific-scope-wins resolution.</summary>
public sealed class TemplateResolver(NotificationHubDbContext db)
{
    private readonly Dictionary<(string, Channel, string, Guid, Guid), (NotificationTemplate, TemplateVersion)?> _cache = [];

    /// <summary>Resolution order: company+application, application, company, tenant; locale exact → language → app default → "en".</summary>
    public async Task<(NotificationTemplate Template, TemplateVersion Version)?> ResolveAsync(
        Guid tenantId, Guid companyId, Guid applicationId, string code, Channel channel, string? locale, string defaultLocale, CancellationToken ct)
    {
        var key = (code.ToUpperInvariant(), channel, (locale ?? "").ToLowerInvariant(), companyId, applicationId);
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var candidates = await db.NotificationTemplates.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.Code == code && t.Channel == channel && t.ActiveVersionId != null
                        && (t.CompanyId == null || t.CompanyId == companyId) && (t.ApplicationId == null || t.ApplicationId == applicationId))
            .ToListAsync(ct);
        var locales = LocaleChain(locale, defaultLocale);
        var best = candidates
            .Where(t => locales.Contains(t.Locale.ToLowerInvariant()))
            .OrderBy(t => locales.IndexOf(t.Locale.ToLowerInvariant()))
            .ThenByDescending(t => (t.CompanyId is null ? 0 : 1) + (t.ApplicationId is null ? 0 : 2))
            .FirstOrDefault();
        (NotificationTemplate, TemplateVersion)? result = null;
        if (best is not null)
        {
            var version = await db.TemplateVersions.AsNoTracking().Include(v => v.Variables).FirstAsync(v => v.Id == best.ActiveVersionId, ct);
            result = (best, version);
        }
        _cache[key] = result;
        return result;
    }

    public static List<string> LocaleChain(string? locale, string defaultLocale)
    {
        var chain = new List<string>();
        void Add(string? l) { if (!string.IsNullOrWhiteSpace(l) && !chain.Contains(l.ToLowerInvariant())) chain.Add(l.ToLowerInvariant()); }
        Add(locale);
        if (locale?.Contains('-') == true) Add(locale.Split('-')[0]);
        Add(defaultLocale);
        if (defaultLocale.Contains('-')) Add(defaultLocale.Split('-')[0]);
        Add("en");
        return chain;
    }
}

/// <summary>Builds the variable dictionary for rendering: request variables plus system variables.</summary>
public static class RenderVariables
{
    public static Dictionary<string, object?> Build(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? requestVars,
        Tenant tenant, Company company, string? recipientName, string? unsubscribeUrl)
    {
        var vars = TemplateEngine.FromJson(requestVars);
        vars.TryAdd("CompanyName", company.Name);
        vars.TryAdd("TenantName", tenant.Name);
        if (!string.IsNullOrEmpty(recipientName)) vars.TryAdd("RecipientName", recipientName);
        // Always defined so templates may reference it; empty for non-promotional messages.
        vars["UnsubscribeUrl"] = unsubscribeUrl;
        vars.TryAdd("CurrentDate", DateTime.UtcNow.ToString("yyyy-MM-dd"));
        return vars;
    }

    public static string? EmailFooter(Tenant tenant, Company company, string? unsubscribeUrl, bool templateHasUnsubscribe)
    {
        var branding = Json.Deserialize<JsonObject>(company.BrandingJson) ?? Json.Deserialize<JsonObject>(tenant.BrandingJson);
        var footer = branding?["emailFooterHtml"]?.GetValue<string>();
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(footer) && TemplateEngine.FindUnsafeHtml(footer) is null) parts.Add(footer);
        if (unsubscribeUrl is not null && !templateHasUnsubscribe)
            parts.Add($"<p style=\"font-size:12px;color:#777\">You received this because you subscribed to updates from {System.Net.WebUtility.HtmlEncode(company.Name)}. <a href=\"{System.Net.WebUtility.HtmlEncode(unsubscribeUrl)}\">Unsubscribe</a></p>");
        return parts.Count == 0 ? null : string.Join("", parts);
    }
}
