using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Planning.Tests;

public sealed class PlanningBehaviorTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Minimal_intake_resolves_ordinary_engineering_choices_without_a_question()
    {
        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()]);
        Assert.True(assessment.Ready);
        Assert.Null(assessment.RecommendedQuestion);
    }

    [Fact]
    public void User_owned_choice_blocks_with_one_recommended_question()
    {
        var request = Request() with
        {
            UserOwnedDecision = "Choose whether v1 may delete user data",
            RecommendedOption = "do not delete user data in v1"
        };
        var assessment = IntakePlanner.Assess(request, [RepositoryEvidence()]);
        Assert.False(assessment.Ready);
        Assert.DoesNotContain('\n', assessment.RecommendedQuestion!);
        Assert.StartsWith("I recommend do not delete user data", assessment.RecommendedQuestion!, StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_intake_is_explicit_and_requires_distinct_bounded_evidence()
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        var shallow = IntakePlanner.Assess(request, [RepositoryEvidence()]);
        var deep = IntakePlanner.Assess(request, [RepositoryEvidence(), PolicyEvidence()]);
        Assert.False(shallow.Ready);
        Assert.True(deep.Ready);
    }

    [Fact]
    public void Optional_missing_import_is_an_honest_nonblocking_caveat()
    {
        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()]);
        Assert.True(assessment.Ready);
        Assert.Contains(assessment.Limitations, value => value.Contains("Optional", StringComparison.Ordinal));
    }

    [Fact]
    public void Required_missing_import_blocks_readiness_and_freeze()
    {
        var draft = Draft();
        var assessment = IntakePlanner.Assess(draft.Request, draft.Provenance, null, EvidenceRequirement.Required);
        Assert.False(assessment.Ready);
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(draft with { Intake = assessment }, "required-missing", ObservedAt));
    }

    [Fact]
    public void Required_private_meaning_without_a_safe_distillation_blocks()
    {
        var envelope = new ImportedContextEnvelope(
            "1.0.0",
            [new("memory", "/private/host/location", "Project Phoenix needs an undocumented exception", null, ObservedAt,
                Requirement: EvidenceRequirement.Required)],
            [],
            []);
        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()], envelope, EvidenceRequirement.Required);
        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, value => value.Contains("cannot be safely distilled", StringComparison.Ordinal));
    }

    [Fact]
    public void Supported_typed_distillation_remains_usable_without_exporting_private_text()
    {
        const string privateText = "Project Phoenix calls this symbol from attic seven";
        var draft = Draft();
        var envelope = new ImportedContextEnvelope(
            "1.0.0",
            [new("code-index", "/private/index/record-7", privateText, null, ObservedAt,
                Requirement: EvidenceRequirement.Required, DistilledMeaning: ImportedMeaningCode.CallerRelationship)],
            [],
            []);
        var assessment = IntakePlanner.Assess(draft.Request, draft.Provenance, envelope, EvidenceRequirement.Required);
        var frozen = PlanFreezer.Freeze(draft with { Intake = assessment }, "typed-distillation", ObservedAt);
        var json = Encoding.UTF8.GetString(frozen.CanonicalBytes);

        Assert.True(assessment.Ready);
        Assert.Contains("identified a caller relationship", json, StringComparison.Ordinal);
        Assert.DoesNotContain(privateText, json, StringComparison.Ordinal);
        Assert.DoesNotContain("/private/index/record-7", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_advisory_conflicts_limitations_and_locators_never_flow_to_public_fields()
    {
        const string privateSummary = "Project Phoenix uses hidden host seven";
        const string privateConflict = "Conflict with the attic deployment";
        const string privateLimitation = "Only /home/alice/private-index was searched";
        var draft = Draft();
        var envelope = new ImportedContextEnvelope(
            "1.0.0",
            [new("memory", "/home/alice/private-index/7", privateSummary, null, ObservedAt,
                DistilledMeaning: ImportedMeaningCode.PolicyConstraint)],
            [privateConflict],
            [privateLimitation]);
        var assessment = IntakePlanner.Assess(draft.Request, draft.Provenance, envelope);
        var frozen = PlanFreezer.Freeze(draft with { Intake = assessment }, "private-boundary", ObservedAt);
        var json = Encoding.UTF8.GetString(frozen.CanonicalBytes);

        Assert.DoesNotContain(privateSummary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(privateConflict, json, StringComparison.Ordinal);
        Assert.DoesNotContain(privateLimitation, json, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/alice", json, StringComparison.Ordinal);
        Assert.Contains("raw details remain local-only", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_advisory_provenance_exports_only_an_opaque_locator_digest_and_typed_caveat()
    {
        var draft = Draft();
        EvidenceItem[] provenance =
        [
            RepositoryEvidence(),
            new(EvidenceSourceKind.Memory, EvidenceProducerKind.PrivateAdvisory, EvidenceLocatorKind.Opaque,
                "opaque:memory-record-7", "sha256:" + new string('d', 64), ObservedAt,
                [EvidenceCaveatCode.Heuristic])
        ];
        var frozen = PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = IntakePlanner.Assess(draft.Request, provenance) },
            "opaque-provenance",
            ObservedAt);
        var json = Encoding.UTF8.GetString(frozen.CanonicalBytes);

        Assert.Contains("private-advisory", json, StringComparison.Ordinal);
        Assert.Contains("opaque:memory-record-7", json, StringComparison.Ordinal);
        Assert.Contains("Private advisory evidence is heuristic", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicitly_public_user_evidence_remains_usable_and_readable()
    {
        var draft = Draft();
        EvidenceItem[] provenance =
        [
            RepositoryEvidence(),
            new(EvidenceSourceKind.User, EvidenceProducerKind.ExplicitUserPublic, EvidenceLocatorKind.Public,
                "user:approved-scope", "sha256:" + new string('e', 64), ObservedAt, [])
        ];
        var frozen = PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = IntakePlanner.Assess(draft.Request, provenance) },
            "explicit-public",
            ObservedAt);
        var json = Encoding.UTF8.GetString(frozen.CanonicalBytes);
        Assert.Contains("explicit-user-public", json, StringComparison.Ordinal);
        Assert.Contains("user:approved-scope", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_private_advisory_cannot_be_relabelled_with_a_public_locator()
    {
        var draft = Draft();
        EvidenceItem[] provenance =
        [
            RepositoryEvidence(),
            new(EvidenceSourceKind.Imported, EvidenceProducerKind.PrivateAdvisory, EvidenceLocatorKind.Public,
                "https://example.invalid/public-looking", "sha256:" + new string('d', 64), ObservedAt, [])
        ];
        var assessment = IntakePlanner.Assess(draft.Request, provenance);
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = assessment }, "relabel", ObservedAt));
    }

    [Fact]
    public void Imported_raw_advisory_accepts_private_text_but_not_controls_or_unbounded_values()
    {
        var json = Encoding.UTF8.GetBytes("""
            {"schemaVersion":"1.0.0","entries":[{"kind":"memory","locator":"/home/alice/private","summary":"password=local-only","digest":null,"observedAt":"2026-10-09T09:00:00Z"}],"conflicts":["private conflict"],"limitations":["private limitation"]}
            """);
        Assert.Single(ImportedContextEnvelope.Parse(json).Entries);

        var control = Encoding.UTF8.GetBytes("""
            {"schemaVersion":"1.0.0","entries":[{"kind":"memory","locator":"opaque","summary":"bad\u0000value","digest":null,"observedAt":"2026-10-09T09:00:00Z"}],"conflicts":[],"limitations":[]}
            """);
        Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(control));
    }

    [Fact]
    public void Typed_commands_render_readably_and_deterministically()
    {
        var command = new PlanCommand("dotnet",
        [
            PlanCommandArgument.Literal("test"),
            PlanCommandArgument.Literal("components/planning/Tests.csproj"),
            PlanCommandArgument.Placeholder("configuration"),
            PlanCommandArgument.SecretReference("feed-token")
        ]);
        Assert.Equal("dotnet test components/planning/Tests.csproj <placeholder:configuration> <secret-ref:feed-token>", command.Render());

        var draft = Draft() with { Gates = [new("test", command, "all tests pass")] };
        var json = Encoding.UTF8.GetString(PlanFreezer.Freeze(draft, "typed-command", ObservedAt).CanonicalBytes);
        Assert.Contains("\"kind\":\"secret-reference\"", json, StringComparison.Ordinal);
        Assert.Contains("\"displayCommand\":\"dotnet test", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_commands_reject_real_secret_values_and_free_form_documents()
    {
        var secret = Draft() with
        {
            Gates = [new("test", new PlanCommand("tool", [PlanCommandArgument.Literal("password=do-not-copy")]), "passes")]
        };
        var document = Draft() with
        {
            Gates = [new("test", new PlanCommand("command:\n  curl", []), "passes")]
        };
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(secret, "secret-command", ObservedAt));
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(document, "document-command", ObservedAt));
    }

    [Fact]
    public void Scanner_and_schema_success_do_not_create_approval_or_publication_authority()
    {
        var frozen = PlanFreezer.Freeze(Draft(), "no-approval", ObservedAt);
        var json = Encoding.UTF8.GetString(frozen.CanonicalBytes);
        Assert.DoesNotContain("approved", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publicationAuthority", json, StringComparison.Ordinal);
        Assert.Equal("implement", Draft().Request.RequestedCeiling);
    }

    [Fact]
    public void Defense_in_depth_rejects_private_host_paths_credentials_and_controls_in_public_fields()
    {
        foreach (var value in new[] { "/home/alice/private", "password=do-not-copy", "Bearer do-not-copy", "bad\u0001value" })
        {
            var draft = Draft() with { Rollout = Draft().Rollout with { Compatibility = value } };
            Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(draft, "defense", ObservedAt));
        }
    }

    [Fact]
    public void Post_verification_mutation_becomes_untrusted()
    {
        var file = RepositoryFileFor("callers.txt");
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file.Bytes))}";
        var verified = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry("code-index", "git:callers.txt", "local advisory", digest, ObservedAt,
                CheckoutDigest: digest, DistilledMeaning: ImportedMeaningCode.CallerRelationship),
            file);
        var mutated = verified with { Summary = "changed after verification" };
        var assessment = IntakePlanner.Assess(
            Request() with { Depth = IntakeDepth.Deep },
            [RepositoryEvidence()],
            new ImportedContextEnvelope("1.0.0", [mutated], [], []));
        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, value => value.Contains("unverified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Exact_freeze_is_reproducible_under_reordering_and_culture()
    {
        var first = Draft();
        var second = first with
        {
            Provenance = first.Provenance.Reverse().ToArray(),
            ChangeMap = first.ChangeMap.Reverse().ToArray(),
            Gates = first.Gates.Reverse().ToArray()
        };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var a = PlanFreezer.Freeze(first, "revision-1", ObservedAt);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var b = PlanFreezer.Freeze(second, "revision-1", ObservedAt);
            Assert.Equal(a.Identity, b.Identity);
            Assert.Equal(a.CanonicalBytes, b.CanonicalBytes);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Material_edits_require_a_new_revision_and_supersede_the_predecessor()
    {
        var first = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
        var changed = Draft() with
        {
            Gates = [new("test", new PlanCommand("dotnet", [PlanCommandArgument.Literal("test"), PlanCommandArgument.Literal("--no-restore")]), "all tests pass")]
        };
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(changed, "revision-1", ObservedAt, first));
        var second = PlanFreezer.Freeze(changed, "revision-2", ObservedAt, first);
        Assert.Equal(first.ContractIdentity, second.Supersedes);
    }

    [Fact]
    public void Base_drift_invalidates_downstream_readiness()
    {
        var draft = Draft();
        var branch = RepositoryContext.Create(
            "/portable/display-only", "refs/heads/main", draft.Repository.Commit, draft.Repository.Tree,
            false, false, false, [], [], exactBranchReferenceVerified: true);
        var frozen = PlanFreezer.Freeze(draft with { Repository = branch }, "base", ObservedAt);
        var changed = RepositoryContext.Create(
            "/portable/display-only", "refs/heads/main", new string('d', 40), new string('e', 40),
            false, false, false, [], [], exactBranchReferenceVerified: true);
        Assert.False(frozen.ReconcileBase(changed).DownstreamReady);
    }

    [Fact]
    public void Required_sections_unknowns_and_authorization_ceiling_are_guarded()
    {
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(Draft() with { ChangeMap = [] }, "missing", ObservedAt));
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(Draft() with { Unknowns = [new("decision", "user", true)] }, "unknown", ObservedAt));
        var invalid = Draft();
        var request = invalid.Request with { RequestedCeiling = "publish" };
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            invalid with { Request = request, Intake = IntakePlanner.Assess(request, invalid.Provenance) }, "ceiling", ObservedAt));
    }

    [Fact]
    public void Frozen_plan_preserves_the_strict_issue_three_contract_adapter()
    {
        var frozen = PlanFreezer.Freeze(Draft(), "contract", ObservedAt);
        var validated = ContractValidator.ParseAndValidate(frozen.PlanContractBytes);
        Assert.Equal(frozen.ContractIdentity, validated.Identity);
        using var document = JsonDocument.Parse(frozen.PlanContractBytes);
        Assert.Equal("plan", document.RootElement.GetProperty("kind").GetString());
        Assert.False(document.RootElement.TryGetProperty("gates", out _));
    }

    [Fact]
    public void Readiness_and_frozen_state_are_not_publicly_constructible_or_mutable()
    {
        Assert.Empty(typeof(IntakeAssessment).GetConstructors());
        Assert.Empty(typeof(RepositoryContext).GetConstructors());
        Assert.All(typeof(FrozenPlan).GetProperties().Where(property => property.Name is not nameof(FrozenPlan.CanonicalBytes) and not nameof(FrozenPlan.PlanContractBytes)),
            property => Assert.False(property.CanWrite));
    }

    private static PlanningRequest Request() => new(
        "nucleoid/delivery-forge", "#4", "implement", "Build planning core",
        ["planning"], ["execution"], ["Behavior is deterministic"], "implement");

    private static EvidenceItem RepositoryEvidence(string path = "README.md") =>
        EvidenceItem.FromRepositoryFile(RepositoryFileFor(path), ObservedAt, []);

    private static EvidenceItem PolicyEvidence() => new(
        EvidenceSourceKind.Policy,
        EvidenceProducerKind.DeterministicGenerated,
        EvidenceLocatorKind.Public,
        "policy:planning",
        "sha256:" + new string('c', 64),
        ObservedAt,
        []);

    private static RepositoryFile RepositoryFileFor(string path) => new(
        path, new string('c', 40), "100644", Encoding.UTF8.GetBytes($"exact bytes for {path}"),
        false, false, "not-detected", SymlinkResolution.NotSymlink,
        new string('a', 40), new string('b', 40));

    private static PlanDraft Draft()
    {
        var request = Request();
        EvidenceItem[] provenance = [RepositoryEvidence(), EvidenceItem.FromRepositoryFile(RepositoryFileFor("Directory.Build.props"), ObservedAt, [])];
        var repository = RepositoryContext.Create(
            "/portable/display-only", "HEAD", new string('a', 40), new string('b', 40),
            false, false, false, [], []);
        return new PlanDraft(
            request,
            repository,
            provenance,
            [new("components/planning/Core.cs", "PlanFreezer", "freeze plans"), new("references/planning.md", "document", "describe boundaries")],
            [new("contracts", [], "issue #3 is integrated")],
            [new("test", new PlanCommand("dotnet", [PlanCommandArgument.Literal("test")]), "all tests pass"),
             new("build", new PlanCommand("dotnet", [PlanCommandArgument.Literal("build")]), "zero warnings")],
            new("additive", "none", "none", "none", "none", "none", "test results", "revert commit", []),
            [],
            IntakePlanner.Assess(request, provenance));
    }
}
