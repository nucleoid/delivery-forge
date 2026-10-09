using System.Globalization;
using System.Security.Cryptography;
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
    private static readonly HashSet<string> EntryFields = ["kind", "locator", "summary", "digest", "checkoutDigest", "observedAt", "stale", "truncated", "heuristic", "requirement", "distilledMeaning"];
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
        var kind = BoundedAdvisoryText(RequireString(item, "kind"), "kind");
        var locator = BoundedAdvisoryText(RequireString(item, "locator"), "locator");
        if (locator.StartsWith("git:", StringComparison.Ordinal) && locator.Contains('\\'))
        {
            throw new PlanningException("Imported context git: locators must use forward slashes; backslash aliases are not accepted.");
        }
        var summary = BoundedAdvisoryText(RequireString(item, "summary"), "summary");
        var digest = item.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind != JsonValueKind.Null
            ? BoundedAdvisoryText(digestElement.GetString() ?? string.Empty, "digest")
            : null;
        ValidateDigest(digest, "digest");
        var checkoutDigest = item.TryGetProperty("checkoutDigest", out var checkoutDigestElement) && checkoutDigestElement.ValueKind != JsonValueKind.Null
            ? BoundedAdvisoryText(checkoutDigestElement.GetString() ?? string.Empty, "checkoutDigest")
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

        var requirement = item.TryGetProperty("requirement", out var requirementElement)
            ? ParseEnum<EvidenceRequirement>(requirementElement, "requirement")
            : EvidenceRequirement.Optional;
        ImportedMeaningCode? distilledMeaning = item.TryGetProperty("distilledMeaning", out var meaningElement) && meaningElement.ValueKind != JsonValueKind.Null
            ? ParseEnum<ImportedMeaningCode>(meaningElement, "distilledMeaning")
            : null;

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
            checkoutDigest,
            requirement,
            distilledMeaning);
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

    private static string BoundedAdvisoryText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.IndexOf('\0') >= 0 ||
            value.Any(character => char.IsControl(character) && character is not '\t' and not '\n' and not '\r'))
        {
            throw new PlanningException($"Imported context field '{field}' is not a bounded advisory value.");
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
                ? BoundedAdvisoryText(item.GetString() ?? string.Empty, property)
                : throw new PlanningException($"Imported context '{property}' must contain strings."))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool OptionalBoolean(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) ? false : value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new PlanningException($"Imported context '{property}' must be boolean.");

    private static T ParseEnum<T>(JsonElement element, string property) where T : struct, Enum
    {
        if (element.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<T>(element.GetString(), ignoreCase: true, out var value) ||
            !Enum.IsDefined(value))
        {
            throw new PlanningException($"Imported context '{property}' is not recognized.");
        }
        return value;
    }
}

public static class IntakePlanner
{
    private const int MaximumEvidenceItems = 256;

