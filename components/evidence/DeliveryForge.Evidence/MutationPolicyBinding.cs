using System.Globalization;
using System.Text.Json;

namespace DeliveryForge.Evidence;

// Supported report-v2 defaults are the actual producer's ExecutionPolicy.Default /
// CheckConfiguration.ParsePolicy contract (d3fa140d11e19e1ee2e15c7ed922a532a9e73bb1).
// This comparison consumes DATA inside the retained current invocation; it issues no proof.
internal static class MutationPolicyBinding
{
    static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "maxWorkers", "mutationCap", "baselineTimeoutSeconds", "mutantTimeoutSeconds",
        "overallDeadlineSeconds", "allowNotApplicable", "stabilityRepetitions"
    };
    sealed record Policy(int Workers, int Cap, int Baseline, int Mutant, int Overall, bool AllowZero, int Repetitions);

    internal static void RequireSame(JsonElement configuration, JsonElement reportedPolicy)
    {
        if (configuration.ValueKind != JsonValueKind.Object)
            throw new EvidenceWriteException("Mutation configuration must be an object.");
        var configuredPolicy = configuration.TryGetProperty("policy", out var selected) ? selected : default;
        var expected = Read(configuredPolicy, complete: false);
        var actual = Read(reportedPolicy, complete: true);
        if (expected != actual)
            throw new EvidenceWriteException("Mutation execution policy differs from captured approved configuration.");
    }

    static Policy Read(JsonElement value, bool complete)
    {
        if (value.ValueKind != JsonValueKind.Undefined || complete)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new EvidenceWriteException("Mutation policy must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!Fields.Contains(property.Name) || !seen.Add(property.Name))
                    throw new EvidenceWriteException("Unknown or duplicate mutation policy field.");
            if (complete && seen.Count != Fields.Count)
                throw new EvidenceWriteException("Mutation report must contain the complete effective policy.");
        }
        var result = new Policy(Number("maxWorkers", 1), Number("mutationCap", 100),
            Number("baselineTimeoutSeconds", 600), Number("mutantTimeoutSeconds", 120),
            Number("overallDeadlineSeconds", 1800), Boolean("allowNotApplicable", false),
            Number("stabilityRepetitions", 1));
        // Defaults never widen the independently selected issue-17 starting limits.
        // The original host/process deadlines remain separately authoritative.
        if (result.Workers > 1 || result.Cap > 100 || result.Baseline > 600 ||
            result.Mutant > 600 || result.Overall > 600 || result.Repetitions > 100)
            throw new EvidenceWriteException("Mutation policy exceeds the approved one-worker/100-mutant/600-second profile.");
        return result;

        int Number(string name, int fallback)
        {
            if (value.ValueKind == JsonValueKind.Undefined || !value.TryGetProperty(name, out var item)) return fallback;
            if (item.ValueKind != JsonValueKind.Number || !ExactInt32(item.GetRawText(), out var number) || number < 1)
                throw new EvidenceWriteException("Mutation policy numbers must be positive exact Int32 values.");
            return number;
        }
        bool Boolean(string name, bool fallback)
        {
            if (value.ValueKind == JsonValueKind.Undefined || !value.TryGetProperty(name, out var item)) return fallback;
            if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new EvidenceWriteException("Mutation policy flag must be a Boolean.");
            return item.GetBoolean();
        }
    }

    // Match the producer's exact integral JSON-number contract, without rounding
    // a fractional token through double/decimal (including near-integral tokens).
    static bool ExactInt32(string token, out int value)
    {
        value = 0;
        var text = token.AsSpan(); var negative = text[0] == '-'; var start = negative ? 1 : 0;
        var exponentMarker = text.IndexOfAny('e', 'E');
        var mantissaEnd = exponentMarker < 0 ? text.Length : exponentMarker;
        var dot = text[start..mantissaEnd].IndexOf('.');
        dot = dot < 0 ? mantissaEnd : start + dot;
        var fractionalDigits = dot == mantissaEnd ? 0 : mantissaEnd - dot - 1;
        var exponent = 0;
        if (exponentMarker >= 0 && !int.TryParse(text[(exponentMarker + 1)..],
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return false;
        if (exponent is < -100000 or > 100000) return false;
        var digits = text[start..mantissaEnd].ToString().Where(c => c != '.').ToArray();
        var decimalPosition = (long)digits.Length - fractionalDigits + exponent;
        var firstNonZero = Array.FindIndex(digits, c => c != '0');
        if (firstNonZero < 0) return true;
        if (decimalPosition <= firstNonZero) return false;
        for (var index = Math.Max((int)Math.Min(decimalPosition, digits.Length), 0); index < digits.Length; index++)
            if (digits[index] != '0') return false;
        var limit = negative ? 2147483648L : int.MaxValue;
        long magnitude = 0;
        for (long index = 0; index < decimalPosition; index++)
        {
            var digit = index < digits.Length ? digits[index] - '0' : 0;
            if (magnitude > (limit - digit) / 10) return false;
            magnitude = magnitude * 10 + digit;
        }
        value = negative ? (int)-magnitude : (int)magnitude;
        return true;
    }
}
