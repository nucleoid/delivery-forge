using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution.Host;
using DeliveryForge.Publication.GitHost;
using DeliveryForge.Review;

namespace DeliveryForge.Publication.Tests;

public sealed class PublicationFixture
{
    private readonly AuthorizationCeiling permittedCeiling;
    public PublicationFixture() : this("publication-fixture-run", 'e', "merge", '2', '3', null) { }

    private PublicationFixture(string runIdentity, char planIdentity, string evidenceCeiling,
        char independentArtifact, char hostedArtifact, string? sourceRoot)
    {
        permittedCeiling = evidenceCeiling == "merge" ? AuthorizationCeiling.Merge : AuthorizationCeiling.Pr;
        Policy = Contract($$"""
            { "schemaVersion":"1.0.0", "kind":"evidence-policy", "identity":"{{Id('0')}}",
              "policyId":"publication-fixture", "requiredGates":["build","test"],
              "authorizedCeiling":"{{evidenceCeiling}}", "requireIndependentReview":true,
              "allowedNotApplicable":["coverage"], "createdAt":"2026-10-11T00:00:01Z" }
            """);
        Root = new ReviewAuthorityRootContext(runIdentity, Id(planIdentity), Policy.Identity,
            Id('f'), Commit('f'), ReviewPolicyAuthorityKind.Caller, "fixture-source",
            sourceRoot ?? Path.GetFullPath(Path.Combine(Path.GetTempPath(), "publication-fixture-source")), Commit('a'), "fixture-view");
        ReviewHost = ReviewAuthorityHost.CreateIsolatedTest(Root, Time);
        var reviewPolicy = ReviewHost.CreateContractFixturePolicy(
            [new FamilyPair("gpt", "claude"), new FamilyPair("gpt", "github-hosted")],
            ["source"], true, new ReviewBudget(3, TimeSpan.FromMinutes(5), 20_000, 2m));
        var independentContext = Acceptance("independent");
        var hostedContext = Acceptance("hosted");
        Receiver = ReviewHost.CreateTransitionReceiver(independentContext, hostedContext);
        Patch = new PatchIdentity(Commit('a'), Commit('b'), Commit('c'), Id('d'), Root.PlanIdentity,
            new Dictionary<string, string> { ["tests"] = Id('1') }, "git version fixture", ["git", "diff"]);
        Review = IssueReview(reviewPolicy, "claude", independentContext, Id(independentArtifact), ReviewEvidenceRole.Independent);
        HostedReview = IssueReview(reviewPolicy, "github-hosted", hostedContext, Id(hostedArtifact), ReviewEvidenceRole.Hosted);
        Gates = [Gate("build", "PASS"), Gate("test", "PASS")];
        Ci = Gate("ci", "PASS");
        Authority = PublicationAuthorityHost.CreateIsolatedFixture(ReviewHost);
    }

    public static DateTimeOffset Time => DateTimeOffset.Parse("2026-10-11T00:00:01Z");
    public static PublicationFixture NewRoot(string? sourceRoot = null) =>
        new("publication-new-root", '9', "pr", '7', '8', sourceRoot);
    public ReviewAuthorityRootContext Root { get; }
    public ReviewAuthorityHost ReviewHost { get; }
    public PublicationAuthorityHost Authority { get; }
    public ReviewTransitionReceiver Receiver { get; }
    public PatchIdentity Patch { get; }
    public ValidatedContract Policy { get; }
    public ValidatedContract Review { get; }
    public ValidatedContract HostedReview { get; }
    public IReadOnlyList<ValidatedContract> Gates { get; }
    public ValidatedContract Ci { get; }
    public PublicationTarget Target { get; } = new("fixture/disposable", "refs/heads/issue-10", "refs/heads/main");

    public PublicationRunDefinition Definition(AuthorizationCeiling ceiling = AuthorizationCeiling.Merge) => new(
        Root.RunIdentity, "revision-" + Guid.NewGuid().ToString("N"), null, ceiling, Target, Patch, Policy, Gates, Ci, Review, HostedReview, Receiver);

    public TransitionEvidence Transition => new(permittedCeiling, Patch.BaseCommit, Patch.HeadCommit,
        Patch.HeadTree, Policy: Policy, GateReceipts: Gates, CiReceipt: Ci,
        ReviewReceipt: Review, HostReviewReceipt: HostedReview);

    public ValidatedContract PublicationReceipt(string action) => Contract($$"""
        { "schemaVersion":"1.0.0", "kind":"publication-receipt", "identity":"{{Id('0')}}",
          "baseCommit":"{{Patch.BaseCommit}}", "headCommit":"{{Patch.HeadCommit}}", "treeId":"{{Patch.HeadTree}}",
          "action":"{{action}}", "remoteRef":"refs/pull/10/head",
          "resultId":"https://example.invalid/fixture/pr/10", "createdAt":"2026-10-11T00:00:01Z" }
        """);

    public ValidatedContract Gate(string gateId, string outcome) => Contract($$"""
        { "schemaVersion":"1.0.0", "kind":"gate-receipt", "identity":"{{Id('0')}}",
          "gateId":"{{gateId}}", "policyIdentity":"{{Policy.Identity}}",
          "baseCommit":"{{Commit('a')}}", "headCommit":"{{Commit('b')}}", "treeId":"{{Commit('c')}}",
          "command":"fixture command", "startedAt":"2026-10-11T00:00:00Z",
          "completedAt":"2026-10-11T00:00:01Z", "exitCode":{{(outcome == "PASS" ? 0 : 1)}},
          "outcome":"{{outcome}}", "reason":"fixture", "artifactHashes":{},
          "sourceChanged":false, "limitations":["fixture"] }
        """);

