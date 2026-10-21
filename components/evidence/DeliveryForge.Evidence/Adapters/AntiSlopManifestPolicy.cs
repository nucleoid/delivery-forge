using System.Text;
using System.Text.Json;

namespace DeliveryForge.Evidence.Adapters;

internal static class AntiSlopManifestPolicy
{
    internal const int MaximumManifestBytes = 8 * 1024 * 1024;
    internal const int MaximumInputCount = 100_000;
    internal const int MaximumInputPathBytes = 4_096;

    internal static void ValidateInputs(IReadOnlyList<string> inputs)
    {
        if (inputs.Count > MaximumInputCount)
            throw new EvaluationPluginException(
                $"Anti-slop input manifest exceeds the {MaximumInputCount}-input ceiling.");

        foreach (var input in inputs)
        {
            if (Encoding.UTF8.GetByteCount(input) > MaximumInputPathBytes)
                throw new EvaluationPluginException(
                    $"Anti-slop input path exceeds the {MaximumInputPathBytes}-byte UTF-8 ceiling.");
        }
    }

    internal static IReadOnlyList<string> ParseSnapshotManifest(ReadOnlySpan<byte> manifestBytes)
    {
        if (manifestBytes.Length > MaximumManifestBytes)
            throw new InvalidOperationException(
                $"Anti-slop input manifest exceeds the {MaximumManifestBytes}-byte ceiling.");

        var reader = new Utf8JsonReader(manifestBytes, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new InvalidOperationException("The anti-slop input manifest is not a JSON string array.");

        var inputs = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new InvalidOperationException("The anti-slop input manifest is not a JSON string array.");
            if (inputs.Count == MaximumInputCount)
                throw new InvalidOperationException(
                    $"Anti-slop input manifest exceeds the {MaximumInputCount}-input ceiling.");
            if (reader.ValueSpan.Length > MaximumInputPathBytes * 6)
                throw new InvalidOperationException(
                    $"Anti-slop input path exceeds the {MaximumInputPathBytes}-byte UTF-8 ceiling.");
            var input = reader.GetString() ??
                throw new InvalidOperationException("The anti-slop input manifest contains a null path.");
            if (Encoding.UTF8.GetByteCount(input) > MaximumInputPathBytes)
                throw new InvalidOperationException(
                    $"Anti-slop input path exceeds the {MaximumInputPathBytes}-byte UTF-8 ceiling.");
            inputs.Add(input);
        }
        if (reader.TokenType != JsonTokenType.EndArray || reader.Read())
            throw new InvalidOperationException("The anti-slop input manifest is malformed.");
        return inputs;
    }
}
