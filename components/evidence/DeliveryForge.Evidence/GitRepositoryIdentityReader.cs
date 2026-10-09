namespace DeliveryForge.Evidence;

public sealed class GitRepositoryIdentityReader(
    ICommandExecutor executor,
    string repositoryRoot,
    string protectedBaseCommit) : IRepositoryIdentityReader
{
    private readonly string _repositoryRoot = Path.GetFullPath(repositoryRoot);

    public async Task<RepositoryIdentity> ReadAsync(CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(_repositoryRoot) ||
            !IsGitObject(protectedBaseCommit))
            throw new EvidenceRepositoryException("Repository root and a full protected base commit are required.");

        var head = await GitAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        var resolvedBase = await GitAsync(
            ["rev-parse", "--verify", $"{protectedBaseCommit}^{{commit}}"], cancellationToken).ConfigureAwait(false);
        var headId = head.StandardOutput.Trim();
        var baseId = resolvedBase.StandardOutput.Trim();
        if (!string.Equals(baseId, protectedBaseCommit, StringComparison.Ordinal) || !IsGitObject(headId))
            throw new EvidenceRepositoryException("Git did not resolve the exact protected base/head identities.");
        await GitAsync(
            ["merge-base", "--is-ancestor", baseId, headId], cancellationToken).ConfigureAwait(false);
        var tree = await GitAsync(["rev-parse", "HEAD^{tree}"], cancellationToken).ConfigureAwait(false);
        var status = await GitAsync(
            ["status", "--porcelain=v1", "--untracked-files=all"], cancellationToken).ConfigureAwait(false);
        var treeId = tree.StandardOutput.Trim();
        if (!IsGitObject(treeId))
            throw new EvidenceRepositoryException("Git returned a malformed head or tree identity.");

        return new RepositoryIdentity(
            baseId,
            headId,
            treeId,
            string.IsNullOrEmpty(status.StandardOutput));
    }

    private async Task<CommandResult> GitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new CommandInvocation(
                "git", arguments, _repositoryRoot, new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" }),
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || result.Cancelled || result.ExitCode != 0)
            throw new EvidenceRepositoryException(
                $"Git identity command failed: git {string.Join(" ", arguments)}");
        return result;
    }

    private static bool IsGitObject(string value) =>
        value.Length is 40 or 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class EvidenceRepositoryException(string message) : Exception(message);
