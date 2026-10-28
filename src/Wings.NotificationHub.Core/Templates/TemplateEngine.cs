using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wings.NotificationHub.Core.Templates;

/// <summary>How variable values are encoded when written into the output.</summary>
public enum OutputEncoding
{
    /// <summary>HTML-encode every value (email HTML bodies).</summary>
    Html,
    /// <summary>No markup encoding; control characters stripped (SMS, WhatsApp, push, plain-text email, subjects).</summary>
    Text,
}

public sealed class TemplateSyntaxException(string message, int line, int column)
    : Exception($"{message} (line {line}, column {column})")
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}

public sealed record RenderResult(string Output, IReadOnlyList<string> MissingVariables);

/// <summary>
/// Restricted, logic-light template language. Supported syntax:
/// <c>{{Name}}</c>, <c>{{Customer.Name}}</c>, filters <c>{{Amount | currency:INR}}</c>,
/// <c>{{#if X}}…{{else}}…{{/if}}</c>, <c>{{#unless X}}…{{/unless}}</c>, <c>{{#each Items}}{{this.Name}} {{@index}}{{/each}}</c>,
/// comments <c>{{! … }}</c>. There is no raw/unescaped output, no expressions, no method calls and no code execution.
/// </summary>
public static class TemplateEngine
{
    private static readonly HtmlEncoder HtmlEnc = HtmlEncoder.Create(UnicodeRanges.All);
    private const int MaxNesting = 20;
    private const int MaxEachItems = 1000;
    private const int MaxOutputChars = 1_000_000;
    private const int MaxCacheEntries = 2000;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<Node>> Cache = new();
    private static readonly Regex PathRegex = new(@"^(@index|this(\.[A-Za-z_][A-Za-z0-9_]*)*|[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*)$", RegexOptions.Compiled);
    private static readonly HashSet<string> KnownFilters = new(StringComparer.OrdinalIgnoreCase)
        { "upper", "lower", "title", "trim", "default", "truncate", "number", "currency", "date", "url" };

    public static RenderResult Render(string? template, IReadOnlyDictionary<string, object?> variables, OutputEncoding encoding)
    {
        if (string.IsNullOrEmpty(template)) return new RenderResult("", []);
        var nodes = Parse(template);
        var ctx = new RenderContext(variables, encoding);
        var sb = new StringBuilder(template.Length + 64);
        RenderNodes(nodes, ctx, sb, null, 0);
        return new RenderResult(sb.ToString(), ctx.Missing.ToList());
    }

    /// <summary>Validates syntax; returns null when valid, otherwise the error message.</summary>
    public static string? Validate(string? template)
    {
        if (string.IsNullOrEmpty(template)) return null;
        try { Parse(template); return null; }
        catch (TemplateSyntaxException ex) { return ex.Message; }
    }

    /// <summary>Root variable names referenced by the template (excluding loop-item fields).</summary>
    public static IReadOnlyList<string> ExtractVariables(string? template)
    {
        if (string.IsNullOrEmpty(template)) return [];
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(Parse(template), 0);
        return names;

        void Collect(IReadOnlyList<Node> nodes, int eachDepth)
        {
            foreach (var n in nodes)
            {
                switch (n)
                {
                    case OutputNode o: Add(o.Expr.Path, eachDepth); break;
                    case IfNode i: Add(i.Expr.Path, eachDepth); Collect(i.Then, eachDepth); Collect(i.Else, eachDepth); break;
                    case EachNode e: Add(e.Expr.Path, eachDepth); Collect(e.Body, eachDepth + 1); break;
                }
            }
        }

        void Add(string path, int eachDepth)
        {
            if (eachDepth > 0 || path.StartsWith("this", StringComparison.Ordinal) || path == "@index") return;
            var root = path.Split('.')[0];
            if (seen.Add(root)) names.Add(root);
        }
    }

    /// <summary>
    /// Root variables with a required flag: a variable is required when it is output outside any #if/#unless block
    /// without a <c>default</c> filter; variables used only conditionally (or with a default) are optional.
    /// </summary>
    public static IReadOnlyList<(string Name, bool Required)> ExtractVariableUsage(string? template)
    {
        if (string.IsNullOrEmpty(template)) return [];
        var order = new List<string>();
        var required = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        Walk(Parse(template), conditional: false, eachDepth: 0);
        return order.Select(n => (n, required[n])).ToList();

        void Walk(IReadOnlyList<Node> nodes, bool conditional, int eachDepth)
        {
            foreach (var n in nodes)
            {
                switch (n)
                {
                    case OutputNode o: Add(o.Expr, conditional || HasDefault(o.Expr), eachDepth); break;
                    case IfNode i: Add(i.Expr, true, eachDepth); Walk(i.Then, true, eachDepth); Walk(i.Else, true, eachDepth); break;
                    case EachNode e: Add(e.Expr, conditional, eachDepth); Walk(e.Body, conditional, eachDepth + 1); break;
                }
            }
        }

        void Add(Expr e, bool optional, int eachDepth)
        {
            if (eachDepth > 0 || e.Path.StartsWith("this", StringComparison.Ordinal) || e.Path == "@index") return;
            var root = e.Path.Split('.')[0];
            if (!required.TryGetValue(root, out var r)) { order.Add(root); required[root] = !optional; }
            else if (!optional && !r) required[root] = true;
        }
    }

