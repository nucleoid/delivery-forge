using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;

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

        var identity = await GitPatchIdentity.ComputeAsync(
            repository.Path, baseCommit, headCommit, Identity('e'),
            new Dictionary<string, string> { ["tests"] = Identity('a') },
            TestContext.Current.CancellationToken);
        var bytes = repository.GitBytes(
            "-c", "core.quotepath=false", "diff", "--binary", "--full-index", "--no-color",
            "--no-ext-diff", baseCommit, headCommit, "--");

        Assert.Equal(Hash(bytes), identity.PatchSha256);
        Assert.Equal(repository.Git("rev-parse", $"{headCommit}^{{tree}}"), identity.HeadTree);
        Assert.Equal(["git", "-c", "core.quotepath=false", "diff", "--binary", "--full-index",
            "--no-color", "--no-ext-diff", baseCommit, headCommit, "--"], identity.Command);
        Assert.Contains("git version", identity.GitVersion, StringComparison.Ordinal);
        Assert.Equal(Identity('e'), identity.PlanIdentity);
        Assert.Equal(Identity('a'), identity.ArtifactIdentities["tests"]);

        repository.Write("dirty.txt", "dirty"u8.ToArray());
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            repository.Path, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Patch_identity_rejects_staged_mismatched_missing_and_non_head_inputs()
    {
        using var repository = new TinyGitRepository();
        var baseCommit = repository.Commit(new Dictionary<string, byte[]> { ["a.txt"] = "a"u8.ToArray() });
        repository.Write("a.txt", "b"u8.ToArray());
        var headCommit = repository.CommitAll();

        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            repository.Path, baseCommit, new string('f', 40), Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            repository.Path, headCommit, baseCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
        repository.Write("staged.txt", "staged"u8.ToArray());
        repository.Git("add", "staged.txt");
        await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(
            repository.Path, baseCommit, headCommit, Identity('e'), new Dictionary<string, string>(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Trusted_fixture_policy_accepts_explicit_different_family_but_not_production()
    {
        var policy = Policy();
        var normalized = ReviewNormalizer.Normalize(Request(policy), Result(), policy);
        Assert.Equal(ReviewOutcome.Pass, normalized.Outcome);
        Assert.False(normalized.ProductionAccepted);
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
            Result() with { Identity = identity with { HeadCommit = Commit('d') } },
            Result() with { Identity = identity with { PatchSha256 = Identity('9') } },
            Result() with { ReviewerFamily = "pi" }
        };
        Assert.All(cases, value => Assert.NotEqual(
            ReviewOutcome.Pass, ReviewNormalizer.Normalize(Request(policy), value, policy).Outcome));
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
        var receipt = ReviewNormalizer.Normalize(Request(policy), Result(), policy);
        Assert.True(ReviewInvalidation.Assess(receipt, Patch(), policy, MaterialChange.None).Reusable);
        Assert.False(ReviewInvalidation.Assess(
            receipt, Patch() with { HeadTree = Commit('d') }, policy, MaterialChange.None).Reusable);
        Assert.False(ReviewInvalidation.Assess(
            WithoutOriginalExecution(receipt), Patch(), policy, MaterialChange.None).Reusable);
        Assert.False(ReviewInvalidation.Assess(
            receipt, Patch(), Policy(false), MaterialChange.None).Reusable);
    }

    [Fact]
    public void Explicit_budget_exhaustion_pauses_and_preserves_findings()
    {
        var ledger = new ReviewBudgetLedger(new ReviewBudget(1, TimeSpan.FromSeconds(5), 100, 1m));
        Assert.True(ledger.TryReserve(new ReviewCost(TimeSpan.FromSeconds(4), 80, .75m), out _));
        var finding = new ReviewFinding("R1", "material", "fix this", "A.cs:1");
        ledger.RecordFindings([finding]);
        Assert.False(ledger.TryReserve(new ReviewCost(TimeSpan.FromSeconds(2), 30, .50m), out var exhausted));
        Assert.Equal(ReviewOutcome.Incomplete, exhausted.Outcome);
        Assert.Equal([finding], exhausted.Findings);
        Assert.Equal(1, ledger.ConsumedRounds);
    }

    [Fact]
    public void Raw_capability_and_mutable_caller_collections_cannot_gain_or_change_authority()
    {
        var capability = new ReviewerCapability("caller", "1", "claude", true, false, false, ["source"]);
        Assert.False(capability.ProductionCapable);

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
              "PolicyIdentity": "sha256:7777777777777777777777777777777777777777777777777777777777777777",
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

        var policy = await new ProtectedReviewPolicyResolver(repository.Path, protectedBase)
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
            new ProtectedReviewPolicyResolver(repository.Path, protectedBase)
                .ResolveAsync("policy/review.json", TestContext.Current.CancellationToken));

        Assert.Contains("malformed", error.Message, StringComparison.Ordinal);
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
