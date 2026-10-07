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
    public void Deep_intake_is_only_enabled_explicitly()
    {
        var request = Request() with { Depth = IntakeDepth.Deep };
        var shallowAssessment = IntakePlanner.Assess(request, [RepositoryEvidence()]);
        var deepAssessment = IntakePlanner.Assess(request,
        [
            RepositoryEvidence(),
            new(EvidenceSourceKind.Policy, "policy:planning", "sha256:" + new string('c', 64), ObservedAt, [])
        ]);
        var importedDeepAssessment = IntakePlanner.Assess(
            request,
            [
                RepositoryEvidence(),
                new(EvidenceSourceKind.Imported, "index:callers", null, ObservedAt, ["Advisory and unverified against checkout bytes."])
            ],
            new ImportedContextEnvelope(
                "1.0.0",
                [new("code-index", "index:callers", "Additional bounded caller evidence", null, ObservedAt)],
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
        var frozen = PlanFreezer.Freeze(Draft(), "revision-1", ObservedAt);
        var changed = Draft();
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
            "/portable/display-only", "HEAD", new string('d', 40), new string('e', 40),
            detachedHead: false, dirty: false, shallow: false, submodules: [], limitations: []);
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
            new string('a', 40), new string('b', 40));
        var factory = typeof(EvidenceItem).GetMethod(
            "FromRepositoryFile",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(factory);
        var bound = Assert.IsType<EvidenceItem>(factory.Invoke(
            null,
            [file, ObservedAt, Array.Empty<string>(), EvidenceRequirement.Optional]));

        Assert.True(IntakePlanner.Assess(Request(), [bound]).Ready);
        Assert.False(IntakePlanner.Assess(Request(), [RepositoryEvidence()]).Ready);

        var draft = Draft();
        EvidenceItem[] provenance = [bound, PolicyEvidence()];
        var error = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(
            draft with { Provenance = provenance, Intake = IntakePlanner.Assess(draft.Request, provenance) },
            "revision-wrong-repository-binding",
            ObservedAt));
        Assert.Contains("commit/tree", error.Message, StringComparison.OrdinalIgnoreCase);
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

    private static EvidenceItem RepositoryEvidence(string locator = "git:README.md") => new(
        EvidenceSourceKind.Repository, locator, "sha256:" + new string('b', 64), ObservedAt, []);

    private static EvidenceItem PolicyEvidence(string locator = "policy:planning") => new(
        EvidenceSourceKind.Policy, locator, "sha256:" + new string('c', 64), ObservedAt, []);

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
