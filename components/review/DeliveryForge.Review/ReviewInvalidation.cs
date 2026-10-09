namespace DeliveryForge.Review;

public enum MaterialChange
{
    None,
    Edit,
    Amend,
    Rebase,
    ConflictResolution,
    Generation,
    DependencyIntegration,
    Plan,
    Policy,
    Artifact
}

public sealed record InvalidationDecision(bool Invalidated, bool Reusable, string Reason);

public static class ReviewInvalidation
{
    public static InvalidationDecision Assess(
        NormalizedReview receipt,
        PatchIdentity current,
        ReviewPolicy policy,
        MaterialChange change)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(policy);
        if (change != MaterialChange.None)
            return new(true, false, $"Material change '{change}' requires fresh review.");
        if (!ReviewNormalizer.SameIdentity(receipt.Identity, current))
            return new(true, false, "Base, head, tree, patch, plan, artifact, Git version, or command identity changed.");
        if (!string.Equals(receipt.PolicyIdentity, policy.PolicyIdentity, StringComparison.Ordinal) ||
            !string.Equals(receipt.PolicyContentIdentity, policy.ContentIdentity, StringComparison.Ordinal) ||
            !string.Equals(receipt.PolicySourceRevision, policy.SourceRevision, StringComparison.Ordinal) ||
            receipt.PolicyAuthorityKind != policy.AuthorityKind ||
            receipt.Fixture != policy.Fixture ||
            receipt.Advisory != policy.Advisory)
            return new(true, false, "Review policy provenance changed.");
        if (!receipt.OriginalExecution)
            return new(true, false, "Original review execution provenance is unavailable.");
        if (!policy.Trusted || !policy.AllowExactReuse)
            return new(false, false, "Trusted policy does not permit exact receipt reuse.");
        if (receipt.Outcome != ReviewOutcome.Pass)
            return new(false, false, "Only a passing receipt is eligible for unchanged reuse.");
        return new(false, true, "All exact identities and original provenance are unchanged.");
    }
}
