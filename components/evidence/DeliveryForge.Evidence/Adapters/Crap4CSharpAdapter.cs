using System.Text.Json;

namespace DeliveryForge.Evidence.Adapters;

public sealed class Crap4CSharpAdapter
{
    public NormalizedEvidence Normalize(ReadOnlySpan<byte> report, ToolCapability capability)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(report.ToArray());
        }
        catch (JsonException exception)
        {
            return NormalizedEvidence.Error($"Malformed Crap4CSharp report: {exception.Message}");
        }

        using (document)
        {
            if (!capability.Supported)
                return NormalizedEvidence.Incomplete("Crap4CSharp executable capability is not proven.", capability.Fixture);
            var root = document.RootElement;
            var schemaError = Crap4CSharpResultSchema.Validate(root);
            if (schemaError is not null)
                return NormalizedEvidence.Error("Malformed Crap4CSharp result: " + schemaError, capability.Fixture);
            if (!TryString(root, "schemaVersion", out var schemaVersion) ||
                !schemaVersion.StartsWith("1.", StringComparison.Ordinal) ||
                !string.Equals(schemaVersion, capability.FormatVersion, StringComparison.Ordinal))
                return NormalizedEvidence.Error("Unsupported or mismatched Crap4CSharp report format version.", true);
            if (!TryString(root, "toolVersion", out var toolVersion) ||
                !string.Equals(toolVersion, capability.Version, StringComparison.Ordinal))
                return NormalizedEvidence.Error("Crap4CSharp report/tool version mismatch.", capability.Fixture);
            if (!root.TryGetProperty("evaluation", out var evaluation) ||
                evaluation.ValueKind != JsonValueKind.Object ||
                !evaluation.TryGetProperty("decision", out var decision) ||
                decision.ValueKind != JsonValueKind.Object ||
                !decision.TryGetProperty("completed", out var completedElement) ||
                completedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !TryString(decision, "policyDecision", out var policyDecision) ||
                !root.TryGetProperty("run", out var run) || run.ValueKind != JsonValueKind.Object)
                return NormalizedEvidence.Error("Crap4CSharp report is missing required evaluation/run fields.", capability.Fixture);
            if (!evaluation.TryGetProperty("coverage", out var coverage) ||
                coverage.ValueKind != JsonValueKind.Object ||
                !TryInt(coverage, "methods", out var methodCount) ||
                !TryInt(coverage, "known", out var knownCount) ||
                !TryInt(coverage, "unknown", out var unknownCount) ||
                methodCount < 0 || knownCount < 0 || unknownCount < 0 ||
                (long)knownCount + unknownCount > methodCount ||
                evaluation.GetProperty("metrics").GetArrayLength() != methodCount)
                return NormalizedEvidence.Error("Crap4CSharp coverage summary is malformed.", capability.Fixture);
            var metrics = evaluation.GetProperty("metrics").EnumerateArray().ToArray();
            var metricIdentities = new HashSet<string>(StringComparer.Ordinal);
            var callableRuleset = root.GetProperty("complexityRulesetVersion").GetString() == "callables-v1";
            foreach (var metric in metrics)
            {
                var identity = JsonSerializer.Serialize(new
                {
                    context = metric.GetProperty("contextId").GetString(),
                    path = metric.GetProperty("path").GetString(),
                    method = metric.GetProperty("methodIdentity").GetString(),
                    signature = callableRuleset ? null : metric.GetProperty("signature").GetString(),
                    start = callableRuleset ? 0 : metric.GetProperty("span").GetProperty("startLine").GetDouble(),
                    end = callableRuleset ? 0 : metric.GetProperty("span").GetProperty("endLine").GetDouble()
                });
                if (!metricIdentities.Add(identity))
                    return NormalizedEvidence.Error("Crap4CSharp metric inventory contains duplicate identities.", capability.Fixture);
            }
            var observedKnown = metrics.Count(metric => metric.GetProperty("coverage").ValueKind == JsonValueKind.Number);
            if (observedKnown != knownCount || unknownCount > methodCount - observedKnown)
                return NormalizedEvidence.Error("Crap4CSharp coverage counts contradict the metric inventory.", capability.Fixture);
            var contexts = evaluation.GetProperty("contexts").EnumerateArray()
                .Select(context => context.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
            if (metrics.Any(metric => !contexts.Contains(metric.GetProperty("contextId").GetString()!)))
                return NormalizedEvidence.Incomplete("Crap4CSharp metric context is unbound.", capability.Fixture);
            var notApplicable = new HashSet<string>(StringComparer.Ordinal);
            if (root.GetProperty("complexityRulesetVersion").GetString() == "callables-v1" &&
                evaluation.TryGetProperty("callables", out var callables))
            {
                foreach (var callable in callables.EnumerateArray())
                    if (TryString(callable, "callableId", out var id) &&
                        TryString(callable, "applicability", out var applicability) && applicability == "not-applicable" &&
                        TryString(callable, "coverageStatus", out var coverageStatus) && coverageStatus == "not-applicable")
                        notApplicable.Add(id);
            }
            var unscoredNotApplicable = metrics.Count(metric =>
                metric.GetProperty("coverage").ValueKind == JsonValueKind.Null &&
                metric.GetProperty("crap").ValueKind == JsonValueKind.Null &&
                notApplicable.Contains(metric.GetProperty("methodIdentity").GetString()!));
            if (methodCount - knownCount - unknownCount > unscoredNotApplicable)
                return NormalizedEvidence.Incomplete("Crap4CSharp omitted coverage is not accounted for by reported applicability.", capability.Fixture);
            if (!TryInt(run, "exitCode", out var exitCode))
                return NormalizedEvidence.Error("Crap4CSharp run exit is missing.", capability.Fixture);

            var expectedExit = policyDecision switch
            {
                "pass" or "notApplicable" => 0,
                "fail" => 2,
                "unknown" => 1,
                _ => -1
            };
            if (expectedExit < 0 || exitCode != expectedExit)
                return NormalizedEvidence.Error("Crap4CSharp outcome and exit code disagree.", capability.Fixture);

            if (!TryString(run, "status", out var status) ||
                status is not ("completed" or "operationalError" or "cancelled"))
                return NormalizedEvidence.Error("Crap4CSharp run status is malformed.", capability.Fixture);
            var completed = completedElement.ValueKind == JsonValueKind.True;
            var requiredChecks = evaluation.GetProperty("checks").EnumerateArray()
                .Where(check => check.GetProperty("required").GetBoolean()).ToArray();
            var requiredIncomplete = requiredChecks.Any(check =>
                check.GetProperty("status").GetString() is "operationalError" or "cancelled" or "skipped");
            var requiredFailed = requiredChecks.Any(check => check.GetProperty("status").GetString() == "fail");
            if (requiredIncomplete && (completed || policyDecision != "unknown" || status == "completed") ||
                requiredFailed && policyDecision is "pass" or "notApplicable")
                return NormalizedEvidence.Error("Crap4CSharp required checks contradict its decision.", capability.Fixture);
            if (status != "completed" && (completed || policyDecision != "unknown") ||
                !completed && policyDecision != "unknown")
                return NormalizedEvidence.Error("Crap4CSharp decision and run completion disagree.", capability.Fixture);
            if (run.TryGetProperty("cancellation", out var cancellation))
            {
                if (cancellation.ValueKind != JsonValueKind.Object ||
                    !TryBool(cancellation, "timedOut", out var timedOut) ||
                    !TryBool(cancellation, "cancelled", out var cancelled))
                    return NormalizedEvidence.Error("Crap4CSharp cancellation fields are malformed.", capability.Fixture);
                if (timedOut || cancelled)
                {
                    if (completed || policyDecision != "unknown" || status == "completed")
                        return NormalizedEvidence.Error("Crap4CSharp cancellation and decision disagree.", capability.Fixture);
                    return NormalizedEvidence.Incomplete(timedOut
                        ? "Crap4CSharp evaluation timed out."
                        : "Crap4CSharp evaluation was cancelled.", true);
                }
            }
            if (policyDecision == "unknown")
                return NormalizedEvidence.Incomplete("Crap4CSharp returned valid inconclusive evidence.", true);
            if (policyDecision == "pass" && metrics.Any(metric =>
                metric.GetProperty("crap").ValueKind == JsonValueKind.Null &&
                !(metric.GetProperty("coverage").ValueKind == JsonValueKind.Null &&
                    notApplicable.Contains(metric.GetProperty("methodIdentity").GetString()!))))
                return NormalizedEvidence.Incomplete("Crap4CSharp applicable metric has no known score.", capability.Fixture);
            if (unknownCount > 0 && policyDecision != "fail" || methodCount == 0 && policyDecision == "pass")
                return NormalizedEvidence.Incomplete("Crap4CSharp coverage is missing, empty, or contains unmapped callables.", capability.Fixture);

            return policyDecision switch
            {
                "pass" => new NormalizedEvidence(
                    GateOutcome.Pass, "Crap4CSharp completed with a passing policy decision.",
                    true, false, [.. capability.Limitations ?? [], "Installed one-command certification is absent."]),
                "fail" => new NormalizedEvidence(
                    GateOutcome.Fail, "Crap4CSharp completed with policy findings.",
                    true, false, [.. capability.Limitations ?? [], "Installed one-command certification is absent.",
                        .. unknownCount > 0 ? new[] { "Some callable coverage remains unknown; known policy findings still fail." } : Array.Empty<string>()]),
                "unknown" => NormalizedEvidence.Incomplete("Crap4CSharp returned valid inconclusive evidence.", true),
                "notApplicable" when TryString(decision, "reason", out var reason) && !string.IsNullOrWhiteSpace(reason) =>
                    new NormalizedEvidence(
                        GateOutcome.NotApplicable, reason, true, false,
                        capability.Limitations ?? [], reason),
                _ => NormalizedEvidence.Error("Crap4CSharp NOT_APPLICABLE lacks a rationale.", true)
            };
        }
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String)
            return false;
        value = item.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryInt(JsonElement element, string property, out int value)
    {
        value = 0;
        return element.TryGetProperty(property, out var item) &&
            item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out value);
    }

    private static bool TryBool(JsonElement element, string property, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(property, out var item) ||
            item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = item.ValueKind == JsonValueKind.True;
        return true;
    }
}
