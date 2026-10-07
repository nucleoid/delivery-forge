using System.Text.Json;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Contracts.State;

public sealed record TransitionEvidence(
    AuthorizationCeiling Ceiling,
    string? HeadCommit = null,
    string? TreeId = null,
    ValidatedContract? GateReceipt = null,
    ValidatedContract? ReviewReceipt = null,
    ValidatedContract? PublicationReceipt = null,
    ValidatedContract? HostReviewReceipt = null);

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
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to) || !Enum.IsDefined(evidence.Ceiling))
        {
            throw new InvalidWorkflowTransitionException("Workflow states and authorization ceiling must be defined enum values.");
        }

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

        if (to == WorkflowState.IndependentReview)
        {
            RequireReceipt(evidence.GateReceipt, "gate-receipt", "PASS", action: null, evidence);
        }

        if (to == WorkflowState.PrAuthorized)
        {
            RequireCeiling(evidence, AuthorizationCeiling.Pr, "PR authorization");
            RequireReceipt(evidence.ReviewReceipt, "review-receipt", "PASS", action: null, evidence);
        }

        if (to == WorkflowState.PrPublished)
        {
            RequireCeiling(evidence, AuthorizationCeiling.Pr, "PR publication");
            RequireReceipt(evidence.ReviewReceipt, "review-receipt", "PASS", action: null, evidence);
            RequireReceipt(evidence.PublicationReceipt, "publication-receipt", outcome: null, "PR_PUBLISHED", evidence);
        }

        if (to == WorkflowState.CiComplete)
        {
            RequireReceipt(evidence.GateReceipt, "gate-receipt", "PASS", action: null, evidence);
        }

        if (to == WorkflowState.HostReviewComplete)
        {
            RequireDistinctHostReview(evidence);
        }

        if (to == WorkflowState.MergeAuthorized)
        {
            RequireCeiling(evidence, AuthorizationCeiling.Merge, "Merge authorization");
            RequireDistinctHostReview(evidence);
        }

        if (to == WorkflowState.Merged)
        {
            RequireCeiling(evidence, AuthorizationCeiling.Merge, "Merge");
            RequireDistinctHostReview(evidence);
            RequireReceipt(evidence.PublicationReceipt, "publication-receipt", outcome: null, "MERGED", evidence);
        }
    }

    private static void RequireCeiling(TransitionEvidence evidence, AuthorizationCeiling minimum, string operation)
    {
        if (evidence.Ceiling < minimum)
        {
            throw new InvalidWorkflowTransitionException($"{operation} requires {minimum.ToString().ToUpperInvariant()} authority.");
        }
    }

    private static void RequireDistinctHostReview(TransitionEvidence evidence)
    {
        RequireReceipt(evidence.HostReviewReceipt, "review-receipt", "PASS", action: null, evidence);
        if (evidence.ReviewReceipt is not null && evidence.HostReviewReceipt is not null &&
            string.Equals(evidence.ReviewReceipt.Identity, evidence.HostReviewReceipt.Identity, StringComparison.Ordinal))
        {
            throw new InvalidWorkflowTransitionException("Hosted review requires a receipt distinct from independent review.");
        }
    }

    private static void RequireReceipt(
        ValidatedContract? receipt,
        string expectedKind,
        string? outcome,
        string? action,
        TransitionEvidence evidence)
    {
        if (receipt is null || !string.Equals(receipt.SchemaName, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidWorkflowTransitionException($"Transition requires a validated {expectedKind}.");
        }

        if (string.IsNullOrWhiteSpace(evidence.HeadCommit) || string.IsNullOrWhiteSpace(evidence.TreeId))
        {
            throw new InvalidWorkflowTransitionException("Receipt-gated transitions require the current head commit and tree identity.");
        }

        using var document = JsonDocument.Parse(receipt.CanonicalBytes);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("headCommit").GetString(), evidence.HeadCommit, StringComparison.Ordinal) ||
            !string.Equals(root.GetProperty("treeId").GetString(), evidence.TreeId, StringComparison.Ordinal))
        {
            throw new InvalidWorkflowTransitionException("Receipt does not match the current head commit and tree identity.");
        }

        if (outcome is not null &&
            (!root.TryGetProperty("outcome", out var actualOutcome) ||
             !string.Equals(actualOutcome.GetString(), outcome, StringComparison.Ordinal)))
        {
            throw new InvalidWorkflowTransitionException($"{expectedKind} must record outcome {outcome}.");
        }

        if (action is not null &&
            (!root.TryGetProperty("action", out var actualAction) ||
             !string.Equals(actualAction.GetString(), action, StringComparison.Ordinal)))
        {
            throw new InvalidWorkflowTransitionException($"{expectedKind} must record action {action}.");
        }
    }
}

public sealed class InvalidWorkflowTransitionException(string message) : Exception(message);
