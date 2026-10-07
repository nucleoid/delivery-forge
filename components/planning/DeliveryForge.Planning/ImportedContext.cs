using System.Text.Json;
using DeliveryForge.Contracts.Serialization;

namespace DeliveryForge.Planning;

public sealed record ImportedContextEnvelope(
    string SchemaVersion,
    IReadOnlyList<ImportedContextEntry> Entries,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Limitations)
{
    private const int MaximumEnvelopeBytes = 256 * 1024;
    private static readonly HashSet<string> EnvelopeFields = ["schemaVersion", "entries", "conflicts", "limitations"];
    private static readonly HashSet<string> EntryFields = ["kind", "locator", "summary", "digest", "observedAt", "stale", "truncated", "heuristic"];

    public static ImportedContextEnvelope Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaximumEnvelopeBytes)
        {
            throw new PlanningException($"Imported context exceeds the {MaximumEnvelopeBytes}-byte portable envelope limit.");
        }

        try
        {
            _ = CanonicalJson.Canonicalize(json);
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            RequireObject(root, "Imported context");
            RejectUnknownFields(root, EnvelopeFields);

            var version = RequireString(root, "schemaVersion");
            if (version != "1.0.0")
            {
                throw new PlanningException($"Unsupported imported-context schemaVersion '{version}'.");
            }

            var entries = RequireArray(root, "entries").EnumerateArray().Select(ParseEntry).ToArray();
            if (entries.Length > 256)
            {
                throw new PlanningException("Imported context exceeds the 256-entry portable envelope limit.");
            }

            return new ImportedContextEnvelope(
                version,
                entries,
                ReadStrings(root, "conflicts"),
                ReadStrings(root, "limitations"));
        }
        catch (PlanningException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new PlanningException($"Imported context is not a valid portable envelope: {exception.Message}");
        }
    }

    private static ImportedContextEntry ParseEntry(JsonElement item)
    {
        RequireObject(item, "Imported context entry");
        RejectUnknownFields(item, EntryFields);
        var kind = PortableText(RequireString(item, "kind"), "kind");
        var locator = PortableText(RequireString(item, "locator"), "locator");
        var summary = PortableText(RequireString(item, "summary"), "summary");
        var digest = item.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind != JsonValueKind.Null
            ? PortableText(digestElement.GetString() ?? string.Empty, "digest")
            : null;
        if (!DateTimeOffset.TryParse(RequireString(item, "observedAt"), out var observedAt) || observedAt.Offset != TimeSpan.Zero)
        {
            throw new PlanningException("Imported context observedAt must be an explicit UTC timestamp.");
        }

        return new ImportedContextEntry(
            kind,
            locator,
            summary,
            digest,
            observedAt,
            OptionalBoolean(item, "stale"),
            OptionalBoolean(item, "truncated"),
            OptionalBoolean(item, "heuristic"));
    }

    private static string PortableText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.IndexOf('\0') >= 0 ||
            value[0] == '/' ||
            (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/')) ||
            value.Contains("-----BEGIN ", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("api_key=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlanningException($"Imported context field '{field}' is not portable or may contain private/secret material.");
        }

        return value;
    }

    private static void RejectUnknownFields(JsonElement element, HashSet<string> allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new PlanningException($"Imported context field '{property.Name}' is outside the portable envelope.");
            }
        }
    }

    private static void RequireObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new PlanningException($"{description} must be a JSON object.");
        }
    }

    private static JsonElement RequireArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new PlanningException($"Imported context '{property}' must be an array.");

    private static string RequireString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new PlanningException($"Imported context '{property}' must be a non-empty string.");

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string property) =>
        RequireArray(element, property).EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? PortableText(item.GetString() ?? string.Empty, property)
                : throw new PlanningException($"Imported context '{property}' must contain strings."))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool OptionalBoolean(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) ? false : value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new PlanningException($"Imported context '{property}' must be boolean.");
}

public static class IntakePlanner
{
    public static IntakeAssessment Assess(
        PlanningRequest request,
        IReadOnlyList<EvidenceItem> evidence,
        ImportedContextEnvelope? importedContext = null,
        EvidenceRequirement importedContextRequirement = EvidenceRequirement.Optional)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);
        var limitations = new List<string>();
        var ready = evidence.Any(item => item.SourceKind is EvidenceSourceKind.Repository or EvidenceSourceKind.Policy && item.IsComplete);

        foreach (var item in evidence)
        {
            limitations.AddRange(item.Caveats);
            if (!item.IsComplete)
            {
                limitations.Add($"Evidence at {item.Locator} is incomplete.");
            }
        }

        if (importedContext is null)
        {
            limitations.Add("Optional imported memory/code-intelligence context is unavailable; repository evidence remains authoritative.");
            ready &= importedContextRequirement == EvidenceRequirement.Optional;
        }
        else
        {
            limitations.AddRange(importedContext.Limitations);
            limitations.AddRange(importedContext.Conflicts.Select(conflict => $"Imported-context conflict: {conflict}"));
            foreach (var entry in importedContext.Entries)
            {
                if (entry.Stale) limitations.Add($"Imported context at {entry.Locator} is stale.");
                if (entry.Truncated) limitations.Add($"Imported context at {entry.Locator} is truncated.");
                if (entry.Heuristic) limitations.Add($"Imported context at {entry.Locator} is heuristic.");
            }
        }

        string? question = null;
        if (!string.IsNullOrWhiteSpace(request.UserOwnedDecision))
        {
            ready = false;
            question = $"I recommend the safest reversible option; should we decide this before planning: {request.UserOwnedDecision.Trim().TrimEnd('?')}?";
        }

        return new IntakeAssessment(
            ready,
            request.Depth,
            question,
            limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
}
