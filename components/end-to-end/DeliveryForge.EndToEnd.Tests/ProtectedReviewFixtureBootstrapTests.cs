using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Review;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class ProtectedReviewFixtureBootstrapTests
{
    [Fact(Timeout = 120_000)]
    public async Task Bounded_protected_resolver_returns_actual_protected_policy_with_original_source_provenance()
    {
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wall.CancelAfter(TimeSpan.FromSeconds(120));
        await using var run = await OwnedScenarioRun.CreateAsync(wall.Token);
        run.RetainArtifacts("Bounded protected resolver real source positive RED retained.");
        var setup = await Setup(run, wall.Token);
        using var view = await PrepareBoundedResolverSource(run, setup, wall.Token);
        var started = Stopwatch.GetTimestamp();
        var policy = await view.Resolver.ResolveBoundedAsync("policy/review.json", TimeSpan.FromSeconds(15), wall.Token);
        Assert.True(policy.Trusted);
        Assert.False(policy.Fixture);
        Assert.Equal(ReviewAuthorityRealm.Test, policy.SourceRealm);
        Assert.Equal(setup.BaseCommit, policy.SourceRevision);
        Assert.Equal(setup.ProtectedPolicySha256, policy.ContentIdentity);
        Assert.Equal(view.Source.SourceAuthorityIdentity, policy.SourceAuthorityIdentity);
        Assert.Equal(view.Source.ProtectedObjectViewIdentity, policy.ProtectedObjectViewIdentity);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15));
    }

    [Fact(Timeout = 120_000)]
    public async Task Bounded_protected_resolver_precancelled_caller_refuses_without_issuing_policy()
    {
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wall.CancelAfter(TimeSpan.FromSeconds(120));
        await using var run = await OwnedScenarioRun.CreateAsync(wall.Token);
        run.RetainArtifacts("Bounded protected resolver caller cancellation RED retained.");
        var setup = await Setup(run, wall.Token);
        using var view = await PrepareBoundedResolverSource(run, setup, wall.Token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(wall.Token); cancelled.Cancel();
        var started = Stopwatch.GetTimestamp();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.Resolver.ResolveBoundedAsync(
            "policy/review.json", TimeSpan.FromSeconds(15), cancelled.Token));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15));
    }

    [Fact(Timeout = 120_000)]
    public async Task Bounded_protected_resolver_expired_original_allowance_refuses_without_late_policy()
    {
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wall.CancelAfter(TimeSpan.FromSeconds(120));
        await using var run = await OwnedScenarioRun.CreateAsync(wall.Token);
        run.RetainArtifacts("Bounded protected resolver exhausted original allowance RED retained.");
        var setup = await Setup(run, wall.Token);
        using var view = await PrepareBoundedResolverSource(run, setup, wall.Token);
        var started = Stopwatch.GetTimestamp();
        await Assert.ThrowsAsync<TimeoutException>(() => view.Resolver.ResolveBoundedAsync(
            "policy/review.json", TimeSpan.FromTicks(1), wall.Token));
        // This bounds the observation; it does not claim hard realtime return within one tick.
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15));
    }

    private sealed record BoundedResolverSource(ControlledGitEvidenceContext View, ProtectedReviewSourceContext Source,
        ProtectedReviewPolicyResolver Resolver) : IDisposable
    { public void Dispose() => View.Dispose(); }
    private static async Task<BoundedResolverSource> PrepareBoundedResolverSource(OwnedScenarioRun run, SetupResult setup, CancellationToken token)
    {
        var host = ReviewAuthorityHost.CreateIsolatedTest();
        var input = setup.Configuration;
        var preparation = await host.PrepareIsolatedTestGitEvidenceAsync(input.SourceRoot, input.WorkerGitDirectory, input.TrustedGitExecutable,
            input.MaximumEvidenceBytes, TimeSpan.FromSeconds(15), token);
        if (preparation.Context is null || preparation.Observation.Outcome != ReviewGitPreparationOutcome.Prepared)
            throw new InvalidOperationException("Actual bounded resolver source preparation failed: " + preparation.Observation.Reason);
        var source = host.FreezeProtectedSource(preparation.Context, setup.BaseCommit, preparation.Context.SourceAuthorityIdentity);
        await run.WriteNewJsonAsync("bounded-resolver-real-source.json", new
        { fixture = true, preparation.Observation, source.SourceAuthorityIdentity, source.ProtectedObjectViewIdentity, source.ProtectedBaseCommit }, token);
        return new(preparation.Context, source, new(source));
    }

    [Theory(Timeout = 120_000)]
    [InlineData("same-issuer-positive")]
    [InlineData("foreign-policy")]
    [InlineData("root-rebind")]
    [InlineData("worker-policy-spoof")]
    public async Task Protected_policy_bootstrap_joins_real_base_view_policy_and_members_on_one_issuer(string scenario)
    {
        var started = Stopwatch.GetTimestamp();
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wall.CancelAfter(TimeSpan.FromSeconds(120));
        var token = wall.Token;
        await using var run = await OwnedScenarioRun.CreateAsync(token);
        run.RetainArtifacts("Protected review fixture bootstrap raw Git/base/policy/owner refusals retained.");
        var setup = await Setup(run, token);
        // All real source/contract/Git/public resolver setup above must succeed before missing behavior.
        using var bootstrap = await ReviewAuthorityHost.CreateProtectedGitContractFixtureAsync(setup.Configuration, token);
        Assert.True(bootstrap.Policy.Trusted);
        Assert.False(bootstrap.Policy.Fixture);
        Assert.Equal(ReviewAuthorityRealm.Test, bootstrap.Policy.SourceRealm);
        Assert.Equal(setup.BaseCommit, bootstrap.Policy.SourceRevision);
        Assert.Equal(setup.ProtectedPolicySha256, bootstrap.Policy.ContentIdentity);
        Assert.Equal(setup.Plan.ContractIdentity, bootstrap.RootContext.PlanIdentity);
        Assert.Equal(setup.Plan.BaseCommit, bootstrap.RootContext.ProtectedBaseCommit);
        Assert.Equal(bootstrap.ObjectView.SourceAuthorityIdentity, bootstrap.RootContext.SourceAuthorityIdentity);
        Assert.Equal(bootstrap.ObjectView.ProtectedObjectViewIdentity, bootstrap.RootContext.ProtectedObjectViewIdentity);
        Assert.Equal(bootstrap.Policy.SourceAuthorityIdentity, bootstrap.RootContext.SourceAuthorityIdentity);
        Assert.Equal(bootstrap.Policy.ProtectedObjectViewIdentity, bootstrap.RootContext.ProtectedObjectViewIdentity);
        var primary = await Issue(run, bootstrap, setup, "primary", token);
        Assert.Equal(ReviewOutcome.Pass, primary.Normalized.Outcome);
        Assert.True(primary.Normalized.AuthorityAccepted);
        Assert.True(primary.Normalized.Fixture);
        Assert.False(primary.Normalized.ProductionAccepted);
        bootstrap.Host.EnsureContractFixtureTransition(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, primary.Evidence, primary.Receiver);
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized, primary.Evidence, primary.Receiver));
        if (scenario == "foreign-policy")
        {
            using var other = await ReviewAuthorityHost.CreateProtectedGitContractFixtureAsync(setup.Configuration, token);
            var secondary = await Issue(run, other, setup, "foreign", token);
            Assert.Equal(ReviewOutcome.Pass, secondary.Normalized.Outcome);
            other.Host.EnsureContractFixtureTransition(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, secondary.Evidence, secondary.Receiver);
            var refused = ReviewNormalizer.Normalize(primary.Request, primary.Result, other.Policy, primary.Capability, primary.Execution);
            Assert.Equal(ReviewOutcome.Incomplete, refused.Outcome);
            Assert.False(refused.AuthorityAccepted);
            Assert.Throws<InvalidWorkflowTransitionException>(() => bootstrap.Host.EnsureContractFixtureTransition(
                WorkflowState.IndependentReview, WorkflowState.PrAuthorized, secondary.Evidence, primary.Receiver));
        }
        if (scenario == "root-rebind")
        {
            var foreignBinding = primary.Context with { SourceAuthorityIdentity = "caller-chosen-rebind" };
            Assert.Throws<InvalidOperationException>(() => bootstrap.Host.RegisterRequest("rebind-request", primary.Request, foreignBinding));
            Assert.Throws<InvalidOperationException>(() => bootstrap.Host.CreateTransitionReceiver(foreignBinding));
            bootstrap.Host.EnsureContractFixtureTransition(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, primary.Evidence, primary.Receiver);
        }
        if (scenario == "worker-policy-spoof")
        {
            Assert.NotEqual(setup.ProtectedPolicySha256, setup.WorkerPolicySha256);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(token); bound.CancelAfter(TimeSpan.FromSeconds(15));
            var reobserved = await new ProtectedReviewPolicyResolver(bootstrap.ProtectedSource).ResolveAsync("policy/review.json", bound.Token);
            Assert.Equal(setup.ProtectedPolicySha256, reobserved.ContentIdentity);
            Assert.Contains(new FamilyPair("gpt", "claude"), reobserved.AllowedFamilies);
        }
        await run.WriteNewJsonAsync("protected-bootstrap-observation.json", new
        {
            fixture = true, scenario, setup.BaseCommit, setup.HeadCommit, setup.ProtectedPolicySha256, setup.WorkerPolicySha256,
            bootstrap.RootContext, outcome = primary.Normalized.Outcome.ToString(), reason = primary.Normalized.Reason,
            primary.Normalized.ProductionAccepted, actualModelCalls = 0, publicationCalls = 0,
            limitation = "Real protected Git policy under original Test issuer; fixture reviewer only, no production/model certificate."
        }, token);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(120));
    }

    internal sealed record SetupResult(ProtectedReviewFixtureConfiguration Configuration, FrozenPlan Plan, ValidatedContract EvidencePolicy,
        string BaseCommit, string HeadCommit, string ProtectedPolicySha256, string WorkerPolicySha256);
    internal static async Task<SetupResult> Setup(OwnedScenarioRun run, CancellationToken token,
        string initialCeiling = "pr", bool includeHostedPolicy = false)
    {
        var protectedJson = includeHostedPolicy ? """
            {"Fixture":false,"Advisory":false,"AllowedFamilies":[{"Author":"gpt","Reviewer":"claude"},{"Author":"gpt","Reviewer":"github-hosted"}],"AllowedScopes":["source","pull-request"],"AllowExactReuse":false,"Budget":{"MaximumRounds":4,"MaximumDuration":"00:00:30","MaximumTokens":8192,"MaximumCost":2.0},"ExemptionApprovers":[]}
            """ : """
            {"Fixture":false,"Advisory":false,"AllowedFamilies":[{"Author":"gpt","Reviewer":"claude"}],"AllowedScopes":["source"],"AllowExactReuse":false,"Budget":{"MaximumRounds":2,"MaximumDuration":"00:00:30","MaximumTokens":4096,"MaximumCost":1.0},"ExemptionApprovers":[]}
            """;
        var protectedBytes = Encoding.UTF8.GetBytes(protectedJson);
        Directory.CreateDirectory(Path.Combine(run.Checkout, "policy"));
        await File.WriteAllBytesAsync(Path.Combine(run.Checkout, "policy", "review.json"), protectedBytes, token);
        await run.GitAsync(token, "add", "--", "policy/review.json");
        await run.GitAsync(token, "-c", "user.name=ReviewFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "protected review policy");
        var baseCommit = await run.GitAsync(token, "rev-parse", "HEAD");
        await run.GitAsync(token, "checkout", "-q", "-b", "protected-review-candidate");
        var workerBytes = "{}"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(run.Checkout, "policy", "review.json"), workerBytes, token);
        await File.WriteAllTextAsync(Path.Combine(run.Checkout, "subject.txt"), "candidate under real protected policy\n", token);
        await run.GitAsync(token, "add", "--", "subject.txt", "policy/review.json");
        await run.GitAsync(token, "-c", "user.name=ReviewFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "candidate cannot replace protected policy");
        var context = await run.Reader.ReadAsync(run.Checkout, "refs/heads/main", token);
        var now = DateTimeOffset.UtcNow;
        var request = new PlanningRequest("fixture/delivery-forge", "#11", "implement", "Protected review bootstrap fixture",
            ["subject.txt", "policy/review.json"], ["excluded"], ["Same original Test issuer, actual protected policy"], initialCeiling);
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(await run.Reader.ReadFileAsync(context, "subject.txt", token), now, [])];
        var plan = PlanFreezer.Freeze(new(request, context, evidence, [new("subject.txt", "fixture", "candidate")], [new("#11", [], "root")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], IntakePlanner.Assess(request, evidence)), "protected-review-fixture", now);
        var policyNode = new JsonObject
        {
            ["schemaVersion"] = "1.0.0", ["kind"] = "evidence-policy", ["policyId"] = "protected-review-bootstrap-fixture",
            ["requiredGates"] = new JsonArray("test"), ["authorizedCeiling"] = initialCeiling, ["requireIndependentReview"] = true,
            ["allowedNotApplicable"] = new JsonArray(), ["createdAt"] = now.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
        policyNode["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(policyNode.ToJsonString()));
        var policyBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(policyNode.ToJsonString()));
        var evidencePolicy = ContractValidator.ParseAndValidate(policyBytes);
        await new AppendOnlyContractStore(Path.Combine(run.Root, "bootstrap-plan-contracts")).WriteImmutableAsync(plan.PlanContractBytes, token);
        await new AppendOnlyContractStore(Path.Combine(run.Root, "bootstrap-evidence-policy-inputs")).WriteImmutableAsync(policyBytes, token);
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "git.exe" : "git")).First(File.Exists);
        // Existing public preparation/resolver proves actual protected bytes before the factory stub.
        // This independent setup host is discarded; it is never supplied as the bootstrap's authority.
        var setupHost = ReviewAuthorityHost.CreateIsolatedTest();
        var preparation = await setupHost.PrepareIsolatedTestGitEvidenceAsync(run.Checkout, Path.Combine(run.Checkout, ".git"), git,
            1024 * 1024, TimeSpan.FromSeconds(15), token);
        if (preparation.Context is null || preparation.Observation.Outcome != ReviewGitPreparationOutcome.Prepared)
            throw new InvalidOperationException("Real bootstrap preparation setup failed: " + preparation.Observation.Reason);
        using (var view = preparation.Context)
        {
            var source = setupHost.FreezeProtectedSource(view, baseCommit, view.SourceAuthorityIdentity);
            using var readBound = CancellationTokenSource.CreateLinkedTokenSource(token); readBound.CancelAfter(TimeSpan.FromSeconds(15));
            var actualPolicy = await new ProtectedReviewPolicyResolver(source).ResolveAsync("policy/review.json", readBound.Token);
            Assert.True(actualPolicy.Trusted); Assert.Equal(Hash(protectedBytes), actualPolicy.ContentIdentity);
            Assert.Equal(baseCommit, actualPolicy.SourceRevision); Assert.Equal(ReviewAuthorityRealm.Test, actualPolicy.SourceRealm);
        }
        await run.WriteNewJsonAsync("protected-bootstrap-real-setup.json", new
        { fixture = true, baseCommit, headCommit = context.HeadCommit, planBase = plan.BaseCommit, protectedPolicySha256 = Hash(protectedBytes), workerPolicySha256 = Hash(workerBytes), originalFactorySeconds = 15 }, token);
        return new(new(run.RunId, plan.PlanContractBytes, policyBytes, run.Checkout, Path.Combine(run.Checkout, ".git"), git,
            "policy/review.json", 1024 * 1024, TimeSpan.FromSeconds(15)), plan, evidencePolicy, baseCommit, context.HeadCommit, Hash(protectedBytes), Hash(workerBytes));
    }

    private sealed record IssuedChain(ReviewRequest Request, ReviewResult Result, NormalizedReview Normalized,
        IssuedReviewerCapability Capability, AcceptedReviewExecution Execution, ReviewAcceptanceContext Context,
        ReviewTransitionReceiver Receiver, TransitionEvidence Evidence);
    private static async Task<IssuedChain> Issue(OwnedScenarioRun run, ProtectedReviewFixtureBootstrap bootstrap, SetupResult setup, string name, CancellationToken token)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token); bound.CancelAfter(TimeSpan.FromSeconds(15));
        var patch = await GitPatchIdentity.ComputeAsync(bootstrap.ObjectView, setup.BaseCommit, setup.HeadCommit, setup.Plan.ContractIdentity,
            new Dictionary<string, string> { ["plan-contract"] = Hash(setup.Plan.PlanContractBytes) }, bound.Token);
        var root = bootstrap.RootContext;
        var context = new ReviewAcceptanceContext(root.RunIdentity, "bootstrap-request-" + Guid.NewGuid().ToString("N"), root.PlanIdentity,
            root.EvidencePolicyIdentity, root.ReviewPolicyContentIdentity, root.ReviewPolicySourceRevision, root.ReviewPolicyAuthorityKind,
            root.SourceAuthorityIdentity, root.SourceRoot, root.ProtectedBaseCommit, root.ProtectedObjectViewIdentity);
        var receiver = bootstrap.Host.CreateTransitionReceiver(context);
        var data = new ReviewerCapability("explicit-external-review-fixture", "1.0.0", "claude", true, true, false, ["source"]);
        var request = new ReviewRequest(patch, "gpt", data, "source", bootstrap.Policy.PolicyIdentity);
        var registration = bootstrap.Host.RegisterRequest(context.RequestIdentity, request, context);
        var now = DateTimeOffset.UtcNow;
        var capability = bootstrap.Host.IssueCapability(data, "bootstrap-capability-" + Guid.NewGuid().ToString("N"), now.AddSeconds(-1), now.AddSeconds(120));
        var raw = await new ReviewFixturePort().ExecuteAsync(registration, Path.Combine(run.Root, "bootstrap-" + name + "-raw.json"), token);
        var execution = bootstrap.Host.RecordExecution(registration, capability, "bootstrap-execution-" + Guid.NewGuid().ToString("N"), true, true,
            raw.RawSha256, raw.Verdict, raw.CompletedAt);
        var result = ReviewResult.Complete(patch, "claude", "PASS", [], raw.RawSha256, raw.CompletedAt);
        var normalized = ReviewNormalizer.Normalize(request, result, bootstrap.Policy, capability, execution);
        var receiptBytes = ReviewReceiptSerializer.Serialize(ReviewReceiptSerializer.Create(normalized));
        var receipt = ContractValidator.ParseAndValidate(receiptBytes);
        await new AppendOnlyContractStore(Path.Combine(run.Root, "bootstrap-review-contracts")).WriteImmutableAsync(receiptBytes, token);
        bootstrap.Host.IssueAcceptance(receipt, normalized, context, ReviewEvidenceRole.Independent);
        return new(request, result, normalized, capability, execution, context, receiver,
            new(AuthorizationCeiling.Pr, setup.BaseCommit, setup.HeadCommit, patch.HeadTree, setup.EvidencePolicy, ReviewReceipt: receipt));
    }
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
