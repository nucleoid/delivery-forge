using System.Text.Json;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Publication.GitHost;
using DeliveryForge.Review;

namespace DeliveryForge.Publication;

public static class PublicationPreflight
{
    public static PublicationPreflightResult Evaluate(PublicationAuthorityHost host, PublicationRun run,
        PublicationRequest request, PublicationLocalObservation local, GitHostObservation remote) =>
        EvaluateCore(host, run, request, local, remote, completedIntent: null);

    internal static PublicationPreflightResult EvaluateCore(PublicationAuthorityHost host, PublicationRun run,
        PublicationRequest request, PublicationLocalObservation local, GitHostObservation remote, PublicationIntent? completedIntent)
    {
        if (!host.Owns(run) || run.Realm != PublicationRealm.Fixture)
            return Denied(PublicationDenial.Authority, "No current opaque parent authority exists for this run.");
        var definition = run.Definition;
        if (!Enum.IsDefined(request.Mode) || request.Mode is PublicationMode.Plan or PublicationMode.Implement ||
            definition.Ceiling < (request.Mode == PublicationMode.Merge ? AuthorizationCeiling.Merge : AuthorizationCeiling.Pr))
            return Denied(PublicationDenial.Ceiling, "Requested action exceeds the immutable run ceiling or forbids publication.");
        if (local.Stopped) return Denied(PublicationDenial.Stopped, "Parent stop observation forbids publication.");
        if (local.RevisionIdentity != definition.RevisionIdentity)
            return Denied(PublicationDenial.Revision, "Observed revision is stale.");
        if (local.Ownership != PublicationOwnership.ReconciledFixture)
            return Denied(PublicationDenial.Ownership, "Ownership is expired, unavailable or uncertain.");
        if (!local.IsClean || !SamePatch(local.Patch, definition.Patch))
            return Denied(PublicationDenial.LocalIdentity, "Clean exact base/head/tree/patch/plan/artifact/command identity is required.");
        if (!GatesAllowed(definition, request.Mode == PublicationMode.Merge))
            return Denied(PublicationDenial.PolicyGate, "Exact protected policy gates or CI are incomplete, mismatched or disallowed.");
        var review = ReviewAllowed(host, run, request.Mode);
        if (!review.Allowed) return review;
        var mergedReadback = completedIntent is not null && completedIntent.Action == PublicationAction.MergeNormal &&
            completedIntent.State != PublicationIntentState.Prepared &&
            remote.PublishedIdempotencyKey == completedIntent.IdempotencyKey && remote.MergedHead == definition.Patch.HeadCommit;
        if (!SameTarget(remote.Target, definition.Target) || remote.SourceHead != definition.Patch.HeadCommit ||
            (!mergedReadback && remote.TargetHead != definition.Patch.BaseCommit) ||
            (remote.PrHead is not null && remote.PrHead != definition.Patch.HeadCommit) ||
            (request.Mode == PublicationMode.Merge && remote.PrHead != definition.Patch.HeadCommit))
            return Denied(PublicationDenial.RemoteIdentity, "Remote repository/ref/PR head has drifted from the frozen exact identities.");
        if (request.Mode == PublicationMode.Merge && remote.Protection != GitHostProtection.RequiredAndObserved)
            return Denied(PublicationDenial.Protection, "Protected normal merge requires positively observed protection.");
        if (request.Mode == PublicationMode.Merge && remote.Checks != GitHostChecks.Passing)
            return Denied(PublicationDenial.Checks, "Every required exact-head check must pass.");
        return new(true, PublicationDenial.None, "Exact fixture preflight accepted; production authority is not claimed.");
    }

