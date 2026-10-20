using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Evidence;

public sealed class GitRepositoryIdentityReader(
    ICommandExecutor executor,
    string repositoryRoot,
    string protectedBaseCommit) : IRepositoryIdentityReader, IRepositoryEvaluationScopeReader
{
    internal const int MaximumGitListingBytes = 64 * 1024 * 1024;
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
        var ignoreSelection = await ReadProtectedIgnoresAsync(entries, cancellationToken, deadline).ConfigureAwait(false);
        var ignores = ignoreSelection.Active;
        var invalidInputs = javascriptPaths
            .Where(entry => entry.Mode is not ("100644" or "100755") && !ignores.Contains(entry.Path))
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
        if (SupportsCreationOwnedBoundedRead)
            await VerifyWorkingTreeInputsAsync(javascript, ignores, cancellationToken, deadline).ConfigureAwait(false);
        var violations = new List<string>();
        foreach (var entry in javascript.Where(entry => changedPaths.Contains(entry.Path)))
        {
            var headBlob = await GitAsync(["cat-file", "blob", "--end-of-options", entry.ObjectId], cancellationToken, deadline)
                .ConfigureAwait(false);
            var baseObject = await TryGitAsync(
                ["rev-parse", "--verify", $"{protectedBaseCommit}:{entry.Path}"], cancellationToken, deadline)
                .ConfigureAwait(false);
            var baseBlob = baseObject is null ? null : await GitAsync(
                ["cat-file", "blob", "--end-of-options", baseObject.StandardOutput.Trim()], cancellationToken, deadline)
                .ConfigureAwait(false);
            // Changed files containing a live directive are not grandfathered. This makes
            // moved enable/disable and next-line directives fail closed because their coverage
            // cannot be widened merely by preserving the directive text.
            if (SuppressionDirectives(headBlob.StandardOutput).Count > 0 ||
                (baseBlob is not null && SuppressionDirectives(baseBlob.StandardOutput).Count > 0))
                violations.Add(entry.Path);
        }

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
        foreach (var retired in ignoreSelection.Retired)
        {
            hash.AppendData(Encoding.UTF8.GetBytes("retired-ignore"));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(retired));
            hash.AppendData([0]);
        }
        return new RepositoryEvaluationScope(
            files,
            ignores,
            $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}",
            violations,
            invalidInputs,
            ignoreSelection.Retired);
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

    private static IReadOnlyList<string> SuppressionDirectives(string content)
    {
        var directives = new List<string>();
        var index = 0;
        ScanCode(content, ref index, directives, stopAtTemplateExpressionEnd: false);
        return directives;
    }

    internal static bool ContainsSuppressionDirective(string content) =>
        SuppressionDirectives(content).Count > 0;

    private static void ScanCode(
        string content,
        ref int index,
        List<string> directives,
        bool stopAtTemplateExpressionEnd)
    {
        var braceDepth = 0;
        var canStartRegularExpression = true;
        while (index < content.Length)
        {
            var current = content[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }
            if (current is '\'' or '"')
            {
                if (current == '\'' && index > 0 && index + 1 < content.Length &&
                    char.IsLetterOrDigit(content[index - 1]) && char.IsLetterOrDigit(content[index + 1]))
                {
                    // JSX text contractions are not JavaScript string delimiters.
                    index++;
                    continue;
                }
                if (!SkipQuoted(content, ref index, current))
                    directives.Add("ambiguous-unterminated-string-or-regex");
                canStartRegularExpression = false;
                continue;
            }
            if (current == '`')
            {
                if (!ScanTemplate(content, ref index, directives))
                    directives.Add("ambiguous-unterminated-template");
                canStartRegularExpression = false;
                continue;
            }
            if (current == '/' && index + 1 < content.Length && content[index + 1] == '/')
            {
                var start = index += 2;
                while (index < content.Length && content[index] is not ('\r' or '\n'))
                    index++;
                AddDirective(content[start..index], directives);
                continue;
            }
            if (current == '/' && index + 1 < content.Length && content[index + 1] == '*')
            {
                var start = index += 2;
                while (index + 1 < content.Length && !(content[index] == '*' && content[index + 1] == '/'))
                    index++;
                var end = index;
                if (index + 1 < content.Length)
                    index += 2;
                AddDirective(content[start..end], directives);
                continue;
            }
            if (current == '/' && (index == 0 || content[index - 1] != '<') &&
                (canStartRegularExpression || FollowsControlCondition(content, index) || StartsStatementLine(content, index)))
            {
                if (!SkipRegularExpression(content, ref index))
                    directives.Add("ambiguous-unterminated-regex");
                canStartRegularExpression = false;
                continue;
            }
            if (current == '{')
            {
                braceDepth++;
                index++;
                canStartRegularExpression = true;
                continue;
            }
            if (current == '}')
            {
                if (stopAtTemplateExpressionEnd && braceDepth == 0)
                {
                    index++;
                    return;
                }
                braceDepth = Math.Max(0, braceDepth - 1);
                index++;
                canStartRegularExpression = false;
                continue;
            }
            if (IsIdentifierStart(current))
            {
                var start = index++;
                while (index < content.Length && IsIdentifierPart(content[index]))
                    index++;
                canStartRegularExpression = ExpressionPrefixKeywords.Contains(content[start..index]);
                continue;
            }
            if (char.IsDigit(current))
            {
                index++;
                while (index < content.Length &&
                       (char.IsLetterOrDigit(content[index]) || content[index] is '.' or '_' or 'x' or 'X'))
                    index++;
                canStartRegularExpression = false;
                continue;
            }

            index++;
            canStartRegularExpression = current switch
            {
                ')' or ']' => false,
                '.' => false,
                '+' or '-' when index < content.Length && content[index] == current => false,
                _ => true
            };
        }
    }

    private static bool ScanTemplate(string content, ref int index, List<string> directives)
    {
        index++;
        while (index < content.Length)
        {
            if (content[index] == '\\')
            {
                index = Math.Min(content.Length, index + 2);
                continue;
            }
            if (content[index] == '`')
            {
                index++;
                return true;
            }
            if (content[index] == '$' && index + 1 < content.Length && content[index + 1] == '{')
            {
                index += 2;
                ScanCode(content, ref index, directives, stopAtTemplateExpressionEnd: true);
                continue;
            }
            index++;
        }
        return false;
    }

    private static bool SkipQuoted(string content, ref int index, char quote)
    {
        index++;
        while (index < content.Length)
        {
            if (content[index] == '\\')
            {
                if (index + 1 < content.Length && content[index + 1] is '\r' or '\n')
                {
                    index += content[index + 1] == '\r' && index + 2 < content.Length && content[index + 2] == '\n' ? 3 : 2;
                    continue;
                }
                index = Math.Min(content.Length, index + 2);
                continue;
            }
            if (content[index] is '\r' or '\n')
                return false;
            if (content[index++] == quote)
                return true;
        }
        return false;
    }

    private static bool SkipRegularExpression(string content, ref int index)
    {
        index++;
        var inCharacterClass = false;
        while (index < content.Length && content[index] is not ('\r' or '\n'))
        {
            if (content[index] == '\\')
            {
                index = Math.Min(content.Length, index + 2);
                continue;
            }
            if (content[index] == '[')
                inCharacterClass = true;
            else if (content[index] == ']')
                inCharacterClass = false;
            else if (content[index] == '/' && !inCharacterClass)
            {
                index++;
                while (index < content.Length && IsIdentifierPart(content[index]))
                    index++;
                return true;
            }
            index++;
        }
        return false;
    }

    private static void AddDirective(string commentBody, List<string> directives)
    {
        var candidate = commentBody.TrimStart();
        foreach (var directive in SuppressionDirectiveNames)
        {
            if (!candidate.StartsWith(directive, StringComparison.Ordinal))
                continue;
            if (candidate.Length == directive.Length || char.IsWhiteSpace(candidate[directive.Length]))
                directives.Add(candidate.Trim());
            return;
        }
    }

    private static bool IsIdentifierStart(char value) =>
        char.IsLetter(value) || value is '_' or '$';

    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$';

    private static bool StartsStatementLine(string content, int index)
    {
        for (var cursor = index - 1; cursor >= 0 && content[cursor] is not ('\r' or '\n'); cursor--)
            if (!char.IsWhiteSpace(content[cursor])) return false;
        return true;
    }

    private static bool FollowsControlCondition(string content, int index)
    {
        var cursor = index - 1;
        while (cursor >= 0 && char.IsWhiteSpace(content[cursor])) cursor--;
        if (cursor < 0 || content[cursor] != ')') return false;
        var depth = 1;
        cursor--;
        while (cursor >= 0 && depth > 0)
        {
            if (content[cursor] == ')') depth++;
            else if (content[cursor] == '(') depth--;
            cursor--;
        }
        while (cursor >= 0 && char.IsWhiteSpace(content[cursor])) cursor--;
        var end = cursor + 1;
        while (cursor >= 0 && IsIdentifierPart(content[cursor])) cursor--;
        return content[(cursor + 1)..end] is "if" or "while" or "for" or "with" or "switch" or "catch";
    }

    private static readonly HashSet<string> ExpressionPrefixKeywords = new(StringComparer.Ordinal)
    {
        "await", "case", "delete", "do", "else", "in", "instanceof", "new", "of", "return", "throw",
        "typeof", "void", "yield"
    };

    private static readonly string[] SuppressionDirectiveNames =
    [
        "eslint-disable-next-line", "eslint-disable-line", "eslint-disable", "eslint-enable",
        "oxlint-disable-next-line", "oxlint-disable-line", "oxlint-disable", "oxlint-enable"
    ];

    private async Task<ProtectedIgnoreSelection> ReadProtectedIgnoresAsync(
        IReadOnlyList<TreeEntry> entries,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        const string path = ".delivery-forge/anti-slop-ignore";
        var result = await TryGitAsync(["show", $"{protectedBaseCommit}:{path}"], cancellationToken, deadline)
            .ConfigureAwait(false);
        if (result is null)
            return new(new HashSet<string>(StringComparer.Ordinal), []);
        var trackedJavascript = entries
            .Where(entry => Adapters.AntiSlopEvaluationPlugin.IsSupportedInput(entry.Path))
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        var retired = new List<string>();
        foreach (var raw in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = raw.Trim().Replace('\\', '/');
            if (value.Length == 0 || value.StartsWith('#'))
                continue;
            var normalized = DeterministicEvaluationPluginRegistry.NormalizeInputs([value]).Single();
            if (trackedJavascript.Contains(normalized))
                ignored.Add(normalized);
            else
                retired.Add(normalized);
        }
        return new(ignored, retired.Order(StringComparer.Ordinal).ToArray());
    }

    private async Task VerifyWorkingTreeInputsAsync(
        IReadOnlyList<TreeEntry> javascript,
        IReadOnlySet<string> ignores,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        var flags = await GitAsync(["ls-files", "-v", "-z", "--"], cancellationToken, deadline)
            .ConfigureAwait(false);
        var flagged = flags.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(value => value.Length > 2 && value[1] == ' ' && value[0] is 'h' or 'S')
            .Select(value => value[2..].Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in javascript.Where(entry => !ignores.Contains(entry.Path)))
        {
            if (flagged.Contains(entry.Path))
                throw new EvidenceRepositoryException(
                    $"Frozen anti-slop input uses a forbidden assume-unchanged/skip-worktree index flag: {entry.Path}");
            var fullPath = Path.GetFullPath(Path.Combine(_repositoryRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(_repositoryRoot + Path.DirectorySeparatorChar, PathComparison) ||
                !File.Exists(fullPath) || File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
                throw new EvidenceRepositoryException($"Frozen anti-slop input is not a regular working-tree file: {entry.Path}");
            var actual = await ComputeGitBlobIdentityAsync(fullPath, entry.ObjectId.Length, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(actual, entry.ObjectId, StringComparison.Ordinal))
                throw new EvidenceRepositoryException(
                    $"Working-tree bytes do not match the frozen Git blob for anti-slop input: {entry.Path}");
        }
    }

    private static async Task<string> ComputeGitBlobIdentityAsync(
        string path,
        int objectIdLength,
        CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        using HashAlgorithm algorithm = objectIdLength == 64 ? SHA256.Create() : SHA1.Create();
        var header = Encoding.ASCII.GetBytes($"blob {length}\0");
        algorithm.TransformBlock(header, 0, header.Length, null, 0);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            algorithm.TransformBlock(buffer, 0, read, null, 0);
        }
        algorithm.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(algorithm.Hash!);
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
                invocation, budget, MaximumGitListingBytes, cancellationToken).ConfigureAwait(false);
            if (capture.CaptureBoundExceeded)
                throw new EvidenceRepositoryException("Frozen Git evidence exceeded the bounded capture limit.");
            return capture.Command;
        }
        if (executor is IBoundedGateFixtureExecutor { SupportsCreationOwnedBoundedCapture: true } fixture)
        {
            var capture = await fixture.ExecuteBoundedCaptureAsync(
                invocation, budget, MaximumGitListingBytes, cancellationToken).ConfigureAwait(false);
            if (capture.CaptureBoundExceeded)
                throw new EvidenceRepositoryException("Frozen Git evidence exceeded the bounded capture limit.");
            return capture.Command;
        }
        return await executor.ExecuteAsync(invocation, budget, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsGitObject(string value) =>
        value.Length is 40 or 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

internal sealed record TreeEntry(string Mode, string Path, string ObjectId);
internal sealed record ProtectedIgnoreSelection(HashSet<string> Active, IReadOnlyList<string> Retired);

public sealed class EvidenceRepositoryException(string message) : Exception(message);
