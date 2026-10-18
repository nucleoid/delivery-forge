using System.Text.Json;

namespace DeliveryForge.Evidence;

// Independent bounded semantic comparison against the actual stock trusted-policy artifact.
// Raw configuration SHA and stock canonical policy SHA are distinct identities.
internal static class CrapPolicyBinding
{
    private static readonly string[] TextFields = ["schemaVersion", "mode", "configuration", "scope", "missingCoverage", "ruleset"];
    private static readonly string[] ArrayFields = ["productionProjects", "testProjects", "targetFrameworks", "requiredChecks", "exclusions", "exemptionFiles"];
    private static readonly string[] Overrides = ["frameworks", "scope", "includeTests", "includeGenerated", "coveragePathMappings"];
    internal static void RequireSame(byte[] rawConfiguration, byte[] actualCanonical)
    {
        using var before = ProducerJson.Read(rawConfiguration);
        using var canonical = ProducerJson.Read(actualCanonical);
        if (Normalize(before.RootElement) != Normalize(canonical.RootElement))
            throw new EvidenceWriteException("Actual canonical Crap policy differs from the exact approved configuration semantics.");
    }
    internal static string Normalize(JsonElement root)
    {
        var allowed = TextFields.Concat(ArrayFields).Concat(new[] { "threshold", "baseline", "allowedOverrides" }).ToHashSet(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new EvidenceWriteException("Unsupported complete Crap policy fields.");
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in TextFields)
        {
            var value = root.GetProperty(name);
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new EvidenceWriteException("Malformed Crap policy scalar.");
            result.Add(name, value.GetString());
        }
        if (result["schemaVersion"] as string != "repository-policy-v1" || result["missingCoverage"] as string != "fail" ||
            result["ruleset"] as string != "callables-v1") throw new EvidenceWriteException("Unsupported Crap policy semantics.");
        var threshold = root.GetProperty("threshold").GetDouble();
        if (!double.IsFinite(threshold) || threshold < 0 || threshold > 30) throw new EvidenceWriteException("Approved CRAP30 ceiling cannot be raised.");
        result.Add("threshold", threshold);
        foreach (var name in ArrayFields)
        {
            if (!root.TryGetProperty(name, out var array))
            {
                if (name != "exemptionFiles") throw new EvidenceWriteException("Missing complete Crap policy set.");
                result.Add(name, Array.Empty<string>()); continue;
            }
            if (array.ValueKind != JsonValueKind.Array || array.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())))
                throw new EvidenceWriteException("Malformed complete Crap policy set.");
            var values = array.EnumerateArray().Select(v => v.GetString()!).Order(StringComparer.Ordinal).ToArray();
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new EvidenceWriteException("Duplicate Crap policy member.");
            result.Add(name, values);
        }
        var checks = (string[])result["requiredChecks"]!;
        if (!new[] { "tests", "coverage", "crap" }.All(c => checks.Contains(c, StringComparer.Ordinal)))
            throw new EvidenceWriteException("Actual policy omits a required quality check.");
        result.Add("baseline", root.TryGetProperty("baseline", out var baseline) ? baseline.GetString() : null);
        var flags = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        if (root.TryGetProperty("allowedOverrides", out var options) &&
            (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any(p => !Overrides.Contains(p.Name, StringComparer.Ordinal))))
            throw new EvidenceWriteException("Unknown policy override.");
        foreach (var name in Overrides)
        {
            var value = false;
            if (options.ValueKind != JsonValueKind.Undefined && options.TryGetProperty(name, out var flag))
            {
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new EvidenceWriteException("Malformed policy override.");
                value = flag.GetBoolean();
            }
            flags.Add(name, value);
        }
        result.Add("allowedOverrides", flags);
        return JsonSerializer.Serialize(result);
    }
}
