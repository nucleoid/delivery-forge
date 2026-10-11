namespace DeliveryForge.Publication.GitHost;

public enum GitHostChecks { Passing, Pending, Failing, Unknown }
public enum GitHostProtection { RequiredAndObserved, Missing, Unknown }
public enum GitHostMutationOutcome { Confirmed, Uncertain, Rejected }

public sealed record GitHostObservation(
    PublicationTarget Target,
    string SourceHead,
    string TargetHead,
    string? PrHead,
    GitHostChecks Checks,
    GitHostProtection Protection,
    string? PublishedIdempotencyKey = null,
    string? ResultId = null,
    string? MergedHead = null);

public sealed record GitHostMutation(PublicationIntent Intent, string ExpectedTargetHead);
public sealed record GitHostMutationResult(GitHostMutationOutcome Outcome, string? ResultId);

// No release, deployment, rebase, squash, auto-merge or protection-bypass operation exists.
public interface IGitHostAdapter
{
    PublicationRealm Realm { get; }
    Task<GitHostObservation> ReadAsync(PublicationTarget target, string idempotencyKey, CancellationToken cancellationToken);
    Task<GitHostMutationResult> CreateOrUpdatePrAsync(GitHostMutation mutation, CancellationToken cancellationToken);
    Task<GitHostMutationResult> MergeNormalAsync(GitHostMutation mutation, CancellationToken cancellationToken);
}
