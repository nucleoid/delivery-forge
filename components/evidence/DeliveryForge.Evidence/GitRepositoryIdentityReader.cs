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
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new EvidenceRepositoryException("Evaluation scope requires a positive finite deadline.");
        var deadline = DateTimeOffset.UtcNow + timeout;
        if (!IsGitObject(expectedHeadCommit))
            throw new EvidenceRepositoryException("A full frozen head commit is required for evaluation scope.");
        var head = await GitAsync(["rev-parse", "HEAD"], cancellationToken, deadline).ConfigureAwait(false);
        if (!string.Equals(head.StandardOutput.Trim(), expectedHeadCommit, StringComparison.Ordinal))
            throw new EvidenceRepositoryException("Evaluation scope head does not match the frozen repository head.");

        var tree = await GitAsync(
            ["ls-tree", "-r", "-z", "--format=%(objectmode)%x09%(objectname)%x09%(path)", expectedHeadCommit],
            cancellationToken, deadline)
            .ConfigureAwait(false);
        var entries = tree.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseTreeEntry)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var files = entries.Select(entry => entry.Path).ToArray();
        var javascriptPaths = entries
            .Where(entry => Adapters.AntiSlopEvaluationPlugin.IsSupportedInput(entry.Path))
            .ToArray();
        var invalidInputs = javascriptPaths
            .Where(entry => entry.Mode is not ("100644" or "100755"))
            .Select(entry => entry.Path)
            .ToArray();
        var javascript = javascriptPaths
            .Where(entry => entry.Mode is "100644" or "100755")
            .ToArray();
        var changed = await GitAsync(
            ["diff", "--name-only", "-z", protectedBaseCommit, expectedHeadCommit, "--"],
            cancellationToken, deadline)
            .ConfigureAwait(false);
        var changedPaths = changed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var entry in javascript.Where(entry => changedPaths.Contains(entry.Path)))
        {
            var headBlob = await GitAsync(["show", $"{expectedHeadCommit}:{entry.Path}"], cancellationToken, deadline)
                .ConfigureAwait(false);
            var baseBlob = await TryGitAsync(
                ["show", $"{protectedBaseCommit}:{entry.Path}"], cancellationToken, deadline)
                .ConfigureAwait(false);
            // Changed files containing a live directive are not grandfathered. This makes
            // moved enable/disable and next-line directives fail closed because their coverage
            // cannot be widened merely by preserving the directive text.
            if (SuppressionDirectives(headBlob.StandardOutput).Count > 0 ||
                (baseBlob is not null && SuppressionDirectives(baseBlob.StandardOutput).Count > 0))
                violations.Add(entry.Path);
        }

        var ignores = await ReadProtectedIgnoresAsync(entries, cancellationToken, deadline).ConfigureAwait(false);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in javascriptPaths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Path));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(entry.ObjectId));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Mode));
            hash.AppendData([0]);
            hash.AppendData([ignores.Contains(entry.Path) ? (byte)1 : (byte)0]);
        }
        return new RepositoryEvaluationScope(
            files,
            ignores,
            $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}",
            violations,
            invalidInputs);
    }

    private static TreeEntry ParseTreeEntry(string value)
    {
        var first = value.IndexOf('\t');
        var second = first < 0 ? -1 : value.IndexOf('\t', first + 1);
        if (first <= 0 || second <= first + 1 || second == value.Length - 1)
            throw new EvidenceRepositoryException("Git returned a malformed frozen tree entry.");
        var mode = value[..first];
        var objectId = value[(first + 1)..second];
        var path = value[(second + 1)..].Replace('\\', '/');
        if (!IsGitObject(objectId) || string.IsNullOrWhiteSpace(path))
            throw new EvidenceRepositoryException("Git returned a malformed frozen tree object identity.");
        return new TreeEntry(mode, path, objectId);
    }

    private static IReadOnlyList<string> SuppressionDirectives(string content) =>
        Regex.Matches(content, @"//[^\r\n]*|/\*[\s\S]*?\*/", RegexOptions.CultureInvariant)
            .SelectMany(comment => Regex.Matches(
                comment.Value,
                @"(?i)\b(?:eslint|oxlint)-(?:disable|enable)(?:-next-line|-line)?\b[^\r\n]*",
                RegexOptions.CultureInvariant).Select(match => match.Value.Trim()))
            .ToArray();

    private async Task<HashSet<string>> ReadProtectedIgnoresAsync(
        IReadOnlyList<TreeEntry> entries,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        const string path = ".delivery-forge/anti-slop-ignore";
        var result = await TryGitAsync(["show", $"{protectedBaseCommit}:{path}"], cancellationToken, deadline)
            .ConfigureAwait(false);
        if (result is null)
            return new HashSet<string>(StringComparer.Ordinal);
        var trackedJavascript = entries
            .Where(entry => Adapters.AntiSlopEvaluationPlugin.IsSupportedInput(entry.Path))
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = raw.Trim().Replace('\\', '/');
            if (value.Length == 0 || value.StartsWith('#'))
                continue;
            var normalized = DeterministicEvaluationPluginRegistry.NormalizeInputs([value]).Single();
            if (!trackedJavascript.Contains(normalized))
                throw new EvidenceRepositoryException(
                    $"Protected anti-slop ignore names a non-tracked JavaScript-family path: {normalized}");
            ignored.Add(normalized);
        }
        return ignored;
    }

    private async Task<CommandResult> GitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? deadline = null)
    {
        var result = await ExecuteGitAsync(arguments, cancellationToken, deadline).ConfigureAwait(false);
        if (result.TimedOut || result.Cancelled || result.ExitCode != 0)
            throw new EvidenceRepositoryException(
                $"Git identity command failed: git {string.Join(" ", arguments)}");
        return result;
    }

    private async Task<CommandResult?> TryGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? deadline = null)
    {
        var result = await ExecuteGitAsync(arguments, cancellationToken, deadline).ConfigureAwait(false);
        if (result.TimedOut || result.Cancelled)
            throw new EvidenceRepositoryException($"Frozen Git blob read did not settle: git {string.Join(" ", arguments)}");
        return result.ExitCode == 0 ? result : null;
    }

    private async Task<CommandResult> ExecuteGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? deadline = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = deadline is null
            ? TimeSpan.FromSeconds(30)
            : deadline.Value - DateTimeOffset.UtcNow;
        if (budget <= TimeSpan.Zero)
            throw new EvidenceRepositoryException("The finite evaluation-scope deadline expired.");
        if (budget > TimeSpan.FromSeconds(30))
            budget = TimeSpan.FromSeconds(30);
        var invocation = new CommandInvocation(
            "git", arguments, _repositoryRoot,
            new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" });
        if (executor is ProcessCommandExecutor { SupportsCreationOwnedBoundedCapture: true } owned)
        {
            var capture = await owned.ExecuteBoundedCaptureAsync(
                invocation, budget, 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (capture.CaptureBoundExceeded)
                throw new EvidenceRepositoryException("Frozen Git evidence exceeded the bounded capture limit.");
            return capture.Command;
        }
        if (executor is IBoundedGateFixtureExecutor { SupportsCreationOwnedBoundedCapture: true } fixture)
        {
            var capture = await fixture.ExecuteBoundedCaptureAsync(
                invocation, budget, 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (capture.CaptureBoundExceeded)
                throw new EvidenceRepositoryException("Frozen Git evidence exceeded the bounded capture limit.");
            return capture.Command;
        }
        return await executor.ExecuteAsync(invocation, budget, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsGitObject(string value) =>
        value.Length is 40 or 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record TreeEntry(string Mode, string Path, string ObjectId);

public sealed class EvidenceRepositoryException(string message) : Exception(message);
