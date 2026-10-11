namespace DeliveryForge.Publication.GitHost;

public sealed class ProcessGitHubCliExecutor(GitHubReadConfiguration configuration) : IGitHubCliExecutor
{
    public Task<GitHubCliResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Task.FromException<GitHubCliResult>(new PublicationUnavailableException(
            "Bounded CLI process capture is not implemented at this RED checkpoint."));
    }
}
