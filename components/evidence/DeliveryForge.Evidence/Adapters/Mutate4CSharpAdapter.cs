using System.Text.Json;

namespace DeliveryForge.Evidence.Adapters;

public sealed class Mutate4CSharpAdapter
{
    public NormalizedEvidence Normalize(ReadOnlySpan<byte> report, ToolCapability capability)
    {
        try
        {
            using var document = JsonDocument.Parse(report.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return NormalizedEvidence.Error("Mutate4CSharp report root is malformed.");
        }
        catch (JsonException exception)
        {
            return NormalizedEvidence.Error($"Malformed Mutate4CSharp report: {exception.Message}");
        }

        return NormalizedEvidence.Incomplete(
            "Mutate4CSharp installed-outcome certification is unavailable; preview evidence is not a usable gate.",
            capability.Fixture,
            "nucleoid/mutate4csharp#5 remains an external acceptance prerequisite");
    }
}