    public static IntakeAssessment Assess(
        PlanningRequest request,
        IReadOnlyList<EvidenceItem> evidence,
        ImportedContextEnvelope? importedContext = null,
        EvidenceRequirement importedContextRequirement = EvidenceRequirement.Optional)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);
        var importedEntryCount = importedContext?.Entries.Count ?? 0;
        if (evidence.Count + importedEntryCount > MaximumEvidenceItems)
        {
            throw new PlanningException($"Planning evidence exceeds the {MaximumEvidenceItems}-item intake limit.");
        }
        if (!IsConcreteSingleLine(request.UserOwnedDecision) ||
            !IsConcreteSingleLine(request.RecommendedOption))
        {
            throw new PlanningException("User-owned decisions and recommendations must each be one concrete non-blank line.");
        }
        if (string.IsNullOrWhiteSpace(request.UserOwnedDecision) != string.IsNullOrWhiteSpace(request.RecommendedOption))
        {
            throw new PlanningException("A genuine user-owned decision and its concrete recommended option must be supplied together.");
        }
        var limitations = new List<string>();
        var privateAdvisoryMaterial = importedContext is null
            ? Array.Empty<string>()
            : importedContext.Entries.SelectMany(entry => new[] { entry.Locator, entry.Summary })
                .Concat(importedContext.Conflicts)
                .Concat(importedContext.Limitations)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        var ready = evidence.Any(IsPinnedReadinessEvidence);
        var verifiedRepositoryIdentities = evidence
            .Where(item => item.IsComplete && item.IsReaderBoundRepositoryEvidence())
            .Select(item => new VerifiedRepositoryIdentity(item.Locator, item.RepositoryCommit!, item.RepositoryTree!))
            .ToList();

        foreach (var item in evidence)
        {
            limitations.AddRange(item.Caveats.Select(CaveatText));
            if (!item.HasConsistentSourceLocator())
            {
                limitations.Add($"Evidence source kind at {item.Locator} is inconsistent with its locator scheme.");
                if (item.Requirement == EvidenceRequirement.Required) ready = false;
            }
            if (item.IsComplete &&
                item.SourceKind is EvidenceSourceKind.Repository or EvidenceSourceKind.Policy &&
                !IsSha256(item.Digest))
            {
                limitations.Add($"Readiness evidence at {item.Locator} lacks an immutable sha256 digest.");
            }
            else if (item.IsComplete && item.SourceKind == EvidenceSourceKind.Repository &&
                     !item.IsReaderBoundRepositoryEvidence())
            {
                limitations.Add($"Repository readiness evidence at {item.Locator} is not bound to an exact reader-issued file/commit/tree.");
            }
            if (!item.IsComplete)
            {
                limitations.Add(item.Requirement == EvidenceRequirement.Required
                    ? "Required typed evidence is incomplete."
                    : "Optional typed evidence is incomplete.");
                if (item.Requirement == EvidenceRequirement.Required) ready = false;
            }
        }

        if (request.Depth == IntakeDepth.Deep)
        {
            if (CountDistinctDeepEvidence(evidence, importedContext) < 2)
            {
                ready = false;
                limitations.Add("Deep intake requires additional bounded repository, policy, or imported evidence beyond minimal intake.");
            }
        }

        var importedContextAvailable = importedContext is { Entries.Count: > 0 };
        if (!importedContextAvailable)
        {
            var empty = importedContext is not null;
            limitations.Add(importedContextRequirement == EvidenceRequirement.Required
                ? empty
                    ? "Required imported memory/code-intelligence context is empty; readiness is blocked."
                    : "Required imported memory/code-intelligence context is unavailable; readiness is blocked."
                : empty
                    ? "Optional imported memory/code-intelligence context is empty; repository evidence remains authoritative."
                    : "Optional imported memory/code-intelligence context is unavailable; repository evidence remains authoritative.");
            ready &= importedContextRequirement == EvidenceRequirement.Optional;
        }
        if (importedContext is not null)
        {
            if (importedContext.Limitations.Count > 0)
                limitations.Add(NoticeText(PlanningNoticeCode.PrivateAdvisoryLimitationReported));
            if (importedContext.Conflicts.Count > 0)
                limitations.Add(NoticeText(PlanningNoticeCode.PrivateAdvisoryConflictReported));
            foreach (var entry in importedContext.Entries)
            {
                if (entry.Stale) limitations.Add(CaveatText(EvidenceCaveatCode.Stale));
                if (entry.Truncated) limitations.Add(CaveatText(EvidenceCaveatCode.Truncated));
                if (entry.Heuristic) limitations.Add(CaveatText(EvidenceCaveatCode.Heuristic));
                if (entry.DistilledMeaning is not null)
                    limitations.Add(MeaningText(entry.DistilledMeaning.Value));
                else if (entry.Requirement == EvidenceRequirement.Required)
                {
                    limitations.Add(CaveatText(EvidenceCaveatCode.RequiredMeaningNotDistillable));
                    ready = false;
                }
                else
                    limitations.Add(NoticeText(PlanningNoticeCode.OptionalPrivateMeaningNotExported));
                var verification = ImportedContextVerifier.GetEffectiveVerification(entry);
                if (verification == CheckoutVerification.Unverified)
                    limitations.Add(CaveatText(EvidenceCaveatCode.UnverifiedCheckout));
                if (verification == CheckoutVerification.Verified)
                {
                    limitations.AddRange(ImportedContextVerifier.GetReaderSafetyCaveats(entry));
                    var identity = ImportedContextVerifier.GetVerifiedRepositoryIdentity(entry);
                    if (identity is not null)
                    {
                        verifiedRepositoryIdentities.Add(identity);
                        limitations.Add($"Imported repository evidence was verified against exact checkout bytes at commit {identity.Commit}, tree {identity.Tree}.");
                    }
                }
                if (verification == CheckoutVerification.Conflict)
                {
                    limitations.Add(CaveatText(EvidenceCaveatCode.CheckoutConflict));
                    ready = false;
                }
            }
        }

        string? question = null;
        if (!string.IsNullOrWhiteSpace(request.UserOwnedDecision))
        {
            ready = false;
            question = $"I recommend {request.RecommendedOption!.Trim().TrimEnd('.')}. Shall we decide this before planning: {request.UserOwnedDecision.Trim().TrimEnd('?')}?";
        }

        var normalizedLimitations = limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new IntakeAssessment(
            ready,
            request.Depth,
            question,
            normalizedLimitations,
            importedContextRequirement,
            importedContextAvailable,
            verifiedRepositoryIdentities,
            privateAdvisoryMaterial,
            ComputeBinding(request, evidence, importedContextRequirement, importedContextAvailable, normalizedLimitations, verifiedRepositoryIdentities, privateAdvisoryMaterial));
    }

    private static bool IsPinnedReadinessEvidence(EvidenceItem item) =>
        item.IsComplete && item.HasConsistentSourceLocator() && IsSha256(item.Digest) &&
        item.ProducerKind != EvidenceProducerKind.PrivateAdvisory &&
        (item.SourceKind == EvidenceSourceKind.Policy ||
         item.SourceKind == EvidenceSourceKind.Repository && item.IsReaderBoundRepositoryEvidence());

    private static bool IsEligibleDeepImportedEvidence(ImportedContextEntry entry) =>
        !entry.Stale &&
        !entry.Truncated &&
        !entry.Heuristic &&
        !string.Equals(entry.Kind, "memory", StringComparison.OrdinalIgnoreCase) &&
        ImportedContextVerifier.GetEffectiveVerification(entry) == CheckoutVerification.Verified &&
        ImportedContextVerifier.IsReaderSafetyEligible(entry);

    private static int CountDistinctDeepEvidence(
        IReadOnlyList<EvidenceItem> evidence,
        ImportedContextEnvelope? importedContext)
    {
        var seenLocators = new HashSet<string>(StringComparer.Ordinal);
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        foreach (var item in evidence.Where(IsPinnedReadinessEvidence)
                     .OrderBy(item => item.Locator, StringComparer.Ordinal))
        {
            var identity = item.SourceKind == EvidenceSourceKind.Repository
                ? $"repository\0{item.Digest}\0{item.RepositoryCommit}\0{item.RepositoryTree}"
                : $"policy\0{item.Digest}";
            if (seenLocators.Contains(item.Locator) || seenIdentities.Contains(identity)) continue;
            seenLocators.Add(item.Locator);
            seenIdentities.Add(identity);
            count++;
        }

        if (importedContext is null) return count;
        foreach (var entry in importedContext.Entries.Where(IsEligibleDeepImportedEvidence)
                     .OrderBy(entry => entry.Locator, StringComparer.Ordinal))
        {
            var repositoryIdentity = ImportedContextVerifier.GetVerifiedRepositoryIdentity(entry)!;
            var identity = $"repository\0{entry.CheckoutDigest}\0{repositoryIdentity.Commit}\0{repositoryIdentity.Tree}";
            if (seenLocators.Contains(entry.Locator) || seenIdentities.Contains(identity)) continue;
            seenLocators.Add(entry.Locator);
            seenIdentities.Add(identity);
            count++;
        }
        return count;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsConcreteSingleLine(string? value)
    {
        if (value is null) return true;
        if (value.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029')) return false;
        return !string.IsNullOrWhiteSpace(value.Trim().Trim('.', '?', '!', ':', ';'));
    }

    internal static string CaveatText(EvidenceCaveatCode code) => code switch
    {
        EvidenceCaveatCode.Stale => "Private advisory evidence is stale.",
        EvidenceCaveatCode.Truncated => "Private advisory evidence is truncated.",
        EvidenceCaveatCode.Heuristic => "Private advisory evidence is heuristic.",
        EvidenceCaveatCode.Incomplete => "Typed evidence is incomplete.",
        EvidenceCaveatCode.UnverifiedCheckout => "Private advisory evidence is unverified against exact checkout bytes.",
        EvidenceCaveatCode.CheckoutConflict => "Private advisory evidence conflicts with exact checkout bytes.",
        EvidenceCaveatCode.UnsafeSymlink => "Repository evidence has an unsafe or unresolved symlink boundary and cannot establish readiness.",
        EvidenceCaveatCode.GeneratedFile => "Repository evidence is generated and cannot establish readiness without authoritative generator provenance.",
        EvidenceCaveatCode.OptionalEvidenceUnavailable => "Optional evidence is unavailable.",
        EvidenceCaveatCode.RequiredMeaningNotDistillable => "Required private advisory meaning cannot be safely distilled; readiness is blocked.",
        _ => throw new PlanningException("Evidence caveat code is not recognized.")
    };

    private static string MeaningText(ImportedMeaningCode code) => code switch
    {
        ImportedMeaningCode.AdditionalRepositoryEvidence => "Private advisory intake identified additional repository evidence.",
        ImportedMeaningCode.RepositoryConflict => "Private advisory intake identified a repository conflict.",
        ImportedMeaningCode.PolicyConstraint => "Private advisory intake identified a policy constraint.",
        ImportedMeaningCode.CallerRelationship => "Private advisory intake identified a caller relationship.",
        _ => throw new PlanningException("Imported meaning code is not recognized.")
    };

    private static string NoticeText(PlanningNoticeCode code) => code switch
    {
        PlanningNoticeCode.PrivateAdvisoryConflictReported => "Private advisory intake reported a conflict; raw details remain local-only.",
        PlanningNoticeCode.PrivateAdvisoryLimitationReported => "Private advisory intake reported one or more limitations; raw details remain local-only.",
        PlanningNoticeCode.OptionalPrivateMeaningNotExported => "Optional private advisory meaning was not exported.",
        _ => throw new PlanningException("Planning notice code is not recognized.")
    };

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
                assessment.Limitations,
                assessment.VerifiedRepositoryIdentities,
                assessment.PrivateAdvisoryMaterial),
            StringComparison.Ordinal);

    private static string ComputeBinding(
        PlanningRequest request,
        IReadOnlyList<EvidenceItem> evidence,
        EvidenceRequirement importedContextRequirement,
        bool importedContextAvailable,
        IReadOnlyList<string> limitations,
        IReadOnlyList<VerifiedRepositoryIdentity> verifiedRepositoryIdentities,
        IReadOnlyList<string> privateAdvisoryMaterial)
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
                request.UserOwnedDecision,
                request.RecommendedOption
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
                    caveats = item.Caveats.Distinct().Order().ToArray(),
                    item.Supersedes,
                    item.IsComplete,
                    requirement = item.Requirement.ToString().ToLowerInvariant(),
                    item.RepositoryCommit,
                    item.RepositoryTree,
                    item.RepositoryIsSymlink,
                    item.RepositorySymlinkResolution,
                    item.RepositoryGenerationClassification
                }).ToArray(),
            importedContextRequirement = importedContextRequirement.ToString().ToLowerInvariant(),
            importedContextAvailable,
            verifiedRepositoryIdentities = verifiedRepositoryIdentities
                .OrderBy(item => item.Locator, StringComparer.Ordinal)
                .Select(item => new { item.Locator, item.Commit, item.Tree })
                .ToArray(),
            limitations = limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            privateAdvisoryDigests = privateAdvisoryMaterial
                .Select(value => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))}")
                .Order(StringComparer.Ordinal)
                .ToArray()
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
            ? entry.Locator[4..]
            : null;
        var actualPath = exactFile.Path;
        if (entry.CheckoutDigest is null)
        {
            return Bind(entry, CheckoutVerification.Unverified, exactFile);
        }
        if (expectedPath is null || expectedPath.Contains('\\') ||
            !string.Equals(expectedPath, actualPath, StringComparison.Ordinal))
        {
            return Bind(entry, CheckoutVerification.Conflict, exactFile);
        }

        var exactDigest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(exactFile.Bytes))}";
        return Bind(
            entry,
            string.Equals(entry.CheckoutDigest, exactDigest, StringComparison.Ordinal)
                ? CheckoutVerification.Verified
                : CheckoutVerification.Conflict,
            exactFile);
    }

    internal static CheckoutVerification GetEffectiveVerification(ImportedContextEntry entry) =>
        string.Equals(entry.VerificationBinding, ComputeBinding(entry, entry.CheckoutVerification), StringComparison.Ordinal)
            ? entry.CheckoutVerification
            : CheckoutVerification.Unverified;

    internal static VerifiedRepositoryIdentity? GetVerifiedRepositoryIdentity(ImportedContextEntry entry) =>
        GetEffectiveVerification(entry) == CheckoutVerification.Verified &&
        entry.VerifiedCommit is not null && entry.VerifiedTree is not null
            ? new VerifiedRepositoryIdentity(entry.Locator, entry.VerifiedCommit, entry.VerifiedTree)
            : null;

    internal static bool IsReaderSafetyEligible(ImportedContextEntry entry) =>
        entry.VerifiedSymlinkResolution is SymlinkResolution.NotSymlink or SymlinkResolution.InTree &&
        entry.VerifiedGenerationClassification is not null &&
        entry.VerifiedGenerationClassification.StartsWith("not-detected", StringComparison.Ordinal);

    internal static IReadOnlyList<string> GetReaderSafetyCaveats(ImportedContextEntry entry)
    {
        var caveats = new List<string>();
        if (entry.VerifiedSymlinkResolution is not (SymlinkResolution.NotSymlink or SymlinkResolution.InTree))
        {
            caveats.Add($"Repository symlink safety is {entry.VerifiedSymlinkResolution}; it cannot count toward deep readiness.");
        }
        if (entry.VerifiedGenerationClassification is not null &&
            !entry.VerifiedGenerationClassification.StartsWith("not-detected", StringComparison.Ordinal))
        {
            caveats.Add($"Repository file generation classification is {entry.VerifiedGenerationClassification}; it cannot count toward deep readiness without authoritative generator provenance.");
        }
        return caveats;
    }

    private static ImportedContextEntry Bind(
        ImportedContextEntry entry,
        CheckoutVerification verification,
        RepositoryFile exactFile)
    {
        var bound = entry with
        {
            CheckoutVerification = verification,
            VerifiedCommit = exactFile.Commit,
            VerifiedTree = exactFile.Tree,
            VerifiedSymlinkResolution = exactFile.SymlinkResolution,
            VerifiedGenerationClassification = exactFile.GenerationClassification
        };
        return bound with { VerificationBinding = ComputeBinding(bound, verification) };
    }

    private static string ComputeBinding(ImportedContextEntry entry, CheckoutVerification verification)
    {
        var material = JsonSerializer.SerializeToUtf8Bytes(new
        {
            entry.Kind,
            entry.Locator,
            entry.Summary,
            entry.Digest,
            entry.ObservedAt,
            entry.Stale,
            entry.Truncated,
            entry.Heuristic,
            entry.CheckoutDigest,
            entry.Requirement,
            entry.DistilledMeaning,
            verification,
            entry.VerifiedCommit,
            entry.VerifiedTree,
            entry.VerifiedSymlinkResolution,
            entry.VerifiedGenerationClassification
        });
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(material))}";
    }
}
