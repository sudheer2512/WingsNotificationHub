using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wings.NotificationHub.Contracts;

namespace Wings.NotificationHub.Core.Rules;

/// <summary>Condition tree node. Group: {"op":"and","children":[…]}; leaf: {"field":"payload.amount","comparison":"gt","value":10000}.</summary>
public sealed class ConditionNode
{
    public string? Op { get; set; }
    public List<ConditionNode>? Children { get; set; }
    public string? Field { get; set; }
    public string? Comparison { get; set; }
    public JsonNode? Value { get; set; }

    public bool IsGroup => Op is not null;
}

/// <summary>Who receives notifications produced by a rule.</summary>
public sealed class RecipientSelector
{
    /// <summary>payload | users | roles | group | company</summary>
    public string Type { get; set; } = "payload";
    /// <summary>For type=payload: path to a recipient object or array in the event payload (e.g. "customer").</summary>
    public string? Path { get; set; }
    public List<string>? UserIds { get; set; }
    public List<string>? Roles { get; set; }
    public Guid? GroupId { get; set; }
}

/// <summary>One channel produced by a matched rule.</summary>
public sealed class RuleChannelAction
{
    public Channel Channel { get; set; }
    public string TemplateCode { get; set; } = "";
    public Channel? FallbackChannel { get; set; }
    public string? FallbackTemplateCode { get; set; }
}

public static class ConditionEvaluator
{
    public static readonly string[] Comparisons =
        ["eq", "ne", "gt", "gte", "lt", "lte", "contains", "notcontains", "startswith", "endswith", "in", "notin", "exists", "notexists", "empty", "notempty"];

    /// <summary>Evaluates a condition tree against a JSON document. A null/empty tree matches everything.</summary>
    public static bool Evaluate(ConditionNode? node, JsonNode? doc)
    {
        if (node is null) return true;
        if (node.IsGroup)
        {
            var children = node.Children ?? [];
            if (children.Count == 0) return true;
            return node.Op!.Equals("or", StringComparison.OrdinalIgnoreCase)
                ? children.Any(c => Evaluate(c, doc))
                : children.All(c => Evaluate(c, doc));
        }
        if (string.IsNullOrWhiteSpace(node.Field)) return true;
        var actual = Select(doc, node.Field);
        return Compare(actual, (node.Comparison ?? "eq").ToLowerInvariant(), node.Value);
    }

