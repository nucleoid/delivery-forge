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
                (long)knownCount + unknownCount != methodCount)
                return NormalizedEvidence.Error("Crap4CSharp coverage summary is malformed.", capability.Fixture);
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
