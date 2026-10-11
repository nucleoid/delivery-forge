using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeliveryForge.Evidence.Adapters;

// Evaluates only the grammar used by the pinned vendor schema. No network or caller-supplied schema.
internal static class Crap4CSharpResultSchema
{
    private static readonly JsonDocument Schema = Load();

    internal static string? Validate(JsonElement report)
    {
        try { Check(report, Schema.RootElement, "$", Schema.RootElement); return null; }
        catch (InvalidReport exception) { return exception.Message; }
        catch (RegexMatchTimeoutException) { return "Vendor result validation exceeded its pattern bound."; }
    }

    private static JsonDocument Load()
    {
        using var stream = typeof(Crap4CSharpResultSchema).Assembly.GetManifestResourceStream(
            "DeliveryForge.Evidence.Crap4CSharp.result-schema-v1.json")
            ?? throw new InvalidOperationException("Pinned Crap4CSharp schema is absent.");
        return JsonDocument.Parse(stream);
    }

    private static void Check(JsonElement value, JsonElement schema, string path, JsonElement root)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!;
            if (!name.StartsWith("#/$defs/", StringComparison.Ordinal))
                throw new InvalidOperationException("Vendor schema contains an unsupported reference.");
            Check(value, root.GetProperty("$defs").GetProperty(name[8..]), path, root);
            return;
        }
        if (schema.TryGetProperty("allOf", out var all))
            foreach (var child in all.EnumerateArray()) Check(value, child, path, root);
        if (schema.TryGetProperty("anyOf", out var alternatives))
            Require(alternatives.EnumerateArray().Any(candidate => Accepts(value, candidate, path, root)),
                path, "no declared schema alternative matches");
        if (schema.TryGetProperty("type", out var type))
        {
            var matches = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Any(candidate => Matches(value, candidate.GetString()!))
                : Matches(value, type.GetString()!);
            Require(matches, path, "wrong JSON type");
        }
        if (schema.TryGetProperty("const", out var constant))
            Require(JsonElement.DeepEquals(value, constant), path, "wrong constant");
        if (schema.TryGetProperty("enum", out var choices))
            Require(choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(value, choice)), path, "unknown enum");

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    Require(value.TryGetProperty(name.GetString()!, out _), path + "." + name.GetString(), "required field absent");
            var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var child))
                    Check(property.Value, child, path + "." + property.Name, root);
                else if (schema.TryGetProperty("additionalProperties", out var additional))
                {
                    Require(additional.ValueKind != JsonValueKind.False, path, "unexpected field");
                    if (additional.ValueKind == JsonValueKind.Object)
                        Check(property.Value, additional, path + "." + property.Name, root);
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) Check(item, items, $"{path}[{index++}]", root);
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var minimum))
                Require(text.Length >= minimum.GetInt32(), path, "string too short");
            if (schema.TryGetProperty("pattern", out var pattern))
                Require(Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    TimeSpan.FromSeconds(1)), path, "pattern mismatch");
            if (schema.TryGetProperty("format", out var format))
            {
                if (format.GetString() == "uuid") Require(Guid.TryParseExact(text, "D", out _), path, "invalid UUID");
                if (format.GetString() == "date-time")
                    Require(Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$",
                        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)) &&
                        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), path, "invalid RFC3339 timestamp");
            }
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            Require(value.TryGetDouble(out var number) && double.IsFinite(number), path, "nonfinite number");
            if (schema.TryGetProperty("minimum", out var minimum)) Require(number >= minimum.GetDouble(), path, "below minimum");
            if (schema.TryGetProperty("maximum", out var maximum)) Require(number <= maximum.GetDouble(), path, "above maximum");
        }
    }

    private static bool Matches(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var integer) && double.IsFinite(integer) && Math.Truncate(integer) == integer,
        _ => throw new InvalidOperationException("Unsupported vendor schema type.")
    };

    private static bool Accepts(JsonElement value, JsonElement schema, string path, JsonElement root)
    {
        try { Check(value, schema, path, root); return true; }
        catch (InvalidReport) { return false; }
    }

    private static void Require(bool condition, string path, string reason)
    {
        if (!condition) throw new InvalidReport($"{path}: {reason}.");
    }

    private sealed class InvalidReport(string message) : Exception(message);
}
