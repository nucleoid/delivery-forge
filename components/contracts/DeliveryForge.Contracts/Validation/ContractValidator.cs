using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeliveryForge.Contracts.Serialization;

namespace DeliveryForge.Contracts.Validation;

public sealed class ValidatedContract
{
    private readonly byte[] _canonicalBytes;

    internal ValidatedContract(string schemaName, string schemaVersion, string identity, byte[] canonicalBytes)
    {
        SchemaName = schemaName;
        SchemaVersion = schemaVersion;
        Identity = identity;
        _canonicalBytes = canonicalBytes.ToArray();
    }

    public string SchemaName { get; }
    public string SchemaVersion { get; }
    public string Identity { get; }
    public ReadOnlyMemory<byte> CanonicalBytes => _canonicalBytes.ToArray();
}

public static partial class ContractValidator
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonDocument>> Schemas = new(LoadSchemas);

    public static ValidatedContract ParseAndValidate(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            StrictJson.EnsureValid(utf8Json);
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = 128 });
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
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
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
                EnsureSupportedSchemaGrammar(schema.RootElement, "$schema");
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

        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var childSchema in allOf.EnumerateArray())
            {
                ValidateAgainstSchema(value, childSchema, path, rootSchema);
            }
        }

        if (schema.TryGetProperty("if", out var condition) && MatchesSchema(value, condition, path, rootSchema) &&
            schema.TryGetProperty("then", out var consequence))
        {
            ValidateAgainstSchema(value, consequence, path, rootSchema);
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
                    if (!distinct.Add(Convert.ToBase64String(CanonicalJson.Canonicalize(item, removeTopLevelIdentity: false))))
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

            if (schema.TryGetProperty("pattern", out var pattern) &&
                !Regex.IsMatch(text, $"(?:{pattern.GetString()!})\\z", RegexOptions.CultureInvariant))
            {
                throw new ContractValidationException($"{path} does not match its required pattern.");
            }

            if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time" &&
                (!UtcTimestamp().IsMatch(text) || !DateTimeOffset.TryParseExact(
                    text,
                    ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out _)))
            {
                throw new ContractValidationException($"{path} must be a UTC RFC 3339 date-time ending in Z.");
            }
        }

        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out var minimum) &&
            value.GetDouble() < minimum.GetDouble())
        {
            throw new ContractValidationException($"{path} is below its minimum.");
        }

        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("maximum", out var maximum) &&
            value.GetDouble() > maximum.GetDouble())
        {
            throw new ContractValidationException($"{path} is above its maximum.");
        }
    }

    private static bool MatchesSchema(JsonElement value, JsonElement schema, string path, JsonElement rootSchema)
    {
        try
        {
            ValidateAgainstSchema(value, schema, path, rootSchema);
            return true;
        }
        catch (ContractValidationException)
        {
            return false;
        }
    }

    private static void EnsureSupportedSchemaGrammar(JsonElement schema, string path)
    {
        if (schema.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in schema.EnumerateArray())
            {
                EnsureSupportedSchemaGrammar(child, $"{path}[{index++}]");
            }

            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string[] annotations = ["$schema", "$id", "title", "x-contract-kind"];
        string[] supported = ["$ref", "$defs", "type", "additionalProperties", "required", "properties", "const", "enum", "allOf", "if", "then", "minItems", "uniqueItems", "items", "minLength", "pattern", "format", "minimum", "maximum"];
        if (schema.TryGetProperty("$ref", out _) && schema.EnumerateObject().Count() != 1)
        {
            throw new InvalidOperationException($"JSON Schema references may not have ignored sibling keywords at {path}.");
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (!annotations.Contains(property.Name, StringComparer.Ordinal) &&
                !supported.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported JSON Schema keyword '{property.Name}' at {path}.");
            }

            if (property.Name is "$defs" or "properties")
            {
                foreach (var namedSchema in property.Value.EnumerateObject())
                {
                    EnsureSupportedSchemaGrammar(namedSchema.Value, $"{path}.{property.Name}.{namedSchema.Name}");
                }
            }
            else if (property.Name == "additionalProperties" && property.Value.ValueKind == JsonValueKind.Object)
            {
                EnsureSupportedSchemaGrammar(property.Value, $"{path}.additionalProperties");
            }
            else if (property.Name is "items" or "if" or "then" or "allOf")
            {
                EnsureSupportedSchemaGrammar(property.Value, $"{path}.{property.Name}");
            }
            else if (property.Name == "format" && property.Value.GetString() != "date-time")
            {
                throw new InvalidOperationException($"Unsupported JSON Schema format '{property.Value.GetString()}' at {path}.");
            }
        }
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && decimal.Truncate(number) == number,
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

        if (kind == "gate-receipt" && RequireString(root, "outcome") == "PASS")
        {
            if (root.GetProperty("sourceChanged").GetBoolean())
            {
                throw new ContractValidationException("A PASS gate receipt cannot set sourceChanged=true.");
            }
            if (!root.GetProperty("exitCode").TryGetDecimal(out var exitCode) || exitCode != 0)
            {
                throw new ContractValidationException("A PASS gate receipt requires exitCode=0.");
            }
        }
    }

    private static string RequireString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new ContractValidationException($"$.{propertyName} is required and must be a string.");

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SemVer();

    [GeneratedRegex("^sha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identity();

    [GeneratedRegex("^[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\\.[0-9]{1,7})?Z\\z", RegexOptions.CultureInvariant)]
    private static partial Regex UtcTimestamp();
}

public sealed class ContractValidationException : Exception
{
    public ContractValidationException(string message) : base(message) { }
    public ContractValidationException(string message, Exception innerException) : base(message, innerException) { }
}
