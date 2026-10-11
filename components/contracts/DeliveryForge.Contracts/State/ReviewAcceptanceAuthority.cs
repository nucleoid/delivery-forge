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
    string TreeId,
    string EvidencePolicyIdentity,
    ReviewConsumerContext ConsumerContext,
    string? OtherReceiptIdentity = null);

/// <summary>
/// Opaque, root-issued context for a review-authorizing workflow consumer.  Public
/// receipt data cannot construct one.
/// </summary>
public sealed class ReviewConsumerContext
{
    internal ReviewConsumerContext(
        object issuer,
        string runIdentity,
        string requestIdentity,
        string planIdentity,
        string evidencePolicyIdentity,
        string reviewPolicyIdentity,
        string protectedSourceIdentity,
        string protectedObjectViewIdentity)
    {
        Issuer = issuer;
        RunIdentity = runIdentity;
        RequestIdentity = requestIdentity;
        PlanIdentity = planIdentity;
        EvidencePolicyIdentity = evidencePolicyIdentity;
        ReviewPolicyIdentity = reviewPolicyIdentity;
        ProtectedSourceIdentity = protectedSourceIdentity;
        ProtectedObjectViewIdentity = protectedObjectViewIdentity;
    }

    internal object Issuer { get; }
    internal string RunIdentity { get; }
    internal string RequestIdentity { get; }
    internal string PlanIdentity { get; }
    internal string EvidencePolicyIdentity { get; }
    internal string ReviewPolicyIdentity { get; }
    internal string ProtectedSourceIdentity { get; }
    internal string ProtectedObjectViewIdentity { get; }
}

public sealed class ReviewAcceptanceAuthority
{
    private readonly Func<ReviewAcceptanceRequirement, bool> _lookup;

    internal ReviewAcceptanceAuthority(Func<ReviewAcceptanceRequirement, bool> lookup)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
    }

    internal bool Accepts(ReviewAcceptanceRequirement requirement) => _lookup(requirement);
}

/// <summary>
/// Root-constructed authority and expected consumer bindings for one scheduled
/// workflow receiver. Review evidence is evaluated against this pinned receiver;
/// callers cannot replace it by supplying a different evidence envelope.
/// </summary>
public sealed class ReviewTransitionReceiver
{
    internal ReviewTransitionReceiver(
        ReviewAcceptanceAuthority authority,
        ReviewAuthorityRealm realm,
        ReviewConsumerContext independentContext,
        ReviewConsumerContext? hostedContext)
    {
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        Realm = realm;
        IndependentContext = independentContext ?? throw new ArgumentNullException(nameof(independentContext));
        HostedContext = hostedContext;
    }

    internal ReviewAcceptanceAuthority Authority { get; }
    internal ReviewAuthorityRealm Realm { get; }
    internal ReviewConsumerContext IndependentContext { get; }
    internal ReviewConsumerContext? HostedContext { get; }
}
