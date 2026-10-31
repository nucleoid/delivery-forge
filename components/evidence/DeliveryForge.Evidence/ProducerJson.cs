using System.Text;
using System.Text.Json;

namespace DeliveryForge.Evidence;

internal static class ProducerJson
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal static JsonDocument Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw new JsonException("Named report byte bound exceeded.");
        try { _ = new UTF8Encoding(false, true).GetCharCount(bytes); }
        catch (DecoderFallbackException error) { throw new JsonException("Invalid complete UTF8 report.", error); }
        var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 64 });
        try { var count = 0; Check(document.RootElement, ref count); return document; }
        catch (InvalidOperationException error) { document.Dispose(); throw new JsonException("Invalid complete JSON string.", error); }
        catch { document.Dispose(); throw; }
    }

    private static void Check(JsonElement item, ref int count)
    {
        if (++count > 1000000) throw new JsonException("Aggregate report value bound exceeded.");
        if (item.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name.Length > 128 || !names.Add(property.Name)) throw new JsonException("Duplicate/oversized JSON property.");
                String(property.Name);
                Check(property.Value, ref count);
            }
        }
        else if (item.ValueKind == JsonValueKind.Array)
        {
            if (item.GetArrayLength() > 100000) throw new JsonException("Report array bound exceeded.");
            foreach (var value in item.EnumerateArray()) Check(value, ref count);
        }
        else if (item.ValueKind == JsonValueKind.String) String(item.GetString()!);
    }
    private static void String(string text)
    {
        if (text.Length > 16384) throw new JsonException("Report string bound exceeded.");
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) throw new JsonException("Unpaired JSON surrogate.");
            }
            else if (char.IsLowSurrogate(text[index])) throw new JsonException("Unpaired JSON surrogate.");
        }
    }
}