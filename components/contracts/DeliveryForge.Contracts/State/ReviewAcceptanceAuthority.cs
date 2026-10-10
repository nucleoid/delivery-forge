namespace DeliveryForge.Contracts.State;

public enum ReviewAuthorityRealm
{
    Test,
    Production
}

public enum ReviewEvidenceRole
{
    Independent,
    Hosted
}

public sealed record ReviewAcceptanceRequirement(
    string ReceiptIdentity,
    ReviewEvidenceRole Role,
    ReviewAuthorityRealm RequiredRealm,
    string BaseCommit,
    string HeadCommit,
    string TreeId);

public sealed class ReviewAcceptanceAuthority
{
    private readonly Func<ReviewAcceptanceRequirement, bool> _lookup;

    internal ReviewAcceptanceAuthority(Func<ReviewAcceptanceRequirement, bool> lookup)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
    }

    internal bool Accepts(ReviewAcceptanceRequirement requirement) => _lookup(requirement);
}
