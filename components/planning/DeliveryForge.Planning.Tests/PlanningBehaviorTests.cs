using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Planning.Tests;

public sealed class PlanningBehaviorTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 7, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Minimal_intake_resolves_ordinary_engineering_choices_without_a_question()
    {
        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()]);

        Assert.True(assessment.Ready);
        Assert.Equal(IntakeDepth.Minimal, assessment.Depth);
        Assert.Null(assessment.RecommendedQuestion);
    }

    [Fact]
    public void User_owned_choice_blocks_with_one_recommended_conversational_question()
    {
        var assessment = IntakePlanner.Assess(Request() with { UserOwnedDecision = "Choose whether v1 may delete user data" }, [RepositoryEvidence()]);

        Assert.False(assessment.Ready);
        Assert.Contains("recommend", assessment.RecommendedQuestion!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\n', assessment.RecommendedQuestion!);
    }

    [Fact]
    public void Deep_intake_is_only_enabled_explicitly()
    {
        var assessment = IntakePlanner.Assess(Request() with { Depth = IntakeDepth.Deep }, [RepositoryEvidence()]);
        Assert.Equal(IntakeDepth.Deep, assessment.Depth);
    }

    [Fact]
    public void Optional_absent_imported_context_degrades_with_a_limitation()
    {
        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()], importedContext: null);
        Assert.True(assessment.Ready);
        Assert.Contains(assessment.Limitations, item => item.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Required_absent_imported_context_blocks()
    {
        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()], null, EvidenceRequirement.Required);
        Assert.False(assessment.Ready);
    }

    [Fact]
    public void Stale_truncated_and_conflicting_context_retains_every_caveat()
    {
        var envelope = new ImportedContextEnvelope(
            "1.0.0",
            [new("memory", "memory:item-1", "Advisory summary", "sha256:" + new string('a', 64), ObservedAt, Stale: true, Truncated: true, Heuristic: true)],
            ["Index and checkout disagree"],
            ["Search was bounded"]);

        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()], envelope);

        Assert.Contains(assessment.Limitations, item => item.Contains("stale", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(assessment.Limitations, item => item.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(assessment.Limitations, item => item.Contains("conflict", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(assessment.Limitations, item => item.Contains("heuristic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Imported_context_rejects_secret_bearing_or_host_specific_fields()
    {
        var json = Encoding.UTF8.GetBytes("""
            {"schemaVersion":"1.0.0","entries":[],"conflicts":[],"limitations":[],"apiKey":"secret","hostPath":"/home/person/private"}
            """);

        var error = Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(json));
        Assert.Contains("portable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Imported_context_parses_only_bounded_advisory_summaries()
    {
        var json = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion":"1.0.0",
              "entries":[{
                "kind":"code-index",
                "locator":"engram:result-1",
                "summary":"Callers may include PlanFreezer",
                "digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "observedAt":"2026-10-07T20:00:00Z",
                "heuristic":true
              }],
              "conflicts":[],
              "limitations":["Checkout verification required"]
            }
            """);

        var envelope = ImportedContextEnvelope.Parse(json);

        Assert.Single(envelope.Entries);
        Assert.True(envelope.Entries[0].Heuristic);
        Assert.Equal("Checkout verification required", Assert.Single(envelope.Limitations));
    }

    [Fact]
    public void Imported_context_rejects_secret_material_inside_allowed_fields()
    {
        var json = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion":"1.0.0",
              "entries":[{
                "kind":"memory",
                "locator":"memory:item-1",
                "summary":"Bearer do-not-copy-this",
                "digest":null,
                "observedAt":"2026-10-07T20:00:00Z"
              }],
              "conflicts":[],
              "limitations":[]
            }
            """);

        Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(json));
    }

    [Fact]
    public void Empty_imported_search_is_not_proof_that_repository_evidence_is_absent()
    {
        var envelope = new ImportedContextEnvelope("1.0.0", [], [], ["No imported matches"]);
        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()], envelope);
        Assert.True(assessment.Ready);
        Assert.DoesNotContain(assessment.Limitations, item => item.Contains("repository evidence is absent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_required_plan_sections_and_blocking_unknowns_prevent_freeze()
    {
        var incomplete = Draft() with { ChangeMap = [], Unknowns = [new("Product decision", "user", true)] };
        var error = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(incomplete, "revision-1", ObservedAt));
        Assert.Contains("change map", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frozen_public_plan_rejects_host_paths_and_credentials()
    {
        var privateDraft = Draft() with
        {
            Provenance = [RepositoryEvidence("/home/person/private-index"), new(
                EvidenceSourceKind.Imported,
                "memory:item-1",
                null,
                ObservedAt,
                ["Bearer do-not-publish"])]
        };

        var error = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(privateDraft, "revision-private", ObservedAt));
        Assert.Contains("private material", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reordered_semantically_equivalent_inputs_freeze_reproducibly()
    {
        var first = Draft();
        var second = first with
        {
            Provenance = first.Provenance.Reverse().ToArray(),
            ChangeMap = first.ChangeMap.Reverse().ToArray(),
            Gates = first.Gates.Reverse().ToArray(),
            Request = first.Request with { Included = first.Request.Included.Reverse().ToArray() }
        };

        var frozenA = PlanFreezer.Freeze(first, "revision-1", ObservedAt);
        var frozenB = PlanFreezer.Freeze(second, "revision-1", ObservedAt);

        Assert.Equal(frozenA.Identity, frozenB.Identity);
        Assert.Equal(frozenA.CanonicalBytes, frozenB.CanonicalBytes);
        using var document = JsonDocument.Parse(frozenA.CanonicalBytes);
        Assert.Equal(frozenA.ContractIdentity, document.RootElement.GetProperty("contractIdentity").GetString());
    }

    [Fact]
    public void Material_revision_changes_identity_and_base_drift_invalidates_readiness()
    {
        var frozen = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
        var revised = PlanFreezer.Freeze(Draft() with { Request = Request() with { Outcome = "A materially different outcome" } }, "revision-2", ObservedAt);

        Assert.NotEqual(frozen.Identity, revised.Identity);
        Assert.True(frozen.DownstreamReady);

        var drifted = frozen.ReconcileBase(new string('d', 40), new string('e', 40));
        Assert.False(drifted.DownstreamReady);
        Assert.Contains(drifted.Limitations, item => item.Contains("drift", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Freeze_rejects_an_unresolved_user_owned_decision()
    {
        var draft = Draft() with
        {
            Request = Request() with { UserOwnedDecision = "Choose whether v1 may delete user data" }
        };

        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(draft, "revision-1", ObservedAt));
    }

    [Fact]
    public void Material_plan_content_changes_the_downstream_contract_identity()
    {
        var first = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
        var second = PlanFreezer.Freeze(
            Draft() with { Gates = [new("test", "dotnet test --configuration Release", "all tests pass")] },
            "revision-1",
            ObservedAt);

        Assert.NotEqual(first.ContractIdentity, second.ContractIdentity);
    }

    [Fact]
    public void Freeze_is_invariant_under_the_ambient_culture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var thai = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);

            Assert.Equal(invariant.Identity, thai.Identity);
            Assert.Equal(invariant.CanonicalBytes, thai.CanonicalBytes);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Planning_boundaries_wrap_contract_validation_failures()
    {
        var malformed = Encoding.UTF8.GetBytes("""
            {"schemaVersion":"1.0.0","schemaVersion":"1.0.0","entries":[],"conflicts":[],"limitations":[]}
            """);
        Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(malformed));

        var invalidMode = Draft() with { Request = Request() with { Mode = "deliver" } };
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(invalidMode, "revision-1", ObservedAt));

        var blankScope = Draft() with { Request = Request() with { Included = [" "] } };
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(blankScope, "revision-1", ObservedAt));
    }

    [Theory]
    [InlineData("C:/Users/alice/private-index")]
    [InlineData("C:\\Users\\alice\\private-index")]
    [InlineData("found in /Users/alice/private-index")]
    [InlineData("found in /root/private-index")]
    [InlineData("found in /tmp/private-index")]
    [InlineData("found in /var/private-index")]
    public void Portable_plan_rejects_cross_platform_host_paths(string privateText)
    {
        var draft = Draft() with { Provenance = [RepositoryEvidence(privateText)] };
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(draft, "revision-1", ObservedAt));
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(Draft(), privateText, ObservedAt));
    }

    [Theory]
    [InlineData("found in /home/alice/.ssh/config")]
    [InlineData("found in C:/Users/alice/private-index")]
    [InlineData("password=do-not-copy")]
    [InlineData("token=do-not-copy")]
    public void Imported_context_rejects_embedded_private_material(string summary)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            entries = new[] { new { kind = "memory", locator = "memory:item-1", summary, digest = (string?)null, observedAt = "2026-10-07T20:00:00Z" } },
            conflicts = Array.Empty<string>(),
            limitations = Array.Empty<string>()
        }));

        Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(json));
    }

    [Theory]
    [InlineData("2026-10-07T20:00:00")]
    [InlineData("07/10/2026 20:00Z")]
    [InlineData("2026-10-07T20:00:00+00:00")]
    public void Imported_context_requires_the_explicit_invariant_Z_timestamp(string observedAt)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            entries = new[] { new { kind = "memory", locator = "memory:item-1", summary = "summary", digest = (string?)null, observedAt } },
            conflicts = Array.Empty<string>(),
            limitations = Array.Empty<string>()
        }));

        Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(json));
    }

    [Fact]
    public void Duplicate_semantic_keys_fail_closed()
    {
        var duplicateGate = Draft() with
        {
            Gates = [new("test", "dotnet test", "passes"), new("test", "dotnet test -c Release", "passes in Release")]
        };
        var duplicateProvenance = Draft() with
        {
            Provenance = [RepositoryEvidence("git:README.md"), RepositoryEvidence("git:README.md") with { Digest = "sha256:" + new string('c', 64) }]
        };
        var duplicateChange = Draft() with
        {
            ChangeMap = [new("README.md", "doc", "first"), new("README.md", "doc", "second")]
        };

        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(duplicateGate, "revision-1", ObservedAt));
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(duplicateProvenance, "revision-1", ObservedAt));
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(duplicateChange, "revision-1", ObservedAt));
    }

    [Fact]
    public void Imported_context_caveats_cannot_disappear_before_freeze()
    {
        var envelope = new ImportedContextEnvelope(
            "1.0.0",
            [new("memory", "memory:item-1", "Advisory summary", null, ObservedAt, Stale: true)],
            [],
            ["Checkout verification is pending"]);
        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()], envelope, EvidenceRequirement.Required);

        var frozen = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
        var text = Encoding.UTF8.GetString(frozen.CanonicalBytes);

        Assert.True(assessment.Ready);
        Assert.Contains("stale", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checkout verification", text, StringComparison.OrdinalIgnoreCase);
    }

    private static PlanningRequest Request() => new(
        "nucleoid/delivery-forge", "#4", "implement", "Build planning core",
        ["planning"], ["execution"], ["Behavior is deterministic"], "implement");

    private static EvidenceItem RepositoryEvidence(string locator = "git:README.md") => new(
        EvidenceSourceKind.Repository, locator, "sha256:" + new string('b', 64), ObservedAt, []);

    private static PlanDraft Draft()
    {
        var repository = new RepositoryContext(
            "/portable/display-only", "HEAD", new string('a', 40), new string('b', 40),
            DetachedHead: false, Dirty: false, Shallow: false, Submodules: [], Limitations: []);
        return new PlanDraft(
            Request(), repository,
            [RepositoryEvidence("git:README.md"), RepositoryEvidence("git:Directory.Build.props")],
            [new("components/planning/Core.cs", "PlanFreezer", "freeze plans"), new("references/planning.md", "document", "describe boundaries")],
            [new("contracts", [], "issue #3 is integrated")],
            [new("test", "dotnet test", "all tests pass"), new("build", "dotnet build", "zero warnings")],
            new("additive", "none", "none", "none", "none", "none", "test results", "revert commit", []),
            []);
    }
}
