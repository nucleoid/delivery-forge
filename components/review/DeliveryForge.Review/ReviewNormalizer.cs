namespace DeliveryForge.Review;

public static class ReviewNormalizer
{
    public static NormalizedReview Normalize(
        ReviewRequest request,
        ReviewResult result,
        ReviewPolicy policy,
        IssuedReviewerCapability? capabilityAuthority = null,
        AcceptedReviewExecution? execution = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(policy);

        var findings = result.Findings?.ToArray() ?? [];
        var capabilityAuthorityValid = capabilityAuthority is not null &&
                                       request.Capability.Scopes is not null &&
                                       capabilityAuthority.Matches(request.Capability, request.Scope, result.CompletedAt);
        var executionAuthorityValid = execution is not null &&
                                      capabilityAuthority is not null &&
                                      string.Equals(execution.RootIdentity, capabilityAuthority.RootIdentity, StringComparison.Ordinal) &&
                                      execution.Realm == capabilityAuthority.Realm &&
                                      execution.Accepted && execution.Completed &&
                                      string.Equals(execution.RawArtifactHash, result.ArtifactHash, StringComparison.Ordinal) &&
                                      execution.CompletedAt == result.CompletedAt &&
                                      VerdictMatches(execution.RawVerdict, result.Verdict);
        ReviewOutcome outcome;
        string reason;

        if (!policy.Trusted || string.IsNullOrEmpty(policy.PolicyIdentity))
            (outcome, reason) = (ReviewOutcome.Incomplete, "Trusted review policy is missing.");
        else if (!string.Equals(request.PolicyIdentity, policy.PolicyIdentity, StringComparison.Ordinal))
            (outcome, reason) = (ReviewOutcome.Incomplete, "Review policy identity does not match.");
        else if (ReviewPolicy.Unknown(request.AuthorFamily) ||
                 ReviewPolicy.Unknown(request.Capability.Family) ||
                 ReviewPolicy.Unknown(result.ReviewerFamily) ||
                 ReviewPolicy.Same(request.AuthorFamily, request.Capability.Family))
            (outcome, reason) = (ReviewOutcome.Incomplete, "Review family is unknown, same-family, or self-reviewing.");
        else if (!policy.AllowedFamilies.Any(pair =>
                     ReviewPolicy.Same(pair.Author, request.AuthorFamily) &&
                     ReviewPolicy.Same(pair.Reviewer, request.Capability.Family)))
            (outcome, reason) = (ReviewOutcome.Incomplete, "The family relationship is not authorized by trusted policy.");
        else if (!request.Capability.Supported)
            (outcome, reason) = (ReviewOutcome.Incomplete, "Reviewer adapter capability is unsupported.");
        else if (request.Capability.Scopes is null ||
                 !policy.AllowedScopes.Contains(request.Scope, StringComparer.Ordinal) ||
                 !request.Capability.Scopes.Contains(request.Scope, StringComparer.Ordinal))
            (outcome, reason) = (ReviewOutcome.Incomplete, "Review scope is not authorized and capability-proven.");
        else if (!ReviewPolicy.Same(request.Capability.Family, result.ReviewerFamily))
            (outcome, reason) = (ReviewOutcome.Incomplete, "Result reviewer family does not match capability metadata.");
        else if (!SameIdentity(request.Identity, result.Identity))
            (outcome, reason) = (ReviewOutcome.Incomplete, "Review result does not bind the exact requested patch identities.");
        else if (result.TimedOut)
            (outcome, reason) = (ReviewOutcome.Error, "Reviewer timed out.");
        else if (result.Truncated)
            (outcome, reason) = (ReviewOutcome.Incomplete, "Reviewer output was truncated.");
        else if (result.Findings is null)
            (outcome, reason) = (ReviewOutcome.Incomplete, "Reviewer findings payload is missing.");
        else if (capabilityAuthority is not null && !capabilityAuthorityValid)
            (outcome, reason) = (ReviewOutcome.Incomplete, "Reviewer capability authority is stale, revoked, or does not match the request.");
        else if (execution is not null && !executionAuthorityValid)
            (outcome, reason) = (ReviewOutcome.Incomplete, "Accepted reviewer execution does not match the result and capability authority.");
        else if (policy.Advisory || request.Capability.Advisory || result.Advisory)
            (outcome, reason) = (ReviewOutcome.Incomplete, "Advisory review evidence cannot pass.");
        else
        {
            (outcome, reason) = NormalizeVerdict(result.Verdict);
            if (outcome == ReviewOutcome.Pass && findings.Length != 0)
                (outcome, reason) = (ReviewOutcome.Incomplete, "A PASS verdict cannot carry unresolved findings.");
        }

        var fixture = policy.Fixture || request.Capability.Fixture || result.Fixture;
        var advisory = policy.Advisory || request.Capability.Advisory || result.Advisory;
        var authorityAccepted =
            outcome == ReviewOutcome.Pass && policy.Trusted && !advisory &&
            capabilityAuthorityValid && executionAuthorityValid;
        var productionAccepted = authorityAccepted && !fixture &&
                                 capabilityAuthority!.Realm == DeliveryForge.Contracts.State.ReviewAuthorityRealm.Production;

        return new NormalizedReview(
            outcome,
            reason,
            Freeze(request.Identity),
            policy.PolicyIdentity,
            policy.ContentIdentity,
            policy.SourceRevision,
            policy.AuthorityKind,
            request.AuthorFamily,
            result.ReviewerFamily,
            findings,
            result.ArtifactHash,
            result.CompletedAt,
            fixture,
            advisory,
            productionAccepted,
            originalExecution: executionAuthorityValid,
            authorityAccepted,
            request.Capability.AdapterId,
            request.Capability.AdapterVersion,
            request.Scope,
            capabilityAuthority?.ProofIdentity ?? string.Empty,
            capabilityAuthority?.ValidUntil ?? default,
            execution?.RequestIdentity ?? string.Empty,
            execution?.ExecutionIdentity ?? string.Empty,
            execution?.RawArtifactHash ?? string.Empty,
            capabilityAuthority?.Realm ?? DeliveryForge.Contracts.State.ReviewAuthorityRealm.Test);
    }

