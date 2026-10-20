namespace DeliveryForge.Evidence;

public sealed class GitRepositoryIdentityReader(
    ICommandExecutor executor,
    string repositoryRoot,
    string protectedBaseCommit) : IRepositoryIdentityReader, IRepositoryEvaluationScopeReader
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

    public async Task<RepositoryEvaluationScope> ReadEvaluationScopeAsync(
        string expectedHeadCommit,
        CancellationToken cancellationToken)
    {
        if (!IsGitObject(expectedHeadCommit))
            throw new EvidenceRepositoryException("A full frozen head commit is required for evaluation scope.");
        var head = await GitAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (!string.Equals(head.StandardOutput.Trim(), expectedHeadCommit, StringComparison.Ordinal))
            throw new EvidenceRepositoryException("Evaluation scope head does not match the frozen repository head.");

        var tree = await GitAsync(
            ["ls-tree", "-r", "--name-only", "-z", expectedHeadCommit], cancellationToken).ConfigureAwait(false);
        var files = tree.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        var ignorePatterns = await ReadIgnorePatternsAsync(
            expectedHeadCommit, files, cancellationToken).ConfigureAwait(false);

        foreach (var file in files.Where(Adapters.AntiSlopEvaluationPlugin.IsSupportedInput))
        {
            if (IgnorePatternMatcher.IsIgnored(file, ignorePatterns))
                ignored.Add(file);
        }
        return new RepositoryEvaluationScope(files, ignored);
    }

    private async Task<IReadOnlyList<ScopedIgnorePattern>> ReadIgnorePatternsAsync(
        string head,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var patterns = new List<ScopedIgnorePattern>
        {
            new(string.Empty, "node_modules/**"),
            new(string.Empty, "vendor/anti-slop/**")
        };
        var ignoreFiles = files
            .Where(file => string.Equals(Path.GetFileName(file), ".gitignore", StringComparison.Ordinal) ||
                           file is ".oxlintignore" or ".eslintignore")
            .OrderBy(file => file.Count(character => character == '/'))
            .ThenBy(file => file, StringComparer.Ordinal);
        foreach (var ignoreFile in ignoreFiles)
        {
            var content = await GitAsync(["show", $"{head}:{ignoreFile}"], cancellationToken).ConfigureAwait(false);
            var basePath = Path.GetDirectoryName(ignoreFile)?.Replace('\\', '/') ?? string.Empty;
            foreach (var pattern in content.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (pattern.Contains('\\') || pattern.Contains('[') || pattern.Contains(']'))
                    throw new EvidenceRepositoryException(
                        $"Ignore pattern uses unsupported escaping or a character class: {ignoreFile}");
                var doubleStarProbe = pattern.TrimStart('!').Trim('/').Split('/');
                if (doubleStarProbe.Any(segment => segment.Contains("**", StringComparison.Ordinal) && segment != "**"))
                    throw new EvidenceRepositoryException(
                        $"Ignore pattern uses an unsupported embedded double-star: {ignoreFile}");
                patterns.Add(new ScopedIgnorePattern(basePath, pattern));
            }
        }
        return patterns;
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

internal static class IgnorePatternMatcher
{
    internal static bool IsIgnored(string path, IReadOnlyList<ScopedIgnorePattern> patterns)
    {
        var ignored = false;
        foreach (var scoped in patterns)
        {
            var prefixPath = scoped.BasePath.Length == 0 ? string.Empty : scoped.BasePath + "/";
            if (!path.StartsWith(prefixPath, StringComparison.Ordinal))
                continue;
            var relativePath = path[prefixPath.Length..];
            var value = scoped.Pattern.TrimEnd();
            if (value.Length == 0 || value.StartsWith('#'))
                continue;
            var negated = value.StartsWith('!');
            if (negated)
                value = value[1..];
            if (value.Length == 0)
                continue;
            var anchored = value.StartsWith('/');
            value = value.TrimStart('/');
            value = value.TrimEnd('/');
            var expression = System.Text.RegularExpressions.Regex.Escape(value)
                .Replace(@"\*\*/", "(?:.*/)?")
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", "[^/]*")
                .Replace(@"\?", "[^/]");
            var prefix = anchored || value.Contains('/') ? "^" : "(?:^|.*/)";
            // A gitignore token can name either a file or a directory. If it names a
            // directory, all descendants are ignored even without a trailing slash.
            var suffix = "(?:/.*)?$";
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    relativePath, prefix + expression + suffix,
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                ignored = !negated;
        }
        return ignored;
    }
}

internal sealed record ScopedIgnorePattern(string BasePath, string Pattern);
