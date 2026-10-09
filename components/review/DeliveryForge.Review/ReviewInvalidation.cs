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
        if (change != MaterialChange.None)
            return new(true, false, $"Material change '{change}' requires fresh review.");
        if (!policy.Trusted || !policy.AllowExactReuse)
            return new(false, false, "Trusted policy does not permit exact receipt reuse.");
        if (!string.Equals(receipt.PolicyIdentity, policy.PolicyIdentity, StringComparison.Ordinal))
            return new(true, false, "Review policy identity changed.");
        if (!receipt.OriginalExecution)
            return new(true, false, "Original review execution provenance is unavailable.");
        if (!ReviewNormalizer.SameIdentity(receipt.Identity, current))
            return new(true, false, "Base, head, tree, patch, plan, or artifact identity changed.");
        if (receipt.Outcome != ReviewOutcome.Pass)
            return new(false, false, "Only a passing receipt is eligible for unchanged reuse.");
        return new(false, true, "All exact identities and original provenance are unchanged.");
    }
}
