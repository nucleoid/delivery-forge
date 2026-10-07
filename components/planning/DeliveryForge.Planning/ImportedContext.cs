using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly HashSet<string> EntryFields = ["kind", "locator", "summary", "digest", "checkoutDigest", "observedAt", "stale", "truncated", "heuristic"];
    private static readonly string[] UtcTimestampFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];

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
            if (entries.Select(entry => (entry.Kind, entry.Locator)).Distinct().Count() != entries.Length)
            {
                throw new PlanningException("Imported context entry kind/locator keys must be unique.");
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
        catch (Exception exception) when (exception is ContractJsonException or JsonException or InvalidOperationException or ArgumentException or FormatException)
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
        ValidateDigest(digest, "digest");
        var checkoutDigest = item.TryGetProperty("checkoutDigest", out var checkoutDigestElement) && checkoutDigestElement.ValueKind != JsonValueKind.Null
            ? PortableText(checkoutDigestElement.GetString() ?? string.Empty, "checkoutDigest")
            : null;
        ValidateDigest(checkoutDigest, "checkoutDigest");
        if (!DateTimeOffset.TryParseExact(
                RequireString(item, "observedAt"),
                UtcTimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var observedAt))
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
            OptionalBoolean(item, "heuristic"),
            CheckoutVerification.Unverified,
            checkoutDigest);
    }

    private static void ValidateDigest(string? digest, string field)
    {
        if (digest is not null &&
            (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
             digest[7..].Any(character => character is < '0' or > '9' && character is < 'a' or > 'f')))
        {
            throw new PlanningException($"Imported context {field} must be lowercase sha256 when supplied.");
        }
    }

    private static string PortableText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.IndexOf('\0') >= 0 ||
            PortableMaterial.ContainsPrivateMaterial(value) ||
            PortableMaterial.IsAbsolutePath(value))
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
        var ready = evidence.Any(item =>
            item.SourceKind is EvidenceSourceKind.Repository or EvidenceSourceKind.Policy && item.IsComplete);

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
            limitations.Add(importedContextRequirement == EvidenceRequirement.Required
                ? "Required imported memory/code-intelligence context is unavailable; readiness is blocked."
                : "Optional imported memory/code-intelligence context is unavailable; repository evidence remains authoritative.");
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
                var verification = ImportedContextVerifier.GetEffectiveVerification(entry);
                if (verification == CheckoutVerification.Unverified)
                    limitations.Add($"Imported context at {entry.Locator} is unverified against exact checkout bytes.");
                if (verification == CheckoutVerification.Verified)
                    limitations.Add($"Imported context at {entry.Locator} was verified against exact checkout bytes.");
                if (verification == CheckoutVerification.Conflict)
                {
                    limitations.Add($"Imported context at {entry.Locator} conflicts with exact checkout bytes.");
                    ready = false;
                }
            }
        }

        string? question = null;
        if (!string.IsNullOrWhiteSpace(request.UserOwnedDecision))
        {
            ready = false;
            question = $"I recommend the safest reversible option; should we decide this before planning: {request.UserOwnedDecision.Trim().TrimEnd('?')}?";
        }

        var normalizedLimitations = limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var importedContextAvailable = importedContext is not null;
        return new IntakeAssessment(
            ready,
            request.Depth,
            question,
            normalizedLimitations,
            importedContextRequirement,
            importedContextAvailable,
            ComputeBinding(request, evidence, importedContextRequirement, importedContextAvailable, normalizedLimitations));
    }

    internal static bool IsBoundTo(
        IntakeAssessment assessment,
        PlanningRequest request,
        IReadOnlyList<EvidenceItem> evidence) =>
        string.Equals(
            assessment.BindingDigest,
            ComputeBinding(
                request,
                evidence,
                assessment.ImportedContextRequirement,
                assessment.ImportedContextAvailable,
                assessment.Limitations),
            StringComparison.Ordinal);

    private static string ComputeBinding(
        PlanningRequest request,
        IReadOnlyList<EvidenceItem> evidence,
        EvidenceRequirement importedContextRequirement,
        bool importedContextAvailable,
        IReadOnlyList<string> limitations)
    {
        var material = new
        {
            request = new
            {
                request.Repository,
                request.WorkItem,
                request.Mode,
                request.Outcome,
                included = request.Included.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                excluded = request.Excluded.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                acceptanceCriteria = request.AcceptanceCriteria.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                request.RequestedCeiling,
                depth = request.Depth.ToString().ToLowerInvariant(),
                request.UserOwnedDecision
            },
            evidence = evidence
                .OrderBy(item => item.SourceKind)
                .ThenBy(item => item.Locator, StringComparer.Ordinal)
                .Select(item => new
                {
                    sourceKind = item.SourceKind.ToString().ToLowerInvariant(),
                    item.Locator,
                    item.Digest,
                    observedAt = item.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    caveats = item.Caveats.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    item.Supersedes,
                    item.IsComplete
                }).ToArray(),
            importedContextRequirement = importedContextRequirement.ToString().ToLowerInvariant(),
            importedContextAvailable,
            limitations = limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        };
        return CanonicalJson.ComputeIdentity(JsonSerializer.SerializeToUtf8Bytes(material));
    }
}

public static class ImportedContextVerifier
{
    public static ImportedContextEntry VerifyAgainst(ImportedContextEntry entry, RepositoryFile exactFile)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(exactFile);
        var expectedPath = entry.Locator.StartsWith("git:", StringComparison.Ordinal)
            ? entry.Locator[4..].Replace('\\', '/')
            : null;
        var actualPath = exactFile.Path.Replace('\\', '/');
        if (entry.CheckoutDigest is null)
        {
            return Bind(entry, CheckoutVerification.Unverified);
        }
        if (!string.Equals(expectedPath, actualPath, StringComparison.Ordinal))
        {
            return Bind(entry, CheckoutVerification.Conflict);
        }

        var exactDigest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(exactFile.Bytes))}";
        return Bind(
            entry,
            string.Equals(entry.CheckoutDigest, exactDigest, StringComparison.Ordinal)
                ? CheckoutVerification.Verified
                : CheckoutVerification.Conflict);
    }

    internal static CheckoutVerification GetEffectiveVerification(ImportedContextEntry entry) =>
        string.Equals(entry.VerificationBinding, ComputeBinding(entry, entry.CheckoutVerification), StringComparison.Ordinal)
            ? entry.CheckoutVerification
            : CheckoutVerification.Unverified;

    private static ImportedContextEntry Bind(ImportedContextEntry entry, CheckoutVerification verification) =>
        entry with
        {
            CheckoutVerification = verification,
            VerificationBinding = ComputeBinding(entry, verification)
        };

    private static string ComputeBinding(ImportedContextEntry entry, CheckoutVerification verification)
    {
        var material = Encoding.UTF8.GetBytes($"{entry.Locator}\0{entry.CheckoutDigest}\0{verification}");
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(material))}";
    }
}
