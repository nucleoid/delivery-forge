using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Review.Tests;

public sealed class ReviewCoreTests
{
    [Fact]
    public async Task Patch_identity_binds_exact_binary_full_index_bytes_and_clean_committed_head()
    {
        using var repository = new TinyGitRepository();
        var baseCommit = repository.Commit(new Dictionary<string, byte[]>
        {
            ["delete.txt"] = "delete me\n"u8.ToArray(),
            ["mode.sh"] = "#!/bin/sh\necho old\n"u8.ToArray(),
            ["rename old.txt"] = "rename payload\n"u8.ToArray(),
            ["unicode-æ.txt"] = "før\n"u8.ToArray(),
            ["binary.bin"] = [0, 1, 2, 0, 255]
        });
        repository.Move("rename old.txt", "rename new.txt");
        repository.Delete("delete.txt");
        repository.Write("mode.sh", "#!/bin/sh\necho new\n"u8.ToArray());
        repository.MakeExecutable("mode.sh");
        repository.Write("unicode-æ.txt", "efter\n"u8.ToArray());
        repository.Write("binary.bin", [0, 9, 2, 0, 255]);
        var headCommit = repository.CommitAll();

        using var controlled = ReviewAuthorityHost.CreateIsolatedTest().PrepareIsolatedTestGitEvidence(
            repository.Path, Path.Combine(repository.Path, ".git"), "/usr/bin/git", 1024 * 1024);
        var identity = await GitPatchIdentity.ComputeAsync(
            controlled, baseCommit, headCommit, Identity('e'),
            new Dictionary<string, string> { ["tests"] = Identity('a') },
            TestContext.Current.CancellationToken);
        var bytes = repository.GitBytes(
            "-c", "core.quotepath=false",
            "-c", "core.fsmonitor=false",
            "-c", "core.hooksPath=/dev/null",
            "-c", "diff.noprefix=false",
            "-c", "diff.mnemonicPrefix=false",
            "-c", "diff.relative=false",
            "-c", "diff.renames=true",
            "-c", "diff.algorithm=myers",
            "-c", "diff.orderFile=/dev/null",
            "diff", "--binary", "--full-index", "--no-color", "--no-ext-diff", "--no-textconv",
            "--ignore-submodules=none", baseCommit, headCommit, "--");

        Assert.Equal(Hash(bytes), identity.PatchSha256);
        Assert.Equal(repository.Git("rev-parse", $"{headCommit}^{{tree}}"), identity.HeadTree);
        Assert.Equal("/usr/bin/git", identity.Command[0]);
        Assert.Contains("--no-replace-objects", identity.Command);
        Assert.Contains("--no-textconv", identity.Command);
        Assert.Contains("--ignore-submodules=none", identity.Command);
        Assert.Contains("git version", identity.GitVersion, StringComparison.Ordinal);
        Assert.Equal(Identity('e'), identity.PlanIdentity);
        Assert.Equal(Identity('a'), identity.ArtifactIdentities["tests"]);

        repository.Write("dirty.txt", "dirty"u8.ToArray());
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            controlled, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Patch_identity_rejects_staged_mismatched_missing_and_non_head_inputs()
    {
        using var repository = new TinyGitRepository();
        var baseCommit = repository.Commit(new Dictionary<string, byte[]> { ["a.txt"] = "a"u8.ToArray() });
        repository.Write("a.txt", "b"u8.ToArray());
        var headCommit = repository.CommitAll();
        using var controlled = ReviewAuthorityHost.CreateIsolatedTest().PrepareIsolatedTestGitEvidence(
            repository.Path, Path.Combine(repository.Path, ".git"), "/usr/bin/git", 1024 * 1024);

        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            controlled, baseCommit, new string('f', 40), Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            controlled, headCommit, baseCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
        repository.Write("staged.txt", "staged"u8.ToArray());
        repository.Git("add", "staged.txt");
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            controlled, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Controlled_git_ignores_worker_helpers_attributes_replacements_and_path_authority()
    {
        using var repository = new TinyGitRepository();
        var baseCommit = repository.Commit(new Dictionary<string, byte[]>
        {
            [".gitattributes"] = "*.txt diff=evil filter=evil\n"u8.ToArray(),
            ["a.txt"] = "before\n"u8.ToArray()
        });
        repository.Write("a.txt", "after\n"u8.ToArray());
        var headCommit = repository.CommitAll();
        var sentinel = Path.Combine(repository.Path, ".git", "helper-ran");
        var helper = Path.Combine(repository.Path, ".git", OperatingSystem.IsWindows() ? "evil.cmd" : "evil.sh");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(helper, $"@echo ran>\"{sentinel}\"\r\n");
        }
        else
        {
            File.WriteAllText(helper, $"#!/bin/sh\nprintf ran > '{sentinel}'\n");
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        repository.Git("config", "core.fsmonitor", helper);
        repository.Git("config", "diff.evil.textconv", helper);
        repository.Git("config", "filter.evil.clean", helper);
        repository.Git("config", "filter.evil.smudge", helper);
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git", "refs", "replace"));
        File.WriteAllText(Path.Combine(repository.Path, ".git", "refs", "replace", baseCommit), headCommit + "\n");
        using var controlled = ReviewAuthorityHost.CreateIsolatedTest().PrepareIsolatedTestGitEvidence(
            repository.Path, Path.Combine(repository.Path, ".git"), "/usr/bin/git", 1024 * 1024);

        var identity = await GitPatchIdentity.ComputeAsync(
            controlled, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken);

        Assert.False(File.Exists(sentinel));
        Assert.Equal(headCommit, identity.HeadCommit);
    }

    [Fact]
    public async Task Controlled_git_raw_source_check_exposes_ignored_skip_worktree_symlink_and_alternates()
    {
        using var repository = new TinyGitRepository();
        var baseCommit = repository.Commit(new Dictionary<string, byte[]>
        {
            [".gitignore"] = "ignored.txt\n"u8.ToArray(),
            ["a.txt"] = "before\n"u8.ToArray()
        });
        repository.Write("a.txt", "after\n"u8.ToArray());
        var headCommit = repository.CommitAll();
        using var controlled = ReviewAuthorityHost.CreateIsolatedTest().PrepareIsolatedTestGitEvidence(
            repository.Path, Path.Combine(repository.Path, ".git"), "/usr/bin/git", 1024 * 1024);

        repository.Git("update-index", "--skip-worktree", "a.txt");
        repository.Write("a.txt", "hidden\n"u8.ToArray());
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            controlled, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
        repository.Git("update-index", "--no-skip-worktree", "a.txt");
        repository.Write("a.txt", "after\n"u8.ToArray());

        repository.Write("ignored.txt", "ignored\n"u8.ToArray());
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            controlled, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
        repository.Delete("ignored.txt");

        File.CreateSymbolicLink(Path.Combine(repository.Path, "outside-link"), "/etc/passwd");
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            controlled, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
        File.Delete(Path.Combine(repository.Path, "outside-link"));

        Directory.CreateDirectory(Path.Combine(controlled.ControlledGitDirectory, "objects", "info"));
        File.WriteAllText(Path.Combine(controlled.ControlledGitDirectory, "objects", "info", "alternates"), "/tmp/untrusted\n");
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            controlled, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Controlled_git_output_bound_and_cancellation_fail_typed()
    {
        using var repository = new TinyGitRepository();
        var baseCommit = repository.Commit(new Dictionary<string, byte[]> { ["a.txt"] = "before\n"u8.ToArray() });
        repository.Write("a.txt", "after\n"u8.ToArray());
        var headCommit = repository.CommitAll();
        using var bounded = ReviewAuthorityHost.CreateIsolatedTest().PrepareIsolatedTestGitEvidence(
            repository.Path, Path.Combine(repository.Path, ".git"), "/usr/bin/git", 8);
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            bounded, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));

        using var cancelled = ReviewAuthorityHost.CreateIsolatedTest().PrepareIsolatedTestGitEvidence(
            repository.Path, Path.Combine(repository.Path, ".git"), "/usr/bin/git", 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            cancelled, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(), cancellation.Token));
    }

