namespace DeliveryForge.Publication.GitHost;

/// <summary>Unavailable until a separately authorized disposable live target and host capabilities exist.</summary>
public sealed class GitHubCliAdapter : IGitHostAdapter
{
    public GitHubCliAdapter() { }
    public GitHubCliAdapter(GitHubReadConfiguration configuration, IGitHubCliExecutor executor,
        ParentDisposableGitHubAuthority? parent = null, ParentDisposableGitHubGrant? grant = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(executor);
    }
    public Task<GitHubCliCapability> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new GitHubCliCapability(false, "", "GitHub CLI producer behavior is not implemented at this RED checkpoint."));
    public PublicationRealm Realm => PublicationRealm.Production;

    public Task<GitHostObservation> ReadAsync(PublicationTarget target, string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromException<GitHostObservation>(Unavailable());
    public Task<GitHostMutationResult> CreateOrUpdatePrAsync(GitHostMutation mutation, CancellationToken cancellationToken) =>
        Task.FromException<GitHostMutationResult>(Unavailable());
    public Task<GitHostMutationResult> MergeNormalAsync(GitHostMutation mutation, CancellationToken cancellationToken) =>
        Task.FromException<GitHostMutationResult>(Unavailable());

    private static PublicationUnavailableException Unavailable() => new(
        "GitHub publication is INCOMPLETE: no disposable-target authorization or live producer is configured.");
}
