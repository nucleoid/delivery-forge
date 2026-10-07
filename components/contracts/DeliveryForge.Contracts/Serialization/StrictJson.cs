using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Contracts.Serialization;

internal static class StrictJson
{
    public static void EnsureValid(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetCharCount(utf8Json);

            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
            var objectMembers = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objectMembers.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        objectMembers.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var property = reader.GetString()!;
                        if (!objectMembers.Peek().Add(property))
                        {
                            throw new ContractJsonException($"Duplicate object member '{property}' is not allowed.");
                        }

                        break;
                    case JsonTokenType.String:
                        _ = reader.GetString();
                        break;
                    case JsonTokenType.Number:
                        ValidateNumber(reader.HasValueSequence
                            ? reader.ValueSequence.ToArray()
                            : reader.ValueSpan);
                        break;
                }
            }

            if (reader.BytesConsumed != utf8Json.Length)
            {
                throw new ContractJsonException("The document contains trailing data.");
            }
        }
        catch (ContractJsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or OverflowException or InvalidOperationException)
        {
            throw new ContractJsonException("The document is not strict UTF-8 JSON.", exception);
        }
    }

    private static void ValidateNumber(ReadOnlySpan<byte> lexeme)
    {
        var text = Encoding.UTF8.GetString(lexeme);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            throw new ContractJsonException($"Number '{text}' is outside finite IEEE-754 binary64 range.");
        }
    }
}

public sealed class ContractJsonException : Exception
{
    public ContractJsonException(string message) : base(message) { }
    public ContractJsonException(string message, Exception innerException) : base(message, innerException) { }
}