    /// <summary>Converts JSON variables into the engine's value model (case-insensitive dictionaries, lists, scalars).</summary>
    public static Dictionary<string, object?> FromJson(IReadOnlyDictionary<string, JsonElement>? json)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (json is null) return d;
        foreach (var (k, v) in json) d[k] = Convert(v);
        return d;
    }

    public static object? Convert(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetDecimal(out var dec) ? dec : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(Convert).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Convert(p.Value), StringComparer.OrdinalIgnoreCase),
        _ => null,
    };

    // ───────────────────────── parsing ─────────────────────────

    private abstract record Node;
    private sealed record TextNode(string Text) : Node;
    private sealed record OutputNode(Expr Expr) : Node;
    private sealed record IfNode(Expr Expr, bool Negate, List<Node> Then, List<Node> Else) : Node;
    private sealed record EachNode(Expr Expr, List<Node> Body) : Node;
    private sealed record Expr(string Path, IReadOnlyList<(string Name, string? Arg)> Filters);

    private static IReadOnlyList<Node> Parse(string template)
    {
        if (Cache.TryGetValue(template, out var cached)) return cached;
        var nodes = new Parser(template).ParseAll();
        if (Cache.Count < MaxCacheEntries) Cache[template] = nodes;
        return nodes;
    }

    private sealed class Parser(string src)
    {
        private int _pos;

        public IReadOnlyList<Node> ParseAll()
        {
            var root = new List<Node>();
            var stack = new Stack<(string Kind, List<Node> Target, Node Owner, int Pos)>();
            var current = root;
            while (_pos < src.Length)
            {
                var open = src.IndexOf("{{", _pos, StringComparison.Ordinal);
                if (open < 0) { current.Add(new TextNode(src[_pos..])); break; }
                if (open > _pos) current.Add(new TextNode(src[_pos..open]));
                if (open + 2 < src.Length && src[open + 2] == '{') throw Error("Raw output '{{{' is not allowed; all values are encoded", open);
                var close = src.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0) throw Error("Unclosed '{{'", open);
                var tag = src[(open + 2)..close].Trim();
                _pos = close + 2;
                if (tag.Length == 0) throw Error("Empty tag", open);
                if (tag[0] == '!') continue; // comment
                if (tag[0] == '#')
                {
                    if (stack.Count >= MaxNesting) throw Error("Blocks nested too deeply", open);
                    var (kw, rest) = SplitKeyword(tag[1..]);
                    var expr = ParseExpr(rest, open);
                    Node owner;
                    List<Node> target;
                    switch (kw)
                    {
                        case "if": case "unless":
                            var ifn = new IfNode(expr, kw == "unless", [], []);
                            owner = ifn; target = ifn.Then; break;
                        case "each":
                            var each = new EachNode(expr, []);
                            owner = each; target = each.Body; break;
                        default: throw Error($"Unknown block '#{kw}'", open);
                    }
                    current.Add(owner);
                    stack.Push((kw, current, owner, open));
                    current = target;
                }
                else if (tag == "else")
                {
                    if (stack.Count == 0 || stack.Peek().Owner is not IfNode ifn) throw Error("'else' outside of an if block", open);
                    if (ReferenceEquals(current, ifn.Else)) throw Error("Duplicate 'else'", open);
                    current = ifn.Else;
                }
                else if (tag[0] == '/')
                {
                    var kw = tag[1..].Trim();
                    if (stack.Count == 0) throw Error($"Unexpected '/{kw}'", open);
                    var top = stack.Pop();
                    if (!string.Equals(top.Kind, kw, StringComparison.Ordinal)) throw Error($"'/{kw}' does not close '#{top.Kind}'", open);
                    current = top.Target;
                }
                else
                {
                    current.Add(new OutputNode(ParseExpr(tag, open)));
                }
            }
            if (stack.Count > 0) throw Error($"Unclosed '#{stack.Peek().Kind}' block", stack.Peek().Pos);
            return root;
        }

        private static (string, string) SplitKeyword(string s)
        {
            var i = s.IndexOf(' ');
            return i < 0 ? (s.Trim(), "") : (s[..i].Trim(), s[(i + 1)..].Trim());
        }

        private Expr ParseExpr(string text, int at)
        {
            var parts = text.Split('|');
            var path = parts[0].Trim();
            if (!PathRegex.IsMatch(path)) throw Error($"Invalid variable reference '{path}'", at);
            var filters = new List<(string, string?)>();
            foreach (var raw in parts.Skip(1))
            {
                var f = raw.Trim();
                var colon = f.IndexOf(':');
                var name = (colon < 0 ? f : f[..colon]).Trim();
                var arg = colon < 0 ? null : f[(colon + 1)..].Trim().Trim('\'', '"');
                if (!KnownFilters.Contains(name)) throw Error($"Unknown filter '{name}'", at);
                filters.Add((name.ToLowerInvariant(), arg));
            }
            return new Expr(path, filters);
        }

        private TemplateSyntaxException Error(string message, int index)
        {
            var line = 1; var col = 1;
            for (var i = 0; i < index && i < src.Length; i++)
            {
                if (src[i] == '\n') { line++; col = 1; } else col++;
            }
            return new TemplateSyntaxException(message, line, col);
        }
    }

    // ───────────────────────── rendering ─────────────────────────

    private sealed class RenderContext(IReadOnlyDictionary<string, object?> vars, OutputEncoding encoding)
    {
        public IReadOnlyDictionary<string, object?> Vars { get; } = vars;
        public OutputEncoding Encoding { get; } = encoding;
        public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Scope(object? Item, int Index, Scope? Parent);

    private static void RenderNodes(IReadOnlyList<Node> nodes, RenderContext ctx, StringBuilder sb, Scope? scope, int depth)
    {
        foreach (var node in nodes)
        {
            if (sb.Length > MaxOutputChars) throw new InvalidOperationException("Rendered output exceeds the maximum size.");
            switch (node)
            {
                case TextNode t: sb.Append(t.Text); break;
                case OutputNode o:
                    var value = ApplyFilters(Resolve(o.Expr.Path, ctx, scope, track: !HasDefault(o.Expr)), o.Expr.Filters, out var isUrl);
                    sb.Append(Encode(Stringify(value), ctx.Encoding, isUrl));
                    break;
                case IfNode i:
                    var truthy = IsTruthy(ApplyFilters(Resolve(i.Expr.Path, ctx, scope, track: false), i.Expr.Filters, out _));
                    RenderNodes(truthy ^ i.Negate ? i.Then : i.Else, ctx, sb, scope, depth + 1);
                    break;
                case EachNode e:
                    if (Resolve(e.Expr.Path, ctx, scope, track: true) is IEnumerable<object?> list)
                    {
                        var idx = 0;
                        foreach (var item in list)
                        {
                            if (idx >= MaxEachItems) break;
                            RenderNodes(e.Body, ctx, sb, new Scope(item, idx, scope), depth + 1);
                            idx++;
                        }
                    }
                    break;
            }
        }
    }

    private static bool HasDefault(Expr e) => e.Filters.Any(f => f.Name == "default");

    private static object? Resolve(string path, RenderContext ctx, Scope? scope, bool track)
    {
        if (path == "@index") return scope is null ? null : scope.Index;
        var segments = path.Split('.');
        object? current;
        int start;
        if (segments[0] == "this")
        {
            current = scope?.Item;
            start = 1;
        }
        else
        {
            // Inside #each, plain names resolve against the item first, then the root variables.
            if (scope?.Item is IDictionary<string, object?> itemDict && TryGet(itemDict, segments[0], out var fromItem))
                current = fromItem;
            else if (TryGet(ctx.Vars, segments[0], out var fromRoot))
                current = fromRoot;
            else
            {
                if (track && scope is null) ctx.Missing.Add(segments[0]);
                return null;
            }
            start = 1;
        }
        for (var i = start; i < segments.Length; i++)
        {
            if (current is IDictionary<string, object?> d && TryGet(d, segments[i], out var next)) current = next;
            else
            {
                if (track && scope is null) ctx.Missing.Add(string.Join('.', segments[..(i + 1)]));
                return null;
            }
        }
        return current;
    }

    private static bool TryGet(IEnumerable<KeyValuePair<string, object?>> dict, string key, out object? value)
    {
        if (dict is IReadOnlyDictionary<string, object?> ro && ro.TryGetValue(key, out value)) return true;
        foreach (var (k, v) in dict)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        }
        value = null;
        return false;
    }

    private static bool IsTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
        decimal d => d != 0,
        double d => d != 0,
        int i => i != 0,
        IEnumerable<object?> l => l.Any(),
        _ => true,
    };

    private static object? ApplyFilters(object? value, IReadOnlyList<(string Name, string? Arg)> filters, out bool isUrl)
    {
        isUrl = false;
        foreach (var (name, arg) in filters)
        {
            switch (name)
            {
                case "upper": value = Stringify(value).ToUpperInvariant(); break;
                case "lower": value = Stringify(value).ToLowerInvariant(); break;
                case "title": value = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Stringify(value).ToLowerInvariant()); break;
                case "trim": value = Stringify(value).Trim(); break;
                case "default": if (!IsTruthy(value)) value = arg ?? ""; break;
                case "truncate":
                    var s = Stringify(value);
                    var max = int.TryParse(arg, out var m) ? Math.Max(1, m) : 100;
                    value = s.Length <= max ? s : s[..max] + "…";
                    break;
                case "number":
                    if (ToDecimal(value) is { } n)
                        value = n.ToString("N" + (int.TryParse(arg, out var dp) ? Math.Clamp(dp, 0, 6) : 0), CultureFor(null));
                    break;
                case "currency":
                    if (ToDecimal(value) is { } amount) value = FormatCurrency(amount, arg);
                    break;
                case "date":
                    if (ToDate(value) is { } date)
                        value = date.ToString(string.IsNullOrWhiteSpace(arg) ? "dd-MMM-yyyy" : arg, CultureInfo.InvariantCulture);
                    break;
                case "url":
                    isUrl = true;
                    value = SafeUrl(Stringify(value));
                    break;
            }
        }
        return value;
    }

    public static string FormatCurrency(decimal amount, string? currency)
    {
        var code = string.IsNullOrWhiteSpace(currency) ? "INR" : currency.ToUpperInvariant();
        var culture = CultureFor(code);
        var symbol = code switch { "INR" => "₹", "USD" => "$", "EUR" => "€", "GBP" => "£", "AED" => "AED ", _ => code + " " };
        return symbol + amount.ToString("N2", culture);
    }

    private static CultureInfo CultureFor(string? currency)
    {
        try { return currency is null or "INR" ? CultureInfo.GetCultureInfo("en-IN") : CultureInfo.InvariantCulture; }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    private static decimal? ToDecimal(object? v) => v switch
    {
        decimal d => d,
        double d => (decimal)d,
        int i => i,
        long l => l,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static DateTimeOffset? ToDate(object? v) => v switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(d),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) => d,
        _ => null,
    };

    /// <summary>Only http(s), mailto and tel links survive; anything else (javascript:, data:) becomes "#".</summary>
    public static string SafeUrl(string url)
    {
        var u = url.Trim();
        if (u.Length == 0) return "#";
        // Relative deep links ("/invoices/42") are allowed; on Unix these would otherwise parse as file:// URIs.
        if (u.StartsWith('/')) return !u.StartsWith("//") && !u.Contains(':') && !u.Contains('\\') ? u : "#";
        if (Uri.TryCreate(u, UriKind.Absolute, out var abs))
            return abs.Scheme is "http" or "https" or "mailto" or "tel" ? abs.AbsoluteUri : "#";
        return "#";
    }

    public static string Stringify(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        IEnumerable<object?> l => string.Join(", ", l.Select(Stringify)),
        IDictionary<string, object?> => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string Encode(string value, OutputEncoding encoding, bool isUrl)
    {
        var cleaned = StripControl(value);
        return encoding switch
        {
            OutputEncoding.Html => isUrl ? HtmlEnc.Encode(cleaned) : HtmlEnc.Encode(cleaned).Replace("&#xA;", "<br>"),
            _ => cleaned,
        };
    }

    private static string StripControl(string s)
    {
        if (!s.Any(c => char.IsControl(c) && c is not '\n' and not '\t')) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (!char.IsControl(c) || c is '\n' or '\t') sb.Append(c);
        return sb.ToString();
    }

    private static readonly Regex[] UnsafeHtml =
    [
        new(@"<\s*(script|iframe|object|embed|applet|form|meta|link|base)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(href|src|action|formaction|xlink:href)\s*=\s*[""']?\s*(javascript|vbscript|data):", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"expression\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>Rejects template HTML that could execute code in a mail client or browser.</summary>
    public static string? FindUnsafeHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        foreach (var r in UnsafeHtml)
        {
            var m = r.Match(html);
            if (m.Success) return $"Template HTML contains a disallowed construct: '{m.Value.Trim()}'";
        }
        return null;
    }

    /// <summary>Crude HTML → text conversion for plain-text email alternatives and SMS previews.</summary>
    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<(br|/p|/div|/tr|/h[1-6]|/li)\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
    }
}
