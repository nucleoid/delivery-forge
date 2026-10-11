namespace DeliveryForge.Planning;

public sealed partial class GitRepositoryContextReader
{
    // Pinned ancestry is a Git fact; it does not establish fresh branch or dispatch authority.
    public async Task<bool> IsAncestorAsync(RepositoryContext descendant, string expectedCommit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descendant);
        if (!descendant.IsReaderIssued()) throw new PlanningException("Ancestry requires an intact reader-issued repository context.");
        if (expectedCommit is null || expectedCommit.Length is not (40 or 64) || !expectedCommit.All(Uri.IsHexDigit))
            throw new PlanningException("Ancestry requires an exact complete commit object ID, never a mutable ref or abbreviation.");
        await RejectPromisorRepositoryAsync(descendant.RepositoryRoot, cancellationToken);
        var shallow = (await GitTextAsync(descendant.RepositoryRoot, cancellationToken, "rev-parse", "--is-shallow-repository")).Trim();
        if (descendant.Shallow || shallow != "false") throw new PlanningException("Incomplete shallow history cannot prove integrated ancestry.");
        var commit = (await GitTextAsync(descendant.RepositoryRoot, cancellationToken, "rev-parse", "--verify", "--end-of-options", $"{descendant.Commit}^{{commit}}")).Trim();
        var tree = (await GitTextAsync(descendant.RepositoryRoot, cancellationToken, "rev-parse", "--verify", "--end-of-options", $"{commit}^{{tree}}")).Trim();
        var expected = (await GitTextAsync(descendant.RepositoryRoot, cancellationToken, "rev-parse", "--verify", "--end-of-options", $"{expectedCommit}^{{commit}}")).Trim();
        if (commit != descendant.Commit || tree != descendant.Tree || !string.Equals(expected, expectedCommit, StringComparison.OrdinalIgnoreCase))
            throw new PlanningException("Ancestry object identities differ from the exact pinned commits/tree.");
        var result = await RunGitAsync(descendant.RepositoryRoot, cancellationToken, true, "merge-base", "--is-ancestor", expected, commit);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new PlanningException("Git ancestry failed; failure is not integration evidence.")
        };
    }
}