    internal static bool SameIdentity(PatchIdentity left, PatchIdentity right) =>
        string.Equals(left.BaseCommit, right.BaseCommit, StringComparison.Ordinal) &&
        string.Equals(left.HeadCommit, right.HeadCommit, StringComparison.Ordinal) &&
        string.Equals(left.HeadTree, right.HeadTree, StringComparison.Ordinal) &&
        string.Equals(left.PatchSha256, right.PatchSha256, StringComparison.Ordinal) &&
        string.Equals(left.PlanIdentity, right.PlanIdentity, StringComparison.Ordinal) &&
        left.ArtifactIdentities.Count == right.ArtifactIdentities.Count &&
        left.ArtifactIdentities.All(pair =>
            right.ArtifactIdentities.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal)) &&
        string.Equals(left.GitVersion, right.GitVersion, StringComparison.Ordinal) &&
        left.Command.SequenceEqual(right.Command, StringComparer.Ordinal);


    private static PatchIdentity Freeze(PatchIdentity identity) => new(
        identity.BaseCommit,
        identity.HeadCommit,
        identity.HeadTree,
        identity.PatchSha256,
        identity.PlanIdentity,
        identity.ArtifactIdentities.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        identity.GitVersion,
        identity.Command.ToArray());
    private static bool VerdictMatches(string rawVerdict, string? normalizedVerdict)
    {
        var mapped = rawVerdict.Trim().ToLowerInvariant() switch
        {
            "ship" => "PASS",
            "needs-attention" => "FAIL",
            "do-not-ship" => "FAIL",
            _ => "ERROR"
        };
        return string.Equals(mapped, normalizedVerdict?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static (ReviewOutcome, string) NormalizeVerdict(string? verdict) =>
        verdict?.Trim().ToUpperInvariant() switch
        {
            "PASS" => (ReviewOutcome.Pass, "Reviewer returned PASS."),
            "FAIL" => (ReviewOutcome.Fail, "Reviewer returned material findings."),
            "INCOMPLETE" => (ReviewOutcome.Incomplete, "Reviewer reported incomplete evidence."),
            "ERROR" => (ReviewOutcome.Error, "Reviewer reported an error."),
            _ => (ReviewOutcome.Error, "Reviewer verdict is unknown.")
        };
}
