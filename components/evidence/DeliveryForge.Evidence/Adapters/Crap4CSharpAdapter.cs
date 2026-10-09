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
                return NormalizedEvidence.Incomplete("Unsupported Crap4CSharp report format version.", capability.Fixture);
            if (!TryString(root, "toolVersion", out var toolVersion) ||
                !string.Equals(toolVersion, capability.Version, StringComparison.Ordinal))
                return NormalizedEvidence.Error("Crap4CSharp report/tool version mismatch.", capability.Fixture);
            if (!root.TryGetProperty("evaluation", out var evaluation) ||
                !evaluation.TryGetProperty("decision", out var decision) ||
                !decision.TryGetProperty("completed", out var completedElement) ||
                completedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !TryString(decision, "policyDecision", out var policyDecision) ||
                !root.TryGetProperty("run", out var run))
                return NormalizedEvidence.Error("Crap4CSharp report is missing required evaluation/run fields.", capability.Fixture);
            if (run.TryGetProperty("cancellation", out var cancellation) &&
                cancellation.TryGetProperty("timedOut", out var timedOut) &&
                timedOut.ValueKind == JsonValueKind.True)
                return NormalizedEvidence.Incomplete("Crap4CSharp evaluation timed out.", capability.Fixture);
            if (completedElement.ValueKind != JsonValueKind.True)
                return NormalizedEvidence.Incomplete("Crap4CSharp evaluation is incomplete.", capability.Fixture);
            if (!evaluation.TryGetProperty("coverage", out var coverage) ||
                !coverage.TryGetProperty("methods", out var methods) ||
                !coverage.TryGetProperty("unknown", out var unknown) ||
                !methods.TryGetInt32(out var methodCount) ||
                !unknown.TryGetInt32(out var unknownCount))
                return NormalizedEvidence.Error("Crap4CSharp coverage summary is malformed.", capability.Fixture);
            if (methodCount <= 0 || unknownCount > 0)
                return NormalizedEvidence.Incomplete("Crap4CSharp coverage is missing, empty, or contains unmapped callables.", capability.Fixture);
            if (!run.TryGetProperty("exitCode", out var exitElement) || !exitElement.TryGetInt32(out var exitCode))
                return NormalizedEvidence.Error("Crap4CSharp run exit is missing.", capability.Fixture);

            var expectedExit = policyDecision switch
            {
                "pass" or "notApplicable" => 0,
                "fail" => 1,
                "unknown" => 2,
                _ => -1
            };
            if (expectedExit < 0 || exitCode != expectedExit)
                return NormalizedEvidence.Error("Crap4CSharp outcome and exit code disagree.", capability.Fixture);

            return policyDecision switch
            {
                "pass" => new NormalizedEvidence(
                    GateOutcome.Pass, "Crap4CSharp completed with a passing policy decision.",
                    capability.Fixture, !capability.Fixture, capability.Limitations ?? []),
                "fail" => new NormalizedEvidence(
                    GateOutcome.Fail, "Crap4CSharp completed with policy findings.",
                    capability.Fixture, !capability.Fixture, capability.Limitations ?? []),
                "unknown" => NormalizedEvidence.Incomplete("Crap4CSharp returned valid inconclusive evidence.", capability.Fixture),
                "notApplicable" when TryString(decision, "reason", out var reason) && !string.IsNullOrWhiteSpace(reason) =>
                    new NormalizedEvidence(
                        GateOutcome.NotApplicable, reason, capability.Fixture, !capability.Fixture,
                        capability.Limitations ?? [], reason),
                _ => NormalizedEvidence.Error("Crap4CSharp NOT_APPLICABLE lacks a rationale.", capability.Fixture)
            };
        }
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String)
            return false;
        value = item.GetString() ?? string.Empty;
        return true;
    }
}
