using System.Text.RegularExpressions;

namespace DeliveryForge.Evidence;

public enum PolicyAuthorityKind
{
    ProtectedGitBase,
    ParentAdmin,
    WorkerBranch,
    CallerPath
}

public sealed record PolicyAuthority(
    PolicyAuthorityKind Kind,
    string SourceRevision,
    string ContentIdentity);

public sealed record PolicyCandidate(
    string PolicyIdentity,
    string ContentIdentity,
    string SourceRevision,
    bool Fixture,
    decimal MaximumChangedCodeDebtIncrease,
    IReadOnlyList<string> RequiredGates);

public sealed class ResolvedEvidencePolicy
{
    internal ResolvedEvidencePolicy(
        string policyIdentity,
        string contentIdentity,
        string sourceRevision,
        PolicyAuthorityKind authorityKind,
        decimal maximumChangedCodeDebtIncrease,
        IReadOnlyList<string> requiredGates,
        bool authorityVerified,
        string? sourceRepositoryRoot = null)
    {
        PolicyIdentity = policyIdentity;
        ContentIdentity = contentIdentity;
        SourceRevision = sourceRevision;
        AuthorityKind = authorityKind;
        MaximumChangedCodeDebtIncrease = maximumChangedCodeDebtIncrease;
        RequiredGates = requiredGates;
        AuthorityVerified = authorityVerified;
        SourceRepositoryRoot = sourceRepositoryRoot;
    }

    internal string? SourceRepositoryRoot { get; }

    public string PolicyIdentity { get; }
    public string ContentIdentity { get; }
    public string SourceRevision { get; }
    public PolicyAuthorityKind AuthorityKind { get; }
    public decimal MaximumChangedCodeDebtIncrease { get; }
    public IReadOnlyList<string> RequiredGates { get; }
    internal bool AuthorityVerified { get; }
}

public static partial class EvidencePolicy
{
    public static ResolvedEvidencePolicy Resolve(PolicyCandidate candidate, PolicyAuthority authority)
    {
        if (candidate.Fixture)
            throw new EvidencePolicyException("Fixture policy cannot grant production authority.");
        if (authority.Kind is not (PolicyAuthorityKind.ProtectedGitBase or PolicyAuthorityKind.ParentAdmin))
            throw new EvidencePolicyException("Policy authority is not a protected Git base or parent-admin source.");
        if (!string.Equals(candidate.SourceRevision, authority.SourceRevision, StringComparison.Ordinal))
            throw new EvidencePolicyException("Policy source revision does not match its trusted authority.");
        if (!string.Equals(candidate.ContentIdentity, authority.ContentIdentity, StringComparison.Ordinal))
            throw new EvidencePolicyException("Policy content identity does not match its trusted authority.");
        if (!IdentityPattern().IsMatch(candidate.PolicyIdentity) ||
            !IdentityPattern().IsMatch(candidate.ContentIdentity) ||
            !GitObjectPattern().IsMatch(candidate.SourceRevision))
            throw new EvidencePolicyException("Policy identities are malformed.");
        if (candidate.MaximumChangedCodeDebtIncrease < 0)
            throw new EvidencePolicyException("Changed-code debt allowance cannot be negative.");
        if (candidate.RequiredGates.Count == 0 || candidate.RequiredGates.Any(string.IsNullOrWhiteSpace))
            throw new EvidencePolicyException("A production policy must identify its required gates.");

        return new ResolvedEvidencePolicy(
            candidate.PolicyIdentity,
            candidate.ContentIdentity,
            candidate.SourceRevision,
            authority.Kind,
            candidate.MaximumChangedCodeDebtIncrease,
            candidate.RequiredGates.ToArray(),
            authorityVerified: false);
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    [GeneratedRegex("^[0-9a-f]{40}(?:[0-9a-f]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitObjectPattern();
}

public sealed class EvidencePolicyException(string message) : Exception(message);
