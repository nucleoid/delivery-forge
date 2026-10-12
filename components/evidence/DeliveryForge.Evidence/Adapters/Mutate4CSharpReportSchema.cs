using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeliveryForge.Evidence.Adapters;

// Evaluates only the grammar used by the pinned vendor schema. No network or caller-supplied schema.
internal static class Mutate4CSharpReportSchema
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
        using var stream = typeof(Mutate4CSharpReportSchema).Assembly.GetManifestResourceStream(
            "DeliveryForge.Evidence.Mutate4CSharp.evaluation-report-v2.schema.json")
            ?? throw new InvalidOperationException("Pinned Mutate4CSharp schema is absent.");
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
        if (schema.TryGetProperty("if", out var condition))
        {
            var branch = Accepts(value, condition, path, root) ? "then" : "else";
            if (schema.TryGetProperty(branch, out var consequent)) Check(value, consequent, path, root);
        }
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
        if (value.ValueKind == JsonValueKind.Array)
        {
            var length = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var minimumItems)) Require(length >= minimumItems.GetInt32(), path, "array too short");
            if (schema.TryGetProperty("maxItems", out var maximumItems)) Require(length <= maximumItems.GetInt32(), path, "array too long");
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
            {
                // The pinned schema declares uniqueItems only for the two bounded member
                // arrays of strings. JSON escape spelling is not part of decoded identity.
                Require(length <= 256 && schema.TryGetProperty("items", out var uniqueItems) &&
                    uniqueItems.TryGetProperty("type", out var itemType) && itemType.GetString() == "string",
                    path, "unsupported uniqueness grammar");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in value.EnumerateArray())
                    Require(item.ValueKind == JsonValueKind.String && seen.Add(item.GetString()!), path, "duplicate or non-string member");
            }
            if (schema.TryGetProperty("contains", out var contains))
                Require(value.EnumerateArray().Count(item => Accepts(item, contains, path, root)) >=
                    (schema.TryGetProperty("minContains", out var minimumContains) ? minimumContains.GetInt32() : 1), path, "required array member absent");
            var index = 0;
            if (schema.TryGetProperty("items", out var items))
                foreach (var item in value.EnumerateArray()) Check(item, items, $"{path}[{index++}]", root);
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var minimum))
                Require(text.Length >= minimum.GetInt32(), path, "string too short");
            if (schema.TryGetProperty("maxLength", out var maximumLength))
                Require(text.Length <= maximumLength.GetInt32(), path, "string too long");
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
            if (schema.TryGetProperty("minimum", out var minimum))
                Require(CompareNumbers(value.GetRawText(), minimum.GetRawText()) >= 0, path, "below minimum");
            if (schema.TryGetProperty("maximum", out var maximum))
                Require(CompareNumbers(value.GetRawText(), maximum.GetRawText()) <= 0, path, "above maximum");
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
        "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value.GetRawText()),
        _ => throw new InvalidOperationException("Unsupported vendor schema type.")
    };

    private static bool IsInteger(string token)
    {
        var text = token.AsSpan();
        var exponentIndex = text.IndexOfAny('e', 'E');
        var mantissa = exponentIndex < 0 ? text : text[..exponentIndex];
        var nonzero = false;
        foreach (var character in mantissa)
            if (character is >= '1' and <= '9') nonzero = true;
        if (!nonzero) return true;
        var exponent = 0;
        if (exponentIndex >= 0 && !int.TryParse(text[(exponentIndex + 1)..],
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return false;
        var dot = mantissa.IndexOf('.');
        var fractionDigits = dot < 0 ? 0 : mantissa.Length - dot - 1;
        var neededZeros = (long)fractionDigits - exponent;
        if (neededZeros <= 0) return true;
        long trailingZeros = 0;
        for (var index = mantissa.Length - 1; index >= 0; index--)
        {
            if (mantissa[index] == '.') continue;
            if (mantissa[index] != '0') break;
            trailingZeros++;
        }
        return trailingZeros >= neededZeros;
    }

    private static int CompareNumbers(string leftToken, string rightToken)
    {
        var left = NormalizeNumber(leftToken);
        var right = NormalizeNumber(rightToken);
        if (left.Sign != right.Sign) return left.Sign.CompareTo(right.Sign);
        if (left.Sign == 0) return 0;
        var magnitude = left.Position.CompareTo(right.Position);
        if (magnitude == 0)
        {
            for (var index = 0; index < Math.Max(left.Digits.Length, right.Digits.Length); index++)
            {
                var leftDigit = index < left.Digits.Length ? left.Digits[index] : '0';
                var rightDigit = index < right.Digits.Length ? right.Digits[index] : '0';
                magnitude = leftDigit.CompareTo(rightDigit);
                if (magnitude != 0) break;
            }
        }
        return left.Sign * Math.Sign(magnitude);
    }

    private static (int Sign, string Digits, long Position) NormalizeNumber(string token)
    {
        var text = token.AsSpan();
        var sign = text[0] == '-' ? -1 : 1;
        if (sign < 0) text = text[1..];
        var exponentIndex = text.IndexOfAny('e', 'E');
        var mantissa = exponentIndex < 0 ? text : text[..exponentIndex];
        var dot = mantissa.IndexOf('.');
        var fractionDigits = dot < 0 ? 0 : mantissa.Length - dot - 1;
        var digits = mantissa.ToString().Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return (0, "", 0);
        long exponent = 0;
        if (exponentIndex >= 0)
        {
            var exponentText = text[(exponentIndex + 1)..];
            if (!long.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                exponent = exponentText[0] == '-' ? long.MinValue : long.MaxValue;
        }
        long position;
        try { position = checked(exponent + digits.Length - fractionDigits); }
        catch (OverflowException) { position = exponent < 0 ? long.MinValue : long.MaxValue; }
        return (sign, digits, position);
    }

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
