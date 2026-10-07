using DeliveryForge.Contracts.Models;

namespace DeliveryForge.Contracts.State;

public sealed record TransitionEvidence(
    AuthorizationCeiling Ceiling,
    string? GateReceiptId = null,
    string? ReviewReceiptId = null,
    string? PublicationReceiptId = null,
    string? HostReviewReceiptId = null);

public static class WorkflowTransition
{
    private static readonly IReadOnlySet<(WorkflowState From, WorkflowState To)> LinearTransitions =
        new HashSet<(WorkflowState, WorkflowState)>
        {
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
        };

    private static readonly IReadOnlySet<WorkflowState> TerminalStates =
        new HashSet<WorkflowState> { WorkflowState.Merged, WorkflowState.Blocked, WorkflowState.Failed, WorkflowState.Stopped };

    public static void EnsureAllowed(WorkflowState from, WorkflowState to, TransitionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var terminalTransition = !TerminalStates.Contains(from) &&
                                 to is WorkflowState.Blocked or WorkflowState.Failed or WorkflowState.Stopped;
        if (!LinearTransitions.Contains((from, to)) && !terminalTransition)
        {
            throw new InvalidWorkflowTransitionException($"Transition {from} -> {to} is not allowed.");
        }

        if (to is WorkflowState.Executing or WorkflowState.LocalComplete && evidence.Ceiling < AuthorizationCeiling.Implement)
        {
            throw new InvalidWorkflowTransitionException($"Transition to {to} requires IMPLEMENT authority.");
        }

        if (to == WorkflowState.IndependentReview && string.IsNullOrWhiteSpace(evidence.GateReceiptId))
        {
            throw new InvalidWorkflowTransitionException("Independent review requires a gate receipt.");
        }

        if (to == WorkflowState.PrAuthorized &&
            (evidence.Ceiling < AuthorizationCeiling.Pr || string.IsNullOrWhiteSpace(evidence.ReviewReceiptId)))
        {
            throw new InvalidWorkflowTransitionException("PR authorization requires PR authority and an independent review receipt.");
        }

        if (to == WorkflowState.PrPublished &&
            (evidence.Ceiling < AuthorizationCeiling.Pr || string.IsNullOrWhiteSpace(evidence.PublicationReceiptId)))
        {
            throw new InvalidWorkflowTransitionException("PR publication requires PR authority and a publication receipt.");
        }

        if (to == WorkflowState.CiComplete && string.IsNullOrWhiteSpace(evidence.GateReceiptId))
        {
            throw new InvalidWorkflowTransitionException("CI completion requires a gate receipt.");
        }

        if (to == WorkflowState.HostReviewComplete && string.IsNullOrWhiteSpace(evidence.HostReviewReceiptId))
        {
            throw new InvalidWorkflowTransitionException("Host review completion requires its distinct receipt.");
        }

        if (to == WorkflowState.MergeAuthorized &&
            (evidence.Ceiling < AuthorizationCeiling.Merge || string.IsNullOrWhiteSpace(evidence.HostReviewReceiptId)))
        {
            throw new InvalidWorkflowTransitionException("Merge authorization requires MERGE authority and hosted-review evidence.");
        }

        if (to == WorkflowState.Merged &&
            (evidence.Ceiling < AuthorizationCeiling.Merge || string.IsNullOrWhiteSpace(evidence.PublicationReceiptId)))
        {
            throw new InvalidWorkflowTransitionException("Merge requires MERGE authority and a publication receipt.");
        }
    }
}

public sealed class InvalidWorkflowTransitionException(string message) : Exception(message);
