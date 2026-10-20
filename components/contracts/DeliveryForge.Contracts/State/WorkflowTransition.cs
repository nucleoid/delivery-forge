using System.Text.Json;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Contracts.State;

public sealed record TransitionEvidence(
    AuthorizationCeiling Ceiling,
    string? BaseCommit = null,
    string? HeadCommit = null,
    string? TreeId = null,
    ValidatedContract? Policy = null,
    IReadOnlyList<ValidatedContract>? GateReceipts = null,
    ValidatedContract? CiReceipt = null,
    ValidatedContract? ReviewReceipt = null,
    ValidatedContract? PublicationReceipt = null,
    ValidatedContract? HostReviewReceipt = null,
    ReviewAcceptanceAuthority? ReviewAuthority = null,
    ReviewConsumerContext? ReviewContext = null,
    ReviewConsumerContext? HostReviewContext = null);

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

    public static void EnsureAllowed(WorkflowState from, WorkflowState to, TransitionEvidence evidence) =>
        EnsureAllowedCore(from, to, evidence, ReviewAuthorityRealm.Production, receiver: null);

    public static void EnsureAllowed(
        WorkflowState from,
        WorkflowState to,
        TransitionEvidence evidence,
        ReviewTransitionReceiver receiver) =>
        EnsureAllowedCore(from, to, evidence, ReviewAuthorityRealm.Production, receiver);

    internal static void EnsureAllowedForIsolatedTest(
        WorkflowState from,
        WorkflowState to,
        TransitionEvidence evidence)
    {
        // Compatibility for pre-existing isolated contract tests only. The public legacy
        // entry point above never derives a receiver from per-call evidence.
        var receiver = evidence.ReviewAuthority is not null && evidence.ReviewContext is not null
            ? new ReviewTransitionReceiver(
                evidence.ReviewAuthority,
                ReviewAuthorityRealm.Test,
                evidence.ReviewContext,
                evidence.HostReviewContext)
            : null;
        EnsureAllowedCore(from, to, evidence, ReviewAuthorityRealm.Test, receiver);
    }

    internal static void EnsureAllowedForIsolatedTest(
        WorkflowState from,
        WorkflowState to,
        TransitionEvidence evidence,
        ReviewTransitionReceiver receiver) =>
        EnsureAllowedCore(from, to, evidence, ReviewAuthorityRealm.Test, receiver);

    private static void EnsureAllowedCore(
        WorkflowState from,
        WorkflowState to,
        TransitionEvidence evidence,
        ReviewAuthorityRealm requiredReviewRealm,
        ReviewTransitionReceiver? receiver)
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
            RequireRequiredGates(evidence);
        }

        if (to == WorkflowState.PrAuthorized)
        {
            RequirePolicyCeiling(evidence, AuthorizationCeiling.Pr, "PR authorization");
            RequireIndependentReview(evidence, requiredReviewRealm, receiver);
        }

        if (to == WorkflowState.PrPublished)
        {
            RequirePolicyCeiling(evidence, AuthorizationCeiling.Pr, "PR publication");
            RequireIndependentReview(evidence, requiredReviewRealm, receiver);
            RequireReceipt(evidence.PublicationReceipt, "publication-receipt", outcome: null, "PR_PUBLISHED", evidence);
        }

        if (to == WorkflowState.CiComplete)
        {
            RequirePolicyCeiling(evidence, AuthorizationCeiling.Pr, "CI completion");
            using var _ = RequirePolicyBoundGate(evidence.CiReceipt, "ci", evidence);
        }

        if (to == WorkflowState.HostReviewComplete)
        {
            RequirePolicyCeiling(evidence, AuthorizationCeiling.Pr, "Hosted review completion");
            RequireDistinctHostReview(evidence, requiredReviewRealm, receiver);
        }

        if (to == WorkflowState.MergeAuthorized)
        {
            RequirePolicyCeiling(evidence, AuthorizationCeiling.Merge, "Merge authorization");
            RequireDistinctHostReview(evidence, requiredReviewRealm, receiver);
        }

        if (to == WorkflowState.Merged)
        {
            RequirePolicyCeiling(evidence, AuthorizationCeiling.Merge, "Merge");
            RequireDistinctHostReview(evidence, requiredReviewRealm, receiver);
            RequireReceipt(evidence.PublicationReceipt, "publication-receipt", outcome: null, "MERGED", evidence);
        }
    }

    private static void RequirePolicyCeiling(TransitionEvidence evidence, AuthorizationCeiling minimum, string operation)
    {
        if (evidence.Ceiling < minimum)
        {
            throw new InvalidWorkflowTransitionException($"{operation} requires {minimum.ToString().ToUpperInvariant()} authority.");
        }

        var policy = RequirePolicy(evidence);
        using var document = JsonDocument.Parse(policy.CanonicalBytes);
        var policyCeiling = document.RootElement.GetProperty("authorizedCeiling").GetString() switch
        {
            "plan" => AuthorizationCeiling.Plan,
            "implement" => AuthorizationCeiling.Implement,
            "pr" => AuthorizationCeiling.Pr,
            "merge" => AuthorizationCeiling.Merge,
            _ => throw new InvalidWorkflowTransitionException("Evidence policy contains an unknown authorization ceiling.")
        };
        if (policyCeiling < minimum || evidence.Ceiling > policyCeiling)
        {
            throw new InvalidWorkflowTransitionException($"{operation} exceeds the immutable evidence-policy ceiling.");
        }

        if (minimum >= AuthorizationCeiling.Pr && !document.RootElement.GetProperty("requireIndependentReview").GetBoolean())
        {
            throw new InvalidWorkflowTransitionException($"{operation} requires a policy that mandates independent review.");
        }
    }

    private static void RequireDistinctHostReview(
        TransitionEvidence evidence,
        ReviewAuthorityRealm requiredReviewRealm,
        ReviewTransitionReceiver? receiver)
    {
        RequireIndependentReview(evidence, requiredReviewRealm, receiver);
        RequireReceipt(evidence.HostReviewReceipt, "review-receipt", "PASS", action: null, evidence);
        if (evidence.HostReviewReceipt is not null &&
            string.Equals(evidence.ReviewReceipt!.Identity, evidence.HostReviewReceipt.Identity, StringComparison.Ordinal))
        {
            throw new InvalidWorkflowTransitionException("Hosted review requires a receipt distinct from independent review.");
        }

        using var hosted = JsonDocument.Parse(evidence.HostReviewReceipt!.CanonicalBytes);
        if (hosted.RootElement.GetProperty("reviewerFamily").GetString() != "github-hosted")
        {
            throw new InvalidWorkflowTransitionException("Hosted review requires reviewerFamily 'github-hosted'.");
        }
        RequireReviewAcceptance(
            evidence, evidence.HostReviewReceipt!, ReviewEvidenceRole.Hosted, requiredReviewRealm, receiver);
    }

    private static void RequireIndependentReview(
        TransitionEvidence evidence,
        ReviewAuthorityRealm requiredReviewRealm,
        ReviewTransitionReceiver? receiver)
    {
        RequireReceipt(evidence.ReviewReceipt, "review-receipt", "PASS", action: null, evidence);
        using var review = JsonDocument.Parse(evidence.ReviewReceipt!.CanonicalBytes);
        if (review.RootElement.GetProperty("reviewerFamily").GetString() == "github-hosted")
        {
            throw new InvalidWorkflowTransitionException("The independent review cannot use reviewerFamily 'github-hosted'.");
        }
        RequireReviewAcceptance(
            evidence, evidence.ReviewReceipt!, ReviewEvidenceRole.Independent, requiredReviewRealm, receiver);
    }

    private static void RequireReviewAcceptance(
        TransitionEvidence evidence,
        ValidatedContract receipt,
        ReviewEvidenceRole role,
        ReviewAuthorityRealm requiredReviewRealm,
        ReviewTransitionReceiver? receiver)
    {
        var consumerContext = role == ReviewEvidenceRole.Hosted
            ? receiver?.HostedContext
            : receiver?.IndependentContext;
        var requirement = new ReviewAcceptanceRequirement(
            receipt.Identity,
            role,
            requiredReviewRealm,
            evidence.BaseCommit!,
            evidence.HeadCommit!,
            evidence.TreeId!,
            RequirePolicy(evidence).Identity,
            consumerContext!,
            role == ReviewEvidenceRole.Hosted ? evidence.ReviewReceipt?.Identity : null);
        if (receiver is null || receiver.Realm != requiredReviewRealm || consumerContext is null ||
            !receiver.Authority.Accepts(requirement))
        {
            throw new InvalidWorkflowTransitionException(
                "Transition requires root-issued review acceptance for the exact receipt and authority realm.");
        }
    }

    private static void RequireRequiredGates(TransitionEvidence evidence)
    {
        var policy = RequirePolicy(evidence);
        using var policyDocument = JsonDocument.Parse(policy.CanonicalBytes);
        var required = policyDocument.RootElement.GetProperty("requiredGates").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gate in evidence.GateReceipts ?? [])
        {
            using var gateDocument = RequirePolicyBoundGate(gate, expectedGateId: null, evidence);
            supplied.Add(gateDocument.RootElement.GetProperty("gateId").GetString()!);
        }

        if (!required.IsSubsetOf(supplied))
        {
            throw new InvalidWorkflowTransitionException("Independent review requires every evidence-policy gate to have a matching policy-satisfying authoritative receipt.");
        }
    }

    private static JsonDocument RequirePolicyBoundGate(ValidatedContract? gate, string? expectedGateId, TransitionEvidence evidence)
    {
        RequireReceipt(gate, "gate-receipt", outcome: null, action: null, evidence);
        var policy = RequirePolicy(evidence);
        var document = JsonDocument.Parse(gate!.CanonicalBytes);
        var root = document.RootElement;
        using var policyDocument = JsonDocument.Parse(policy.CanonicalBytes);
        var gateId = root.GetProperty("gateId").GetString()!;
        var outcome = root.GetProperty("outcome").GetString();
        var allowedNotApplicable = policyDocument.RootElement.GetProperty("allowedNotApplicable").EnumerateArray()
            .Any(item => item.GetString() == gateId);
        if (root.GetProperty("policyIdentity").GetString() != policy.Identity ||
            (expectedGateId is not null && gateId != expectedGateId) ||
            outcome != "PASS" && (outcome != "NOT_APPLICABLE" || !allowedNotApplicable))
        {
            document.Dispose();
            throw new InvalidWorkflowTransitionException("Gate receipt does not match the required policy, gate identity, or allowed outcome.");
        }
        if (!GateReceiptAuthority.TryValidateAuthoritativeOutcome(root, out var authorityFailure))
        {
            document.Dispose();
            throw new InvalidWorkflowTransitionException(authorityFailure);
        }

        return document;
    }

    private static ValidatedContract RequirePolicy(TransitionEvidence evidence)
    {
        if (evidence.Policy is null || evidence.Policy.SchemaName != "evidence-policy")
        {
            throw new InvalidWorkflowTransitionException("Receipt-gated authority requires a validated evidence-policy.");
        }

        return evidence.Policy;
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

        if (string.IsNullOrWhiteSpace(evidence.BaseCommit) || string.IsNullOrWhiteSpace(evidence.HeadCommit) || string.IsNullOrWhiteSpace(evidence.TreeId))
        {
            throw new InvalidWorkflowTransitionException("Receipt-gated transitions require the current head commit and tree identity.");
        }

        using var document = JsonDocument.Parse(receipt.CanonicalBytes);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("baseCommit").GetString(), evidence.BaseCommit, StringComparison.Ordinal) ||
            !string.Equals(root.GetProperty("headCommit").GetString(), evidence.HeadCommit, StringComparison.Ordinal) ||
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
