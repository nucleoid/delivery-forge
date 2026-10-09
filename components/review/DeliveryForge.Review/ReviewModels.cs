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

public sealed record NormalizedReview(
    ReviewOutcome Outcome,
    string Reason,
    PatchIdentity Identity,
    string PolicyIdentity,
    string AuthorFamily,
    string ReviewerFamily,
    IReadOnlyList<ReviewFinding> Findings,
    string ArtifactHash,
    DateTimeOffset CompletedAt,
    bool Fixture,
    bool Advisory,
    bool ProductionAccepted,
    bool OriginalExecution);
