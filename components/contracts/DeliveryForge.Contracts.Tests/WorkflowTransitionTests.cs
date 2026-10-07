using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Contracts.Tests;

public sealed class WorkflowTransitionTests
{
    private static readonly HashSet<(WorkflowState From, WorkflowState To)> ExpectedLinear =
    [
        (WorkflowState.Understanding, WorkflowState.Planned),
        (WorkflowState.Planned, WorkflowState.Ready),
        (WorkflowState.Ready, WorkflowState.Executing),
        (WorkflowState.Executing, WorkflowState.Paused),
        (WorkflowState.Paused, WorkflowState.Executing),
        (WorkflowState.Executing, WorkflowState.LocalComplete),
        (WorkflowState.LocalComplete, WorkflowState.Evaluating),
        (WorkflowState.Evaluating, WorkflowState.IndependentReview),
        (WorkflowState.IndependentReview, WorkflowState.PrAuthorized),
        (WorkflowState.PrAuthorized, WorkflowState.PrPublished),
        (WorkflowState.PrPublished, WorkflowState.CiComplete),
        (WorkflowState.CiComplete, WorkflowState.HostReviewComplete),
        (WorkflowState.HostReviewComplete, WorkflowState.MergeAuthorized),
        (WorkflowState.MergeAuthorized, WorkflowState.Merged)
    ];

