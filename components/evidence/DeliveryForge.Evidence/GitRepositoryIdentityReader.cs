using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeliveryForge.Evidence;

public sealed class GitRepositoryIdentityReader(
    ICommandExecutor executor,
    string repositoryRoot,
    string protectedBaseCommit) : IRepositoryIdentityReader, IRepositoryEvaluationScopeReader
{
    private readonly string _repositoryRoot = Path.GetFullPath(repositoryRoot);
    internal bool SupportsCreationOwnedBoundedRead =>
        executor is ProcessCommandExecutor { SupportsCreationOwnedBoundedCapture: true };

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
            ["ls-tree", "-r", "-z", "--format=%(objectname)%x09%(path)", expectedHeadCommit], cancellationToken)
            .ConfigureAwait(false);
        var entries = tree.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseTreeEntry)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var files = entries.Select(entry => entry.Path).ToArray();
        var javascript = entries.Where(entry => Adapters.AntiSlopEvaluationPlugin.IsSupportedInput(entry.Path)).ToArray();
        var changed = await GitAsync(
            ["diff", "--name-only", "-z", protectedBaseCommit, expectedHeadCommit, "--"], cancellationToken)
            .ConfigureAwait(false);
        var changedPaths = changed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var entry in javascript)
        {
            var headBlob = await GitAsync(["show", $"{expectedHeadCommit}:{entry.Path}"], cancellationToken)
                .ConfigureAwait(false);
            var headDirectives = SuppressionDirectives(headBlob.StandardOutput);
            if (headDirectives.Count == 0 || !changedPaths.Contains(entry.Path))
                continue;

            var baseBlob = await TryGitAsync(["show", $"{protectedBaseCommit}:{entry.Path}"], cancellationToken)
                .ConfigureAwait(false);
            var baseDirectives = baseBlob is null ? [] : SuppressionDirectives(baseBlob.StandardOutput);
            if (!headDirectives.SequenceEqual(baseDirectives, StringComparer.Ordinal))
                violations.Add(entry.Path);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in javascript)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Path));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(entry.ObjectId));
            hash.AppendData([0]);
        }
        return new RepositoryEvaluationScope(
            files,
            new HashSet<string>(StringComparer.Ordinal),
            $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}",
            violations);
    }

    private static TreeEntry ParseTreeEntry(string value)
    {
        var separator = value.IndexOf('\t');
        if (separator <= 0 || separator == value.Length - 1)
            throw new EvidenceRepositoryException("Git returned a malformed frozen tree entry.");
        var objectId = value[..separator];
        var path = value[(separator + 1)..].Replace('\\', '/');
        if (!IsGitObject(objectId) || string.IsNullOrWhiteSpace(path))
            throw new EvidenceRepositoryException("Git returned a malformed frozen tree object identity.");
        return new TreeEntry(path, objectId);
    }

    private static IReadOnlyList<string> SuppressionDirectives(string content) =>
        Regex.Matches(
                content,
                @"(?im)(?:eslint|oxlint)(?:-(?:disable|enable)(?:-next-line|-line)?|\s+[a-z@][a-z0-9@/_-]*)[^\r\n]*",
                RegexOptions.CultureInvariant)
            .Select(match => match.Value.Trim())
            .Order(StringComparer.Ordinal)
            .ToArray();

    private async Task<CommandResult> GitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteGitAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || result.Cancelled || result.ExitCode != 0)
            throw new EvidenceRepositoryException(
                $"Git identity command failed: git {string.Join(" ", arguments)}");
        return result;
    }

    private async Task<CommandResult?> TryGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteGitAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || result.Cancelled)
            throw new EvidenceRepositoryException($"Frozen Git blob read did not settle: git {string.Join(" ", arguments)}");
        return result.ExitCode == 0 ? result : null;
    }

    private async Task<CommandResult> ExecuteGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var invocation = new CommandInvocation(
            "git", arguments, _repositoryRoot,
            new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" });
        if (executor is ProcessCommandExecutor { SupportsCreationOwnedBoundedCapture: true } owned)
        {
            var capture = await owned.ExecuteBoundedCaptureAsync(
                invocation, TimeSpan.FromSeconds(30), 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (capture.CaptureBoundExceeded)
                throw new EvidenceRepositoryException("Frozen Git evidence exceeded the bounded capture limit.");
            return capture.Command;
        }
        if (executor is IBoundedGateFixtureExecutor { SupportsCreationOwnedBoundedCapture: true } fixture)
        {
            var capture = await fixture.ExecuteBoundedCaptureAsync(
                invocation, TimeSpan.FromSeconds(30), 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (capture.CaptureBoundExceeded)
                throw new EvidenceRepositoryException("Frozen Git evidence exceeded the bounded capture limit.");
            return capture.Command;
        }
        return await executor.ExecuteAsync(invocation, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsGitObject(string value) =>
        value.Length is 40 or 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record TreeEntry(string Path, string ObjectId);

public sealed class EvidenceRepositoryException(string message) : Exception(message);
