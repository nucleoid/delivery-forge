using DeliveryForge.Contracts.State;

namespace DeliveryForge.Review;

public enum ReviewOutcome
{
    Pass,
    Fail,
    Incomplete,
    Error
}

public sealed record PatchIdentity(
    string BaseCommit,
    string HeadCommit,
    string HeadTree,
    string PatchSha256,
    string PlanIdentity,
    IReadOnlyDictionary<string, string> ArtifactIdentities,
    string GitVersion,
    IReadOnlyList<string> Command);

public sealed record ReviewerCapability(
    string AdapterId,
    string AdapterVersion,
    string Family,
    bool Supported,
    bool Fixture,
    bool Advisory,
    IReadOnlyList<string> Scopes);

public sealed class IssuedReviewerCapability
{
    internal IssuedReviewerCapability(
        string rootIdentity,
        string proofIdentity,
        ReviewerCapability capability,
        ReviewAuthorityRealm realm,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        Func<bool> isRevoked)
    {
        RootIdentity = rootIdentity;
        ProofIdentity = proofIdentity;
        Capability = capability with { Scopes = capability.Scopes?.ToArray() ?? [] };
        Realm = realm;
        ValidFrom = validFrom;
        ValidUntil = validUntil;
        IsRevoked = isRevoked;
    }

    internal string RootIdentity { get; }
    public string ProofIdentity { get; }
    public ReviewerCapability Capability { get; }
    public ReviewAuthorityRealm Realm { get; }
    public DateTimeOffset ValidFrom { get; }
    public DateTimeOffset ValidUntil { get; }
    internal Func<bool> IsRevoked { get; }

    internal bool Matches(ReviewerCapability capability, string scope, DateTimeOffset completedAt) =>
        !IsRevoked() &&
        completedAt >= ValidFrom && completedAt <= ValidUntil &&
        string.Equals(Capability.AdapterId, capability.AdapterId, StringComparison.Ordinal) &&
        string.Equals(Capability.AdapterVersion, capability.AdapterVersion, StringComparison.Ordinal) &&
        ReviewPolicy.Same(Capability.Family, capability.Family) &&
        Capability.Supported == capability.Supported &&
        Capability.Fixture == capability.Fixture &&
        Capability.Advisory == capability.Advisory &&
        Capability.Scopes.SequenceEqual(capability.Scopes ?? [], StringComparer.Ordinal) &&
        Capability.Scopes.Contains(scope, StringComparer.Ordinal);
}

public sealed class AcceptedReviewExecution
{
    internal AcceptedReviewExecution(
        RegisteredReviewRequest registration,
        IssuedReviewerCapability capability,
        string executionIdentity,
        bool accepted,
        bool completed,
        string rawArtifactHash,
        string rawVerdict,
        DateTimeOffset completedAt,
        ReviewAuthorityRealm realm)
    {
        Registration = registration;
        Capability = capability;
        ExecutionIdentity = executionIdentity;
        Accepted = accepted;
        Completed = completed;
        RawArtifactHash = rawArtifactHash;
        RawVerdict = rawVerdict;
        CompletedAt = completedAt;
        Realm = realm;
    }

    internal RegisteredReviewRequest Registration { get; }
    internal IssuedReviewerCapability Capability { get; }
    internal string RootIdentity => Registration.RootIdentity;
    public string RequestIdentity => Registration.RequestIdentity;
    public string ExecutionIdentity { get; }
    public bool Accepted { get; }
    public bool Completed { get; }
    public string RawArtifactHash { get; }
    public string RawVerdict { get; }
    public DateTimeOffset CompletedAt { get; }
    public ReviewAuthorityRealm Realm { get; }
}

public sealed class RegisteredReviewRequest
{
    internal RegisteredReviewRequest(
        object issuer,
        string rootIdentity,
        string requestIdentity,
        ReviewRequest request,
        string runIdentity,
        string evidencePolicyIdentity,
        string reviewPolicyContentIdentity,
        string protectedSourceIdentity,
        string protectedObjectViewIdentity,
        ReviewEvidenceRole role)
    {
        Issuer = issuer;
        RootIdentity = rootIdentity;
        RequestIdentity = requestIdentity;
        Request = ReviewNormalizer.FreezeRequest(request);
        RunIdentity = runIdentity;
        EvidencePolicyIdentity = evidencePolicyIdentity;
        ReviewPolicyContentIdentity = reviewPolicyContentIdentity;
        ProtectedSourceIdentity = protectedSourceIdentity;
        ProtectedObjectViewIdentity = protectedObjectViewIdentity;
        Role = role;
    }

    internal object Issuer { get; }
    internal string RootIdentity { get; }
    public string RequestIdentity { get; }
    public ReviewRequest Request { get; }
    internal string RunIdentity { get; }
    internal string EvidencePolicyIdentity { get; }
    internal string ReviewPolicyContentIdentity { get; }
    internal string ProtectedSourceIdentity { get; }
    internal string ProtectedObjectViewIdentity { get; }
    internal ReviewEvidenceRole Role { get; }
}

internal sealed record ReviewAuthorityProvenance(
    object Issuer,
    RegisteredReviewRequest Registration,
    IssuedReviewerCapability Capability,
    AcceptedReviewExecution Execution);

