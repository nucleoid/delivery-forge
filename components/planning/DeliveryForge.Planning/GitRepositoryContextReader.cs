using System.Diagnostics;
using System.Text;

namespace DeliveryForge.Planning;

public sealed class GitRepositoryContextReader
{
    private const int MaximumGitOutputBytes = 16 * 1024 * 1024;
    private const int MaximumSymlinkTargetBytes = 4 * 1024;
    private static readonly TimeSpan GitOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GitTerminationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SymlinkResolutionTimeout = TimeSpan.FromSeconds(10);
    private readonly string _gitExecutable;

    public GitRepositoryContextReader(string? gitExecutablePath = null)
    {
        _gitExecutable = ResolveGitExecutable(
            gitExecutablePath,
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows());
    }

    internal static string ResolveGitExecutable(
        string? configuredPath,
        string? path,
        bool windows)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath))
            {
                throw new PlanningException("The configured Git executable path must be absolute.");
            }

            var configured = Path.GetFullPath(configuredPath);
            if (!File.Exists(configured))
            {
                throw new PlanningException($"The configured Git executable '{configured}' does not exist.");
            }
            return configured;
        }

        var executableName = windows ? "git.exe" : "git";
        foreach (var rawEntry in (path ?? string.Empty).Split(Path.PathSeparator))
        {
            var entry = rawEntry.Trim().Trim('"');
            if (entry.Length == 0 || !Path.IsPathFullyQualified(entry)) continue;
            var candidate = Path.GetFullPath(Path.Combine(entry, executableName));
            if (File.Exists(candidate)) return candidate;
        }

        throw new PlanningException("Git could not be resolved from an absolute configured path or an absolute PATH entry.");
    }

    public async Task<RepositoryContext> ReadAsync(
        string repositoryRoot,
        string reference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(reference))
        {
            throw new PlanningException("Repository root and ref are required.");
        }

        var requestedRoot = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(requestedRoot))
        {
            throw new PlanningException($"Repository directory '{requestedRoot}' does not exist.");
        }

        var root = (await GitTextAsync(requestedRoot, cancellationToken, "rev-parse", "--show-toplevel")).Trim();
        root = Path.GetFullPath(root);
        await RejectPromisorRepositoryAsync(root, cancellationToken);
        var commit = (await GitTextAsync(root, cancellationToken, "rev-parse", "--verify", "--end-of-options", $"{reference}^{{commit}}")).Trim();
        var exactBranchReferenceVerified = false;
        if (RepositoryContext.IsFreshnessBearingReference(reference))
        {
            var exactRef = await RunGitAsync(
                root,
                cancellationToken,
                allowFailure: true,
                "show-ref", "--verify", "--hash", reference);
            var exactRefCommit = Encoding.UTF8.GetString(exactRef.StandardOutput).Trim();
            if (exactRef.ExitCode != 0 || !string.Equals(exactRefCommit, commit, StringComparison.Ordinal))
            {
                throw new PlanningException(
                    $"The requested exact branch ref '{reference}' does not exist or does not resolve to the requested commit.");
            }
            exactBranchReferenceVerified = true;
        }
        var tree = (await GitTextAsync(root, cancellationToken, "rev-parse", "--verify", "--end-of-options", $"{commit}^{{tree}}")).Trim();
        var headCommit = (await GitTextAsync(root, cancellationToken, "rev-parse", "--verify", "HEAD^{commit}")).Trim();
        var headTree = (await GitTextAsync(root, cancellationToken, "rev-parse", "--verify", $"{headCommit}^{{tree}}")).Trim();
        var status = await GitTextAsync(
            root,
            cancellationToken,
            "status", "--porcelain=v1", "--untracked-files=normal", "--ignore-submodules=all");
        var shallow = string.Equals(
            (await GitTextAsync(root, cancellationToken, "rev-parse", "--is-shallow-repository")).Trim(),
            "true",
            StringComparison.Ordinal);
        var symbolic = await RunGitAsync(root, cancellationToken, allowFailure: true, "symbolic-ref", "-q", "HEAD");
        var submodules = await ReadGitlinksAsync(root, commit, cancellationToken);
        var limitations = new List<string>();
        if (shallow) limitations.Add("Repository is shallow; objects outside the retained history may be unavailable.");
        if (submodules.Length > 0)
            limitations.Add("Submodule gitlinks are recorded from the parent tree; submodule worktrees are not entered or inspected.");
        if (!string.IsNullOrEmpty(status)) limitations.Add("Mutable worktree is dirty; exact-object reads remain pinned to the resolved commit.");

        return RepositoryContext.Create(
            root,
            reference,
            commit,
            tree,
            detachedHead: symbolic.ExitCode != 0,
            dirty: !string.IsNullOrEmpty(status),
            shallow,
            submodules,
            limitations,
            headCommit,
            headTree,
            exactBranchReferenceVerified);
    }

    public async Task<RepositoryFile> ReadFileAsync(
        RepositoryContext context,
        string repositoryRelativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsReaderIssued())
        {
            throw new PlanningException("Repository context was not issued by the exact Git reader or its bound observations changed.");
        }
        await RejectPromisorRepositoryAsync(context.RepositoryRoot, cancellationToken);
        var commit = (await GitTextAsync(
            context.RepositoryRoot,
            cancellationToken,
            "rev-parse", "--verify", "--end-of-options", $"{context.Commit}^{{commit}}")).Trim();
        var tree = (await GitTextAsync(
            context.RepositoryRoot,
            cancellationToken,
            "rev-parse", "--verify", "--end-of-options", $"{commit}^{{tree}}")).Trim();
        if (!string.Equals(commit, context.Commit, StringComparison.Ordinal) ||
            !string.Equals(tree, context.Tree, StringComparison.Ordinal))
        {
            throw new PlanningException("Repository context commit/tree identity does not match the exact Git objects.");
        }
        var path = NormalizeRepositoryPath(repositoryRelativePath);
        var treeEntry = await TryReadTreeEntryAsync(context, path, cancellationToken)
            ?? throw new PlanningException($"Path '{path}' does not exist at exact commit {context.Commit}.");
        if (treeEntry.Type != "blob")
        {
            throw new PlanningException($"Path '{path}' is not a blob at exact commit {context.Commit}.");
        }

        var bytes = await ReadBlobAsync(context, treeEntry.ObjectId, cancellationToken);
        var isSymlink = treeEntry.Mode == "120000";
        var resolution = isSymlink
            ? await ResolveSymlinkAsync(context, path, Encoding.UTF8.GetString(bytes), cancellationToken)
            : SymlinkResolution.NotSymlink;
        var generated = path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            ? "generated-by-convention"
            : "not-detected; generator metadata was not asserted";

        return new RepositoryFile(
            path,
            treeEntry.ObjectId,
            treeEntry.Mode,
            bytes,
            isSymlink,
            resolution is not (SymlinkResolution.NotSymlink or SymlinkResolution.InTree),
            generated,
            resolution,
            context.Commit,
            context.Tree);
    }

    private async Task RejectPromisorRepositoryAsync(string root, CancellationToken cancellationToken)
    {
        var effectiveConfiguration = await RunGitAsync(
            root,
            cancellationToken,
            allowFailure: true,
            "config", "--includes", "--get-regexp", "^(remote\\..*\\.promisor|extensions\\.partialclone)$");
        if (effectiveConfiguration.ExitCode != 1)
        {
            throw new PlanningException(
                effectiveConfiguration.ExitCode == 0
                    ? "Exact object reads reject partial clone/promisor repositories before object access so missing objects cannot trigger a network fetch."
                    : "Exact object reads reject repositories whose effective included promisor configuration cannot be inspected safely.");
        }
    }

    private async Task<string[]> ReadGitlinksAsync(
        string root,
        string commit,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            root,
            cancellationToken,
            allowFailure: false,
            "ls-tree", "-r", "-z", "--full-tree", "--end-of-options", commit);
        return Encoding.UTF8.GetString(result.StandardOutput)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('\t', 2))
            .Where(parts => parts.Length == 2 && parts[0].StartsWith("160000 commit ", StringComparison.Ordinal))
            .Select(parts => $"{parts[0][14..]} {parts[1]}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeRepositoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || PortableMaterial.IsAbsolutePath(path) || path.IndexOf('\0') >= 0)
        {
            throw new PlanningException("Repository paths must be non-empty, relative, and portable.");
        }
        if (path.Contains('\\'))
        {
            throw new PlanningException("Repository paths must use forward slashes; backslash aliases are not accepted.");
        }

        if (path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new PlanningException("Repository paths cannot contain empty, current, or parent traversal segments.");
        }

        return path;
    }

    private async Task<SymlinkResolution> ResolveSymlinkAsync(
        RepositoryContext context,
        string symlinkPath,
        string target,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SymlinkResolutionTimeout);
        var budget = new ResolutionBudget();
        var treeListings = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            var posix = await ResolveTargetAsync(
                context,
                ParentSegments(symlinkPath),
                target,
                [],
                budget,
                treeListings,
                SymlinkSemantics.PosixExpansion,
                deadline.Token);
            var windows = await ResolveTargetAsync(
                context,
                ParentSegments(symlinkPath),
                target,
                [],
                budget,
                treeListings,
                SymlinkSemantics.WindowsLexicalCollapse,
                deadline.Token);
            return CombineResolution(posix, windows);
        }
        catch (ResolutionBoundExceededException)
        {
            return SymlinkResolution.BoundExceeded;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SymlinkResolution.BoundExceeded;
        }
    }

    private async Task<SymlinkResolution> ResolveTargetAsync(
        RepositoryContext context,
        IReadOnlyList<string> parent,
        string target,
        IReadOnlyList<string> remaining,
        ResolutionBudget budget,
        Dictionary<string, byte[]> treeListings,
        SymlinkSemantics semantics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(target) || PortableMaterial.IsAbsolutePath(target))
        {
            return SymlinkResolution.Escapes;
        }
        if (Encoding.UTF8.GetByteCount(target) > MaximumSymlinkTargetBytes)
        {
            return SymlinkResolution.BoundExceeded;
        }
        List<string> resolved;
        Queue<string> pending;
        if (semantics == SymlinkSemantics.WindowsLexicalCollapse)
        {
            var collapsed = CollapseTarget(parent, target, budget);
            if (collapsed is null) return SymlinkResolution.Escapes;
            resolved = [];
            pending = new Queue<string>(collapsed.Concat(remaining));
        }
        else
        {
            resolved = parent.ToList();
            pending = new Queue<string>(SplitPosixTarget(target, budget).Concat(remaining));
        }
        while (pending.Count > 0)
        {
            budget.VisitSegment();
            var segment = pending.Dequeue();
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (resolved.Count == 0) return SymlinkResolution.Escapes;
                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            var candidate = string.Join('/', resolved.Append(segment));
            var entry = await TryReadTreeEntryAsync(context, candidate, cancellationToken, budget, treeListings);
            if (entry is null)
            {
                return SymlinkResolution.Missing;
            }

            if (entry.Mode == "120000")
            {
                if (!budget.FollowSymlink())
                {
                    return SymlinkResolution.Cycle;
                }
                var nestedTarget = Encoding.UTF8.GetString(await ReadBlobAsync(context, entry.ObjectId, cancellationToken, budget));
                if (string.IsNullOrEmpty(nestedTarget) || PortableMaterial.IsAbsolutePath(nestedTarget))
                {
                    return SymlinkResolution.Escapes;
                }
                if (Encoding.UTF8.GetByteCount(nestedTarget) > MaximumSymlinkTargetBytes)
                {
                    return SymlinkResolution.BoundExceeded;
                }
                if (semantics == SymlinkSemantics.WindowsLexicalCollapse)
                {
                    var collapsed = CollapseTarget(ParentSegments(candidate), nestedTarget, budget);
                    if (collapsed is null) return SymlinkResolution.Escapes;
                    resolved.Clear();
                    pending = new Queue<string>(collapsed.Concat(pending));
                }
                else
                {
                    pending = new Queue<string>(SplitPosixTarget(nestedTarget, budget).Concat(pending));
                }
                continue;
            }

            if (pending.Count > 0 && entry.Type != "tree")
            {
                return SymlinkResolution.Missing;
            }
            if (entry.Type == "commit")
            {
                return SymlinkResolution.Missing;
            }
            resolved.Add(segment);
        }

        return resolved.Count == 0 ? SymlinkResolution.Missing : SymlinkResolution.InTree;
    }

    private static string[] SplitPosixTarget(string target, ResolutionBudget budget)
    {
        var segments = target.Split('/');
        budget.AddSegments(segments.Length);
        return segments;
    }

    private static string[] SplitWindowsTarget(string target, ResolutionBudget budget)
    {
        var segments = target.Replace('\\', '/').Split('/');
        budget.AddSegments(segments.Length);
        return segments;
    }

    private static string[]? CollapseTarget(
        IReadOnlyList<string> parent,
        string target,
        ResolutionBudget budget)
    {
        var collapsed = parent.ToList();
        foreach (var segment in SplitWindowsTarget(target, budget))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (collapsed.Count == 0) return null;
                collapsed.RemoveAt(collapsed.Count - 1);
            }
            else
            {
                collapsed.Add(segment);
            }
        }
        return collapsed.ToArray();
    }

    private static SymlinkResolution CombineResolution(
        SymlinkResolution posix,
        SymlinkResolution windows)
    {
        if (posix == SymlinkResolution.Escapes || windows == SymlinkResolution.Escapes)
            return SymlinkResolution.Escapes;
        if (posix == SymlinkResolution.BoundExceeded || windows == SymlinkResolution.BoundExceeded)
            return SymlinkResolution.BoundExceeded;
        if (posix == SymlinkResolution.Cycle || windows == SymlinkResolution.Cycle)
            return SymlinkResolution.Cycle;
        if (posix == SymlinkResolution.Missing || windows == SymlinkResolution.Missing)
            return SymlinkResolution.Missing;
        return posix == SymlinkResolution.InTree && windows == SymlinkResolution.InTree
            ? SymlinkResolution.InTree
            : SymlinkResolution.Missing;
    }

    private static string[] ParentSegments(string path)
    {
        var segments = path.Split('/');
        return segments.Length == 1 ? [] : segments[..^1];
    }

    private async Task<TreeEntry?> TryReadTreeEntryAsync(
        RepositoryContext context,
        string path,
        CancellationToken cancellationToken,
        ResolutionBudget? budget = null,
        Dictionary<string, byte[]>? treeListings = null)
    {
        var segments = path.Split('/');
        var treeObject = context.Tree;
        for (var index = 0; index < segments.Length; index++)
        {
            if (treeListings is null || !treeListings.TryGetValue(treeObject, out var listing))
            {
                budget?.InvokeGit();
                var result = await RunGitAsync(
                    context.RepositoryRoot,
                    cancellationToken,
                    allowFailure: false,
                    "ls-tree", "-z", "--end-of-options", treeObject);
                listing = result.StandardOutput;
                treeListings?.Add(treeObject, listing);
            }
            var entry = ParseExactTreeEntry(listing, segments[index]);
            if (entry is null) return null;
            if (index == segments.Length - 1) return entry;
            if (entry.Type != "tree") return null;
            treeObject = entry.ObjectId;
        }

        return null;
    }

    private static TreeEntry? ParseExactTreeEntry(byte[] output, string exactName)
    {
        TreeEntry? match = null;
        foreach (var rawEntry in Encoding.UTF8.GetString(output).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = rawEntry.IndexOf('\t');
            if (tab < 0 || !string.Equals(rawEntry[(tab + 1)..], exactName, StringComparison.Ordinal)) continue;
            var parts = rawEntry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                throw new PlanningException("Git returned a malformed exact tree entry.");
            }
            if (match is not null)
            {
                throw new PlanningException("Git returned duplicate exact tree entry names.");
            }
            match = new TreeEntry(parts[0], parts[1], parts[2]);
        }
        return match;
    }

    private async Task<byte[]> ReadBlobAsync(
        RepositoryContext context,
        string objectId,
        CancellationToken cancellationToken,
        ResolutionBudget? budget = null)
    {
        budget?.InvokeGit();
        return (await RunGitAsync(
            context.RepositoryRoot,
            cancellationToken,
            allowFailure: false,
            "cat-file", "blob", "--end-of-options", objectId)).StandardOutput;
    }

    private async Task<string> GitTextAsync(string root, CancellationToken cancellationToken, params string[] arguments) =>
        Encoding.UTF8.GetString((await RunGitAsync(root, cancellationToken, false, arguments)).StandardOutput);

    private async Task<GitResult> RunGitAsync(
        string root,
        CancellationToken cancellationToken,
        bool allowFailure,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(_gitExecutable)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var inheritedRoutingVariables = new[]
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
            "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_CONFIG", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_GLOBAL",
            "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_CEILING_DIRECTORIES",
            "GIT_DISCOVERY_ACROSS_FILESYSTEM"
        };
        foreach (var variable in inheritedRoutingVariables) startInfo.Environment.Remove(variable);
        foreach (var variable in startInfo.Environment.Keys
                     .Where(name => name.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal) ||
                                    name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal))
                     .ToArray())
        {
            startInfo.Environment.Remove(variable);
        }
        startInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        startInfo.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        startInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("protocol.allow=never");
        foreach (var protocol in new[] { "file", "git", "http", "https", "ssh", "ftp", "ftps", "ext" })
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"protocol.{protocol}.allow=never");
        }
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitOperationTimeout);
        using var process = Process.Start(startInfo) ?? throw new PlanningException("Unable to start Git.");
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, timeout.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, timeout.Token);
        var exitTask = process.WaitForExitAsync(timeout.Token);
        var pending = new List<Task> { stdoutTask, stderrTask, exitTask };
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                await completed;
            }
        }
        catch (Exception exception)
        {
            await TerminateAsync(process);
            Observe(stdoutTask);
            Observe(stderrTask);
            if (exception is PlanningException) throw;
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (timeout.IsCancellationRequested)
                throw new PlanningException($"Git operation exceeded the {GitOperationTimeout.TotalSeconds:0}-second timeout.");
            throw new PlanningException($"Git operation failed: {exception.Message}");
        }

        var result = new GitResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
        if (!allowFailure && result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError).Trim();
            throw new PlanningException($"Git could not resolve the requested ref/object: {error}");
        }

        return result;
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(GitTerminationTimeout);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
        }
    }

    private static void Observe(Task task)
    {
        if (task.IsFaulted) _ = task.Exception;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumGitOutputBytes)
            {
                throw new PlanningException($"Git output exceeded the {MaximumGitOutputBytes}-byte bound.");
            }
            output.Write(buffer, 0, read);
        }
    }

    private sealed record GitResult(int ExitCode, byte[] StandardOutput, byte[] StandardError);
    private sealed record TreeEntry(string Mode, string Type, string ObjectId);
    private enum SymlinkSemantics { PosixExpansion, WindowsLexicalCollapse }

    private sealed class ResolutionBudget
    {
        private const int MaximumSegments = 256;
        private const int MaximumSymlinkHops = 40;
        private const int MaximumGitInvocations = 128;
        private int _segments;
        private int _symlinkHops;
        private int _gitInvocations;

        public void AddSegments(int count)
        {
            _segments = checked(_segments + count);
            if (_segments > MaximumSegments) throw new ResolutionBoundExceededException();
        }

        public void VisitSegment()
        {
            _segments = checked(_segments + 1);
            if (_segments > MaximumSegments) throw new ResolutionBoundExceededException();
        }

        public bool FollowSymlink() => ++_symlinkHops <= MaximumSymlinkHops;

        public void InvokeGit()
        {
            if (++_gitInvocations > MaximumGitInvocations) throw new ResolutionBoundExceededException();
        }
    }

    private sealed class ResolutionBoundExceededException : Exception;
}