    [Fact]
    public void Transition_matrix_denies_every_unlisted_nonterminal_edge()
    {
        var terminalTargets = new[] { WorkflowState.Blocked, WorkflowState.Failed, WorkflowState.Stopped };
        var terminalSources = terminalTargets.Append(WorkflowState.Merged).ToHashSet();
        foreach (var from in Enum.GetValues<WorkflowState>())
        {
            foreach (var to in Enum.GetValues<WorkflowState>())
            {
                var expected = ExpectedLinear.Contains((from, to)) || (!terminalSources.Contains(from) && terminalTargets.Contains(to));
                var evidence = FullEvidence(to == WorkflowState.Merged);
                if (expected)
                {
                    WorkflowTransition.EnsureAllowed(from, to, evidence);
                }
                else
                {
                    Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(from, to, evidence));
                }
            }
        }
    }

    [Theory]
    [InlineData(WorkflowState.IndependentReview, WorkflowState.PrAuthorized)]
    [InlineData(WorkflowState.PrAuthorized, WorkflowState.PrPublished)]
    [InlineData(WorkflowState.HostReviewComplete, WorkflowState.MergeAuthorized)]
    [InlineData(WorkflowState.MergeAuthorized, WorkflowState.Merged)]
    public void Authorization_ceiling_cannot_be_escalated(WorkflowState from, WorkflowState to) =>
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            from, to, FullEvidence(to == WorkflowState.Merged) with { Ceiling = AuthorizationCeiling.Implement }));

    [Theory]
    [InlineData(WorkflowState.Evaluating, WorkflowState.IndependentReview, "gate")]
    [InlineData(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, "review")]
    [InlineData(WorkflowState.PrAuthorized, WorkflowState.PrPublished, "publication")]
    [InlineData(WorkflowState.PrPublished, WorkflowState.CiComplete, "ci")]
    [InlineData(WorkflowState.CiComplete, WorkflowState.HostReviewComplete, "host")]
    public void Receipt_gated_edges_reject_missing_evidence(WorkflowState from, WorkflowState to, string missing)
    {
        var evidence = FullEvidence() with
        {
            GateReceipts = missing == "gate" ? [] : FullEvidence().GateReceipts,
            CiReceipt = missing == "ci" ? null : FullEvidence().CiReceipt,
            ReviewReceipt = missing == "review" ? null : FullEvidence().ReviewReceipt,
            PublicationReceipt = missing == "publication" ? null : FullEvidence().PublicationReceipt,
            HostReviewReceipt = missing == "host" ? null : FullEvidence().HostReviewReceipt
        };

        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(from, to, evidence));
    }

    [Fact]
    public void Receipts_must_match_kind_outcome_action_and_exact_head()
    {
        var failingReview = MutatedFixture("review-receipt.json", node => node["outcome"] = "FAIL");
        var wrongAction = ParseFixture("publication-receipt.json");
        var wrongHead = FullEvidence() with { HeadCommit = new string('f', 40) };

        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized, FullEvidence() with { ReviewReceipt = failingReview }));
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.MergeAuthorized, WorkflowState.Merged, FullEvidence() with { PublicationReceipt = wrongAction }));
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.Evaluating, WorkflowState.IndependentReview, wrongHead));

        var wrongKind = FullEvidence().GateReceipts![0];
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized, FullEvidence() with { ReviewReceipt = wrongKind }));
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.Evaluating, WorkflowState.IndependentReview, FullEvidence() with { TreeId = new string('e', 40) }));
    }

    [Fact]
    public void Implement_authority_is_required_for_execution()
    {
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.Ready, WorkflowState.Executing, FullEvidence() with { Ceiling = AuthorizationCeiling.Plan }));
    }

    [Fact]
    public void Hosted_review_and_ci_require_distinct_evidence()
    {
        var evidence = FullEvidence();
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.CiComplete, WorkflowState.HostReviewComplete,
            evidence with { ReviewReceipt = null, HostReviewReceipt = evidence.ReviewReceipt }));
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.PrPublished, WorkflowState.CiComplete,
            evidence with { CiReceipt = evidence.GateReceipts![0] }));
    }

    [Fact]
    public void Immutable_policy_limits_authority_and_requires_independent_review()
    {
        var implementPolicy = MutatedFixture("evidence-policy.json", node => node["authorizedCeiling"] = "implement");
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized,
            FullEvidence() with { Policy = implementPolicy }));

        var optionalReviewPolicy = MutatedFixture("evidence-policy.json", node =>
        {
            node["authorizedCeiling"] = "merge";
            node["requireIndependentReview"] = false;
        });
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized,
            FullEvidence() with { Policy = optionalReviewPolicy }));
    }

    [Fact]
    public void Validated_contract_bytes_are_defensive_copies()
    {
        var receipt = ParseFixture("review-receipt.json");
        var exposed = receipt.CanonicalBytes.ToArray();
        exposed[0] = (byte)'[';

        Assert.Equal((byte)'{', receipt.CanonicalBytes.Span[0]);
    }

    [Fact]
    public void Undefined_enum_values_fail_closed()
    {
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            (WorkflowState)99, WorkflowState.Blocked, FullEvidence()));
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.Ready, WorkflowState.Executing, FullEvidence() with { Ceiling = (AuthorizationCeiling)99 }));
    }

    [Fact]
    public void Checkpoint_sequence_must_strictly_increase()
    {
        CheckpointSequence.EnsureIncreasing(4, 5);
        Assert.Throws<CheckpointSequenceException>(() => CheckpointSequence.EnsureIncreasing(4, 4));
        Assert.Throws<CheckpointSequenceException>(() => CheckpointSequence.EnsureIncreasing(4, 3));
    }

    private static TransitionEvidence FullEvidence(bool merged = false)
    {
        var policy = MutatedFixture("evidence-policy.json", node => node["authorizedCeiling"] = "merge");
        var gates = new[] { "build", "test", "review" }
            .Select(gateId => GateFixture(policy.Identity, gateId))
            .ToArray();
        return new TransitionEvidence(
            AuthorizationCeiling.Merge,
            BaseCommit: "6304c90f83df3cdb918dff03f6358dcf4a939d16",
            HeadCommit: "43113d9000000000000000000000000000000000",
            TreeId: "2222222222222222222222222222222222222222",
            Policy: policy,
            GateReceipts: gates,
            CiReceipt: GateFixture(policy.Identity, "ci"),
            ReviewReceipt: ParseFixture("review-receipt.json"),
            PublicationReceipt: merged
                ? MutatedFixture("publication-receipt.json", node => node["action"] = "MERGED")
                : ParseFixture("publication-receipt.json"),
            HostReviewReceipt: MutatedFixture("review-receipt.json", node => node["reviewerFamily"] = "github-hosted"));
    }

    private static ValidatedContract GateFixture(string policyIdentity, string gateId) =>
        MutatedFixture("gate-receipt.json", node =>
        {
            node["policyIdentity"] = policyIdentity;
            node["gateId"] = gateId;
        });

    private static ValidatedContract ParseFixture(string file) => ContractValidator.ParseAndValidate(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Valid", file)));

    private static ValidatedContract MutatedFixture(string file, Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Valid", file)))!.AsObject();
        mutate(node);
        node["identity"] = "sha256:" + new string('0', 64);
        node["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(node.ToJsonString()));
        return ContractValidator.ParseAndValidate(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }
}