public sealed class IssuedReviewReuseContext
{
    internal IssuedReviewReuseContext(
        string rootIdentity,
        ReviewRequest request,
        string capabilityProofIdentity,
        ReviewAuthorityRealm realm,
        Func<bool> acceptanceCurrent,
        Func<bool> capabilityCurrent)
    {
        RootIdentity = rootIdentity;
        Request = request with
        {
            Identity = new PatchIdentity(
                request.Identity.BaseCommit,
                request.Identity.HeadCommit,
                request.Identity.HeadTree,
                request.Identity.PatchSha256,
                request.Identity.PlanIdentity,
                request.Identity.ArtifactIdentities.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                request.Identity.GitVersion,
                request.Identity.Command.ToArray()),
            Capability = request.Capability with { Scopes = request.Capability.Scopes?.ToArray() ?? [] }
        };
        CapabilityProofIdentity = capabilityProofIdentity;
        Realm = realm;
        _acceptanceCurrent = acceptanceCurrent;
        _capabilityCurrent = capabilityCurrent;
    }

    private readonly Func<bool> _acceptanceCurrent;
    private readonly Func<bool> _capabilityCurrent;

    internal string RootIdentity { get; }
    public ReviewRequest Request { get; }
    public string CapabilityProofIdentity { get; }
    public ReviewAuthorityRealm Realm { get; }
    public bool AcceptanceCurrent => _acceptanceCurrent();
    public bool CapabilityCurrent => _capabilityCurrent();
}

public sealed record ReviewRequest(
    PatchIdentity Identity,
    string AuthorFamily,
    ReviewerCapability Capability,
    string Scope,
    string PolicyIdentity);

public sealed record ReviewFinding(string Code, string Severity, string Message, string Location);

public sealed record ReviewResult(
    PatchIdentity Identity,
    string ReviewerFamily,
    string Verdict,
    IReadOnlyList<ReviewFinding>? Findings,
    string ArtifactHash,
    DateTimeOffset CompletedAt,
    bool TimedOut,
    bool Truncated,
    bool Fixture,
    bool Advisory)
{
    public static ReviewResult Complete(
        PatchIdentity identity,
        string reviewerFamily,
        string verdict,
        IReadOnlyList<ReviewFinding> findings,
        string artifactHash,
        DateTimeOffset completedAt) =>
        new(identity, reviewerFamily, verdict, findings.ToArray(), artifactHash, completedAt,
            false, false, true, false);
}

public sealed class NormalizedReview
{
    internal NormalizedReview(
        ReviewOutcome outcome,
        string reason,
        PatchIdentity identity,
        string policyIdentity,
        string policyContentIdentity,
        string policySourceRevision,
        ReviewPolicyAuthorityKind policyAuthorityKind,
        string authorFamily,
        string reviewerFamily,
        IReadOnlyList<ReviewFinding> findings,
        string artifactHash,
        DateTimeOffset completedAt,
        bool fixture,
        bool advisory,
        bool productionAccepted,
        bool originalExecution,
        bool authorityAccepted = false,
        string adapterId = "",
        string adapterVersion = "",
        string scope = "",
        string capabilityProofIdentity = "",
        DateTimeOffset capabilityValidUntil = default,
        string executionRequestIdentity = "",
        string executionIdentity = "",
        string rawArtifactHash = "",
        ReviewAuthorityRealm authorityRealm = ReviewAuthorityRealm.Test,
        ReviewAuthorityProvenance? provenance = null)
    {
        Outcome = outcome;
        Reason = reason;
        Identity = identity;
        PolicyIdentity = policyIdentity;
        PolicyContentIdentity = policyContentIdentity;
        PolicySourceRevision = policySourceRevision;
        PolicyAuthorityKind = policyAuthorityKind;
        AuthorFamily = authorFamily;
        ReviewerFamily = reviewerFamily;
        Findings = findings.ToArray();
        ArtifactHash = artifactHash;
        CompletedAt = completedAt;
        Fixture = fixture;
        Advisory = advisory;
        ProductionAccepted = productionAccepted;
        OriginalExecution = originalExecution;
        AuthorityAccepted = authorityAccepted;
        AdapterId = adapterId;
        AdapterVersion = adapterVersion;
        Scope = scope;
        CapabilityProofIdentity = capabilityProofIdentity;
        CapabilityValidUntil = capabilityValidUntil;
        ExecutionRequestIdentity = executionRequestIdentity;
        ExecutionIdentity = executionIdentity;
        RawArtifactHash = rawArtifactHash;
        AuthorityRealm = authorityRealm;
        Provenance = provenance;
    }

    public ReviewOutcome Outcome { get; }
    public string Reason { get; }
    public PatchIdentity Identity { get; }
    public string PolicyIdentity { get; }
    public string PolicyContentIdentity { get; }
    public string PolicySourceRevision { get; }
    public ReviewPolicyAuthorityKind PolicyAuthorityKind { get; }
    public string AuthorFamily { get; }
    public string ReviewerFamily { get; }
    public IReadOnlyList<ReviewFinding> Findings { get; }
    public string ArtifactHash { get; }
    public DateTimeOffset CompletedAt { get; }
    public bool Fixture { get; }
    public bool Advisory { get; }
    public bool ProductionAccepted { get; }
    public bool OriginalExecution { get; }
    public bool AuthorityAccepted { get; }
    public string AdapterId { get; }
    public string AdapterVersion { get; }
    public string Scope { get; }
    public string CapabilityProofIdentity { get; }
    public DateTimeOffset CapabilityValidUntil { get; }
    public string ExecutionRequestIdentity { get; }
    public string ExecutionIdentity { get; }
    public string RawArtifactHash { get; }
    public ReviewAuthorityRealm AuthorityRealm { get; }
    internal ReviewAuthorityProvenance? Provenance { get; }
}
