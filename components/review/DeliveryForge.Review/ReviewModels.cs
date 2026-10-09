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
    IReadOnlyList<string> Scopes)
{
    internal bool AuthorityVerified { get; init; }
    public bool ProductionCapable =>
        AuthorityVerified && Supported && !Fixture && !Advisory;

    internal ReviewerCapability WithVerifiedAuthority() => this with { AuthorityVerified = true };
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
        bool originalExecution)
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
}
