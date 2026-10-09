using System.Text;
using System.Text.Json;

namespace DeliveryForge.Planning.Tests;

public sealed class TypedBoundaryRedesignTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Private_advisory_conflict_text_never_enters_the_frozen_plan()
    {
        const string privateAdvisory = "Project Phoenix is coordinated from attic seven";
        var draft = CreateDraft();
        var imported = new ImportedContextEnvelope(
            "1.0.0",
            [new("memory", "memory:opaque-1", "Bounded private advisory", null, ObservedAt)],
            [privateAdvisory],
            []);
        var intake = IntakePlanner.Assess(draft.Request, draft.Provenance, imported);

        var frozen = PlanFreezer.Freeze(draft with { Intake = intake }, "typed-boundary-red", ObservedAt);

        Assert.DoesNotContain(privateAdvisory, Encoding.UTF8.GetString(frozen.CanonicalBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_private_advisory_projection_slot_is_absent_from_every_exported_collection_and_map()
    {
        const string privateKind = "private-kind-marker";
        const string privateLocator = "opaque:private-locator-marker";
        const string privateSummary = "private-summary-marker";
        const string privateSupersedes = "private-supersedes-marker";
        const string privateConflict = "private-conflict-marker";
        const string privateLimitation = "private-limitation-marker";
        var privateDigest = "sha256:" + new string('d', 64);
        var draft = CreateDraft();
        EvidenceItem[] provenance =
        [
            .. draft.Provenance,
            new(EvidenceSourceKind.Memory, EvidenceProducerKind.PrivateAdvisory, EvidenceLocatorKind.Opaque,
                privateLocator, privateDigest, ObservedAt, [EvidenceCaveatCode.Heuristic], privateSupersedes)
        ];
        var imported = new ImportedContextEnvelope(
            "1.0.0",
            [new(privateKind, privateLocator, privateSummary, privateDigest, ObservedAt,
                Heuristic: true, DistilledMeaning: ImportedMeaningCode.CallerRelationship)],
            [privateConflict],
            [privateLimitation]);
        var intake = IntakePlanner.Assess(draft.Request, provenance, imported);

        var frozen = PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = intake },
            "all-private-slots",
            ObservedAt);

        using var document = JsonDocument.Parse(frozen.CanonicalBytes);
        var exportedStrings = WalkStrings(document.RootElement).ToArray();
        Assert.DoesNotContain(exportedStrings, value => value.Contains(privateKind, StringComparison.Ordinal));
        Assert.DoesNotContain(exportedStrings, value => value.Contains(privateLocator, StringComparison.Ordinal));
        Assert.DoesNotContain(exportedStrings, value => value.Contains(privateSummary, StringComparison.Ordinal));
        Assert.DoesNotContain(exportedStrings, value => value.Contains(privateDigest, StringComparison.Ordinal));
        Assert.DoesNotContain(exportedStrings, value => value.Contains(privateSupersedes, StringComparison.Ordinal));
        Assert.DoesNotContain(exportedStrings, value => value.Contains(privateConflict, StringComparison.Ordinal));
        Assert.DoesNotContain(exportedStrings, value => value.Contains(privateLimitation, StringComparison.Ordinal));
        Assert.DoesNotContain(exportedStrings, value => value == "private-advisory");
        Assert.Contains(exportedStrings, value => value == "heuristic");
        Assert.Contains(exportedStrings, value => value.Contains("raw details remain local-only", StringComparison.Ordinal));
    }

    [Fact]
    public void Portable_evidence_has_no_public_constructor_or_advisory_identity_conversion()
    {
        Assert.Empty(typeof(EvidenceItem).GetConstructors());
        Assert.DoesNotContain(
            typeof(EvidenceItem).GetMethods(),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(ImportedContextEntry) ||
                parameter.ParameterType == typeof(ImportedContextEnvelope)));
    }

    [Fact]
    public void Required_private_meaning_blocks_but_optional_private_context_freezes_as_typed_caveats_only()
    {
        const string privateMarker = "required-private-marker";
        var draft = CreateDraft();
        var required = new ImportedContextEnvelope(
            "1.0.0",
            [new("memory", "private-required-locator", privateMarker, null, ObservedAt,
                Requirement: EvidenceRequirement.Required)],
            [],
            []);
        var requiredIntake = IntakePlanner.Assess(
            draft.Request, draft.Provenance, required, EvidenceRequirement.Required);
        Assert.False(requiredIntake.Ready);
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            draft with { Intake = requiredIntake }, "required-private", ObservedAt));

        var optional = new ImportedContextEnvelope(
            "1.0.0",
            [new("memory", "private-optional-locator", privateMarker, null, ObservedAt,
                Stale: true, Heuristic: true)],
            [],
            []);
        var optionalIntake = IntakePlanner.Assess(draft.Request, draft.Provenance, optional);
        var frozen = PlanFreezer.Freeze(draft with { Intake = optionalIntake }, "optional-private", ObservedAt);
        var json = Encoding.UTF8.GetString(frozen.CanonicalBytes);
        Assert.DoesNotContain(privateMarker, json, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"stale\"", json, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"heuristic\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Verified_public_repository_locator_remains_public_when_the_same_locator_is_imported()
    {
        var draft = CreateDraft();
        var file = new RepositoryFile(
            "README.md", new string('c', 40), "100644", Encoding.UTF8.GetBytes("exact bytes"),
            isSymlink: false, escapesWorktree: false, "not-detected", SymlinkResolution.NotSymlink,
            new string('a', 40), new string('b', 40));
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file.Bytes))}";
        var verified = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry("code-index", "git:README.md", "planning", digest, ObservedAt,
                CheckoutDigest: digest, DistilledMeaning: ImportedMeaningCode.AdditionalRepositoryEvidence),
            file);
        var request = draft.Request with { Depth = IntakeDepth.Deep };
        EvidenceItem[] provenance =
        [
            .. draft.Provenance,
            EvidenceItem.FromRepositoryFile(
                new RepositoryFile(
                    "Directory.Build.props", new string('d', 40), "100644", Encoding.UTF8.GetBytes("other exact bytes"),
                    isSymlink: false, escapesWorktree: false, "not-detected", SymlinkResolution.NotSymlink,
                    new string('a', 40), new string('b', 40)),
                ObservedAt,
                [])
        ];
        var imported = new ImportedContextEnvelope("1.0.0", [verified], [], []);
        var intake = IntakePlanner.Assess(request, provenance, imported);

        var frozen = PlanFreezer.Freeze(
            draft with { Request = request, Provenance = provenance, Intake = intake }, "repository-and-import", ObservedAt);
        var json = Encoding.UTF8.GetString(frozen.CanonicalBytes);
        Assert.True(intake.Ready);
        Assert.Contains("git:README.md", json, StringComparison.Ordinal);
        Assert.DoesNotContain("code-index", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("planning")]
    [InlineData("test")]
    public void Short_common_advisory_values_do_not_false_positive_public_facts(string advisory)
    {
        var draft = CreateDraft();
        var imported = new ImportedContextEnvelope(
            "1.0.0",
            [new("memory", advisory, advisory, null, ObservedAt)],
            [],
            []);
        var intake = IntakePlanner.Assess(draft.Request, draft.Provenance, imported);

        var frozen = PlanFreezer.Freeze(draft with { Intake = intake }, "short-values", ObservedAt);
        Assert.True(frozen.DownstreamReady);
    }

    private static IEnumerable<string> WalkStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var value in WalkStrings(property.Value)) yield return value;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var value in WalkStrings(item)) yield return value;
                break;
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
        }
    }

    private static PlanDraft CreateDraft()
    {
        var request = new PlanningRequest(
            "nucleoid/delivery-forge", "#4", "implement", "Build planning core",
            ["planning"], ["execution"], ["Behavior is deterministic"], "implement");
        var file = new RepositoryFile(
            "README.md", new string('c', 40), "100644", Encoding.UTF8.GetBytes("exact bytes"),
            isSymlink: false, escapesWorktree: false, "not-detected", SymlinkResolution.NotSymlink,
            new string('a', 40), new string('b', 40));
        EvidenceItem[] provenance = [EvidenceItem.FromRepositoryFile(file, ObservedAt, [])];
        var repository = RepositoryContext.Create(
            "/portable/display-only", "HEAD", new string('a', 40), new string('b', 40),
            detachedHead: false, dirty: false, shallow: false, submodules: [], limitations: []);

        return new PlanDraft(
            request,
            repository,
            provenance,
            [new("components/planning/Core.cs", "PlanFreezer", "freeze plans")],
            [new("contracts", [], "issue #3 is integrated")],
            [new("test", new PlanCommand("dotnet", [PlanCommandArgument.Literal("test")]), "all tests pass")],
            new("additive", "none", "none", "none", "none", "none", "test results", "revert commit", []),
            [],
            IntakePlanner.Assess(request, provenance));
    }
}
