using System.Collections.Concurrent;
using DeliveryForge.Contracts.State;

namespace DeliveryForge.Execution.Host;

public sealed class ReviewAuthorityHost
{
    private readonly ReviewAuthorityRealm _realm;
    private readonly ConcurrentDictionary<(string ReceiptIdentity, ReviewEvidenceRole Role), byte> _acceptances = new();

    private ReviewAuthorityHost(ReviewAuthorityRealm realm)
    {
        _realm = realm;
        Authority = new ReviewAcceptanceAuthority(Accepts);
    }

    public ReviewAcceptanceAuthority Authority { get; }

    public static ReviewAuthorityHost CreateIsolatedTest() => new(ReviewAuthorityRealm.Test);

    public void IssueTestAcceptance(string receiptIdentity, ReviewEvidenceRole role)
    {
        if (_realm != ReviewAuthorityRealm.Test)
            throw new InvalidOperationException("Test acceptance can only be issued by an isolated test authority.");
        if (string.IsNullOrWhiteSpace(receiptIdentity) || !Enum.IsDefined(role))
            throw new ArgumentException("A valid receipt identity and review role are required.");
        _acceptances.TryAdd((receiptIdentity, role), 0);
    }

    private bool Accepts(ReviewAcceptanceRequirement requirement) =>
        requirement.RequiredRealm == _realm &&
        !string.IsNullOrWhiteSpace(requirement.BaseCommit) &&
        !string.IsNullOrWhiteSpace(requirement.HeadCommit) &&
        !string.IsNullOrWhiteSpace(requirement.TreeId) &&
        _acceptances.ContainsKey((requirement.ReceiptIdentity, requirement.Role));
}
