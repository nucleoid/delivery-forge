using System.Globalization;
using System.Text.Json;

namespace DeliveryForge.Coordination;

internal static class ContinuumWireJson
{
    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 1 or > 1048576) throw Invalid();
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try
        {
            var pending = new Stack<JsonElement>();
            pending.Push(document.RootElement);
            var nodes = 0;
            while (pending.Count != 0)
            {
                if (++nodes > 8192) throw Invalid();
                var value = pending.Pop();
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (property.Name.Length > 128 || !names.Add(property.Name)) throw Invalid();
                        pending.Push(property.Value);
                    }
                }
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var child in value.EnumerateArray()) pending.Push(child);
            }
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    internal static void Shape(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var permitted = new HashSet<string>(required.Concat(optional), StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!permitted.Contains(property.Name)) throw Invalid();
        foreach (var name in required) if (!value.TryGetProperty(name, out _)) throw Invalid();
    }

    internal static string Text(JsonElement value, string name, int maximum = 512)
    {
        var child = value.GetProperty(name);
        if (child.ValueKind != JsonValueKind.String) throw Invalid();
        var text = child.GetString()!;
        if (text.Length is < 1 || text.Length > maximum || text.Any(char.IsControl)) throw Invalid();
        return text;
    }

    internal static bool Boolean(JsonElement value, string name)
    {
        var child = value.GetProperty(name);
        if (child.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        return child.GetBoolean();
    }

    internal static string Uuid(JsonElement value, string name)
    {
        var text = Text(value, name, 36);
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D")) throw Invalid();
        return text;
    }

    internal static string Fence(JsonElement value)
    {
        var text = Text(value, "fencing_token", 19);
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var token) || token < 1 || text != token.ToString(CultureInfo.InvariantCulture)) throw Invalid();
        return text;
    }

    internal static DateTimeOffset Time(JsonElement value, string name)
    {
        var text = Text(value, name, 40);
        if (!text.EndsWith('Z') || !DateTimeOffset.TryParseExact(text,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) throw Invalid();
        return time;
    }

    internal static CoordinationProtocolException Invalid() => new("Continuum response is malformed, inconsistent or outside its bounded protocol.");
}
