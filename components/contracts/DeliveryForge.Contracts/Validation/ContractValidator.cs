using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeliveryForge.Contracts.Serialization;

namespace DeliveryForge.Contracts.Validation;

public sealed record ValidatedContract(string SchemaName, string SchemaVersion, string Identity, byte[] CanonicalBytes);

public static partial class ContractValidator
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonDocument>> Schemas = new(LoadSchemas);

    public static ValidatedContract ParseAndValidate(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            StrictJson.EnsureValid(utf8Json);
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ContractValidationException("A contract must be a JSON object.");
            }

            var root = document.RootElement;
            var kind = RequireString(root, "kind");
            var schemaVersion = RequireString(root, "schemaVersion");
            if (!SemVer().IsMatch(schemaVersion))
            {
                throw new ContractValidationException("schemaVersion must be semantic version major.minor.patch.");
            }

            var key = $"{kind}@{schemaVersion}";
            if (!Schemas.Value.TryGetValue(key, out var schema))
            {
                throw new ContractValidationException($"Unsupported kind/schemaVersion combination '{key}'.");
            }

            ValidateAgainstSchema(root, schema.RootElement, "$", schema.RootElement);
            ValidateCrossFieldRules(kind, root);

            var identity = RequireString(root, "identity");
            if (!Identity().IsMatch(identity))
            {
                throw new ContractValidationException("identity must be sha256 followed by 64 lowercase hexadecimal characters.");
            }

            var actualIdentity = CanonicalJson.ComputeIdentity(utf8Json);
            if (!string.Equals(identity, actualIdentity, StringComparison.Ordinal))
            {
                throw new ContractValidationException($"Identity mismatch: expected '{identity}', computed '{actualIdentity}'.");
            }

            return new ValidatedContract(kind, schemaVersion, identity, CanonicalJson.Canonicalize(utf8Json));
        }
        catch (ContractValidationException)
        {
            throw;
        }
        catch (ContractJsonException exception)
        {
            throw new ContractValidationException(exception.Message, exception);
        }
        catch (JsonException exception)
        {
            throw new ContractValidationException("Invalid contract JSON.", exception);
        }
    }

    private static IReadOnlyDictionary<string, JsonDocument> LoadSchemas()
    {
        var assembly = typeof(ContractValidator).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".schema.json", StringComparison.Ordinal))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Embedded schema '{name}' is missing.");
                var schema = JsonDocument.Parse(stream);
                var kind = RequireString(schema.RootElement, "x-contract-kind");
                var version = schema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetString()!;
                return (Key: $"{kind}@{version}", Schema: schema);
            })
            .ToDictionary(item => item.Key, item => item.Schema, StringComparer.Ordinal);
    }

    private static void ValidateAgainstSchema(JsonElement value, JsonElement schema, string path, JsonElement rootSchema)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var refPath = reference.GetString()!;
            if (!refPath.StartsWith("#/$defs/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported schema reference '{refPath}'.");
            }

            ValidateAgainstSchema(value, rootSchema.GetProperty("$defs").GetProperty(refPath[8..]), path, rootSchema);
            return;
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var validType = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Any(candidate => MatchesType(value, candidate.GetString()!))
                : MatchesType(value, type.GetString()!);
            if (!validType)
            {
                throw new ContractValidationException($"{path} has the wrong JSON type.");
            }
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(value, constant))
        {
            throw new ContractValidationException($"{path} must equal {constant.GetRawText()}.");
        }

        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(value, choice)))
        {
            throw new ContractValidationException($"{path} contains an unknown enum value.");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var requiredName in required.EnumerateArray().Select(item => item.GetString()!))
                {
                    if (!value.TryGetProperty(requiredName, out _))
                    {
                        throw new ContractValidationException($"{path}.{requiredName} is required.");
                    }
                }
            }

            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    ValidateAgainstSchema(property.Value, propertySchema, $"{path}.{property.Name}", rootSchema);
                }
                else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                {
                    throw new ContractValidationException($"{path}.{property.Name} is not a recognized field.");
                }
                else if (additional.ValueKind == JsonValueKind.Object)
                {
                    ValidateAgainstSchema(property.Value, additional, $"{path}.{property.Name}", rootSchema);
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var minItems) && value.GetArrayLength() < minItems.GetInt32())
            {
                throw new ContractValidationException($"{path} has too few items.");
            }

            if (schema.TryGetProperty("uniqueItems", out var uniqueItems) && uniqueItems.GetBoolean())
            {
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in value.EnumerateArray())
                {
                    if (!distinct.Add(item.GetRawText()))
                    {
                        throw new ContractValidationException($"{path} must contain unique items.");
                    }
                }
            }

            if (schema.TryGetProperty("items", out var itemSchema))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    ValidateAgainstSchema(item, itemSchema, $"{path}[{index++}]", rootSchema);
                }
            }
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var minLength) && text.Length < minLength.GetInt32())
            {
                throw new ContractValidationException($"{path} is too short.");
            }

            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant))
            {
                throw new ContractValidationException($"{path} does not match its required pattern.");
            }

            if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time" &&
                (!DateTimeOffset.TryParse(text, out var parsed) || !text.EndsWith('Z') || parsed.Offset != TimeSpan.Zero))
            {
                throw new ContractValidationException($"{path} must be a UTC RFC 3339 date-time ending in Z.");
            }
        }

        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out var minimum) &&
            value.GetDouble() < minimum.GetDouble())
        {
            throw new ContractValidationException($"{path} is below its minimum.");
        }
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new InvalidOperationException($"Unsupported schema type '{type}'.")
    };

    private static void ValidateCrossFieldRules(string kind, JsonElement root)
    {
        if (kind == "gate-receipt" && RequireString(root, "outcome") == "NOT_APPLICABLE" &&
            (!root.TryGetProperty("notApplicableRationale", out var rationale) || string.IsNullOrWhiteSpace(rationale.GetString())))
        {
            throw new ContractValidationException("NOT_APPLICABLE requires notApplicableRationale.");
        }

        if (kind == "gate-receipt" && RequireString(root, "outcome") == "PASS" &&
            root.GetProperty("sourceChanged").GetBoolean())
        {
            throw new ContractValidationException("A PASS gate receipt cannot set sourceChanged=true.");
        }
    }

    private static string RequireString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new ContractValidationException($"$.{propertyName} is required and must be a string.");

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SemVer();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identity();
}

public sealed class ContractValidationException : Exception
{
    public ContractValidationException(string message) : base(message) { }
    public ContractValidationException(string message, Exception innerException) : base(message, innerException) { }
}