    internal static PublicationPreflightResult ReviewAllowed(PublicationAuthorityHost host, PublicationRun run, PublicationMode mode)
    {
        var d = run.Definition;
        var evidence = new TransitionEvidence(d.Ceiling, d.Patch.BaseCommit, d.Patch.HeadCommit, d.Patch.HeadTree,
            Policy: d.Policy, ReviewReceipt: d.ReviewReceipt, HostReviewReceipt: d.HostedReviewReceipt);
        try
        {
            host.ReviewHost.EnsureContractFixtureRootBinding(d.RunIdentity, d.Patch.PlanIdentity, d.Policy.Identity, d.Receiver);
            host.ReviewHost.EnsureContractFixtureTransition(mode == PublicationMode.Merge ? WorkflowState.HostReviewComplete : WorkflowState.IndependentReview,
                mode == PublicationMode.Merge ? WorkflowState.MergeAuthorized : WorkflowState.PrAuthorized, evidence, d.Receiver);
            return new(true, PublicationDenial.None, "Current root-issued review accepted.");
        }
        catch (Exception error) when (error is InvalidWorkflowTransitionException or InvalidOperationException)
        {
            return Denied(PublicationDenial.Review, "Review acceptance is missing, stale, revoked or bound to another receiver/root.");
        }
    }

    private static bool GatesAllowed(PublicationRunDefinition d, bool merge)
    {
        if (d.Policy.SchemaName != "evidence-policy") return false;
        using var policy = JsonDocument.Parse(d.Policy.CanonicalBytes);
        var root = policy.RootElement;
        var ceiling = root.GetProperty("authorizedCeiling").GetString();
        if (!root.GetProperty("requireIndependentReview").GetBoolean() ||
            (merge ? ceiling != "merge" : ceiling is not ("pr" or "merge"))) return false;
        var allowedNa = root.GetProperty("allowedNotApplicable").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in d.GateReceipts)
        {
            if (!GateAllowed(receipt, d, allowedNa, out var gateId) || !seen.Add(gateId)) return false;
        }
        if (!root.GetProperty("requiredGates").EnumerateArray().All(x => seen.Contains(x.GetString()!))) return false;
        return !merge || d.CiReceipt is not null && GateAllowed(d.CiReceipt, d, allowedNa, out var ci) && ci == "ci";
    }

    private static bool GateAllowed(ValidatedContract receipt, PublicationRunDefinition d, HashSet<string?> allowedNa, out string gateId)
    {
        gateId = "";
        if (receipt.SchemaName != "gate-receipt") return false;
        using var document = JsonDocument.Parse(receipt.CanonicalBytes);
        var r = document.RootElement;
        gateId = r.GetProperty("gateId").GetString()!;
        if (r.GetProperty("policyIdentity").GetString() != d.Policy.Identity || r.GetProperty("baseCommit").GetString() != d.Patch.BaseCommit ||
            r.GetProperty("headCommit").GetString() != d.Patch.HeadCommit || r.GetProperty("treeId").GetString() != d.Patch.HeadTree ||
            r.GetProperty("sourceChanged").GetBoolean()) return false;
        var outcome = r.GetProperty("outcome").GetString();
        return outcome == "PASS" || outcome == "NOT_APPLICABLE" && allowedNa.Contains(gateId) &&
            r.TryGetProperty("notApplicableRationale", out var rationale) && !string.IsNullOrWhiteSpace(rationale.GetString());
    }

    internal static bool SamePatch(PatchIdentity a, PatchIdentity b) => a.BaseCommit == b.BaseCommit && a.HeadCommit == b.HeadCommit &&
        a.HeadTree == b.HeadTree && a.PatchSha256 == b.PatchSha256 && a.PlanIdentity == b.PlanIdentity && a.GitVersion == b.GitVersion &&
        a.Command.SequenceEqual(b.Command, StringComparer.Ordinal) && a.ArtifactIdentities.Count == b.ArtifactIdentities.Count &&
        a.ArtifactIdentities.All(pair => b.ArtifactIdentities.TryGetValue(pair.Key, out var value) && value == pair.Value);
    internal static bool SameTarget(PublicationTarget a, PublicationTarget b) =>
        string.Equals(a.Repository, b.Repository, StringComparison.OrdinalIgnoreCase) && a.SourceRef == b.SourceRef && a.TargetRef == b.TargetRef;
    internal static PublicationPreflightResult Denied(PublicationDenial denial, string reason) => new(false, denial, reason);
}