    [Fact]
    public async Task Controlled_git_rechecks_root_owned_quiescence_before_accepting_evidence()
    {
        using var repository = new TinyGitRepository();
        var baseCommit = repository.Commit(new Dictionary<string, byte[]> { ["a.txt"] = "before\n"u8.ToArray() });
        repository.Write("a.txt", "after\n"u8.ToArray());
        var headCommit = repository.CommitAll();
        using var controlled = ReviewAuthorityHost.CreateIsolatedTest().PrepareIsolatedTestGitEvidence(
            repository.Path,
            Path.Combine(repository.Path, ".git"),
            "/usr/bin/git",
            1024 * 1024,
            () => repository.Write("a.txt", "mutated during capture\n"u8.ToArray()));

        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            controlled, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Isolated_root_issuer_drives_the_exact_safe_receipt_and_consumer_path()
    {
        var policy = Policy();
        var request = Request(policy);
        var evidencePolicy = EvidencePolicy();
        var host = ReviewAuthorityHost.CreateIsolatedTest(
            RootContext("run-1", request, policy, evidencePolicy.Identity), Time(1));
        var capability = host.IssueCapability(
            request.Capability,
            "capability-proof-1",
            Time(0),
            Time(2));
        var execution = host.RecordExecution(
            "review-request-1",
            "review-execution-1",
            accepted: true,
            completed: true,
            rawArtifactHash: Identity('f'),
            rawVerdict: "ship",
            completedAt: Time(1));

        var normalized = ReviewNormalizer.Normalize(request, Result(), policy, capability, execution);
        Assert.True(normalized.AuthorityAccepted);
        Assert.False(normalized.ProductionAccepted);

        var receipt = ContractValidator.ParseAndValidate(
            ReviewReceiptSerializer.Serialize(ReviewReceiptSerializer.Create(normalized)));
        Assert.Equal("PASS", JsonNode.Parse(receipt.CanonicalBytes.Span)!["outcome"]!.GetValue<string>());

        host.IssueAcceptance(
            receipt,
            normalized,
            new ReviewAcceptanceContext(
                "run-1",
                "review-request-1",
                request.Identity.PlanIdentity,
                evidencePolicy.Identity,
                policy.ContentIdentity,
                policy.SourceRevision,
                policy.AuthorityKind,
                "source-authority-1",
                "/trusted/source",
                request.Identity.BaseCommit),
            ReviewEvidenceRole.Independent);

        var transition = new TransitionEvidence(
            AuthorizationCeiling.Pr,
            request.Identity.BaseCommit,
            request.Identity.HeadCommit,
            request.Identity.HeadTree,
            Policy: evidencePolicy,
            ReviewReceipt: receipt,
            ReviewAuthority: host.Authority,
            RequiredReviewAuthorityRealm: ReviewAuthorityRealm.Test);
        WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview,
            WorkflowState.PrAuthorized,
            transition);

        host.RevokeCapability(capability.ProofIdentity);
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized, transition));
        var restarted = ReviewAuthorityHost.CreateIsolatedTest(
            RootContext("run-1", request, policy, evidencePolicy.Identity), Time(1));
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview,
            WorkflowState.PrAuthorized,
            transition with { ReviewAuthority = restarted.Authority }));
    }

    [Fact]
    public void Root_issuer_rejects_every_mutated_acceptance_binding()
    {
        var policy = Policy();
        var request = Request(policy);
        var evidencePolicy = EvidencePolicy();
        var root = RootContext("binding-run", request, policy, evidencePolicy.Identity) with
        {
            SourceAuthorityIdentity = "binding-source"
        };
        var host = ReviewAuthorityHost.CreateIsolatedTest(root, Time(1));
        var capability = host.IssueCapability(request.Capability, "binding-capability", Time(0), Time(2));
        var execution = host.RecordExecution(
            "binding-request", "binding-execution", true, true, Identity('f'), "ship", Time(1));
        var review = ReviewNormalizer.Normalize(request, Result(), policy, capability, execution);
        var receipt = ContractValidator.ParseAndValidate(
            ReviewReceiptSerializer.Serialize(ReviewReceiptSerializer.Create(review)));
        var valid = new ReviewAcceptanceContext(
            root.RunIdentity, "binding-request", root.PlanIdentity, root.EvidencePolicyIdentity,
            root.ReviewPolicyContentIdentity, root.ReviewPolicySourceRevision,
            root.ReviewPolicyAuthorityKind, root.SourceAuthorityIdentity, root.SourceRoot,
            root.ProtectedBaseCommit);
        var mutations = new[]
        {
            valid with { RunIdentity = "wrong" },
            valid with { RequestIdentity = "wrong" },
            valid with { PlanIdentity = Identity('9') },
            valid with { EvidencePolicyIdentity = Identity('9') },
            valid with { ReviewPolicyContentIdentity = Identity('9') },
            valid with { ReviewPolicySourceRevision = Commit('9') },
            valid with { ReviewPolicyAuthorityKind = ReviewPolicyAuthorityKind.WorkerBranch },
            valid with { SourceAuthorityIdentity = "wrong" },
            valid with { SourceRoot = "/wrong/source" },
            valid with { ProtectedBaseCommit = Commit('9') }
        };

        Assert.All(mutations, mutated => Assert.Throws<InvalidOperationException>(() =>
            host.IssueAcceptance(receipt, review, mutated, ReviewEvidenceRole.Independent)));
        host.IssueAcceptance(receipt, review, valid, ReviewEvidenceRole.Independent);
        Assert.False(host.Authority.Equals(null));
    }

    [Theory]
    [InlineData("ship", ReviewOutcome.Pass, 0)]
    [InlineData("needs-attention", ReviewOutcome.Fail, 1)]
    [InlineData("do-not-ship", ReviewOutcome.Fail, 1)]
    [InlineData("anything-else", ReviewOutcome.Error, 1)]
    public void Supported_live_producer_maps_real_verdict_grammar_and_preserves_findings(
        string rawVerdict,
        ReviewOutcome expected,
        int findingCount)
    {
        var host = ReviewAuthorityHost.CreateIsolatedTest(Time(1));
        var policy = Policy();
        var findings = Enumerable.Range(0, findingCount)
            .Select(index => new ReviewFinding($"R{index}", "material", "finding", "A.cs:1"))
            .ToArray();
        var observation = new LiveReviewerObservation(
            true, true, true, true, true,
            "fixture", "1", "source", "live-capability", Time(0), Time(2),
            "live-request", "live-execution", "VERDICT output"u8.ToArray(), rawVerdict,
            findings, Time(1), true);

        var normalized = SupportedLiveReviewProducer.Produce(host, Request(policy), policy, observation);

        Assert.Equal(expected, normalized.Outcome);
        Assert.Equal(findings, normalized.Findings);
        Assert.Equal(expected == ReviewOutcome.Pass, normalized.AuthorityAccepted);
    }

    [Fact]
    public void Missing_live_configuration_remains_incomplete()
    {
        var policy = Policy();
        var observation = new LiveReviewerObservation(
            false, false, false, false, false,
            "fixture", "1", "source", "missing", Time(0), Time(2),
            "request", "execution", [], "ship", [], Time(1), true);

        var normalized = SupportedLiveReviewProducer.Produce(
            ReviewAuthorityHost.CreateIsolatedTest(Time(1)), Request(policy), policy, observation);

        Assert.Equal(ReviewOutcome.Incomplete, normalized.Outcome);
        Assert.False(normalized.AuthorityAccepted);
    }

    [Fact]
    public void Trusted_fixture_policy_accepts_explicit_different_family_but_not_production()
    {
        var policy = Policy();
        var normalized = ReviewNormalizer.Normalize(Request(policy), Result(), policy);
        Assert.Equal(ReviewOutcome.Pass, normalized.Outcome);
        Assert.False(normalized.ProductionAccepted);
        Assert.False(normalized.AuthorityAccepted);
        Assert.True(normalized.Fixture);
    }

    [Theory]
    [InlineData("gpt", "gpt")]
    [InlineData("gpt", "unknown")]
    [InlineData("unknown", "claude")]
    public void Same_unknown_or_self_family_never_passes(string author, string reviewer)
    {
        var policy = Policy();
        var request = Request(policy) with
        {
            AuthorFamily = author,
            Capability = Capability() with { Family = reviewer }
        };
        Assert.NotEqual(ReviewOutcome.Pass,
            ReviewNormalizer.Normalize(request, Result() with { ReviewerFamily = reviewer }, policy).Outcome);
    }

    [Fact]
    public void Forged_authority_advisory_downgrade_and_untrusted_scope_are_non_pass()
    {
        var candidate = new ReviewPolicyCandidate(Identity('7'), Identity('8'), Commit('a'), false, false,
            [new FamilyPair("gpt", "claude")], ["source"], true,
            new ReviewBudget(1, TimeSpan.FromMinutes(1), 100, 1m), []);
        foreach (var authority in new[]
                 {
                     new ReviewPolicyAuthority(ReviewPolicyAuthorityKind.Caller, Commit('a'), Identity('8')),
                     new ReviewPolicyAuthority(ReviewPolicyAuthorityKind.ParentAdmin, Commit('a'), Identity('8'))
                 })
        {
            var policy = ReviewPolicy.Resolve(candidate, authority);
            Assert.NotEqual(ReviewOutcome.Pass, ReviewNormalizer.Normalize(Request(policy), Result(), policy).Outcome);
        }

        var trusted = Policy();
        Assert.NotEqual(ReviewOutcome.Pass, ReviewNormalizer.Normalize(
            Request(trusted) with { Capability = Capability() with { Fixture = false, Advisory = true } },
            Result(), trusted).Outcome);
        Assert.NotEqual(ReviewOutcome.Pass, ReviewNormalizer.Normalize(
            Request(trusted) with { Scope = "docs" }, Result(), trusted).Outcome);
        Assert.NotEqual(ReviewOutcome.Pass, ReviewNormalizer.Normalize(
            Request(trusted) with { PolicyIdentity = Identity('9') }, Result(), trusted).Outcome);
    }

    [Fact]
    public void Policy_is_frozen_against_caller_collection_mutation()
    {
        var families = new List<FamilyPair> { new("gpt", "claude") };
        var scopes = new List<string> { "source" };
        var policy = ReviewPolicy.TrustedFixture(Identity('7'), families, scopes, false,
            new ReviewBudget(1, TimeSpan.FromMinutes(1), 100, 1m));
        families.Clear();
        scopes.Add("docs");
        Assert.Equal(ReviewOutcome.Pass, ReviewNormalizer.Normalize(Request(policy), Result(), policy).Outcome);
        Assert.DoesNotContain("docs", policy.AllowedScopes);
    }

    [Fact]
    public void Timeout_truncation_missing_findings_and_binding_mismatches_are_non_pass()
    {
        var policy = Policy();
        var identity = Patch();
        var cases = new[]
        {
            Result() with { TimedOut = true },
            Result() with { Truncated = true },
            Result() with { Findings = null },
            Result() with { Verdict = null! },
            Result() with { Identity = identity with { HeadCommit = Commit('d') } },
            Result() with { Identity = identity with { PatchSha256 = Identity('9') } },
            Result() with { ReviewerFamily = "pi" }
        };
        Assert.All(cases, value => Assert.NotEqual(
            ReviewOutcome.Pass, ReviewNormalizer.Normalize(Request(policy), value, policy).Outcome));
        Assert.NotEqual(
            ReviewOutcome.Pass,
            ReviewNormalizer.Normalize(
                Request(policy) with { Capability = Capability() with { Scopes = null! } },
                Result(),
                policy).Outcome);
    }

    [Theory]
    [InlineData(MaterialChange.Edit)]
    [InlineData(MaterialChange.Amend)]
    [InlineData(MaterialChange.Rebase)]
    [InlineData(MaterialChange.ConflictResolution)]
    [InlineData(MaterialChange.Generation)]
    [InlineData(MaterialChange.DependencyIntegration)]
    [InlineData(MaterialChange.Plan)]
    [InlineData(MaterialChange.Policy)]
    [InlineData(MaterialChange.Artifact)]
    public void Material_changes_invalidate_review(MaterialChange change)
    {
        var policy = Policy(true);
        var receipt = ReviewNormalizer.Normalize(Request(policy), Result(), policy);
        var decision = ReviewInvalidation.Assess(receipt, Patch(), policy, change);
        Assert.True(decision.Invalidated);
        Assert.False(decision.Reusable);
    }

    [Fact]
    public void Unchanged_reuse_requires_trusted_policy_exact_identity_and_original_provenance()
    {
        var policy = Policy(true);
        var request = Request(policy);
        var evidencePolicy = EvidencePolicy();
        var host = ReviewAuthorityHost.CreateIsolatedTest(
            RootContext("reuse-run", request, policy, evidencePolicy.Identity), Time(1));
        var capability = host.IssueCapability(request.Capability, "reuse-capability", Time(0), Time(2));
        var execution = host.RecordExecution(
            "reuse-request", "reuse-execution", true, true, Identity('f'), "ship", Time(1));
        var receipt = ReviewNormalizer.Normalize(request, Result(), policy, capability, execution);
        var contract = ContractValidator.ParseAndValidate(
            ReviewReceiptSerializer.Serialize(ReviewReceiptSerializer.Create(receipt)));
        host.IssueAcceptance(
            contract,
            receipt,
            new ReviewAcceptanceContext(
                "reuse-run", "reuse-request", request.Identity.PlanIdentity, evidencePolicy.Identity,
                policy.ContentIdentity, policy.SourceRevision, policy.AuthorityKind,
                "reuse-source-authority", "/trusted/source", request.Identity.BaseCommit),
            ReviewEvidenceRole.Independent);
        var reuse = host.CreateReuseContext(contract, ReviewEvidenceRole.Independent, request, capability);
        Assert.True(ReviewInvalidation.Assess(
            receipt, Patch(), policy, MaterialChange.None, reuse).Reusable);
        Assert.False(ReviewInvalidation.Assess(
            receipt, Patch() with { HeadTree = Commit('d') }, policy, MaterialChange.None, reuse).Reusable);

        var changedRequests = new[]
        {
            request with { Capability = request.Capability with { AdapterId = "other" } },
            request with { Capability = request.Capability with { AdapterVersion = "2" } },
            request with { Capability = request.Capability with { Family = "pi" } },
            request with { Capability = request.Capability with { Fixture = false } },
            request with { Capability = request.Capability with { Scopes = ["docs"] } },
            request with { Scope = "docs" }
        };
        foreach (var changed in changedRequests)
        {
            var changedContext = host.CreateReuseContext(
                contract, ReviewEvidenceRole.Independent, changed, capability);
            var decision = ReviewInvalidation.Assess(
                receipt, Patch(), Policy(false), MaterialChange.None, changedContext);
            Assert.True(decision.Invalidated);
            Assert.False(decision.Reusable);
        }

        Assert.False(ReviewInvalidation.Assess(
            WithoutOriginalExecution(receipt), Patch(), policy, MaterialChange.None, reuse).Reusable);
        host.RevokeCapability(capability.ProofIdentity);
        Assert.True(ReviewInvalidation.Assess(
            receipt, Patch(), policy, MaterialChange.None, reuse).Invalidated);
        Assert.False(ReviewInvalidation.Assess(
            receipt, Patch(), Policy(false), MaterialChange.None, reuse).Reusable);
    }

    [Fact]
    public void Explicit_budget_exhaustion_pauses_and_preserves_findings()
    {
        var ledger = ReviewBudgetLedger.FromTrustedPolicy(Policy());
        Assert.True(ledger.TryReserve(
            new ReviewCost(TimeSpan.FromMinutes(4), 18_000, 1.5m),
            out var reservation,
            out _));
        var finding = new ReviewFinding("R1", "material", "fix this", "A.cs:1");
        ledger.RecordFindings([finding]);
        var settled = ledger.Settle(
            reservation!,
            new ReviewCost(TimeSpan.FromMinutes(6), 21_000, 2.5m));
        Assert.Equal(ReviewOutcome.Incomplete, settled.Outcome);
        Assert.Contains("exhausted", settled.Reason, StringComparison.Ordinal);
        Assert.Equal([finding], settled.Findings);
        Assert.Equal(1, ledger.ConsumedRounds);
        Assert.False(ledger.TryReserve(
            new ReviewCost(TimeSpan.FromSeconds(1), 1, .01m),
            out _,
            out var exhausted));
        Assert.Equal([finding], exhausted.Findings);
    }

    [Fact]
    public void Budget_reservation_is_atomic_under_concurrency_and_every_success_settles()
    {
        var ledger = ReviewBudgetLedger.FromTrustedPolicy(Policy());
        var reservations = new System.Collections.Concurrent.ConcurrentBag<ReviewBudgetReservation>();
        Parallel.For(0, 12, iteration =>
        {
            if (ledger.TryReserve(
                    new ReviewCost(TimeSpan.FromSeconds(1), 10, .01m),
                    out var reservation,
                    out _))
                reservations.Add(reservation!);
        });

        Assert.Equal(2, reservations.Count);
        foreach (var reservation in reservations)
            ledger.Settle(reservation, new ReviewCost(TimeSpan.FromSeconds(1), 10, .01m));
        Assert.Equal(2, ledger.ConsumedRounds);
    }

    [Fact]
    public void Raw_capability_and_mutable_caller_collections_cannot_gain_or_change_authority()
    {
        var capability = new ReviewerCapability("caller", "1", "claude", true, false, false, ["source"]);

        var artifacts = new Dictionary<string, string> { ["tests"] = Identity('1') };
        var findings = new List<ReviewFinding> { new("R1", "material", "before", "A.cs:1") };
        var identity = Patch() with { ArtifactIdentities = artifacts };
        var policy = Policy();
        var request = Request(policy) with { Identity = identity };
        var result = ReviewResult.Complete(identity, "claude", "PASS", findings, Identity('f'), Time(1));
        var receipt = ReviewNormalizer.Normalize(request, result, policy);

        artifacts["tests"] = Identity('2');
        findings[0] = new ReviewFinding("R2", "material", "after", "B.cs:2");
        Assert.Equal(Identity('1'), receipt.Identity.ArtifactIdentities["tests"]);
        Assert.Equal("R1", receipt.Findings[0].Code);
        Assert.False(receipt.ProductionAccepted);
    }

    [Fact]
    public async Task Protected_policy_resolver_freezes_exact_base_and_ignores_worker_branch_edits()
    {
        using var repository = new TinyGitRepository();
        var protectedJson = """
            {
              "Fixture": false,
              "Advisory": false,
              "AllowedFamilies": [{ "Author": "gpt", "Reviewer": "claude" }],
              "AllowedScopes": ["source"],
              "AllowExactReuse": true,
              "Budget": {
                "MaximumRounds": 2,
                "MaximumDuration": "00:05:00",
                "MaximumTokens": 20000,
                "MaximumCost": 2.0
              },
              "ExemptionApprovers": []
            }
            """;
        var protectedBase = repository.Commit(new Dictionary<string, byte[]>
        {
            ["policy/review.json"] = Encoding.UTF8.GetBytes(protectedJson)
        });
        repository.Write("policy/review.json", Encoding.UTF8.GetBytes("{}"));
        repository.CommitAll();

        var source = ReviewAuthorityHost.CreateIsolatedTest().FreezeProtectedSource(
            repository.Path, protectedBase, "test-source-authority", "/usr/bin/git");
        var policy = await new ProtectedReviewPolicyResolver(source)
            .ResolveAsync("policy/review.json", TestContext.Current.CancellationToken);

        Assert.True(policy.Trusted);
        Assert.False(policy.Fixture);
        Assert.Equal(protectedBase, policy.SourceRevision);
        Assert.Equal(policy.ContentIdentity, policy.PolicyIdentity);
        Assert.NotEqual(Identity('7'), policy.PolicyIdentity);
        Assert.Contains(new FamilyPair("gpt", "claude"), policy.AllowedFamilies);
    }

    [Fact]
    public async Task Protected_policy_missing_required_fields_fails_typed_and_closed()
    {
        using var repository = new TinyGitRepository();
        var protectedBase = repository.Commit(new Dictionary<string, byte[]>
        {
            ["policy/review.json"] = "{}"u8.ToArray()
        });

        var error = await Assert.ThrowsAsync<ReviewPolicyException>(() =>
            new ProtectedReviewPolicyResolver(
                    ReviewAuthorityHost.CreateIsolatedTest().FreezeProtectedSource(
                        repository.Path, protectedBase, "test-source-authority", "/usr/bin/git"))
                .ResolveAsync("policy/review.json", TestContext.Current.CancellationToken));

        Assert.Contains("malformed", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("null-family")]
    public async Task Protected_policy_duplicate_and_null_inputs_fail_typed(string malformed)
    {
        using var repository = new TinyGitRepository();
        var families = malformed == "null-family"
            ? "[null]"
            : "[{ \"Author\": \"gpt\", \"Reviewer\": \"claude\" }]";
        var duplicate = malformed == "duplicate" ? ", \"Fixture\": true" : string.Empty;
        var json = $$"""
            {
              "Fixture": false{{duplicate}},
              "Advisory": false,
              "AllowedFamilies": {{families}},
              "AllowedScopes": ["source"],
              "AllowExactReuse": false,
              "Budget": {
                "MaximumRounds": 1,
                "MaximumDuration": "00:01:00",
                "MaximumTokens": 100,
                "MaximumCost": 1.0
              },
              "ExemptionApprovers": []
            }
            """;
        var protectedBase = repository.Commit(new Dictionary<string, byte[]>
        {
            ["policy/review.json"] = Encoding.UTF8.GetBytes(json)
        });
        var source = ReviewAuthorityHost.CreateIsolatedTest().FreezeProtectedSource(
            repository.Path, protectedBase, "test-source-authority", "/usr/bin/git");

        await Assert.ThrowsAsync<ReviewPolicyException>(() =>
            new ProtectedReviewPolicyResolver(source)
                .ResolveAsync("policy/review.json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Production_source_context_rejects_worker_selected_ancestor_or_branch_base()
    {
        using var repository = new TinyGitRepository();
        var protectedBase = repository.Commit(new Dictionary<string, byte[]> { ["policy/review.json"] = "{}"u8.ToArray() });
        repository.Write("worker.txt", "worker"u8.ToArray());
        var workerBase = repository.CommitAll();
        var host = ReviewAuthorityHost.CreateProduction(new ProductionReviewAuthorityConfiguration(
            "production-root",
            new ReviewAuthorityRootContext(
                "run", Identity('e'), Identity('0'), Identity('7'), Commit('f'),
                ReviewPolicyAuthorityKind.ProtectedGitBase,
                "parent-source", repository.Path, protectedBase)));

        Assert.Throws<InvalidOperationException>(() => host.FreezeProtectedSource(
            repository.Path, workerBase, "parent-source", "/usr/bin/git"));
    }

    [Fact]
    public void Fixture_pass_receipt_cannot_authorize_pr_transition()
    {
        var policy = Policy();
        var normalized = ReviewNormalizer.Normalize(Request(policy), Result(), policy);
        var receipt = ContractValidator.ParseAndValidate(
            ReviewReceiptSerializer.Serialize(ReviewReceiptSerializer.Create(normalized)));
        var identity = normalized.Identity;
        var evidence = new TransitionEvidence(
            AuthorizationCeiling.Pr,
            identity.BaseCommit,
            identity.HeadCommit,
            identity.HeadTree,
            Policy: EvidencePolicy(),
            ReviewReceipt: receipt);

        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized, evidence));
    }

    [Fact]
    public void Identity_policy_and_findings_mismatches_are_behavioral_non_pass()
    {
        var noReuse = Policy(false);
        var receipt = ReviewNormalizer.Normalize(Request(noReuse), Result(), noReuse);
        Assert.True(ReviewInvalidation.Assess(
            receipt,
            Patch() with { HeadTree = Commit('d') },
            noReuse,
            MaterialChange.None).Invalidated);

        var versionMismatch = Result() with
        {
            Identity = Patch() with { GitVersion = "git version 9.9.9" }
        };
        Assert.NotEqual(ReviewOutcome.Pass,
            ReviewNormalizer.Normalize(Request(noReuse), versionMismatch, noReuse).Outcome);

        var materialFinding = new ReviewFinding("R1", "material", "must fix", "A.cs:1");
        Assert.NotEqual(ReviewOutcome.Pass,
            ReviewNormalizer.Normalize(
                Request(noReuse),
                Result() with { Findings = [materialFinding] },
                noReuse).Outcome);
    }

    [Fact]
    public void Normalized_result_emits_the_existing_versioned_review_receipt_contract()
    {
        var policy = Policy();
        var normalized = ReviewNormalizer.Normalize(Request(policy), Result(), policy);

        var receipt = ReviewReceiptSerializer.Create(normalized);
        var bytes = ReviewReceiptSerializer.Serialize(receipt);
        var validated = DeliveryForge.Contracts.Validation.ContractValidator.ParseAndValidate(bytes);

        Assert.Equal("review-receipt", validated.SchemaName);
        Assert.Equal(normalized.Identity.BaseCommit, receipt.BaseCommit);
        Assert.Equal(normalized.Identity.HeadCommit, receipt.HeadCommit);
        Assert.Equal(normalized.Identity.HeadTree, receipt.TreeId);
        Assert.Equal(normalized.Identity.PatchSha256, receipt.PatchSha256);
        Assert.Equal(normalized.ReviewerFamily, receipt.ReviewerFamily);
        Assert.Equal("INCOMPLETE", receipt.Outcome);
        Assert.Equal(normalized.Findings.Count, receipt.FindingCount);
        Assert.Equal(normalized.ArtifactHash, receipt.ArtifactHash);
        Assert.Equal(validated.Identity, receipt.Identity);
    }

    [Fact]
    public void Missing_policy_and_fixture_or_advisory_evidence_cannot_satisfy_production()
    {
        var fixture = ReviewNormalizer.Normalize(Request(Policy()), Result(), Policy());
        Assert.False(fixture.ProductionAccepted);
        Assert.NotEqual(ReviewOutcome.Pass,
            ReviewNormalizer.Normalize(Request(ReviewPolicy.Missing), Result(), ReviewPolicy.Missing).Outcome);
    }

    private static ReviewAuthorityRootContext RootContext(
        string runIdentity,
        ReviewRequest request,
        ReviewPolicy policy,
        string evidencePolicyIdentity) => new(
        runIdentity,
        request.Identity.PlanIdentity,
        evidencePolicyIdentity,
        policy.ContentIdentity,
        policy.SourceRevision,
        policy.AuthorityKind,
        runIdentity == "reuse-run" ? "reuse-source-authority" : "source-authority-1",
        "/trusted/source",
        request.Identity.BaseCommit);

    private static ReviewPolicy Policy(bool allowReuse = false) => ReviewPolicy.TrustedFixture(
        Identity('7'), [new FamilyPair("gpt", "claude")], ["source"], allowReuse,
        new ReviewBudget(2, TimeSpan.FromMinutes(5), 20_000, 2m));
    private static ReviewerCapability Capability() =>
        new("fixture", "1", "claude", true, true, false, ["source"]);
    private static ReviewRequest Request(ReviewPolicy policy) =>
        new(Patch(), "gpt", Capability(), "source", policy.PolicyIdentity);
    private static ReviewResult Result() =>
        ReviewResult.Complete(Patch(), "claude", "PASS", [], Identity('f'), Time(1));
    private static PatchIdentity Patch() => new(
        Commit('a'), Commit('b'), Commit('c'), Identity('d'), Identity('e'),
        new Dictionary<string, string> { ["tests"] = Identity('1') }, "git version 2.51.0",
        ["git", "diff"]);
    private static string Identity(char value) => $"sha256:{new string(value, 64)}";
    private static string Commit(char value) => new(value, 40);
    private static string Hash(byte[] bytes) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    private static DateTimeOffset Time(int seconds) =>
        DateTimeOffset.Parse("2026-10-09T00:00:00Z").AddSeconds(seconds);

    private static NormalizedReview WithoutOriginalExecution(NormalizedReview source) => new(
        source.Outcome,
        source.Reason,
        source.Identity,
        source.PolicyIdentity,
        source.PolicyContentIdentity,
        source.PolicySourceRevision,
        source.PolicyAuthorityKind,
        source.AuthorFamily,
        source.ReviewerFamily,
        source.Findings,
        source.ArtifactHash,
        source.CompletedAt,
        source.Fixture,
        source.Advisory,
        source.ProductionAccepted,
        originalExecution: false);

    private static ValidatedContract EvidencePolicy()
    {
        var node = JsonNode.Parse($$"""
            {
              "schemaVersion": "1.0.0",
              "kind": "evidence-policy",
              "identity": "{{Identity('0')}}",
              "policyId": "review-tests",
              "requiredGates": ["review"],
              "authorizedCeiling": "pr",
              "requireIndependentReview": true,
              "allowedNotApplicable": [],
              "createdAt": "2026-10-09T00:00:00Z"
            }
            """)!.AsObject();
        node["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(node.ToJsonString()));
        return ContractValidator.ParseAndValidate(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }
}

internal sealed class TinyGitRepository : IDisposable
{
    public TinyGitRepository()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "delivery-forge-review", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Git("init", "--quiet");
        Git("config", "user.name", "Delivery Forge Tests");
        Git("config", "user.email", "tests@example.invalid");
    }

    public string Path { get; }
    public string Commit(IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (path, bytes) in files) Write(path, bytes);
        return CommitAll();
    }
    public string CommitAll()
    {
        Git("add", "-A");
        Git("commit", "--quiet", "-m", "fixture");
        return Git("rev-parse", "HEAD");
    }
    public void Write(string relative, byte[] bytes)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }
    public void Move(string from, string to) => Git("mv", from, to);
    public void Delete(string relative) => File.Delete(System.IO.Path.Combine(Path, relative));
    public void MakeExecutable(string relative) => Git("update-index", "--chmod=+x", relative);
    public string Git(params string[] args) => Encoding.UTF8.GetString(GitBytes(args)).Trim();
    public byte[] GitBytes(params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo("git", args)
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output.ToArray();
    }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
