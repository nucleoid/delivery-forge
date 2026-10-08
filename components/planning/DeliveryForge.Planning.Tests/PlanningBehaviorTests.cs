using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Validation;

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
        var assessment = IntakePlanner.Assess(
            Request() with
            {
                UserOwnedDecision = "Choose whether v1 may delete user data",
                RecommendedOption = "do not delete user data in v1"
            },
            [RepositoryEvidence()]);

        Assert.False(assessment.Ready);
        Assert.StartsWith("I recommend do not delete user data in v1.", assessment.RecommendedQuestion!, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', assessment.RecommendedQuestion!);
        Assert.Throws<PlanningException>(() => IntakePlanner.Assess(
            Request() with { UserOwnedDecision = "Choose whether v1 may delete user data" },
            [RepositoryEvidence()]));
    }

    [Theory]
    [InlineData("Choose rollout\nChoose deletion", "do the safe thing")]
    [InlineData("Choose rollout", "safe\r\nrisky")]
    [InlineData("Choose rollout\u0085Choose deletion", "do the safe thing")]
    [InlineData("Choose rollout", "safe\u2028risky")]
    [InlineData("Choose rollout\u2029Choose deletion", "do the safe thing")]
    [InlineData("Choose rollout", "safe\vrisky")]
    [InlineData("Choose rollout\frisky", "do the safe thing")]
    [InlineData("Choose rollout", "safe\trisky")]
    [InlineData("Choose rollout", ".")]
    [InlineData("?", "safe")]
    public void User_owned_choice_requires_one_concrete_single_line_decision(
        string decision,
        string recommendation)
    {
        Assert.Throws<PlanningException>(() => IntakePlanner.Assess(
            Request() with { UserOwnedDecision = decision, RecommendedOption = recommendation },
            [RepositoryEvidence()]));
    }

    [Fact]
    public void User_owned_choice_accepts_valid_single_line_unicode_prose()
    {
        var assessment = IntakePlanner.Assess(
            Request() with
            {
                UserOwnedDecision = "Choose the café rollout for 東京",
                RecommendedOption = "use the safer café rollout ✅"
            },
            [RepositoryEvidence()]);

        Assert.False(assessment.Ready);
        Assert.Contains("東京", assessment.RecommendedQuestion!, StringComparison.Ordinal);
        Assert.Contains("✅", assessment.RecommendedQuestion!, StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_intake_is_only_enabled_explicitly()
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        var shallowAssessment = IntakePlanner.Assess(request, [RepositoryEvidence()]);
        var deepAssessment = IntakePlanner.Assess(request,
        [
            RepositoryEvidence(),
            new(EvidenceSourceKind.Policy, "policy:planning", "sha256:" + new string('c', 64), ObservedAt, [])
        ]);
        var exactFile = RepositoryFileFor("callers.txt");
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(exactFile.Bytes))}";
        var verifiedImport = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry(
                "code-index",
                "git:callers.txt",
                "Additional bounded caller evidence",
                digest,
                ObservedAt,
                CheckoutDigest: digest),
            exactFile);
        var importedDeepAssessment = IntakePlanner.Assess(
            request,
            [RepositoryEvidence()],
            new ImportedContextEnvelope(
                "1.0.0",
                [verifiedImport],
                [],
                []));

        Assert.Equal(IntakeDepth.Deep, shallowAssessment.Depth);
        Assert.False(shallowAssessment.Ready);
        Assert.Contains(shallowAssessment.Limitations, item => item.Contains("additional", StringComparison.OrdinalIgnoreCase));
        Assert.True(deepAssessment.Ready);
        Assert.True(importedDeepAssessment.Ready);
    }

    [Fact]
    public void Deep_intake_evidence_is_bounded()
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        var evidence = Enumerable.Range(0, 257)
            .Select(index => new EvidenceItem(
                index == 0 ? EvidenceSourceKind.Repository : EvidenceSourceKind.Policy,
                $"policy:item-{index}",
                null,
                ObservedAt,
                []))
            .ToArray();

        Assert.Throws<PlanningException>(() => IntakePlanner.Assess(request, evidence));
    }

    [Fact]
    public void Deep_intake_does_not_count_user_or_memory_claims_as_bounded_extra_evidence()
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        EvidenceItem[] evidence =
        [
            RepositoryEvidence(),
            new(EvidenceSourceKind.User, "user:statement", "sha256:" + new string('c', 64), ObservedAt, []),
            new(EvidenceSourceKind.Memory, "memory:item", "sha256:" + new string('d', 64), ObservedAt, [])
        ];

        var assessment = IntakePlanner.Assess(request, evidence);

        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, item => item.Contains("additional bounded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Deep_intake_counts_each_eligible_locator_once_across_evidence_and_imports()
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        var repository = RepositoryEvidence();
        var exactFile = RepositoryFileFor("README.md");
        var duplicateImport = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry(
                "repository",
                repository.Locator,
                "Duplicate checkout claim",
                repository.Digest,
                ObservedAt,
                CheckoutDigest: repository.Digest),
            exactFile);

        var assessment = IntakePlanner.Assess(
            request,
            [repository],
            new ImportedContextEnvelope("1.0.0", [duplicateImport], [], []));

        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, item => item.Contains("additional bounded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Deep_intake_does_not_count_distinct_locator_aliases_for_the_same_bound_content()
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        var sharedBytes = Encoding.UTF8.GetBytes("same exact repository bytes");
        EvidenceItem BoundAlias(string path) => EvidenceItem.FromRepositoryFile(
            new RepositoryFile(
                path, new string('c', 40), "100644", sharedBytes,
                isSymlink: false, escapesWorktree: false, "not-detected", SymlinkResolution.NotSymlink,
                new string('a', 40), new string('b', 40)),
            ObservedAt,
            []);
        var first = BoundAlias("README.md");
        var alias = BoundAlias("docs/README-alias.md");

        var assessment = IntakePlanner.Assess(request, [first, alias]);

        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, item => item.Contains("additional bounded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Deep_intake_deduplicates_a_repository_alias_across_evidence_and_imported_context()
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        var exactFile = RepositoryFileFor("callers.txt");
        var evidenceAlias = EvidenceItem.FromRepositoryFile(
            new RepositoryFile(
                "docs/callers-alias.txt", new string('c', 40), "100644", exactFile.Bytes,
                isSymlink: false, escapesWorktree: false, "not-detected", SymlinkResolution.NotSymlink,
                new string('a', 40), new string('b', 40)),
            ObservedAt,
            []);
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(exactFile.Bytes))}";
        var imported = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry(
                "code-index", "git:callers.txt", "Same checkout bytes under another locator", digest, ObservedAt,
                CheckoutDigest: digest),
            exactFile);

        var assessment = IntakePlanner.Assess(
            request,
            [evidenceAlias],
            new ImportedContextEnvelope("1.0.0", [imported], [], []));

        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, item => item.Contains("additional bounded", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("memory", false, false, false)]
    [InlineData("code-index", true, false, false)]
    [InlineData("code-index", false, true, false)]
    [InlineData("code-index", false, false, true)]
    public void Deep_intake_rejects_ineligible_imported_claims(
        string kind,
        bool stale,
        bool truncated,
        bool heuristic)
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        var exactFile = RepositoryFileFor("callers.txt");
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(exactFile.Bytes))}";
        var imported = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry(
                kind,
                "git:callers.txt",
                "Additional claim",
                digest,
                ObservedAt,
                stale,
                truncated,
                heuristic,
                CheckoutDigest: digest),
            exactFile);

        var assessment = IntakePlanner.Assess(
            request,
            [RepositoryEvidence()],
            new ImportedContextEnvelope("1.0.0", [imported], [], []));

        Assert.False(assessment.Ready);
    }

    [Fact]
    public void Deep_intake_accepts_a_distinct_verified_non_memory_import()
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        var exactFile = RepositoryFileFor("callers.txt");
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(exactFile.Bytes))}";
        var imported = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry(
                "code-index",
                "git:callers.txt",
                "Verified caller evidence",
                digest,
                ObservedAt,
                CheckoutDigest: digest),
            exactFile);

        var assessment = IntakePlanner.Assess(
            request,
            [RepositoryEvidence()],
            new ImportedContextEnvelope("1.0.0", [imported], [], []));

        Assert.True(assessment.Ready);
    }

    [Fact]
    public void Verified_generated_import_retains_reader_classification_and_cannot_satisfy_deep_intake()
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        var generatedFile = new RepositoryFile(
            "Generated.g.cs", new string('c', 40), "100644", Encoding.UTF8.GetBytes("generated"),
            isSymlink: false, escapesWorktree: false, "generated-by-convention", SymlinkResolution.NotSymlink,
            new string('a', 40), new string('b', 40));
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(generatedFile.Bytes))}";
        var imported = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry(
                "code-index", "git:Generated.g.cs", "Generated index evidence", digest, ObservedAt,
                CheckoutDigest: digest),
            generatedFile);

        var assessment = IntakePlanner.Assess(
            request,
            [PolicyEvidence()],
            new ImportedContextEnvelope("1.0.0", [imported], [], []));

        Assert.Equal(CheckoutVerification.Verified, imported.CheckoutVerification);
        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, item =>
            item.Contains("generation classification is generated-by-convention", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unsafe_or_generated_required_imports_preserve_minimal_readiness_with_deep_only_caveats(
        bool unsafeSymlink)
    {
        var file = new RepositoryFile(
            unsafeSymlink ? "unsafe-link" : "Generated.g.cs",
            new string('c', 40),
            unsafeSymlink ? "120000" : "100644",
            Encoding.UTF8.GetBytes("bounded evidence"),
            isSymlink: unsafeSymlink,
            escapesWorktree: unsafeSymlink,
            unsafeSymlink ? "not-detected" : "generated-by-convention",
            unsafeSymlink ? SymlinkResolution.Escapes : SymlinkResolution.NotSymlink,
            new string('a', 40),
            new string('b', 40));
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file.Bytes))}";
        var imported = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry(
                "code-index", $"git:{file.Path}", "Verified imported evidence", digest, ObservedAt,
                CheckoutDigest: digest),
            file);

        var assessment = IntakePlanner.Assess(
            Request(),
            [RepositoryEvidence()],
            new ImportedContextEnvelope("1.0.0", [imported], [], []),
            EvidenceRequirement.Required);

        Assert.True(assessment.Ready);
        Assert.Contains(assessment.Limitations, item =>
            item.Contains("deep readiness", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(assessment.Limitations, item =>
            item.Contains("cannot establish readiness", StringComparison.OrdinalIgnoreCase));
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

    [Theory]
    [InlineData("verified")]
    [InlineData("7")]
    [InlineData("unverified,verified")]
    public void Imported_context_cannot_assert_checkout_verification(string verification)
    {
        var json = Encoding.UTF8.GetBytes($$"""
            {
              "schemaVersion":"1.0.0",
              "entries":[{
                "kind":"repository",
                "locator":"git:tracked.txt",
                "summary":"Advisory summary",
                "digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "observedAt":"2026-10-07T20:00:00Z",
                "checkoutVerification":"{{verification}}"
              }],
              "conflicts":[],
              "limitations":[]
            }
            """);

        Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(json));
    }

    [Fact]
    public void Caller_constructed_checkout_verification_is_treated_as_unverified()
    {
        var entry = new ImportedContextEntry(
            "repository",
            "git:tracked.txt",
            "summary",
            null,
            ObservedAt,
            CheckoutVerification: CheckoutVerification.Verified,
            CheckoutDigest: "sha256:" + new string('a', 64));
        var envelope = new ImportedContextEnvelope("1.0.0", [entry], [], []);

        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()], envelope);

        Assert.Contains(assessment.Limitations, item => item.Contains("unverified", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(assessment.Limitations, item => item.Contains("was verified", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("summary")]
    [InlineData("digest")]
    [InlineData("observedAt")]
    [InlineData("stale")]
    [InlineData("truncated")]
    [InlineData("heuristic")]
    public void Post_verification_imported_record_mutation_downgrades_to_unverified(string field)
    {
        var exactFile = RepositoryFileFor("callers.txt");
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(exactFile.Bytes))}";
        var verified = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry(
                "code-index", "git:callers.txt", "Verified caller evidence", digest, ObservedAt,
                CheckoutDigest: digest),
            exactFile);
        var mutated = field switch
        {
            "kind" => verified with { Kind = "repository" },
            "summary" => verified with { Summary = "Changed summary" },
            "digest" => verified with { Digest = "sha256:" + new string('d', 64) },
            "observedAt" => verified with { ObservedAt = verified.ObservedAt.AddSeconds(1) },
            "stale" => verified with { Stale = true },
            "truncated" => verified with { Truncated = true },
            "heuristic" => verified with { Heuristic = true },
            _ => throw new InvalidOperationException(field)
        };

        var assessment = IntakePlanner.Assess(
            Request() with { Depth = IntakeDepth.Deep },
            [RepositoryEvidence()],
            new ImportedContextEnvelope("1.0.0", [mutated], [], []));

        Assert.Contains(assessment.Limitations, item => item.Contains("unverified", StringComparison.OrdinalIgnoreCase));
        Assert.False(assessment.Ready);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Post_verification_reader_safety_mutation_downgrades_to_unverified(bool mutateSymlink)
    {
        var exactFile = RepositoryFileFor("callers.txt");
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(exactFile.Bytes))}";
        var verified = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry(
                "code-index", "git:callers.txt", "Verified caller evidence", digest, ObservedAt,
                CheckoutDigest: digest),
            exactFile);
        var mutated = mutateSymlink
            ? verified with { VerifiedSymlinkResolution = SymlinkResolution.Escapes }
            : verified with { VerifiedGenerationClassification = "generated-by-convention" };

        var assessment = IntakePlanner.Assess(
            Request() with { Depth = IntakeDepth.Deep },
            [RepositoryEvidence()],
            new ImportedContextEnvelope("1.0.0", [mutated], [], []));

        Assert.Contains(assessment.Limitations, item => item.Contains("unverified", StringComparison.OrdinalIgnoreCase));
        Assert.False(assessment.Ready);
    }

    [Fact]
    public void Verified_safe_in_tree_symlink_can_satisfy_deep_intake()
    {
        var file = new RepositoryFile(
            "linked.txt", new string('c', 40), "120000", Encoding.UTF8.GetBytes("target.txt"),
            isSymlink: true, escapesWorktree: false, "not-detected; generator metadata was not asserted",
            SymlinkResolution.InTree, new string('a', 40), new string('b', 40));
        var digest = $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file.Bytes))}";
        var imported = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry(
                "code-index", "git:linked.txt", "Safe linked evidence", digest, ObservedAt,
                CheckoutDigest: digest),
            file);

        var assessment = IntakePlanner.Assess(
            Request() with { Depth = IntakeDepth.Deep },
            [PolicyEvidence()],
            new ImportedContextEnvelope("1.0.0", [imported], [], []));

        Assert.True(assessment.Ready);
        Assert.DoesNotContain(assessment.Limitations, item => item.Contains("symlink safety", StringComparison.OrdinalIgnoreCase));
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
    public void Required_empty_imported_context_blocks_readiness_and_freeze()
    {
        var draft = Draft();
        var empty = new ImportedContextEnvelope("1.0.0", [], [], ["The bounded search returned no matches."]);
        var assessment = IntakePlanner.Assess(draft.Request, draft.Provenance, empty, EvidenceRequirement.Required);

        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, item => item.Contains("required", StringComparison.OrdinalIgnoreCase) && item.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(draft with { Intake = assessment }, "revision-required-empty", ObservedAt));
    }

    [Fact]
    public void Optional_incomplete_advisory_provenance_degrades_without_blocking_freeze()
    {
        var draft = Draft();
        EvidenceItem[] provenance =
        [
            RepositoryEvidence(),
            new(EvidenceSourceKind.Memory, "memory:item-1", null, ObservedAt, ["Search was truncated."], IsComplete: false)
        ];
        var assessment = IntakePlanner.Assess(draft.Request, provenance);

        Assert.True(assessment.Ready);
        var frozen = PlanFreezer.Freeze(draft with { Provenance = provenance, Intake = assessment }, "revision-incomplete-optional", ObservedAt);
        Assert.Contains("Search was truncated", Encoding.UTF8.GetString(frozen.CanonicalBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Required_incomplete_provenance_blocks_intake_and_freeze_without_hiding_caveats()
    {
        var draft = Draft();
        EvidenceItem[] provenance =
        [
            RepositoryEvidence(),
            new(EvidenceSourceKind.Policy, "policy:required", null, ObservedAt, ["Policy retrieval was truncated."], IsComplete: false, Requirement: EvidenceRequirement.Required)
        ];
        var assessment = IntakePlanner.Assess(draft.Request, provenance);

        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, item => item.Contains("Policy retrieval was truncated", StringComparison.Ordinal));
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = assessment },
            "revision-incomplete-required",
            ObservedAt));
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
    public void Normal_scheme_urls_are_portable_evidence_locators()
    {
        var locator = "https://github.com/nucleoid/delivery-forge/issues/4#issuecomment-6028955264";
        var draft = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(locator)];
        var frozen = PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = IntakePlanner.Assess(draft.Request, provenance) },
            "revision-url",
            ObservedAt);

        Assert.Contains(locator, Encoding.UTF8.GetString(frozen.CanonicalBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_url_with_host_path_shaped_segments_remains_portable()
    {
        var locator = "https://example.invalid/home/alice/.ssh/config";
        var draft = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(locator)];

        var frozen = PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = IntakePlanner.Assess(draft.Request, provenance) },
            "revision-url-shaped-path",
            ObservedAt);

        Assert.Contains(locator, Encoding.UTF8.GetString(frozen.CanonicalBytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file:///home/alice/work/repo/README.md")]
    [InlineData("ftp:///home/alice/work/repo/README.md")]
    [InlineData("https://alice:secret@example.invalid/repo.git")]
    [InlineData("https://example.invalid/a|/home/alice/.ssh/config")]
    [InlineData("<path>/home/alice/.ssh/id_rsa</path>")]
    [InlineData("https://example.invalid/a>/home/alice/.ssh/config")]
    [InlineData("found in /home/Ølaf/.ssh/config")]
    [InlineData("found in /home/Алиса/.ssh/config")]
    [InlineData("found@/home/alice/.ssh/config")]
    [InlineData("found!/home/alice/.ssh/config")]
    [InlineData("found*/home/alice/.ssh/config")]
    [InlineData("found#/home/alice/.ssh/config")]
    [InlineData("found+/home/alice/.ssh/config")]
    [InlineData("found&/home/alice/.ssh/config")]
    [InlineData("see `/home/alice/.ssh/config`")]
    [InlineData("see [/home/alice/.ssh/config]")]
    [InlineData("see `C:\\Users\\alice\\.ssh\\config`")]
    [InlineData("github_pat_11AA22BB33CC44DD55EE66FF77GG88HH99II")]
    [InlineData("_https://alice:hunter2@db.example")]
    [InlineData("1https://alice:hunter2@db.example")]
    [InlineData("éhttps://alice:hunter2@db.example")]
    [InlineData("https:\\\\alice:hunter2@db.example")]
    [InlineData("password: hunter2")]
    [InlineData("{\"password\":\"hunter2\"}")]
    [InlineData("api_key: hunter2")]
    [InlineData("client_secret=hunter2")]
    [InlineData("Authorization: Basic Zm9vOmJhcg==")]
    [InlineData("found in /workspace/alice/client-x")]
    [InlineData("found in /data/private/report")]
    [InlineData("found in /run/secrets/db-password")]
    [InlineData("~/.ssh/id_rsa")]
    public void Portable_material_rejects_non_network_urls_embedded_paths_and_credentials(string value)
    {
        var draft = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(value)];
        var frozenError = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = IntakePlanner.Assess(draft.Request, provenance) },
            "revision-portability",
            ObservedAt));
        Assert.Contains("private material", frozenError.Message, StringComparison.OrdinalIgnoreCase);

        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            entries = new[] { new { kind = "memory", locator = "memory:item-1", summary = value, digest = (string?)null, observedAt = "2026-10-07T20:00:00Z" } },
            conflicts = Array.Empty<string>(),
            limitations = Array.Empty<string>()
        }));
        Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(json));
    }

    [Theory]
    [InlineData("//server/share/private")]
    [InlineData("\\\\server\\share\\private")]
    public void Unc_and_host_paths_remain_non_portable(string locator)
    {
        var draft = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(locator)];
        var error = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = IntakePlanner.Assess(draft.Request, provenance) },
            "revision-host-path",
            ObservedAt));

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
        var original = Draft();
        var branch = RepositoryContext.Create(
            "/portable/display-only", "refs/heads/main", original.Repository.Commit, original.Repository.Tree,
            detachedHead: false, dirty: false, shallow: false, submodules: [], limitations: [],
            exactBranchReferenceVerified: true);
        var frozen = PlanFreezer.Freeze(original with { Repository = branch }, "revision-1", ObservedAt);
        var changed = Draft() with { Repository = branch };
        var changedRequest = changed.Request with { Outcome = "A materially different outcome" };
        var revised = PlanFreezer.Freeze(
            changed with { Request = changedRequest, Intake = IntakePlanner.Assess(changedRequest, changed.Provenance) },
            "revision-2",
            ObservedAt);

        Assert.NotEqual(frozen.Identity, revised.Identity);
        Assert.True(frozen.DownstreamReady);

        var contextBoundary = typeof(FrozenPlan).GetMethod(
            nameof(FrozenPlan.ReconcileBase),
            [typeof(RepositoryContext)]);
        Assert.NotNull(contextBoundary);
        Assert.Null(typeof(FrozenPlan).GetMethod(
            nameof(FrozenPlan.ReconcileBase),
            [typeof(string), typeof(string)]));
        var driftContext = RepositoryContext.Create(
            "/portable/display-only", "refs/heads/main", new string('d', 40), new string('e', 40),
            detachedHead: false, dirty: false, shallow: false, submodules: [], limitations: [],
            exactBranchReferenceVerified: true);
        var drifted = Assert.IsType<FrozenPlan>(contextBoundary.Invoke(frozen, [driftContext]));
        Assert.False(drifted.DownstreamReady);
        Assert.Contains(drifted.Limitations, item => item.Contains("drift", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Base_reconciliation_rejects_a_forged_repository_context()
    {
        var frozen = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
        var valid = Draft().Repository;
        var constructor = Assert.Single(typeof(RepositoryContext).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic));
        object ConstructorArgument(ParameterInfo parameter) => parameter.Name switch
        {
            "repositoryRoot" => (object)valid.RepositoryRoot,
            "requestedRef" => valid.RequestedRef,
            "commit" or "headCommit" => valid.Commit,
            "tree" or "headTree" => valid.Tree,
            "detachedHead" => false,
            "dirty" => false,
            "shallow" => false,
            "exactBranchReferenceVerified" => false,
            "submodules" or "limitations" => Array.Empty<string>(),
            "readerBinding" => "sha256:" + new string('0', 64),
            _ => throw new InvalidOperationException(parameter.Name)
        };
        var arguments = constructor.GetParameters().Select(ConstructorArgument).ToArray();
        var forged = (RepositoryContext)constructor.Invoke(arguments);
        var reconcile = typeof(FrozenPlan).GetMethod(nameof(FrozenPlan.ReconcileBase), [typeof(RepositoryContext)]);
        Assert.NotNull(reconcile);

        var error = Assert.Throws<TargetInvocationException>(() => reconcile.Invoke(frozen, [forged]));
        Assert.IsType<PlanningException>(error.InnerException);
    }

    [Theory]
    [InlineData(EvidenceSourceKind.Repository, "git:README.md")]
    [InlineData(EvidenceSourceKind.Policy, "policy:planning")]
    public void Unpinned_readiness_evidence_cannot_make_intake_ready(EvidenceSourceKind kind, string locator)
    {
        var assessment = IntakePlanner.Assess(
            Request(),
            [new EvidenceItem(kind, locator, null, ObservedAt, [])]);

        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, item => item.Contains("digest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Repository_readiness_evidence_requires_an_exact_reader_file_binding()
    {
        var file = new RepositoryFile(
            "README.md", new string('c', 40), "100644", Encoding.UTF8.GetBytes("readme"),
            isSymlink: false, escapesWorktree: false, "not-detected", SymlinkResolution.NotSymlink,
            new string('d', 40), new string('e', 40));
        var factory = typeof(EvidenceItem).GetMethod(
            "FromRepositoryFile",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(factory);
        var bound = Assert.IsType<EvidenceItem>(factory.Invoke(
            null,
            [file, ObservedAt, Array.Empty<string>(), EvidenceRequirement.Optional]));

        Assert.True(IntakePlanner.Assess(Request(), [bound]).Ready);
        Assert.False(IntakePlanner.Assess(Request(), [new EvidenceItem(
            EvidenceSourceKind.Repository,
            "git:README.md",
            "sha256:" + new string('b', 64),
            ObservedAt,
            [])]).Ready);

        EvidenceItem unbound = new(
            EvidenceSourceKind.Repository,
            "git:README.md",
            "sha256:" + new string('b', 64),
            ObservedAt,
            []);
        EvidenceItem[] mixedProvenance = [unbound, PolicyEvidence()];
        var mixedDraft = Draft();
        var unboundError = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            mixedDraft with
            {
                Provenance = mixedProvenance,
                Intake = IntakePlanner.Assess(mixedDraft.Request, mixedProvenance)
            },
            "revision-unbound-repository-evidence",
            ObservedAt));
        Assert.Contains("reader-issued", unboundError.Message, StringComparison.OrdinalIgnoreCase);

        var draft = Draft();
        EvidenceItem[] provenance = [bound, PolicyEvidence()];
        var error = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = IntakePlanner.Assess(draft.Request, provenance) },
            "revision-wrong-repository-binding",
            ObservedAt));
        Assert.Contains("commit/tree", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Git_locator_cannot_masquerade_as_policy_readiness()
    {
        var masquerading = new EvidenceItem(
            EvidenceSourceKind.Policy,
            "git:README.md",
            "sha256:" + new string('c', 64),
            ObservedAt,
            []);

        var assessment = IntakePlanner.Assess(Request(), [masquerading]);

        Assert.False(assessment.Ready);
        Assert.Contains(assessment.Limitations, item => item.Contains("source kind", StringComparison.OrdinalIgnoreCase));
        var draft = Draft();
        EvidenceItem[] provenance = [masquerading, PolicyEvidence()];
        var error = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            draft with
            {
                Provenance = provenance,
                Intake = IntakePlanner.Assess(draft.Request, provenance)
            },
            "revision-locator-kind",
            ObservedAt));
        Assert.Contains("locator", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SymlinkResolution.Escapes)]
    [InlineData(SymlinkResolution.Cycle)]
    [InlineData(SymlinkResolution.Missing)]
    [InlineData(SymlinkResolution.BoundExceeded)]
    public void Unsafe_repository_file_metadata_blocks_readiness_and_survives_freeze(
        SymlinkResolution resolution)
    {
        var unsafeFile = new RepositoryFile(
            "unsafe-link", new string('c', 40), "120000", Encoding.UTF8.GetBytes("target"),
            isSymlink: true, escapesWorktree: true, "not-detected", resolution,
            new string('a', 40), new string('b', 40));
        var unsafeEvidence = EvidenceItem.FromRepositoryFile(unsafeFile, ObservedAt, []);

        Assert.False(IntakePlanner.Assess(Request(), [unsafeEvidence]).Ready);
        Assert.False(unsafeEvidence.IsComplete);

        var draft = Draft();
        EvidenceItem[] provenance = [unsafeEvidence, PolicyEvidence()];
        var frozen = PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = IntakePlanner.Assess(draft.Request, provenance) },
            "revision-unsafe-file",
            ObservedAt);
        var canonical = Encoding.UTF8.GetString(frozen.CanonicalBytes);
        Assert.Contains(resolution.ToString().ToLowerInvariant(), canonical, StringComparison.Ordinal);
        Assert.Contains("symlink", canonical, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generated_repository_file_classification_survives_intake_and_freeze()
    {
        var generatedFile = new RepositoryFile(
            "Generated.g.cs", new string('c', 40), "100644", Encoding.UTF8.GetBytes("generated"),
            isSymlink: false, escapesWorktree: false, "generated-by-convention", SymlinkResolution.NotSymlink,
            new string('a', 40), new string('b', 40));
        var generated = EvidenceItem.FromRepositoryFile(generatedFile, ObservedAt, []);
        var draft = Draft();
        EvidenceItem[] provenance = [generated, PolicyEvidence()];
        var assessment = IntakePlanner.Assess(draft.Request, provenance);

        var frozen = PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = assessment },
            "revision-generated-file",
            ObservedAt);
        var canonical = Encoding.UTF8.GetString(frozen.CanonicalBytes);

        Assert.Contains(assessment.Limitations, item => item.Contains("generated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("generated-by-convention", canonical, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Freeze_rejects_every_repository_provenance_item_bound_to_a_different_base(bool unsafeSymlink)
    {
        var file = new RepositoryFile(
            unsafeSymlink ? "unsafe-link" : "Generated.g.cs",
            new string('c', 40),
            unsafeSymlink ? "120000" : "100644",
            Encoding.UTF8.GetBytes("content"),
            isSymlink: unsafeSymlink,
            escapesWorktree: unsafeSymlink,
            unsafeSymlink ? "not-detected" : "generated-by-convention",
            unsafeSymlink ? SymlinkResolution.Escapes : SymlinkResolution.NotSymlink,
            new string('d', 40),
            new string('e', 40));
        var repositoryEvidence = EvidenceItem.FromRepositoryFile(file, ObservedAt, []);
        var draft = Draft();
        EvidenceItem[] provenance = [repositoryEvidence, PolicyEvidence()];

        var error = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = IntakePlanner.Assess(draft.Request, provenance) },
            "revision-mismatched-incomplete-repository",
            ObservedAt));

        Assert.Contains("commit/tree", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frozen_canonical_provenance_exposes_each_repository_commit_and_tree()
    {
        var frozen = PlanFreezer.Freeze(Draft(), "revision-auditable-repository-binding", ObservedAt);
        var canonical = Encoding.UTF8.GetString(frozen.CanonicalBytes);

        Assert.Contains("\"repositoryCommit\":\"" + new string('a', 40) + "\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"repositoryTree\":\"" + new string('b', 40) + "\"", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Freeze_rejects_backslash_change_map_repository_path_aliases()
    {
        var draft = Draft() with
        {
            ChangeMap = [new("components\\planning\\Core.cs", "PlanFreezer", "freeze plans")]
        };

        var error = Assert.Throws<PlanningException>(() =>
            PlanFreezer.Freeze(draft, "revision-backslash-change-map", ObservedAt));

        Assert.Contains("repository-relative", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pinned_old_commit_cannot_reconcile_a_frozen_mutable_ref()
    {
        var frozen = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
        var pinnedOldCommit = RepositoryContext.Create(
            "/portable/display-only",
            frozen.BaseCommit,
            frozen.BaseCommit,
            frozen.BaseTree,
            detachedHead: true,
            dirty: false,
            shallow: false,
            submodules: [],
            limitations: []);

        var reconciled = frozen.ReconcileBase(pinnedOldCommit);

        var baseReference = typeof(FrozenPlan).GetProperty("BaseReference");
        Assert.NotNull(baseReference);
        Assert.Equal("HEAD", baseReference.GetValue(frozen));
        Assert.False(reconciled.DownstreamReady);
        Assert.Contains(reconciled.Limitations, item => item.Contains("ref", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("refs/tags/v1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("main")]
    [InlineData("HEAD")]
    [InlineData("refs/heads/main@{1}")]
    [InlineData("refs/heads/main~1")]
    [InlineData("refs/heads/main^")]
    [InlineData("refs/heads/main:path")]
    [InlineData("refs/heads/main..backup")]
    [InlineData("refs/heads/.hidden")]
    [InlineData("refs/heads/topic.lock")]
    [InlineData("refs/heads/topic.")]
    [InlineData("refs/heads/feature//topic")]
    [InlineData("refs/heads/feature?topic")]
    public void Non_freshness_bearing_refs_cannot_reconcile_an_unchanged_base(string requestedRef)
    {
        var draft = Draft();
        var baseContext = RepositoryContext.Create(
            "/portable/display-only",
            requestedRef,
            draft.Repository.Commit,
            draft.Repository.Tree,
            detachedHead: false,
            dirty: false,
            shallow: false,
            submodules: [],
            limitations: []);
        var frozen = PlanFreezer.Freeze(draft with { Repository = baseContext }, "revision-1", ObservedAt);
        var snapshot = RepositoryContext.Create(
            "/portable/display-only",
            requestedRef,
            frozen.BaseCommit,
            frozen.BaseTree,
            detachedHead: false,
            dirty: false,
            shallow: false,
            submodules: [],
            limitations: []);

        Assert.False(frozen.ReconcileBase(snapshot).DownstreamReady);
    }

    [Theory]
    [InlineData("refs/heads/main")]
    [InlineData("refs/heads/feature/topic")]
    [InlineData("refs/heads/topic.LOCK")]
    [InlineData("refs/heads/@")]
    public void Same_exact_branch_ref_and_unchanged_base_preserves_downstream_readiness(string requestedRef)
    {
        var draft = Draft();
        var branch = RepositoryContext.Create(
            "/portable/display-only",
            requestedRef,
            draft.Repository.Commit,
            draft.Repository.Tree,
            detachedHead: false,
            dirty: false,
            shallow: false,
            submodules: [],
            limitations: [],
            exactBranchReferenceVerified: true);
        var frozen = PlanFreezer.Freeze(draft with { Repository = branch }, "revision-1", ObservedAt);
        var sameBranch = RepositoryContext.Create(
            "/portable/display-only",
            requestedRef,
            frozen.BaseCommit,
            frozen.BaseTree,
            detachedHead: false,
            dirty: false,
            shallow: false,
            submodules: [],
            limitations: [],
            exactBranchReferenceVerified: true);

        Assert.Same(frozen, frozen.ReconcileBase(sameBranch));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Base_reconciliation_requires_exact_branch_verification_on_both_sides(
        bool frozenRefVerified,
        bool currentRefVerified)
    {
        var draft = Draft();
        var branch = RepositoryContext.Create(
            "/portable/display-only", "refs/heads/main", draft.Repository.Commit, draft.Repository.Tree,
            detachedHead: false, dirty: false, shallow: false, submodules: [], limitations: [],
            exactBranchReferenceVerified: frozenRefVerified);
        var frozen = PlanFreezer.Freeze(draft with { Repository = branch }, "revision-bound-branch", ObservedAt);
        var current = RepositoryContext.Create(
            "/portable/display-only", "refs/heads/main", frozen.BaseCommit, frozen.BaseTree,
            detachedHead: false, dirty: false, shallow: false, submodules: [], limitations: [],
            exactBranchReferenceVerified: currentRefVerified);

        Assert.False(frozen.ReconcileBase(current).DownstreamReady);
    }

    [Fact]
    public void Detached_checkout_reconciles_the_exact_same_mutable_branch_ref_commit_and_tree()
    {
        var draft = Draft();
        var detachedBranch = RepositoryContext.Create(
            "/portable/display-only",
            "refs/heads/main",
            draft.Repository.Commit,
            draft.Repository.Tree,
            detachedHead: true,
            dirty: false,
            shallow: false,
            submodules: [],
            limitations: [],
            exactBranchReferenceVerified: true);
        var frozen = PlanFreezer.Freeze(
            draft with { Repository = detachedBranch },
            "revision-detached-branch",
            ObservedAt);
        var current = RepositoryContext.Create(
            "/portable/display-only",
            "refs/heads/main",
            frozen.BaseCommit,
            frozen.BaseTree,
            detachedHead: true,
            dirty: false,
            shallow: false,
            submodules: [],
            limitations: [],
            exactBranchReferenceVerified: true);

        Assert.Same(frozen, frozen.ReconcileBase(current));
    }

    [Fact]
    public void Detached_checkout_still_reports_drift_for_a_changed_mutable_branch_ref()
    {
        var draft = Draft();
        var detachedBranch = RepositoryContext.Create(
            "/portable/display-only", "refs/heads/main", draft.Repository.Commit, draft.Repository.Tree,
            detachedHead: true, dirty: false, shallow: false, submodules: [], limitations: [],
            exactBranchReferenceVerified: true);
        var frozen = PlanFreezer.Freeze(
            draft with { Repository = detachedBranch },
            "revision-detached-branch-drift",
            ObservedAt);
        var changed = RepositoryContext.Create(
            "/portable/display-only", "refs/heads/main", new string('d', 40), new string('e', 40),
            detachedHead: true, dirty: false, shallow: false, submodules: [], limitations: [],
            exactBranchReferenceVerified: true);

        var reconciled = frozen.ReconcileBase(changed);

        Assert.False(reconciled.DownstreamReady);
        Assert.Contains(reconciled.Limitations, item => item.Contains("drift", StringComparison.OrdinalIgnoreCase));
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
    [InlineData("found in /opt/build/private-index")]
    public void Portable_plan_rejects_cross_platform_host_paths(string privateText)
    {
        var draft = Draft() with { Provenance = [RepositoryEvidence(privateText)] };
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(draft, "revision-1", ObservedAt));
        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(Draft(), privateText, ObservedAt));
    }

    [Theory]
    [InlineData("found in ~alice/private-index")]
    [InlineData("found in $HOME/private-index")]
    [InlineData("found in ${HOME}/private-index")]
    [InlineData("found in %USERPROFILE%\\private-index")]
    [InlineData("found in \\Users\\alice\\private-index")]
    [InlineData("secret=do-not-copy")]
    [InlineData("passwd: do-not-copy")]
    [InlineData("access_key=do-not-copy")]
    [InlineData("aws_access_key_id=DO-NOT-COPY")]
    public void Portable_plan_rejects_home_aliases_and_common_credential_assignments(string privateText)
    {
        var original = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(privateText)];
        var draft = original with
        {
            Provenance = provenance,
            Intake = IntakePlanner.Assess(original.Request, provenance)
        };

        var error = Assert.Throws<PlanningException>(() =>
            PlanFreezer.Freeze(draft, "revision-private-material", ObservedAt));
        Assert.Contains("host path or credential", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("found in $env:USERPROFILE\\private-index")]
    [InlineData("found in $env:HOME/private-index")]
    [InlineData("found in %APPDATA%\\private-index")]
    [InlineData("found in %LOCALAPPDATA%\\private-index")]
    [InlineData("sk-proj-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("tool --password hunter2")]
    [InlineData("curl -u alice:hunter2 https://example.invalid")]
    public void Portable_plan_and_imported_context_reject_current_home_and_cli_secret_forms(string privateText)
    {
        var original = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(privateText)];
        var draft = original with
        {
            Provenance = provenance,
            Intake = IntakePlanner.Assess(original.Request, provenance)
        };
        Assert.Throws<PlanningException>(() =>
            PlanFreezer.Freeze(draft, "revision-current-private-material", ObservedAt));

        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            entries = new[] { new { kind = "memory", locator = "memory:item-1", summary = privateText, digest = (string?)null, observedAt = "2026-10-07T20:00:00Z" } },
            conflicts = Array.Empty<string>(),
            limitations = Array.Empty<string>()
        }));
        Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(json));
    }

    [Theory]
    [InlineData("found in ${env:USERPROFILE}\\.ssh\\id_rsa")]
    [InlineData("found in $env:APPDATA\\Code\\User\\settings.json")]
    [InlineData("found in $env:LOCALAPPDATA/tool/cache")]
    [InlineData("found in $env:HOMEPATH\\private-index")]
    [InlineData("sk-admin-12345678901234567890")]
    [InlineData("sk-live_service-12345678901234567890")]
    [InlineData("curl --user alice:hunter2 https://example.invalid")]
    [InlineData("curl --user=alice:hunter2 https://example.invalid")]
    [InlineData("curl -ualice:hunter2 https://example.invalid")]
    [InlineData("curl -fsu alice:hunter2 https://example.invalid")]
    public void Portable_consumers_reject_powerShell_key_and_curl_credential_form_classes(string privateText)
    {
        var original = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(privateText)];
        var draft = original with
        {
            Provenance = provenance,
            Intake = IntakePlanner.Assess(original.Request, provenance)
        };
        Assert.Throws<PlanningException>(() =>
            PlanFreezer.Freeze(draft, "revision-private-form-class", ObservedAt));

        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            entries = new[] { new { kind = "memory", locator = "memory:item-1", summary = privateText, digest = (string?)null, observedAt = "2026-10-07T20:00:00Z" } },
            conflicts = Array.Empty<string>(),
            limitations = Array.Empty<string>()
        }));
        Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(json));
    }

    [Theory]
    [InlineData("make CFLAGS=-I/home/alice/sdk/include")]
    [InlineData("cc -L/opt/alice/lib app.c")]
    [InlineData("cl -IC:/Users/alice/sdk/include app.c")]
    [InlineData("make CFLAGS=\"-I/home/alice/sdk/include\"")]
    [InlineData("'-L/opt/alice/lib'")]
    [InlineData("(-I/home/alice/inc)")]
    [InlineData("cc -Wl,-L/home/alice/lib")]
    [InlineData("cl \"-IC:/Users/alice/sdk\"")]
    [InlineData("cl /IC:\\work\\alice\\sdk")]
    [InlineData("cc /I/home/alice/sdk/include")]
    [InlineData("-I~/sdk/include")]
    [InlineData("-I$HOME/sdk/include")]
    [InlineData("-I%USERPROFILE%\\sdk")]
    [InlineData("tool -I\\work\\alice\\repo")]
    [InlineData("found at \\work\\alice\\repo")]
    [InlineData(@"cc -I\\fileserver\alice\include app.c")]
    [InlineData(@"cl /I\\fileserver\alice\include app.c")]
    [InlineData(@"cc ""-I\\fileserver\alice\include"" app.c")]
    [InlineData(@"cl '/I\\fileserver\alice\include' app.c")]
    [InlineData(@"cc -I=\\fileserver\alice\include app.c")]
    [InlineData(@"cl /I:\\fileserver\alice\include app.c")]
    [InlineData(@"cc -Wl,-rpath,\\fileserver\alice\lib app.c")]
    public void Portable_consumers_reject_option_attached_absolute_host_paths(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-option-attached-host-path");
    }

    [Theory]
    [InlineData("curl --proxy-user alice:hunter2 https://example.invalid")]
    [InlineData("curl --proxy-user=alice:hunter2 https://example.invalid")]
    [InlineData("curl -x alice:hunter2@proxy.example:3128 https://example.invalid")]
    [InlineData("curl -xalice:hunter2@proxy.example:3128 https://example.invalid")]
    [InlineData("curl --proxy alice:hunter2@proxy.example:3128 https://example.invalid")]
    [InlineData("curl --proxy=alice:hunter2@proxy.example:3128 https://example.invalid")]
    [InlineData("curl -H \"Authorization: token 0123456789abcdef0123456789abcdef01234567\" https://example.invalid")]
    [InlineData("curl -H \"Authorization: Digest opaque-value\" https://example.invalid")]
    [InlineData("curl -sSx alice:hunter2@proxy.example:3128 https://example.invalid")]
    [InlineData("curl -fsxalice:hunter2@proxy.example:3128 https://example.invalid")]
    [InlineData("curl --proxy1.0 alice:hunter2@proxy.example:3128 https://example.invalid")]
    [InlineData("curl --proxy1.0=alice:hunter2@proxy.example:3128 https://example.invalid")]
    [InlineData("curl --preproxy alice:hunter2@proxy.example:3128 https://example.invalid")]
    [InlineData("curl --preproxy=alice:hunter2@proxy.example:3128 https://example.invalid")]
    [InlineData("curl --socks4 alice:hunter2@proxy.example:1080 https://example.invalid")]
    [InlineData("curl --socks4a=alice:hunter2@proxy.example:1080 https://example.invalid")]
    [InlineData("curl --socks5 alice:hunter2@proxy.example:1080 https://example.invalid")]
    [InlineData("curl --socks5-hostname=alice:hunter2@proxy.example:1080 https://example.invalid")]
    [InlineData("curl -H \"Authorization: lin_api_0123456789\" https://example.invalid")]
    [InlineData("curl -H \"Authorization: 0123456789\" https://example.invalid")]
    [InlineData("Authorization: x")]
    public void Portable_consumers_reject_curl_proxy_credentials(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-curl-proxy-credential");
    }

    [Theory]
    [InlineData("{\"Authorization\": \"Basic abc123\"}")]
    [InlineData("{'Authorization': 'Bearer abc123'}")]
    [InlineData("@{ Authorization = 'Digest abc123' }")]
    [InlineData("@{ 'Authorization' = \"Token abc123\" }")]
    [InlineData("Authorization = Basic abc123")]
    [InlineData("\"Authorization\": \"Basic abc123")]
    [InlineData("'Authorization' = 'Bearer abc123")]
    public void Portable_consumers_reject_populated_authorization_maps(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-authorization-map");
    }

    [Theory]
    [InlineData("curl \\\n        --user alice: https://example.invalid")]
    [InlineData("curl `\n        --user :hunter2 https://example.invalid")]
    [InlineData("curl ^\r\n        -U proxy-user: https://example.invalid")]
    [InlineData("curl \\\n        --proxy-user :proxy-password https://example.invalid")]
    [InlineData("curl -fsu \\\n        alice: https://example.invalid")]
    [InlineData("curl -x \\\n        :proxy-password@proxy.example:3128 https://example.invalid")]
    public void Portable_consumers_reject_curl_credentials_across_explicit_continuations(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-curl-continuation");
    }

    [Theory]
    [InlineData("curl -u alice: https://example.invalid")]
    [InlineData("curl -u :hunter2 https://example.invalid")]
    [InlineData("curl -ualice: https://example.invalid")]
    [InlineData("curl -fsu:hunter2 https://example.invalid")]
    [InlineData("curl --user=alice: https://example.invalid")]
    [InlineData("curl --user :hunter2 https://example.invalid")]
    [InlineData("curl -U proxy-user: https://example.invalid")]
    [InlineData("curl -fsU:proxy-password https://example.invalid")]
    [InlineData("curl --proxy-user=proxy-user: https://example.invalid")]
    [InlineData("curl --proxy-user :proxy-password https://example.invalid")]
    [InlineData("curl --proxy=:proxy-password@proxy.example:3128 https://example.invalid")]
    [InlineData("curl -xproxy-user:@proxy.example:3128 https://example.invalid")]
    public void Portable_consumers_reject_one_sided_curl_userinfo(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-one-sided-curl-userinfo");
    }

    [Theory]
    [InlineData("'api_key' = 'private-value'")]
    [InlineData("'api-key': 'private-value'")]
    [InlineData("'password' = 'private-value'")]
    [InlineData("'passwd': 'private-value'")]
    [InlineData("'token' = 'private-value'")]
    [InlineData("'secret': 'private-value'")]
    [InlineData("'client_secret' = 'private-value'")]
    [InlineData("'client-secret': 'private-value'")]
    [InlineData("'access_key' = 'private-value'")]
    [InlineData("'access-key': 'private-value'")]
    [InlineData("'aws_access_key_id' = 'private-value'")]
    [InlineData("'aws-access-key-id': 'private-value'")]
    [InlineData("\"api_key\" = \"private-value\"")]
    [InlineData("\"password\": \"private-value\"")]
    [InlineData("\"token\" = \"private-value\"")]
    [InlineData("\"client-secret\": \"private-value\"")]
    [InlineData("\"access-key\" = \"private-value\"")]
    [InlineData("\"aws-access-key-id\": \"private-value\"")]
    public void Portable_consumers_reject_quoted_credential_assignment_keys(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-quoted-credential-key");
    }

    [Theory]
    [InlineData("curl -d \"password=hunter2\" https://example.invalid")]
    [InlineData("curl --data-urlencode 'token=private-value' https://example.invalid")]
    [InlineData("curl -F \"client_secret=private-value\" https://example.invalid")]
    [InlineData("curl --form-string 'api_key=private-value' https://example.invalid")]
    [InlineData("curl -d \\\"access_token=private-value\\\" https://example.invalid")]
    [InlineData("payload='DB_PASSWORD=private-value'")]
    public void Portable_consumers_reject_quote_adjacent_credential_assignments(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-quote-adjacent-credential");
    }

    [Theory]
    [InlineData("Proxy-Authorization: Basic private-value")]
    [InlineData("X-API-Key: private-value")]
    [InlineData("apiKey=private-value")]
    [InlineData("clientSecret: private-value")]
    [InlineData("accessToken=private-value")]
    [InlineData("DB_PASSWORD=private-value")]
    [InlineData("PGPASSWORD=private-value")]
    [InlineData("NPM_TOKEN=private-value")]
    [InlineData("AWS_SECRET_ACCESS_KEY=private-value")]
    public void Portable_consumers_reject_compound_and_environment_credential_keys(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-compound-credential-key");
    }

    [Theory]
    [InlineData("env MODE=test curl -u alice:hunter2 https://example.invalid")]
    [InlineData("sudo curl.exe --user alice:hunter2 https://example.invalid")]
    [InlineData("$ curl -u alice:hunter2 https://example.invalid")]
    [InlineData("PS> curl.exe --user=alice:hunter2 https://example.invalid")]
    [InlineData("$(curl -u alice:hunter2 https://example.invalid )")]
    [InlineData("(curl.exe --user alice:hunter2 https://example.invalid )")]
    [InlineData("Run `curl -u alice:hunter2 https://example.invalid` to verify")]
    [InlineData("```sh\ncurl.exe --user alice:hunter2 https://example.invalid\n```")]
    public void Portable_consumers_reject_credentials_on_supported_embedded_curl_executables(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-embedded-curl-executable");
    }

    [Theory]
    [InlineData("The quoted prose 'password policy' remains portable")]
    [InlineData("Proxy-Authorization headers are described in the manual")]
    [InlineData("X-API-Key is the documented header name")]
    [InlineData("passwordPolicy=strict")]
    [InlineData("tokenizer=bounded")]
    [InlineData("secretary=available")]
    [InlineData("DB_PASSWORD_POLICY=twelve-characters")]
    [InlineData("NPM_TOKEN_FORMAT=opaque")]
    [InlineData("Read https://example.invalid/docs/curl-u-example")]
    [InlineData("The curl.exe -u option accepts a user name and password")]
    [InlineData("docker run -u 1000:1000 curl-image")]
    [InlineData("curl -H \"Proxy-Authorization:\" https://example.invalid")]
    [InlineData("curl -H 'X-API-Key:   ' https://example.invalid")]
    [InlineData("env MODE=test curl --user-agent delivery-forge https://example.invalid")]
    public void Portable_consumers_preserve_round_thirteen_sibling_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-thirteen-control");
    }

    [Theory]
    [InlineData("RESPONSE=$(curl -u alice:hunter2 https://example.invalid/health)")]
    [InlineData("sudo -u deploy curl -u alice:hunter2 https://example.invalid/health")]
    [InlineData("$ sudo curl -u alice:hunter2 https://example.invalid/health")]
    [InlineData("user@host:~$ curl -u alice:hunter2 https://example.invalid/health")]
    [InlineData("# curl.exe --user alice:hunter2 https://example.invalid/health")]
    [InlineData("- curl -u alice:hunter2 https://example.invalid/health")]
    [InlineData("* curl.exe --user=alice:hunter2 https://example.invalid/health")]
    [InlineData("if curl -u alice:hunter2 https://example.invalid/health; then echo healthy; fi")]
    [InlineData("time curl -u alice:hunter2 https://example.invalid/health")]
    [InlineData("timeout 5 curl -u alice:hunter2 https://example.invalid/health")]
    [InlineData("nice -n 5 curl -u alice:hunter2 https://example.invalid/health")]
    [InlineData("exec curl -u alice:hunter2 https://example.invalid/health")]
    [InlineData("xargs curl -u alice:hunter2 https://example.invalid/health")]
    [InlineData("env -u CURL_HOME MODE=test curl.exe --user alice:hunter2 https://example.invalid/health")]
    [InlineData("command -- curl -u alice:hunter2 https://example.invalid/health")]
    public void Portable_consumers_reject_round_fourteen_curl_executable_positions(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-fourteen-curl-position");
    }

    [Theory]
    [InlineData("Don't commit DB_PASSWORD='hunter2'")]
    [InlineData("It's configured as password: 'hunter2'")]
    [InlineData("{\"size\":\"6\\\" pipe\",\"password\":\"hunter2\"}")]
    [InlineData("The note \\\"quoted\\\" earlier says authToken='private-value'")]
    public void Portable_consumers_reject_round_fourteen_locally_quoted_assignments(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-fourteen-local-quote");
    }

    [Theory]
    [InlineData("db_password: private-value")]
    [InlineData("db-password=private-value")]
    [InlineData("dbPassword=private-value")]
    [InlineData("DbPassword=private-value")]
    [InlineData("DBPassword=private-value")]
    [InlineData("\"refresh_token\": \"private-value\"")]
    [InlineData("authToken=private-value")]
    [InlineData("aws_session_token = private-value")]
    [InlineData("AWS_SESSION_TOKEN=private-value")]
    [InlineData("{\"SecretAccessKey\": \"private-value\"}")]
    [InlineData("{\"AWSSecretAccessKey\": \"private-value\"}")]
    [InlineData("{\"SessionToken\": \"private-value\"}")]
    [InlineData("aws-secret-access-key: private-value")]
    public void Portable_consumers_reject_round_fourteen_terminal_credential_words(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-fourteen-terminal-word");
    }

    [Theory]
    [InlineData("Use `curl` for health checks and `docker run -u 1000:1000 image` for the job")]
    [InlineData("Use `curl https://example.invalid/health` then `docker run -u 1000:1000 image`")]
    [InlineData("$(curl https://example.invalid/health) && docker run -u 1000:1000 image")]
    [InlineData("RESULT=$(curl https://example.invalid/health); docker run -u 1000:1000 image")]
    [InlineData("The scurl-helper -u 1000:1000 example is prose")]
    [InlineData("passwordPolicy=strict")]
    [InlineData("tokenizer=bounded")]
    [InlineData("secretary=available")]
    [InlineData("DB_PASSWORD_POLICY=twelve-characters")]
    [InlineData("NPM_TOKEN_FORMAT=opaque")]
    [InlineData("aws_session_token_format=opaque")]
    [InlineData("secret_access_key_policy=rotated")]
    [InlineData("{\"Authorization\": \"\"}")]
    [InlineData("{'Authorization' = '   '}")]
    [InlineData("curl -H \"Proxy-Authorization:\" https://example.invalid")]
    [InlineData("curl -H 'X-API-Key:   ' https://example.invalid")]
    public void Portable_consumers_preserve_round_fourteen_scope_and_key_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-fourteen-control");
    }

    [Theory]
    [InlineData("Don't run curl -u alice:hunter2 https://example.invalid")]
    [InlineData("It's easiest to run `curl --user alice:hunter2 https://example.invalid`")]
    [InlineData("The operator's note says: ```sh\ncurl.exe -u alice:hunter2 https://example.invalid\n```")]
    [InlineData("The operators' note says run curl -u alice:hunter2 https://example.invalid")]
    [InlineData("The 6\" pipe note says run `curl -u alice:hunter2 https://example.invalid`")]
    public void Portable_consumers_reject_round_fifteen_curl_after_prose_apostrophes(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-fifteen-apostrophe-curl");
    }

    [Theory]
    [InlineData("sh -c \"curl -u alice:hunter2 https://example.invalid\"")]
    [InlineData("bash -c 'curl --user alice:hunter2 https://example.invalid'")]
    [InlineData("docker exec app sh -c \"curl -u alice:hunter2 https://example.invalid\"")]
    [InlineData("ssh example.invalid 'curl --user alice:hunter2 https://service.invalid'")]
    [InlineData("pwsh -Command \"curl.exe --user alice:hunter2 https://example.invalid\"")]
    [InlineData("HEALTHCHECK CMD [\"curl\", \"-u\", \"alice:hunter2\", \"https://example.invalid\"]")]
    [InlineData("command: [\"curl.exe\", \"--user=alice:hunter2\", \"https://example.invalid\"]")]
    public void Portable_consumers_reject_round_fifteen_wrapped_and_exec_array_curl(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-fifteen-wrapped-curl");
    }

    [Theory]
    [InlineData("\\curl -u alice:hunter2 https://example.invalid")]
    [InlineData("./curl --user alice:hunter2 https://example.invalid")]
    [InlineData(".\\curl.exe -u alice:hunter2 https://example.invalid")]
    [InlineData("../curl --user=alice:hunter2 https://example.invalid")]
    [InlineData("..\\curl.exe -ualice:hunter2 https://example.invalid")]
    [InlineData("../../curl --user alice:hunter2 https://example.invalid")]
    public void Portable_consumers_reject_round_fifteen_bounded_curl_paths(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-fifteen-curl-path");
    }

    [Theory]
    [InlineData("curl -d \"{\\\"password\\\":\\\"hunter2\\\"}\" https://example.invalid")]
    [InlineData("curl -d \"{\\\"Authorization\\\":\\\"Basic YWxpY2U6aHVudGVyMg==\\\"}\" https://example.invalid")]
    [InlineData("curl -d '{`\"password`\": `\"hunter2`\"}' https://example.invalid")]
    [InlineData("curl -d '{`\"Authorization`\": `\"Basic YWxpY2U6aHVudGVyMg==`\"}' https://example.invalid")]
    [InlineData("curl -d \"{^\"password^\":^\"hunter2^\"}\" https://example.invalid")]
    [InlineData("curl -d \"{\\'secret_key\\':\\'private-value\\'}\" https://example.invalid")]
    public void Portable_consumers_reject_round_fifteen_escaped_credential_keys(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-fifteen-escaped-key");
    }

    [Theory]
    [InlineData("SECRET_KEY=private-value")]
    [InlineData("MINIO_SECRET_KEY=private-value")]
    [InlineData("stripe-secret-key: private-value")]
    [InlineData("privateKey=private-value")]
    [InlineData("TlsPrivateKey=private-value")]
    [InlineData("SIGNING_KEY=private-value")]
    [InlineData("jwtSigningKey=private-value")]
    [InlineData("encryption-key: private-value")]
    [InlineData("StorageEncryptionKey=private-value")]
    public void Portable_consumers_reject_round_fifteen_terminal_credential_key_pairs(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-fifteen-terminal-key-pair");
    }

    [Theory]
    [InlineData("The operator's guide says curl accepts authentication options")]
    [InlineData("The operators' guide says curl accepts authentication options")]
    [InlineData("The 6\" pipe guide says curl accepts authentication options")]
    [InlineData("It's safe to run `curl https://example.invalid/health`")]
    [InlineData("sh -c \"curl https://example.invalid/health\"")]
    [InlineData("HEALTHCHECK CMD [\"curl\", \"-fsS\", \"https://example.invalid/health\"]")]
    [InlineData("./curl --user-agent delivery-forge https://example.invalid")]
    [InlineData("\\curl https://example.invalid/health")]
    [InlineData("curl -d \"{\\\"Authorization\\\":\\\"\\\"}\" https://example.invalid")]
    [InlineData("curl -d '{`\"password`\": `\"   `\"}' https://example.invalid")]
    [InlineData("primary_key=id")]
    [InlineData("sort_key=created_at")]
    [InlineData("cache_key=planning-v1")]
    [InlineData("private_key_policy=managed")]
    [InlineData("signing_key_format=pem")]
    [InlineData("ENCRYPTION_KEY_POLICY=rotated")]
    [InlineData("Both curl and docker run -u 1000:1000 image are documented")]
    [InlineData("docker exec -u 1000:1000 app healthcheck")]
    [InlineData("Both curl and podman run --user 1000:1000 image are documented")]
    public void Portable_consumers_preserve_round_fifteen_sibling_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-fifteen-control");
    }

    [Theory]
    [InlineData("C:\\private\\curl.exe --user-agent delivery-forge https://example.invalid")]
    [InlineData("/private/tools/curl --user-agent delivery-forge https://example.invalid")]
    [InlineData("\\curl\\private.txt")]
    [InlineData("\\\\server\\share\\curl.exe --user-agent delivery-forge https://example.invalid")]
    public void Portable_consumers_still_reject_round_fifteen_private_curl_paths(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-fifteen-private-curl-path");
    }

    [Theory]
    [InlineData("Run C:\\curl.exe -u alice:hunter2 https://example.invalid")]
    [InlineData("%USERPROFILE%\\curl.exe --version")]
    [InlineData("~\\curl.exe -u alice:hunter2 https://example.invalid")]
    [InlineData("${HOME}\\curl --user alice:hunter2 https://example.invalid")]
    [InlineData("\\\\server\\share\\curl.exe -u alice:hunter2 https://example.invalid")]
    public void Portable_consumers_reject_round_sixteen_private_curl_alias_boundaries(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-sixteen-private-curl-alias");
    }

    [Theory]
    [InlineData("Use \"`curl -u alice:hunter2 https://example.invalid`\" for checks")]
    [InlineData("The `curl`'s manual shows `curl -u alice:hunter2 https://example.invalid`")]
    [InlineData("The operator said \"use this later: `curl --user alice:hunter2 https://example.invalid`")]
    [InlineData("The operators' notes say ```sh\ncurl.exe -u alice:hunter2 https://example.invalid\n```")]
    public void Portable_consumers_reject_round_sixteen_curl_after_local_prose_punctuation(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-sixteen-local-prose-punctuation");
    }

    [Theory]
    [InlineData("curl -d 'status=built and tested' -u ci:hunter2 https://hooks.example.invalid")]
    [InlineData("curl --data \"status=ready then shipped\" --user ci:hunter2 https://hooks.example.invalid")]
    [InlineData("curl -A docker -u alice:hunter2 https://registry.example.invalid")]
    [InlineData("curl --user-agent podman --user alice:hunter2 https://registry.example.invalid")]
    [InlineData("curl -H 'X-Tool: kubectl' -u alice:hunter2 https://example.invalid")]
    public void Portable_consumers_reject_round_sixteen_credentials_after_curl_option_values(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-sixteen-curl-option-arity");
    }

    [Theory]
    [InlineData("{\n  \"command\": [\n    \"curl\",\n    \"-u\",\n    \"alice:hunter2\",\n    \"https://example.invalid\"\n  ]\n}")]
    [InlineData("command:\n- curl\n- -u\n- alice:hunter2\n- https://example.invalid")]
    [InlineData("command:\n  - curl.exe\n  - --user=alice:hunter2\n  - https://example.invalid")]
    [InlineData("HEALTHCHECK CMD [\n  \"curl\",\n  \"--user\",\n  \"alice:hunter2\",\n  \"https://example.invalid\"\n]")]
    public void Portable_consumers_reject_round_sixteen_multiline_exec_sequence_credentials(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-sixteen-multiline-exec");
    }

    [Theory]
    [InlineData("curl.exe -d \"{\"\"password\"\":\"\"hunter2\"\"}\" https://example.invalid")]
    [InlineData("curl.exe -d \"{\"\"Authorization\"\":\"\"Basic YWxpY2U6aHVudGVyMg==\"\"}\" https://example.invalid")]
    [InlineData("curl.exe -d '{''secret_key'':''private-value''}' https://example.invalid")]
    public void Portable_consumers_reject_round_sixteen_doubled_quote_credential_keys(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-sixteen-doubled-quote-key");
    }

    [Theory]
    [InlineData(".\\curl.exe --user-agent delivery-forge https://example.invalid")]
    [InlineData("The operator's guide says `curl https://example.invalid/health`")]
    [InlineData("Use \"quoted prose and punctuation without a closing quote")]
    [InlineData("curl -d 'status=built and tested' https://hooks.example.invalid")]
    [InlineData("curl -A docker https://registry.example.invalid")]
    [InlineData("curl https://example.invalid and docker run -u 1000:1000 image")]
    [InlineData("curl https://example.invalid then podman run --user 1000:1000 image")]
    [InlineData("curl https://example.invalid and kubectl exec pod -- id -u")]
    [InlineData("{\n  \"command\": [\n    \"curl\",\n    \"--user-agent\",\n    \"delivery-forge\",\n    \"https://example.invalid\"\n  ]\n}")]
    [InlineData("The command is curl.\nThe separate example uses -u 1000:1000 for a container.")]
    [InlineData("curl.exe -d \"{\"\"password\"\":\"\"\"\"}\" https://example.invalid")]
    [InlineData("curl.exe -d \"{\"\"Authorization\"\":\"\"   \"\"}\" https://example.invalid")]
    public void Portable_consumers_preserve_round_sixteen_sibling_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-sixteen-control");
    }

    [Theory]
    [InlineData("`\"curl.exe`\" --oauth2-bearer private-token https://example.invalid")]
    [InlineData("^\"curl^\" --pass private-phrase https://example.invalid")]
    [InlineData("\\\"curl.exe\\\" --proxy-pass private-phrase https://example.invalid")]
    [InlineData("'curl' --tlspassword private-phrase https://example.invalid")]
    [InlineData("\"curl.exe\" --proxy-tlspassword private-phrase https://example.invalid")]
    public void Portable_consumers_reject_round_seventeen_quoted_and_escaped_curl_executables(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-seventeen-quoted-curl");
    }

    [Theory]
    [InlineData("command:\n  - curl\nargs:\n  - --oauth2-bearer\n  - private-token\n  - https://example.invalid")]
    [InlineData("spec:\n  command: [\n    \"curl.exe\"\n  ]\n  args: [\n    \"--pass\",\n    \"private-phrase\",\n    \"https://example.invalid\"\n  ]")]
    [InlineData("healthcheck:\n  test:\n    - CMD\n    - curl\n    - --proxy-pass\n    - private-phrase\n    - https://example.invalid")]
    [InlineData("ENTRYPOINT [\n  \"curl\"\n]\nCMD [\n  \"--oauth2-bearer\",\n  \"private-token\",\n  \"https://example.invalid\"\n]")]
    [InlineData("RUN [\n  \"curl\",\n  \"--pass\",\n  \"private-phrase\",\n  \"https://example.invalid\"\n]")]
    [InlineData("{\n  \"Cmd\": [\n    \"curl\",\n    \"--cert\",\n    \"client.pem:private-phrase\",\n    \"https://example.invalid\"\n  ]\n}")]
    [InlineData("{\n  \"Test\": [\n    \"CMD\",\n    \"curl\",\n    \"--cookie\",\n    \"session=private-value\",\n    \"https://example.invalid\"\n  ]\n}")]
    public void Portable_consumers_reject_round_seventeen_bounded_structured_curl_commands(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-seventeen-structured-curl");
    }

    [Theory]
    [InlineData("curl --output-dir docker --oauth2-bearer private-token https://example.invalid")]
    [InlineData("curl --request-target then --pass private-phrase https://example.invalid")]
    [InlineData("curl --proxy-cert-type kubectl --proxy-pass private-phrase https://example.invalid")]
    [InlineData("curl --url-query podman --tlspassword private-phrase https://example.invalid")]
    public void Portable_consumers_reject_round_seventeen_credentials_after_consumed_option_values(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-seventeen-option-arity");
    }

    [Theory]
    [InlineData("curl --oauth2-bearer private-token https://example.invalid")]
    [InlineData("curl --pass private-phrase https://example.invalid")]
    [InlineData("curl --proxy-pass=private-phrase https://example.invalid")]
    [InlineData("curl --tlspassword private-phrase https://example.invalid")]
    [InlineData("curl --proxy-tlspassword=private-phrase https://example.invalid")]
    [InlineData("curl --cert client.pem:private-phrase https://example.invalid")]
    [InlineData("curl --proxy-cert=client.pem:private-phrase https://example.invalid")]
    [InlineData("curl --cookie session=private-value https://example.invalid")]
    [InlineData("curl -bsession=private-value https://example.invalid")]
    public void Portable_consumers_reject_round_seventeen_fail_closed_curl_credentials(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-seventeen-fail-closed-curl");
    }

    [Theory]
    [InlineData("curl.exe -d \"{\"\"db.password\"\":\"\"hunter2\"\"}\" https://example.invalid")]
    [InlineData("curl.exe -d \"{\"\"spring.datasource.password\"\":\"\"hunter2\"\"}\" https://example.invalid")]
    [InlineData("curl.exe -d \"{\"\"x.api_key\"\":\"\"private-value\"\"}\" https://example.invalid")]
    public void Portable_consumers_reject_round_seventeen_dotted_doubled_quote_keys(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-seventeen-dotted-key");
    }

    [Theory]
    [InlineData("The quoted prose `\"curl\" accepts OAuth options` remains portable")]
    [InlineData("curl --oauth2-bearer '' https://example.invalid")]
    [InlineData("curl --pass= https://example.invalid")]
    [InlineData("curl --cookie '' https://example.invalid")]
    [InlineData("curl --cookie-jar cookies.txt https://example.invalid")]
    [InlineData("curl --output-dir docker https://example.invalid and docker run -u 1000:1000 image")]
    [InlineData("command:\n  - curl\nargs:\n  - --user-agent\n  - delivery-forge\n  - https://example.invalid")]
    [InlineData("command:\n  - curl\nunrelated:\n  args:\n    - --oauth2-bearer\n    - documentation-only")]
    [InlineData("ENTRYPOINT [\"curl\", \"https://example.invalid\"]\nRUN docker run -u 1000:1000 image")]
    public void Portable_consumers_preserve_round_seventeen_false_positive_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-seventeen-control");
    }

    [Theory]
    [InlineData("curl -R -u alice:hunter2 https://example.invalid")]
    [InlineData("curl -RsS --user alice:hunter2 https://example.invalid")]
    [InlineData("curl -h --proxy-user proxy:private https://example.invalid")]
    [InlineData("curl --help --oauth2-bearer private-token https://example.invalid")]
    [InlineData("curl --help=all --cookie session=private-value https://example.invalid")]
    [InlineData("curl --haproxy-clientip 192.0.2.10 --user alice:hunter2 https://example.invalid")]
    [InlineData("curl --output result.txt --user alice:hunter2 https://example.invalid")]
    [InlineData("curl --connect-timeout then --user alice:hunter2 https://example.invalid")]
    [InlineData("curl --expand-remote-time --user alice:hunter2 https://example.invalid")]
    public void Portable_consumers_reject_round_eighteen_credentials_after_authoritative_option_arities(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-eighteen-option-semantics");
    }

    [Theory]
    [InlineData("curl alice:hunter2@example.invalid")]
    [InlineData("curl -fsS alice:hunter2@example.invalid/path")]
    [InlineData("curl --url alice:hunter2@example.invalid/resource")]
    [InlineData("curl --url=alice:hunter2@example.invalid/resource")]
    [InlineData("curl --expand-url 'alice:hunter2@example.invalid/{{path}}'")]
    public void Portable_consumers_reject_round_eighteen_schemeless_url_userinfo(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-eighteen-schemeless-userinfo");
    }

    [Theory]
    [InlineData("curl --expand-user alice:hunter2 https://example.invalid")]
    [InlineData("curl --expand-user=alice:hunter2 https://example.invalid")]
    [InlineData("curl --expand-proxy-user proxy:private https://example.invalid")]
    [InlineData("curl --expand-proxy http://proxy:private@proxy.example:3128 https://example.invalid")]
    [InlineData("curl --expand-cookie session=private-value https://example.invalid")]
    [InlineData("curl --expand-cert client.pem:private-phrase https://example.invalid")]
    [InlineData("curl --no-user=alice:hunter2 https://example.invalid")]
    [InlineData("curl --no-user alice:hunter2 https://example.invalid")]
    [InlineData("curl --no-cookie session=private-value https://example.invalid")]
    public void Portable_consumers_reject_round_eighteen_modifier_credentials(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-eighteen-modifier-credential");
    }

    [Theory]
    [InlineData("args:\n  - --user\n  - alice:hunter2\ncommand:\n  - curl\n  - https://example.invalid")]
    [InlineData("entrypoint: [\"curl\"]\nimage: curlimages/curl:8.5.0\ncommand: [\"--user\", \"alice:hunter2\", \"https://example.invalid\"]")]
    [InlineData("command: [\"--oauth2-bearer\", \"private-token\", \"https://example.invalid\"]\n# entrypoint is intentionally declared later\nentrypoint: [\"curl\"]")]
    [InlineData("{\n  \"Config\": {\n    \"Cmd\": [\"--cookie\", \"session=private-value\", \"https://example.invalid\"],\n    \"Image\": \"curlimages/curl:8.5.0\",\n    \"Entrypoint\": [\"curl\"]\n  }\n}")]
    [InlineData("{\n  \"Config\": {\n    \"Entrypoint\": [\"curl\"],\n    \"WorkingDir\": \"/workspace\",\n    \"Cmd\": [\"--pass\", \"private-phrase\", \"https://example.invalid\"]\n  }\n}")]
    [InlineData("containers:\n  - command: [\"curl\"]\n    name: probe\n    args: [\"--proxy-user\", \"proxy:private\", \"https://example.invalid\"]")]
    [InlineData("services:\n  probe:\n    entrypoint:\n      - curl\n    restart: on-failure\n    command:\n      - --cert\n      - client.pem:private-phrase\n      - https://example.invalid")]
    public void Portable_consumers_reject_round_eighteen_context_grouped_structured_commands(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-eighteen-structured-context");
    }

    [Theory]
    [InlineData("curl -R https://example.invalid")]
    [InlineData("curl -h auth")]
    [InlineData("curl --help=all")]
    [InlineData("curl --haproxy-clientip 192.0.2.10 https://example.invalid")]
    [InlineData("curl example.invalid:8443/health")]
    [InlineData("curl example.invalid/path")]
    [InlineData("curl --url example.invalid/resource")]
    [InlineData("curl --url '<user>:<password>@example.invalid/resource'")]
    [InlineData("curl '${USER}:${PASSWORD}@example.invalid/resource'")]
    [InlineData("curl --expand-user '' https://example.invalid")]
    [InlineData("curl --expand-cookie= https://example.invalid")]
    [InlineData("curl --no-user='' https://example.invalid")]
    [InlineData("command: [\"curl\", \"https://example.invalid\"]\nmetadata:\n  args: [\"--user\", \"documentation-only\"]")]
    [InlineData("containers:\n  - name: probe\n    command: [\"curl\"]\n  - name: unrelated\n    args: [\"--user\", \"documentation-only\"]")]
    [InlineData("{\n  \"one\": { \"Entrypoint\": [\"curl\"] },\n  \"two\": { \"Cmd\": [\"--user\", \"documentation-only\"] }\n}")]
    [InlineData("entrypoint: [\"curl\", \"https://example.invalid\"]\ncommand: [\"echo\", \"healthy\"]\nLater run docker run -u 1000:1000 image")]
    [InlineData("The prose example says command: curl, while args: --user documentation-only appears later.")]
    public void Portable_consumers_preserve_round_eighteen_sibling_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-eighteen-control");
    }

    [Theory]
    [InlineData("curl --no-location alice:hunter2@example.invalid/resource")]
    [InlineData("curl --no-silent alice:hunter2@example.invalid/resource")]
    [InlineData("curl --no-fail alice:hunter2@example.invalid/resource")]
    [InlineData("curl --no-insecure alice:hunter2@example.invalid/resource")]
    [InlineData("curl --no-compressed alice:hunter2@example.invalid/resource")]
    public void Portable_consumers_reject_round_nineteen_userinfo_after_no_value_negations(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-nineteen-no-value-negation");
    }

    [Theory]
    [InlineData("curl --pass -Zq9secret https://example.invalid")]
    [InlineData("curl --oauth2-bearer -private-token https://example.invalid")]
    [InlineData("curl --user -alice:hunter2 https://example.invalid")]
    [InlineData("curl --output -alice:hunter2@example.invalid https://example.invalid")]
    [InlineData("curl -o -alice:hunter2@example.invalid https://example.invalid")]
    [InlineData("curl --help alice:hunter2@example.invalid")]
    public void Portable_consumers_reject_round_nineteen_credential_values_consumed_by_arity(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-nineteen-consumed-value");
    }

    [Theory]
    [InlineData("curl alice:hunter2@[2001:db8::1]/resource")]
    [InlineData("curl --url alice:hunter2@[2001:db8::1]/resource")]
    [InlineData("curl alice:hunter2@{api,backup}.example.invalid/resource")]
    [InlineData("curl --url=alice:hunter2@{api,backup}.example.invalid/resource")]
    public void Portable_consumers_reject_round_nineteen_bracketed_and_globbed_userinfo(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-nineteen-userinfo-punctuation");
    }

    [Theory]
    [InlineData("services:\n  probe:\n    entrypoint: [\"curl\"]\n    healthcheck:\n      test: [\"CMD\", \"echo\", \"healthy\"]\n    command: [\"--pass\", \"private-phrase\", \"https://example.invalid\"]")]
    [InlineData("containers:\n  - name: probe\n    command: [\"curl\"]\n    livenessProbe:\n      exec:\n        command: [\"true\"]\n    args: [\"--oauth2-bearer\", \"private-token\", \"https://example.invalid\"]")]
    [InlineData("{\n  \"Config\": {\n    \"Entrypoint\": [\"curl\"],\n    \"Healthcheck\": {\n      \"Test\": [\"CMD\", \"echo\", \"healthy\"]\n    },\n    \"Cmd\": [\"--cookie\", \"session=private-value\", \"https://example.invalid\"]\n  }\n}")]
    public void Portable_consumers_reject_round_nineteen_parent_commands_across_deeper_exec_fragments(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-nineteen-structured-ancestry");
    }

    [Fact]
    public void Portable_consumers_fail_closed_when_round_nineteen_structured_join_exceeds_line_bound()
    {
        var privateText = "entrypoint: [\"curl\"]\n" +
                          string.Join('\n', Enumerable.Range(0, 33).Select(index => $"# bounded metadata {index}")) +
                          "\ncommand: [\"--pass\", \"private-phrase\", \"https://example.invalid\"]";

        AssertPortableConsumersReject(privateText, "revision-round-nineteen-line-bound");
    }

    [Fact]
    public void Portable_consumers_fail_closed_when_round_nineteen_structured_fragment_exceeds_size_bound()
    {
        var privateText = "entrypoint: [\"curl\"]\ncommand: [\"" + new string('a', 8200) + "\"]";

        AssertPortableConsumersReject(privateText, "revision-round-nineteen-size-bound");
    }

    [Fact]
    public void Portable_consumers_fail_closed_when_round_nineteen_structured_fragment_is_unbalanced()
    {
        const string privateText = "entrypoint: [\n  \"curl\"\ncommand: [\"--pass\", \"private-phrase\", \"https://example.invalid\"]";

        AssertPortableConsumersReject(privateText, "revision-round-nineteen-delimiter-bound");
    }

    [Theory]
    [InlineData("curl --no-location https://example.invalid")]
    [InlineData("curl --no-silent --no-fail --no-insecure --no-compressed https://example.invalid")]
    [InlineData("curl -G https://example.invalid")]
    [InlineData("curl -o output.txt https://example.invalid")]
    [InlineData("curl --haproxy-clientip --user alice:hunter2 https://example.invalid")]
    [InlineData("curl [2001:db8::1]/health")]
    [InlineData("curl {api,backup}.example.invalid/health")]
    [InlineData("curl '<user>:<password>@[2001:db8::1]/resource'")]
    [InlineData("docker run -u 1000:1000 image && podman run -u 1000:1000 image")]
    [InlineData("FROM base AS probe\nENTRYPOINT [\"curl\"]\nHEALTHCHECK CMD [\"echo\", \"healthy\"]\nFROM base AS runtime\nCMD [\"--pass\", \"documentation-only\"]")]
    [InlineData("containers:\n  - name: probe\n    command: [\"curl\"]\n  - name: unrelated\n    args: [\"--pass\", \"documentation-only\"]")]
    [InlineData("entrypoint: [\"curl\"]\n---\ncommand: [\"--pass\", \"documentation-only\"]")]
    public void Portable_consumers_preserve_round_nineteen_sibling_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-nineteen-control");
    }

    [Theory]
    [InlineData("command:\n  - curl\n  # auth follows\n  - --pass\n  - private-phrase\n  - https://example.invalid")]
    [InlineData("command:\n  - curl\n\n  - --user\n  - alice:hunter2\n  - https://example.invalid")]
    [InlineData("entrypoint: [\"curl\"]\ncommand: [\n  \"--pass\",\n  \"private-phrase\"")]
    [InlineData("command: [\n  \"curl\",\n  \"--data\", \"]\",\n  \"--pass\", \"private-phrase\",\n  \"https://example.invalid\"\n]")]
    public void Portable_consumers_reject_round_twenty_abandoned_structured_arrays(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-structured-abandonment");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_block_sequence_line_bound()
    {
        var privateText = "command:\n  - curl\n" +
                          string.Join('\n', Enumerable.Range(0, 32).Select(index => $"  - harmless-{index}")) +
                          "\n  - --pass\n  - private-phrase\n  - https://example.invalid";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-block-line-bound");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_privacy_character_bound_below_import_limit()
    {
        var privateText = "command: [\"curl\", \"" + new string('a', 2200) +
                          "\"]";

        Assert.True(privateText.Length < 4096);
        AssertPortableConsumersReject(privateText, "revision-round-twenty-character-bound");
    }

    [Theory]
    [InlineData("FROM curlimages/curl:8.5.0\nENTRYPOINT [\"curl\"]\nHEALTHCHECK CMD [\"true\"]\nCMD [\"--pass\", \"private-phrase\", \"https://example.invalid\"]")]
    [InlineData("FROM curlimages/curl:8.5.0\nENTRYPOINT [\"curl\"]\nRUN echo preparing\n# harmless metadata\nLABEL purpose=probe\nCMD [\"--user\", \"alice:hunter2\", \"https://example.invalid\"]")]
    public void Portable_consumers_reject_round_twenty_same_stage_dockerfile_fragments(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-dockerfile-stage");
    }

    [Theory]
    [InlineData("curl :hunter2@example.invalid/resource")]
    [InlineData("curl --url :hunter2@example.invalid/resource")]
    [InlineData("curl 'https://deploy{1,2}:hunter2@example.invalid/'")]
    [InlineData("curl https://deploy{1,2}:hunter2@example.invalid/")]
    [InlineData("curl --url 'https://deploy[1-2]:hunter2@example.invalid/'")]
    [InlineData("curl --url=https://deploy[1-2]:hunter2@example.invalid/")]
    public void Portable_consumers_reject_round_twenty_password_only_and_globbed_userinfo(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-userinfo");
    }

    [Theory]
    [InlineData("curl -H 'Cookie: session=private-value' https://example.invalid")]
    [InlineData("curl --header 'Set-Cookie: session=private-value' https://example.invalid")]
    [InlineData("curl --proxy-header='Cookie: session=private-value' https://example.invalid")]
    public void Portable_consumers_reject_round_twenty_cookie_headers(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-cookie-header");
    }

    [Theory]
    [InlineData("curl --oauth2 abc.def.ghi https://api.example.invalid")]
    [InlineData("curl --tlspass private-phrase https://example.invalid")]
    [InlineData("curl --proxy-tlsp private-phrase https://example.invalid")]
    [InlineData("curl --proxy-us alice:hunter2 --proxy proxy.example:3128 https://example.invalid")]
    [InlineData("curl --user alice:hunter2 https://example.invalid")]
    [InlineData("curl --cook https://example.invalid")]
    [InlineData("curl --proxy-tls https://example.invalid")]
    [InlineData("curl --globof :hunter2@example.invalid/resource")]
    [InlineData("curl --hel :hunter2@example.invalid/resource")]
    [InlineData("curl --url-q alice:hunter2@example.invalid/resource https://example.invalid")]
    [InlineData("curl --future-auth private-phrase https://example.invalid")]
    public void Portable_consumers_reject_round_twenty_unique_ambiguous_and_unknown_long_options(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-long-option-resolution");
    }

    [Theory]
    [InlineData("containers:\n  - name: a\n    command: [\"curl\", \"-fsS\", \"http://localhost/health\"]\n  - args: [\"-u\", \"1000:1000\"]")]
    [InlineData("[\n  { \"Entrypoint\": [\"curl\"] },\n  { \"Cmd\": [\"--pass\", \"documentation-only\"] }\n]")]
    public void Portable_consumers_preserve_round_twenty_sibling_structural_parents(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-structural-parent-control");
    }

    [Theory]
    [InlineData("curl -H 'Cookie:' https://example.invalid")]
    [InlineData("curl --header 'Set-Cookie:   ' https://example.invalid")]
    [InlineData("curl --proxy-header 'X-Trace: ordinary' https://example.invalid")]
    [InlineData("curl :@example.invalid/resource")]
    [InlineData("curl --url '<user>:<password>@example.invalid/resource'")]
    [InlineData("curl --future-flag && docker run -u 1000:1000 image")]
    [InlineData("curl --globof https://example.invalid")]
    [InlineData("curl --hel all https://example.invalid")]
    [InlineData("curl --url-q name=value https://example.invalid")]
    [InlineData("curl --haproxy-clienti 192.0.2.10 https://example.invalid")]
    [InlineData("curl --proxy-tlsu ordinary-name https://example.invalid")]
    [InlineData("FROM base AS probe\nENTRYPOINT [\"curl\"]\nFROM base AS runtime\nCMD [\"--pass\", \"documentation-only\"]")]
    public void Portable_consumers_preserve_round_twenty_sibling_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-control");
    }

    [Theory]
    [InlineData("command: [\n  \"curl\", # documentation closes with ]\n  \"--pass\", \"private-phrase\",\n  \"https://example.invalid\"\n]")]
    [InlineData("command: [\n  \"curl\", # escaped URL https://example.invalid/a#b and ]\n  \"--user\", \"alice:hunter2\",\n  \"https://example.invalid\"\n]")]
    public void Portable_consumers_reject_round_twenty_one_flow_comments(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-one-flow-comment");
    }

    [Theory]
    [InlineData("command: [\"curl\", \"https://example.invalid/a#b]c\"]")]
    [InlineData("command: [\"curl\", 'https://example.invalid/a#b]c']")]
    [InlineData("command: [\"curl\", https://example.invalid/a#fragment]")]
    public void Portable_consumers_preserve_round_twenty_one_quoted_flow_comment_markers(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-one-flow-comment-control");
    }

    [Theory]
    [InlineData("containers:\n- name: probe\n  command: [\"curl\"]\n  env:\n  - name: MODE\n    value: probe\n  args: [\"--user\", \"alice:hunter2\", \"https://example.invalid\"]")]
    [InlineData("containers:\n- name: probe\n  command: [\"curl\"]\n  ports:\n  - containerPort: 8080\n  volumeMounts:\n  - name: cache\n    mountPath: relative/cache\n  args: [\"--pass\", \"private-phrase\", \"https://example.invalid\"]")]
    [InlineData("containers:\n- name: probe\n  command: [\"curl\"]\n  readinessProbe:\n    httpGet:\n      path: /ready\n      port: 8080\n  args: [\"--cookie\", \"session=private\", \"https://example.invalid\"]")]
    public void Portable_consumers_reject_round_twenty_one_compact_child_collections(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-one-compact-child");
    }

    [Theory]
    [InlineData("containers:\n- name: probe\n  command: [\"curl\"]\n- name: sibling\n  args: [\"--pass\", \"documentation-only\"]")]
    [InlineData("containers:\n- name: probe\n  command: [\"curl\"]\n---\ncontainers:\n- name: sibling\n  args: [\"--pass\", \"documentation-only\"]")]
    [InlineData("[\n  { \"Entrypoint\": [\"curl\"] },\n  { \"Cmd\": [\"--pass\", \"documentation-only\"] }\n]")]
    [InlineData("FROM base AS probe\nENTRYPOINT [\"curl\"]\nFROM base AS sibling\nCMD [\"--pass\", \"documentation-only\"]")]
    public void Portable_consumers_preserve_round_twenty_one_structural_siblings(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-one-structural-control");
    }

    [Theory]
    [InlineData("command: >\n  curl -fsS\n  --user alice:hunter2\n  https://example.invalid")]
    [InlineData("command: >-\n  curl -fsS\n    --pass private-phrase\n  https://example.invalid")]
    [InlineData("command: >+\n  curl -fsS\n  --cookie session=private\n  https://example.invalid")]
    [InlineData("command: |\n  curl -fsS\n  -H\"Cookie: session=private\"\n  https://example.invalid")]
    [InlineData("command: curl -fsS\n  --user alice:hunter2\n  https://example.invalid")]
    [InlineData("args: >\n  curl -fsS\n  --pass private-phrase\n  https://example.invalid")]
    [InlineData("healthcheck:\n  test: >-\n    curl -fsS\n    --user alice:hunter2\n    https://example.invalid")]
    public void Portable_consumers_reject_round_twenty_one_multiline_exec_scalars(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-one-multiline-scalar");
    }

    [Theory]
    [InlineData("description: >\n  curl is named in prose\n  --pass is documented separately")]
    [InlineData("command: >\n  curl -fsS https://example.invalid\nlabels:\n  note: --pass documentation-only")]
    [InlineData("healthcheck:\n  test: curl -fsS https://example.invalid\n  interval: 30s\nother: --pass documentation-only")]
    public void Portable_consumers_preserve_round_twenty_one_multiline_boundaries(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-one-multiline-control");
    }

    [Theory]
    [InlineData("curl -H\"Cookie: session=private-value\" https://example.invalid")]
    [InlineData("curl -H'Cookie: session=private-value' https://example.invalid")]
    [InlineData("curl -sSH\"Cookie: session=private-value\" https://example.invalid")]
    [InlineData("curl -sSH'Set-Cookie: id=private' https://example.invalid")]
    public void Portable_consumers_reject_round_twenty_one_attached_quoted_cookie_headers(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-one-attached-header");
    }

    [Theory]
    [InlineData("curl -H\"Cookie:\" https://example.invalid")]
    [InlineData("curl -sSH'Set-Cookie:   ' https://example.invalid")]
    [InlineData("curl -H\"X-Trace: ordinary\" https://example.invalid")]
    [InlineData("curl -sS 'Cookie: session=ordinary-argument' https://example.invalid")]
    public void Portable_consumers_preserve_round_twenty_one_attached_header_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-one-attached-header-control");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_one_line_bound_before_curl()
    {
        var privateText = "command:\n" +
                          string.Join('\n', Enumerable.Range(0, 32).Select(index => $"  - harmless-{index}")) +
                          "\n  - curl\n  - --pass\n  - private-phrase\n  - https://example.invalid";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-one-line-bound-before-curl");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_one_character_bound_before_curl()
    {
        var privateText = "command: [\n  \"" + new string('a', 2100) +
                          "\",\n  \"curl\",\n  \"--pass\", \"private-phrase\",\n" +
                          "  \"https://example.invalid\"\n]";

        Assert.True(privateText.Length < 4096);
        AssertPortableConsumersReject(privateText, "revision-round-twenty-one-character-bound-before-curl");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_one_unclosed_delimiter_reason()
    {
        const string privateText = "command: [\n  \"harmless\",\n  \"curl\",\n  \"--pass\", \"private-phrase\"";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-one-unclosed-delimiter");
    }

    [Theory]
    [InlineData("notes:\n  - harmless-0\n  - harmless-1\n  - curl\n  - --pass\n  - documentation-only")]
    public void Portable_consumers_preserve_round_twenty_one_bounded_non_exec_material(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-one-bounded-non-exec-control");
    }

    [Theory]
    [InlineData("command: [\"/bin/sh\", \"-c\"]\nargs:\n  - >-\n    curl -fsS\n    --user alice:hunter2\n    https://example.invalid")]
    [InlineData("command: [\"/bin/sh\", \"-c\"]\nargs:\n  - |+\n    curl -fsS\n    --pass private-phrase\n    https://example.invalid")]
    [InlineData("command:\n  - sh\n  - -c\n  - curl -fsS\n    --user alice:hunter2\n    https://example.invalid")]
    [InlineData("healthcheck:\n  test:\n    curl -fsS\n    --cookie session=private\n    https://example.invalid")]
    [InlineData("command:\n  [\"curl\",\n   \"--pass\", \"private-phrase\",\n   \"https://example.invalid\"]")]
    [InlineData("args:\n  - >\n    curl -fsS\n    -H \"Cookie: session=private\"\n    https://example.invalid")]
    public void Portable_consumers_reject_round_twenty_two_following_line_exec_values(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-two-following-line-value");
    }

    [Theory]
    [InlineData("containers:\n- name: probe\n  args:\n    - >-\n      curl -fsS https://example.invalid\n- name: sibling\n  args:\n    - --pass private-phrase")]
    [InlineData("command:\n  [\"curl\", \"https://example.invalid\"]\n---\ncommand:\n  [\"--pass\", \"private-phrase\"]")]
    [InlineData("FROM base AS probe\nCMD\n  [\"curl\", \"https://example.invalid\"]\nFROM base AS sibling\nCMD\n  [\"--pass\", \"private-phrase\"]")]
    [InlineData("healthcheck:\n  test:\n    curl -fsS https://example.invalid\nother:\n  --pass private-phrase")]
    public void Portable_consumers_preserve_round_twenty_two_following_line_boundaries(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-two-following-line-control");
    }

    [Theory]
    [InlineData("command: [\"sh\", \"-c\", \"curl -H \\\"Cookie: session=private\\\" https://example.invalid\"]")]
    [InlineData("command: [\"sh\", \"-c\", \"curl -H\\\"Set-Cookie: session=private\\\" https://example.invalid\"]")]
    [InlineData("args: ['sh', '-c', 'curl --header \\\"Cookie: session=private\\\" https://example.invalid']")]
    [InlineData("HEALTHCHECK CMD [\"CMD-SHELL\", \"curl -H \\\"Set-Cookie: session=private\\\" https://example.invalid\"]")]
    [InlineData("healthcheck:\n  test: [\"CMD-SHELL\", \"curl -H\\\"Cookie: session=private\\\" https://example.invalid\"]")]
    [InlineData("command: [\"sh\", \"-c\", \"curl -H \\\"Authorization: Bearer private\\\" https://example.invalid\"]")]
    public void Portable_consumers_reject_round_twenty_two_escaped_nested_shell_headers(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-two-escaped-header");
    }

    [Theory]
    [InlineData("command: [\"sh\", \"-c\", \"curl -H \\\"Cookie:\\\" https://example.invalid\"]")]
    [InlineData("command: [\"sh\", \"-c\", \"curl -H\\\"Set-Cookie:   \\\" https://example.invalid\"]")]
    [InlineData("HEALTHCHECK CMD [\"CMD-SHELL\", \"curl -H \\\"X-Trace: ordinary\\\" https://example.invalid\"]")]
    [InlineData("command: [\"sh\", \"-c\", \"printf \\\"Cookie: session=ordinary prose\\\"\"]")]
    [InlineData("command: [\"sh\", \"-c\", \"curl \\\"Cookie: session=ordinary argument\\\" https://example.invalid\"]")]
    public void Portable_consumers_preserve_round_twenty_two_escaped_nested_shell_header_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-two-escaped-header-control");
    }

    [Theory]
    [InlineData("curl -4H\"Cookie: session=private\" https://example.invalid")]
    [InlineData("curl -#H'Set-Cookie: session=private' https://example.invalid")]
    [InlineData("curl -:H\"Cookie: session=private\" https://example.invalid")]
    [InlineData("curl -4H\"Authorization: Bearer private\" https://example.invalid")]
    public void Portable_consumers_reject_round_twenty_two_catalogued_attached_short_clusters(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-two-short-cluster");
    }

    [Theory]
    [InlineData("curl -4H\"Cookie:\" https://example.invalid")]
    [InlineData("curl -#H'Set-Cookie:   ' https://example.invalid")]
    [InlineData("curl -:H\"X-Trace: ordinary\" https://example.invalid")]
    [InlineData("curl -4 'Cookie: session=ordinary argument' https://example.invalid")]
    public void Portable_consumers_preserve_round_twenty_two_catalogued_short_cluster_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-two-short-cluster-control");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_two_scalar_line_abandonment()
    {
        var privateText = "command: >-\n" +
                          string.Join('\n', Enumerable.Range(0, 31).Select(index => $"  harmless-{index}")) +
                          "\n  curl -fsS\n  --pass private-phrase\n  https://example.invalid";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-two-scalar-line-bound");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_two_scalar_character_abandonment()
    {
        var privateText = "command: >-\n  " + new string('a', 2050) +
                          "\n  curl -fsS\n  --pass private-phrase\n  https://example.invalid";

        Assert.True(privateText.Length < 4096);
        AssertPortableConsumersReject(privateText, "revision-round-twenty-two-scalar-character-bound");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_two_block_sequence_character_abandonment()
    {
        var privateText = "command:\n  - " + new string('a', 2050) +
                          "\n  - curl\n  - --pass\n  - private-phrase\n  - https://example.invalid";

        Assert.True(privateText.Length < 4096);
        AssertPortableConsumersReject(privateText, "revision-round-twenty-two-block-sequence-character-bound");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_two_multiline_flow_line_abandonment()
    {
        var privateText = "command: [\n" +
                          string.Join('\n', Enumerable.Range(0, 31).Select(index => $"  \"harmless-{index}\",")) +
                          "\n  \"curl\",\n  \"--pass\", \"private-phrase\",\n  \"https://example.invalid\"\n]";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-two-flow-line-bound");
    }

    [Theory]
    [InlineData("command: ['sh', '-c', 'curl -H ''Cookie: session=private'' https://example.invalid']")]
    [InlineData("command: ['sh', '-c', 'curl -H''Set-Cookie: id=private'' https://example.invalid']")]
    [InlineData("pwsh -Command \"curl.exe -H \"\"Cookie: session=private\"\" https://example.invalid\"")]
    [InlineData("cmd /c \"curl.exe -H\"\"Set-Cookie: id=private\"\" https://example.invalid\"")]
    public void Portable_consumers_reject_round_twenty_three_doubled_quote_headers(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-doubled-header");
    }

    [Theory]
    [InlineData("command: ['sh', '-c', 'curl -H ''Cookie:'' https://example.invalid']")]
    [InlineData("pwsh -Command \"curl.exe -H \"\"Set-Cookie:   \"\" https://example.invalid\"")]
    [InlineData("cmd /c \"curl.exe -H \"\"X-Trace: ordinary\"\" https://example.invalid\"")]
    [InlineData("pwsh -Command \"Write-Output \"\"Cookie: ordinary prose\"\"\"")]
    public void Portable_consumers_preserve_round_twenty_three_doubled_quote_header_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-three-doubled-header-control");
    }

    [Theory]
    [InlineData("healthcheck:\n  test: \"curl -fsS --header\n    'Cookie: session=private'\n    https://example.invalid\"")]
    [InlineData("args:\n  - \"curl -fsS -H\n    'Set-Cookie: id=private' https://example.invalid\"")]
    [InlineData("command: > # probe\n  curl -fsS -H\n  'Cookie: session=private'\n  https://example.invalid")]
    [InlineData("command: >2\n    curl -fsS -H\n    'Cookie: session=private'\n    https://example.invalid")]
    [InlineData("command: |2-\n    curl -fsS -H\n    'Set-Cookie: id=private'\n    https://example.invalid")]
    [InlineData("command: >-2 # probe\n    curl -fsS -H\n    'Cookie: session=private'\n    https://example.invalid")]
    public void Portable_consumers_reject_round_twenty_three_quoted_multiline_and_block_headers(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-multiline-header");
    }

    [Theory]
    [InlineData("healthcheck:\n  test: \"curl -fsS -H\n    'X-Trace: ordinary'\n    https://example.invalid\"")]
    [InlineData("command: >2\n    curl -fsS https://example.invalid\nsibling:\n  note: Cookie: documentation-only")]
    [InlineData("command: |2- # probe\n    curl -fsS -H 'Cookie:' https://example.invalid")]
    [InlineData("command: >-2\n    printf 'Cookie: ordinary prose'")]
    public void Portable_consumers_preserve_round_twenty_three_multiline_header_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-three-multiline-header-control");
    }

    [Theory]
    [InlineData("command: >--\n  curl -fsS --pass private-phrase")]
    [InlineData("command: |0\n  curl -fsS --pass private-phrase")]
    [InlineData("command: >2 trailing\n  curl -fsS --pass private-phrase")]
    public void Portable_consumers_reject_round_twenty_three_malformed_block_headers(string privateText)
    {
        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-malformed-block-header");
    }

    [Theory]
    [InlineData("command: >2\n  sibling: harmless\nother:\n  --pass private-phrase")]
    [InlineData("command: |2-\n  harmless\n---\ncommand: --pass private-phrase")]
    [InlineData("FROM base AS probe\nRUN >2\n  echo harmless\nFROM base AS sibling\nRUN --pass private-phrase")]
    public void Portable_consumers_preserve_round_twenty_three_block_scalar_boundaries(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twenty-three-block-boundary-control");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_three_next_line_plain_line_limit()
    {
        var privateText = "command:\n  curl -fsS \"" +
                          string.Join('\n', Enumerable.Range(0, 31).Select(index => $"  harmless-{index}")) +
                          "\n  Cookie: session=private\"";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-next-plain-line-limit");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_three_next_line_plain_character_limit()
    {
        var privateText = "command:\n  curl -fsS \"" + new string('a', 2050) + "\n  Cookie: session=private\"";

        Assert.True(privateText.Length < 4096);
        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-next-plain-character-limit");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_three_next_line_plain_unclosed_delimiter()
    {
        const string privateText = "command:\n  \"curl -fsS -H\n    Cookie: session=private";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-next-plain-unclosed");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_three_next_line_flow_line_limit()
    {
        var privateText = "command:\n  [\"curl\",\n" +
                          string.Join('\n', Enumerable.Range(0, 31).Select(index => $"   \"harmless-{index}\",")) +
                          "\n   \"--pass\", \"private-phrase\"]";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-next-flow-line-limit");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_three_next_line_flow_character_limit()
    {
        var privateText = "command:\n  [\"curl\", \"" + new string('a', 2050) + "\", \"--pass\", \"private-phrase\"]";

        Assert.True(privateText.Length < 4096);
        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-next-flow-character-limit");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_three_next_line_flow_unclosed_delimiter()
    {
        const string privateText = "command:\n  [\"curl\", \"--pass\", \"private-phrase\"";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-next-flow-unclosed");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_three_sequence_block_scalar_line_limit()
    {
        var privateText = "args:\n  - >-\n    curl -fsS \"" +
                          string.Join('\n', Enumerable.Range(0, 30).Select(index => $"    harmless-{index}")) +
                          "\n    Cookie: session=private\"";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-sequence-scalar-line-limit");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_three_sequence_block_scalar_character_limit()
    {
        var privateText = "args:\n  - >-\n    curl -fsS \"" + new string('a', 2050) + "\n    Cookie: session=private\"";

        Assert.True(privateText.Length < 4096);
        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-sequence-scalar-character-limit");
    }

    [Fact]
    public void Portable_consumers_reject_round_twenty_three_sequence_block_scalar_unclosed_delimiter()
    {
        const string privateText = "args:\n  - >-\n    \"curl -fsS -H\n    Cookie: session=private";

        AssertPortableConsumersReject(privateText, "revision-round-twenty-three-sequence-scalar-unclosed");
    }

    [Fact]
    public void Curl_8_5_option_arity_snapshot_audits_every_long_and_short_name()
    {
        var audit = PortableMaterial.CurlOptionArityAudit();
        var canonical = string.Join('\n', audit);
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();

        Assert.Equal(317, audit.Count);
        Assert.Equal(audit.Count, audit.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("long --haproxy-clientip RequiredValue", audit);
        Assert.Contains("long --get NoValue", audit);
        Assert.Contains("short -o RequiredValue", audit);
        Assert.Contains("short -G NoValue", audit);
        Assert.Equal("462fbb5609e72add17436ee0f0f91439627335a7d487b00d7fcba07d2dcd9256", digest);
    }

    [Theory]
    [InlineData("{\"Authorization\": \"\"}")]
    [InlineData("{'Authorization' = '   '}")]
    [InlineData("Authorization:\nordinary next line")]
    [InlineData("curl \\\n        --user-agent delivery-forge https://example.invalid")]
    [InlineData("curl https://example.invalid/path?user=alice:8080")]
    [InlineData("The curl -u option accepts a user name and password")]
    [InlineData("curl -x proxy.example:3128 https://example.invalid")]
    [InlineData("docker run -u 1000:1000 image")]
    [InlineData("curl https://example.invalid && docker run -u 1000:1000 image")]
    [InlineData("dotnet test ../tests/Foo.csproj")]
    public void Portable_consumers_preserve_round_twelve_sibling_controls(string portableText)
    {
        AssertPortableConsumersAccept(portableText, "revision-round-twelve-control");
    }

    [Theory]
    [InlineData("dotnet test ./tests/Foo.csproj")]
    [InlineData("dotnet test ../tests/Foo.csproj")]
    [InlineData("pwsh ..\\tmp\\build.ps1")]
    [InlineData("pwsh .\\tmp\\build.ps1")]
    public void Portable_plan_accepts_ordinary_relative_gate_paths(string command)
    {
        var draft = Draft() with { Gates = [new("test", command, "all tests pass")] };

        var frozen = PlanFreezer.Freeze(draft, "revision-relative-gate", ObservedAt);

        Assert.True(frozen.DownstreamReady);
        var serializedCommand = JsonSerializer.Serialize(command)[1..^1];
        Assert.Contains(serializedCommand, Encoding.UTF8.GetString(frozen.CanonicalBytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("curl --user-agent delivery-forge https://example.invalid")]
    [InlineData("curl -fsS http://auth-user:8080/healthz")]
    [InlineData("curl -fsS https://example.invalid && docker run -u 1000:1000 image")]
    [InlineData("sk-short")]
    [InlineData("PowerShell exposes $env:APPDATA without revealing a path")]
    [InlineData("Use -v/--verbose or -n/--dry-run")]
    [InlineData("curl -x proxy.example:3128 https://example.invalid")]
    [InlineData("curl --proxy=proxy.example:3128 https://example.invalid")]
    [InlineData("curl -H \"Accept: application/json\" https://example.invalid")]
    [InlineData("The Authorization header selects an authentication scheme")]
    [InlineData("curl -sSx proxy.example:3128 https://example.invalid")]
    [InlineData("curl --proxy1.0=proxy.example:3128 https://example.invalid")]
    [InlineData("curl --preproxy proxy.example:3128 https://example.invalid")]
    [InlineData("curl --socks5-hostname=proxy.example:1080 https://example.invalid")]
    [InlineData("curl -H \"Authorization:\" https://example.invalid")]
    [InlineData("curl -H 'Authorization:   ' https://example.invalid")]
    [InlineData(@"cc -I.\include app.c")]
    [InlineData(@"cl /I..\include app.c")]
    [InlineData("cc -Wl,-rpath,./lib app.c")]
    public void Portable_consumers_preserve_non_secret_sibling_controls(string portableText)
    {
        var original = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(portableText)];
        var draft = original with
        {
            Provenance = provenance,
            Intake = IntakePlanner.Assess(original.Request, provenance)
        };

        var frozen = PlanFreezer.Freeze(draft, "revision-portable-sibling", ObservedAt);
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            entries = new[] { new { kind = "memory", locator = "memory:item-1", summary = portableText, digest = (string?)null, observedAt = "2026-10-07T20:00:00Z" } },
            conflicts = Array.Empty<string>(),
            limitations = Array.Empty<string>()
        }));

        Assert.True(frozen.DownstreamReady);
        Assert.Single(ImportedContextEnvelope.Parse(json).Entries);
    }

    [Theory]
    [InlineData("Use secret rotation documentation")]
    [InlineData("The password policy has twelve requirements")]
    [InlineData("See https://example.invalid/docs/access-key-rotation")]
    [InlineData("Contact the home team before rollout")]
    public void Portable_plan_accepts_portable_prose_and_urls(string portableText)
    {
        var original = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(portableText)];
        var draft = original with
        {
            Provenance = provenance,
            Intake = IntakePlanner.Assess(original.Request, provenance)
        };

        var frozen = PlanFreezer.Freeze(draft, "revision-portable-control", ObservedAt);

        Assert.True(frozen.DownstreamReady);
    }

    [Theory]
    [InlineData("found in /home/alice/.ssh/config")]
    [InlineData("found in /opt/build/private-index")]
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
        var draft = Draft();
        var envelope = new ImportedContextEnvelope(
            "1.0.0",
            [new("memory", "memory:item-1", "Advisory summary", null, ObservedAt, Stale: true)],
            [],
            ["Checkout verification is pending"]);
        var assessment = IntakePlanner.Assess(draft.Request, draft.Provenance, envelope, EvidenceRequirement.Required);

        var frozen = PlanFreezer.Freeze(draft with { Intake = assessment }, "revision-1", ObservedAt);
        var text = Encoding.UTF8.GetString(frozen.CanonicalBytes);

        Assert.True(assessment.Ready);
        Assert.Contains("stale", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checkout verification", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Required_imported_context_readiness_is_enforced_at_freeze()
    {
        var assessment = IntakePlanner.Assess(Request(), [RepositoryEvidence()], null, EvidenceRequirement.Required);
        var error = Assert.Throws<PlanningException>(() =>
            PlanFreezer.Freeze(Draft() with { Intake = assessment }, "revision-1", ObservedAt));

        Assert.Contains("required imported context", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frozen_plan_exposes_validated_contract_bytes_and_defensive_copies()
    {
        var frozen = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
        var validated = ContractValidator.ParseAndValidate(frozen.PlanContractBytes);
        var first = frozen.PlanContractBytes;
        first[0] = (byte)'!';

        Assert.Equal(frozen.ContractIdentity, validated.Identity);
        Assert.Equal((byte)'{', frozen.PlanContractBytes[0]);
        Assert.Equal((byte)'{', frozen.CanonicalBytes[0]);
    }

    [Fact]
    public void Same_declared_revision_with_different_content_fails_closed_against_predecessor()
    {
        var first = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
        var changed = Draft() with { Gates = [new("test", "dotnet test -c Release", "all tests pass")] };

        Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(changed, "revision-1", ObservedAt, first));

        var successor = PlanFreezer.Freeze(changed, "revision-2", ObservedAt, first);
        Assert.Equal(first.ContractIdentity, successor.Supersedes);
        Assert.NotEqual(first.PlanRevision, successor.PlanRevision);
    }

    [Fact]
    public void Declared_revision_only_change_directly_supersedes_predecessor()
    {
        var first = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);

        var successor = PlanFreezer.Freeze(Draft(), "revision-2", ObservedAt, first);

        Assert.NotEqual(first.ContractIdentity, successor.ContractIdentity);
        Assert.Equal(first.ContractIdentity, successor.Supersedes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Predecessor_must_share_repository_and_work_item_lineage(bool changeRepository)
    {
        var first = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
        var successorDraft = Draft();
        var changedRequest = changeRepository
            ? successorDraft.Request with { Repository = "nucleoid/another-repository" }
            : successorDraft.Request with { WorkItem = "#999" };
        successorDraft = successorDraft with
        {
            Request = changedRequest,
            Intake = IntakePlanner.Assess(changedRequest, successorDraft.Provenance)
        };

        var error = Assert.Throws<PlanningException>(() =>
            PlanFreezer.Freeze(successorDraft, "revision-2", ObservedAt, first));
        Assert.Contains("lineage", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mutable_repository_observations_do_not_change_declared_revision_identity()
    {
        var clean = Draft();
        var first = PlanFreezer.Freeze(clean, "revision-1", ObservedAt);
        var dirty = clean with
        {
            Repository = RepositoryContext.Create(
                clean.Repository.RepositoryRoot,
                clean.Repository.RequestedRef,
                clean.Repository.Commit,
                clean.Repository.Tree,
                clean.Repository.DetachedHead,
                dirty: true,
                clean.Repository.Shallow,
                ["+0123456789012345678901234567890123456789 dependency"],
                ["Mutable worktree is dirty; exact-object reads remain pinned to the resolved commit."])
        };

        var second = PlanFreezer.Freeze(dirty, "revision-1", ObservedAt, first);

        Assert.Equal(first.ContentDigest, second.ContentDigest);
        Assert.Equal(first.PlanRevision, second.PlanRevision);
        Assert.Equal(first.ContractIdentity, second.ContractIdentity);
        Assert.NotEqual(first.Identity, second.Identity);
        Assert.Contains("dirty", Encoding.UTF8.GetString(second.CanonicalBytes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Intake_assessment_is_bound_to_the_request_and_evidence_used_to_create_it()
    {
        var draft = Draft();
        var unrelatedEvidence = new[] { RepositoryEvidence("git:other.txt") };
        var unrelated = IntakePlanner.Assess(
            draft.Request with { Outcome = "Different outcome" },
            unrelatedEvidence,
            new ImportedContextEnvelope("1.0.0", [], [], ["Different imported caveat"]),
            EvidenceRequirement.Required);

        var error = Assert.Throws<PlanningException>(() =>
            PlanFreezer.Freeze(draft with { Intake = unrelated }, "revision-1", ObservedAt));

        Assert.Contains("intake assessment", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readiness_and_frozen_plan_state_are_not_publicly_constructible_or_mutable()
    {
        Assert.Empty(typeof(IntakeAssessment).GetConstructors());
        Assert.Empty(typeof(RepositoryFile).GetConstructors());
        Assert.Empty(typeof(RepositoryContext).GetConstructors());
        Assert.All(typeof(RepositoryContext).GetProperties(), property =>
            Assert.False(property.CanWrite, $"{property.Name} must be reader-issued and get-only."));
        Assert.All(
            typeof(FrozenPlan).GetProperties().Where(property => property.Name != nameof(FrozenPlan.CanonicalBytes) && property.Name != nameof(FrozenPlan.PlanContractBytes)),
            property => Assert.False(property.CanWrite, $"{property.Name} must be get-only."));
    }

    [Fact]
    public void Freeze_rejects_a_repository_context_with_forged_mutable_observations()
    {
        var draft = Draft();
        var constructor = Assert.Single(typeof(RepositoryContext).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic));
        var forged = (RepositoryContext)constructor.Invoke(
        [
            draft.Repository.RepositoryRoot,
            draft.Repository.RequestedRef,
            draft.Repository.Commit,
            draft.Repository.Tree,
            draft.Repository.DetachedHead,
            true,
            draft.Repository.Shallow,
            Array.Empty<string>(),
            Array.Empty<string>(),
            false,
            "sha256:" + new string('0', 64)
        ]);

        var error = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            draft with { Repository = forged },
            "revision-forged-observations",
            ObservedAt));

        Assert.Contains("issued", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Imported_context_rejects_non_sha256_digest()
    {
        var json = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion":"1.0.0",
              "entries":[{"kind":"memory","locator":"memory:item","summary":"summary","digest":"md5:nope","observedAt":"2026-10-07T20:00:00Z"}],
              "conflicts":[],
              "limitations":[]
            }
            """);

        Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(json));
    }

    private static PlanningRequest Request() => new(
        "nucleoid/delivery-forge", "#4", "implement", "Build planning core",
        ["planning"], ["execution"], ["Behavior is deterministic"], "implement");

    private static EvidenceItem RepositoryEvidence(string locator = "git:README.md")
    {
        if (!locator.StartsWith("git:", StringComparison.Ordinal))
        {
            return new EvidenceItem(EvidenceSourceKind.Policy, locator, "sha256:" + new string('b', 64), ObservedAt, []);
        }
        var path = locator[4..];
        var file = new RepositoryFile(
            path,
            new string('c', 40),
            "100644",
            Encoding.UTF8.GetBytes($"exact bytes for {path}"),
            isSymlink: false,
            escapesWorktree: false,
            "not-detected",
            SymlinkResolution.NotSymlink,
            new string('a', 40),
            new string('b', 40));
        return EvidenceItem.FromRepositoryFile(file, ObservedAt, []);
    }

    private static EvidenceItem PolicyEvidence(string locator = "policy:planning") => new(
        EvidenceSourceKind.Policy, locator, "sha256:" + new string('c', 64), ObservedAt, []);

    private static void AssertPortableConsumersReject(string privateText, string revision)
    {
        var original = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(privateText)];
        var draft = original with
        {
            Provenance = provenance,
            Intake = IntakePlanner.Assess(original.Request, provenance)
        };
        var planError = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(draft, revision, ObservedAt));
        Assert.Contains("host path or credential-like private material", planError.Message, StringComparison.OrdinalIgnoreCase);

        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            entries = new[] { new { kind = "memory", locator = "memory:item-1", summary = privateText, digest = (string?)null, observedAt = "2026-10-07T20:00:00Z" } },
            conflicts = Array.Empty<string>(),
            limitations = Array.Empty<string>()
        }));
        var importedError = Assert.Throws<PlanningException>(() => ImportedContextEnvelope.Parse(json));
        Assert.Contains("not portable or may contain private/secret material", importedError.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPortableConsumersAccept(string portableText, string revision)
    {
        var original = Draft();
        EvidenceItem[] provenance = [RepositoryEvidence(portableText)];
        var draft = original with
        {
            Provenance = provenance,
            Intake = IntakePlanner.Assess(original.Request, provenance)
        };
        var frozen = PlanFreezer.Freeze(draft, revision, ObservedAt);

        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            entries = new[] { new { kind = "memory", locator = "memory:item-1", summary = portableText, digest = (string?)null, observedAt = "2026-10-07T20:00:00Z" } },
            conflicts = Array.Empty<string>(),
            limitations = Array.Empty<string>()
        }));

        Assert.True(frozen.DownstreamReady);
        Assert.Single(ImportedContextEnvelope.Parse(json).Entries);
    }

    private static RepositoryFile RepositoryFileFor(string path) => new(
        path,
        new string('c', 40),
        "100644",
        Encoding.UTF8.GetBytes($"exact bytes for {path}"),
        isSymlink: false,
        escapesWorktree: false,
        "not-detected",
        SymlinkResolution.NotSymlink,
        new string('a', 40),
        new string('b', 40));

    private static PlanDraft Draft()
    {
        var request = Request();
        EvidenceItem[] provenance = [RepositoryEvidence("git:README.md"), RepositoryEvidence("git:Directory.Build.props")];
        var repository = RepositoryContext.Create(
            "/portable/display-only", "HEAD", new string('a', 40), new string('b', 40),
            detachedHead: false, dirty: false, shallow: false, submodules: [], limitations: []);
        return new PlanDraft(
            request, repository,
            provenance,
            [new("components/planning/Core.cs", "PlanFreezer", "freeze plans"), new("references/planning.md", "document", "describe boundaries")],
            [new("contracts", [], "issue #3 is integrated")],
            [new("test", "dotnet test", "all tests pass"), new("build", "dotnet build", "zero warnings")],
            new("additive", "none", "none", "none", "none", "none", "test results", "revert commit", []),
            [],
            IntakePlanner.Assess(request, provenance));
    }
}
