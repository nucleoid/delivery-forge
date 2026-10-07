using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;

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
        var evidence = FullEvidence();

        foreach (var from in Enum.GetValues<WorkflowState>())
        foreach (var to in Enum.GetValues<WorkflowState>())
        {
            var expected = ExpectedLinear.Contains((from, to)) || (!terminalSources.Contains(from) && terminalTargets.Contains(to));
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

    [Theory]
    [InlineData(WorkflowState.IndependentReview, WorkflowState.PrAuthorized)]
    [InlineData(WorkflowState.PrAuthorized, WorkflowState.PrPublished)]
    [InlineData(WorkflowState.HostReviewComplete, WorkflowState.MergeAuthorized)]
    [InlineData(WorkflowState.MergeAuthorized, WorkflowState.Merged)]
    public void Authorization_ceiling_cannot_be_escalated(WorkflowState from, WorkflowState to) =>
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            from, to, FullEvidence() with { Ceiling = AuthorizationCeiling.Implement }));

    [Fact]
    public void Checkpoint_sequence_must_strictly_increase()
    {
        CheckpointSequence.EnsureIncreasing(4, 5);
        Assert.Throws<CheckpointSequenceException>(() => CheckpointSequence.EnsureIncreasing(4, 4));
        Assert.Throws<CheckpointSequenceException>(() => CheckpointSequence.EnsureIncreasing(4, 3));
    }

    private static TransitionEvidence FullEvidence() => new(
        AuthorizationCeiling.Merge,
        GateReceiptId: "gate",
        ReviewReceiptId: "review",
        PublicationReceiptId: "publication",
        HostReviewReceiptId: "host-review");
}