    private ValidatedContract IssueReview(ReviewPolicy policy, string family, ReviewAcceptanceContext context,
        string artifact, ReviewEvidenceRole role)
    {
        var capability = new ReviewerCapability("fixture", "1", family, true, true, false, ["source"]);
        var request = new ReviewRequest(Patch, "gpt", capability, "source", policy.PolicyIdentity);
        var registration = ReviewHost.RegisterRequest(context.RequestIdentity, request, context);
        var proof = ReviewHost.IssueCapability(capability, family + "-proof", Time.AddSeconds(-1), Time.AddMinutes(1));
        var execution = ReviewHost.RecordExecution(registration, proof, family + "-execution", true, true, artifact, "ship", Time);
        var normalized = ReviewNormalizer.Normalize(request, ReviewResult.Complete(Patch, family, "PASS", [], artifact, Time),
            policy, proof, execution);
        Assert.True(normalized.AuthorityAccepted);
        Assert.False(normalized.ProductionAccepted);
        var receipt = ContractValidator.ParseAndValidate(ReviewReceiptSerializer.Serialize(ReviewReceiptSerializer.Create(normalized)));
        ReviewHost.IssueAcceptance(receipt, normalized, context, role);
        return receipt;
    }

    private ReviewAcceptanceContext Acceptance(string requestId) => new(Root.RunIdentity, requestId,
        Root.PlanIdentity, Root.EvidencePolicyIdentity, Root.ReviewPolicyContentIdentity, Root.ReviewPolicySourceRevision,
        Root.ReviewPolicyAuthorityKind, Root.SourceAuthorityIdentity, Root.SourceRoot, Root.ProtectedBaseCommit, Root.ProtectedObjectViewIdentity);

    public static string Id(char value) => "sha256:" + new string(value, 64);
    public static string Commit(char value) => new(value, 40);
    public static ValidatedContract Contract(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(node.ToJsonString()));
        return ContractValidator.ParseAndValidate(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }
}

internal sealed class FixtureFacts(PublicationFixture fixture) : IPublicationFactSource
{
    public Func<PublicationRun, int, PublicationLocalObservation>? Observe { get; set; }
    public int Reads { get; private set; }
    public Task<PublicationLocalObservation> ObserveAsync(PublicationRun run, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(Observe?.Invoke(run, Reads) ?? new PublicationLocalObservation(fixture.Patch, true,
            PublicationOwnership.ReconciledFixture, false, run.Definition.RevisionIdentity));
    }
}

// External-call ledger is fixture-only. It makes no operating-system/network writes.
internal sealed class FixtureGitHost(PublicationFixture fixture) : IGitHostAdapter
{
    private int active;
    public PublicationRealm Realm => PublicationRealm.Fixture;
    public int Mutations { get; private set; }
    public int Reads { get; private set; }
    public int MaximumConcurrentMutations { get; private set; }
    public bool LoseResponse { get; set; }
    public TimeSpan MutationDelay { get; set; }
    public Func<int, GitHostObservation>? Read { get; set; }
    public Action<GitHostMutation>? BeforeMutation { get; set; }
    public GitHostObservation State { get; private set; } = new(fixture.Target, fixture.Patch.HeadCommit,
        fixture.Patch.BaseCommit, fixture.Patch.HeadCommit, GitHostChecks.Passing, GitHostProtection.RequiredAndObserved);
    public List<PublicationAction> Actions { get; } = [];
    public Task<GitHostObservation> ReadAsync(PublicationTarget target, string idempotencyKey, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(Read?.Invoke(Reads) ?? State);
    }
    public Task<GitHostMutationResult> CreateOrUpdatePrAsync(GitHostMutation mutation, CancellationToken cancellationToken) =>
        MutateAsync(mutation, cancellationToken);
    public Task<GitHostMutationResult> MergeNormalAsync(GitHostMutation mutation, CancellationToken cancellationToken) =>
        MutateAsync(mutation, cancellationToken);
    private async Task<GitHostMutationResult> MutateAsync(GitHostMutation mutation, CancellationToken cancellationToken)
    {
        var concurrent = Interlocked.Increment(ref active);
        MaximumConcurrentMutations = Math.Max(MaximumConcurrentMutations, concurrent);
        try
        {
            BeforeMutation?.Invoke(mutation);
            Mutations++;
            Actions.Add(mutation.Intent.Action);
            if (MutationDelay > TimeSpan.Zero) await Task.Delay(MutationDelay, cancellationToken);
            State = State with
            {
                PublishedIdempotencyKey = mutation.Intent.IdempotencyKey,
                ResultId = "https://example.invalid/fixture/pr/10",
                MergedHead = mutation.Intent.Action == PublicationAction.MergeNormal ? mutation.Intent.HeadCommit : null
            };
            return new(LoseResponse ? GitHostMutationOutcome.Uncertain : GitHostMutationOutcome.Confirmed, State.ResultId);
        }
        finally { Interlocked.Decrement(ref active); }
    }
}

internal sealed class FixtureJournal : IPublicationJournal
{
    public List<PublicationIntent> Records { get; } = [];
    public ConcurrentDictionary<string, ValidatedContract> Receipts { get; } = new(StringComparer.Ordinal);
    public Task<PublicationIntent?> ReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Records.LastOrDefault(record => record.IdempotencyKey == key));
    public Task AppendAsync(PublicationIntent intent, CancellationToken cancellationToken)
    {
        Records.Add(intent);
        return Task.CompletedTask;
    }
    public Task AppendReceiptAsync(ValidatedContract receipt, CancellationToken cancellationToken)
    {
        Receipts.TryAdd(receipt.Identity, receipt);
        return Task.CompletedTask;
    }
}