    /// <summary>Validates a tree; returns error messages.</summary>
    public static List<string> Validate(ConditionNode? node, int depth = 0)
    {
        var errors = new List<string>();
        if (node is null) return errors;
        if (depth > 10) { errors.Add("Conditions are nested too deeply (max 10)."); return errors; }
        if (node.IsGroup)
        {
            if (!node.Op!.Equals("and", StringComparison.OrdinalIgnoreCase) && !node.Op.Equals("or", StringComparison.OrdinalIgnoreCase))
                errors.Add($"Unknown group operator '{node.Op}'.");
            foreach (var c in node.Children ?? []) errors.AddRange(Validate(c, depth + 1));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(node.Field)) errors.Add("Condition field is required.");
            if (!Comparisons.Contains((node.Comparison ?? "eq").ToLowerInvariant())) errors.Add($"Unknown comparison '{node.Comparison}'.");
        }
        return errors;
    }

    /// <summary>Dot path lookup, case-insensitive. A leading "payload." is optional. Supports array indexes "items.0.sku".</summary>
    public static JsonNode? Select(JsonNode? doc, string path)
    {
        var found = SelectExact(doc, path);
        // Event documents wrap the payload: "AmountDue" also resolves as "payload.AmountDue".
        if (found is null && doc is JsonObject root && HasKey(root, "payload") && !path.TrimStart('$', '.').StartsWith("payload", StringComparison.OrdinalIgnoreCase))
            found = SelectExact(root.First(kv => string.Equals(kv.Key, "payload", StringComparison.OrdinalIgnoreCase)).Value, path);
        return found;
    }

    private static JsonNode? SelectExact(JsonNode? doc, string path)
    {
        var p = path.Trim();
        if (p.StartsWith("$.")) p = p[2..];
        if (p.StartsWith("payload.", StringComparison.OrdinalIgnoreCase) && doc is JsonObject o && !HasKey(o, "payload")) p = p[8..];
        JsonNode? current = doc;
        foreach (var seg in p.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (current)
            {
                case JsonObject obj:
                    current = obj.FirstOrDefault(kv => string.Equals(kv.Key, seg, StringComparison.OrdinalIgnoreCase)).Value;
                    break;
                case JsonArray arr when int.TryParse(seg, out var i) && i >= 0 && i < arr.Count:
                    current = arr[i];
                    break;
                default:
                    return null;
            }
            if (current is null) return null;
        }
        return current;
    }

    private static bool HasKey(JsonObject o, string key) => o.Any(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));

    private static bool Compare(JsonNode? actual, string cmp, JsonNode? expected)
    {
        switch (cmp)
        {
            case "exists": return actual is not null;
            case "notexists": return actual is null;
            case "empty": return IsEmpty(actual);
            case "notempty": return !IsEmpty(actual);
            case "in": return expected is JsonArray inArr && inArr.Any(e => ScalarEquals(actual, e));
            case "notin": return expected is not JsonArray notArr || !notArr.Any(e => ScalarEquals(actual, e));
        }
        if (actual is null) return cmp == "ne" && expected is not null;
        switch (cmp)
        {
            case "eq": return ScalarEquals(actual, expected);
            case "ne": return !ScalarEquals(actual, expected);
            case "gt": case "gte": case "lt": case "lte":
                var c = CompareOrdered(actual, expected);
                if (c is null) return false;
                return cmp switch { "gt" => c > 0, "gte" => c >= 0, "lt" => c < 0, _ => c <= 0 };
            case "contains":
                if (actual is JsonArray a) return a.Any(e => ScalarEquals(e, expected));
                return AsString(actual).Contains(AsString(expected), StringComparison.OrdinalIgnoreCase);
            case "notcontains":
                if (actual is JsonArray na) return !na.Any(e => ScalarEquals(e, expected));
                return !AsString(actual).Contains(AsString(expected), StringComparison.OrdinalIgnoreCase);
            case "startswith": return AsString(actual).StartsWith(AsString(expected), StringComparison.OrdinalIgnoreCase);
            case "endswith": return AsString(actual).EndsWith(AsString(expected), StringComparison.OrdinalIgnoreCase);
            default: return false;
        }
    }

    private static bool IsEmpty(JsonNode? n) => n switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        _ => AsString(n).Length == 0,
    };

    private static bool ScalarEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (AsNumber(a) is { } x && AsNumber(b) is { } y) return x == y;
        if (AsBool(a) is { } p && AsBool(b) is { } q) return p == q;
        return string.Equals(AsString(a), AsString(b), StringComparison.OrdinalIgnoreCase);
    }

    private static int? CompareOrdered(JsonNode a, JsonNode? b)
    {
        if (b is null) return null;
        if (AsNumber(a) is { } x && AsNumber(b) is { } y) return x.CompareTo(y);
        if (DateTimeOffset.TryParse(AsString(a), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d1)
            && DateTimeOffset.TryParse(AsString(b), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d2))
            return d1.CompareTo(d2);
        return string.Compare(AsString(a), AsString(b), StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? AsNumber(JsonNode n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<double>(out var dbl)) return (decimal)dbl;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var ps)) return ps;
        }
        return null;
    }

    private static bool? AsBool(JsonNode n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var pb)) return pb;
        }
        return null;
    }

    public static string AsString(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString().Trim('"'),
        _ => n.ToJsonString(),
    };

    /// <summary>Flattens an event payload into template variables: top-level keys plus nested objects (accessible as {{a.b}}).</summary>
    public static Dictionary<string, JsonElement> ToVariables(JsonObject payload)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in payload)
            d[k] = v is null ? default : JsonSerializer.SerializeToElement(v);
        return d;
    }
}
