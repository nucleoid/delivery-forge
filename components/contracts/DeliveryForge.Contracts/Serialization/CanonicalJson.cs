using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Contracts.Serialization;

/// <summary>RFC 8785 JSON Canonicalization Scheme operations used by every contract identity.</summary>
public static class CanonicalJson
{
    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json)
    {
        StrictJson.EnsureValid(utf8Json);
        using var document = JsonDocument.Parse(utf8Json.ToArray());
        return Canonicalize(document.RootElement, removeTopLevelIdentity: false);
    }

    public static string ComputeIdentity(ReadOnlySpan<byte> utf8Json)
    {
        StrictJson.EnsureValid(utf8Json);
        using var document = JsonDocument.Parse(utf8Json.ToArray());
        var canonical = Canonicalize(document.RootElement, removeTopLevelIdentity: true);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(canonical))}";
    }

    internal static byte[] Canonicalize(JsonElement element, bool removeTopLevelIdentity)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteElement(output, element, removeTopLevelIdentity, isRoot: true);
        return output.WrittenSpan.ToArray();
    }

    private static void WriteElement(ArrayBufferWriter<byte> writer, JsonElement element, bool removeIdentity, bool isRoot)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteAscii(writer, "{");
                var firstProperty = true;
                foreach (var property in element.EnumerateObject()
                             .Where(property => !(isRoot && removeIdentity && property.NameEquals("identity")))
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        WriteAscii(writer, ",");
                    }

                    WriteString(writer, property.Name);
                    WriteAscii(writer, ":");
                    WriteElement(writer, property.Value, removeIdentity: false, isRoot: false);
                    firstProperty = false;
                }

                WriteAscii(writer, "}");
                break;
            case JsonValueKind.Array:
                WriteAscii(writer, "[");
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        WriteAscii(writer, ",");
                    }

                    WriteElement(writer, item, removeIdentity: false, isRoot: false);
                    firstItem = false;
                }

                WriteAscii(writer, "]");
                break;
            case JsonValueKind.String:
                WriteString(writer, element.GetString()!);
                break;
            case JsonValueKind.Number:
                WriteAscii(writer, FormatNumber(element.GetDouble()));
                break;
            case JsonValueKind.True:
                WriteAscii(writer, "true");
                break;
            case JsonValueKind.False:
                WriteAscii(writer, "false");
                break;
            case JsonValueKind.Null:
                WriteAscii(writer, "null");
                break;
            default:
                throw new ContractJsonException($"Unsupported JSON token {element.ValueKind}.");
        }
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string value)
    {
        WriteAscii(writer, "\"");
        Span<byte> encoded = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case 0x08: WriteAscii(writer, "\\b"); break;
                case 0x09: WriteAscii(writer, "\\t"); break;
                case 0x0a: WriteAscii(writer, "\\n"); break;
                case 0x0c: WriteAscii(writer, "\\f"); break;
                case 0x0d: WriteAscii(writer, "\\r"); break;
                case 0x22: WriteAscii(writer, "\\\""); break;
                case 0x5c: WriteAscii(writer, "\\\\"); break;
                case < 0x20:
                    WriteAscii(writer, $"\\u{rune.Value:x4}");
                    break;
                default:
                    var length = rune.EncodeToUtf8(encoded);
                    writer.Write(encoded[..length]);
                    break;
            }
        }

        WriteAscii(writer, "\"");
    }

    private static void WriteAscii(ArrayBufferWriter<byte> writer, string value) =>
        writer.Write(Encoding.ASCII.GetBytes(value));

    // .NET's round-trip format supplies the shortest binary64 significand. JCS then applies
    // ECMAScript's fixed/scientific thresholds and exponent spelling.
    private static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ContractJsonException("JSON numbers must be finite IEEE-754 binary64 values.");
        }

        if (value == 0)
        {
            return "0";
        }

        var negative = value < 0;
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentMarker = text.IndexOfAny(['E', 'e']);
        var explicitExponent = exponentMarker < 0
            ? 0
            : int.Parse(text[(exponentMarker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = exponentMarker < 0 ? text : text[..exponentMarker];
        var dot = mantissa.IndexOf('.');
        var decimalPosition = (dot < 0 ? mantissa.Length : dot) + explicitExponent;
        var digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);

        var leadingZeroCount = 0;
        while (leadingZeroCount < digits.Length - 1 && digits[leadingZeroCount] == '0')
        {
            leadingZeroCount++;
        }

        if (leadingZeroCount > 0)
        {
            digits = digits[leadingZeroCount..];
            decimalPosition -= leadingZeroCount;
        }

        var scientificExponent = decimalPosition - 1;
        string formatted;
        if (scientificExponent >= -6 && scientificExponent < 21)
        {
            formatted = decimalPosition switch
            {
                <= 0 => $"0.{new string('0', -decimalPosition)}{digits}",
                _ when decimalPosition >= digits.Length => $"{digits}{new string('0', decimalPosition - digits.Length)}",
                _ => $"{digits[..decimalPosition]}.{digits[decimalPosition..]}"
            };
        }
        else
        {
            var significand = digits.Length == 1 ? digits : $"{digits[0]}.{digits[1..]}";
            var exponentSign = scientificExponent >= 0 ? "+" : string.Empty;
            formatted = $"{significand}e{exponentSign}{scientificExponent}";
        }

        return negative ? $"-{formatted}" : formatted;
    }
}
