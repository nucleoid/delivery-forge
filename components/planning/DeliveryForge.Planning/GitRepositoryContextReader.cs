using System.Diagnostics;
using System.Text;

namespace DeliveryForge.Planning;

public sealed class GitRepositoryContextReader
{
    private const int MaximumGitOutputBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan GitOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GitTerminationTimeout = TimeSpan.FromSeconds(5);

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
        var commit = (await GitTextAsync(root, cancellationToken, "rev-parse", "--verify", "--end-of-options", $"{reference}^{{commit}}")).Trim();
        var tree = (await GitTextAsync(root, cancellationToken, "rev-parse", "--verify", "--end-of-options", $"{commit}^{{tree}}")).Trim();
        var status = await GitTextAsync(root, cancellationToken, "status", "--porcelain=v1", "--untracked-files=normal");
        var shallow = string.Equals(
            (await GitTextAsync(root, cancellationToken, "rev-parse", "--is-shallow-repository")).Trim(),
            "true",
            StringComparison.Ordinal);
        var symbolic = await RunGitAsync(root, cancellationToken, allowFailure: true, "symbolic-ref", "-q", "HEAD");
        var submoduleResult = await RunGitAsync(root, cancellationToken, allowFailure: true, "submodule", "status", "--recursive");
        var submodules = Encoding.UTF8.GetString(submoduleResult.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var limitations = new List<string>();
        if (shallow) limitations.Add("Repository is shallow; objects outside the retained history may be unavailable.");
        if (submodules.Length > 0) limitations.Add("Submodule commits are recorded separately and are not expanded into the parent tree.");
        if (!string.IsNullOrEmpty(status)) limitations.Add("Mutable worktree is dirty; exact-object reads remain pinned to the resolved commit.");

        return new RepositoryContext(
            root,
            reference,
            commit,
            tree,
            DetachedHead: symbolic.ExitCode != 0,
            Dirty: !string.IsNullOrEmpty(status),
            Shallow: shallow,
            Submodules: submodules,
            Limitations: limitations);
    }

    public async Task<RepositoryFile> ReadFileAsync(
        RepositoryContext context,
        string repositoryRelativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
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
            resolution == SymlinkResolution.Escapes,
            generated,
            resolution);
    }

    private static string NormalizeRepositoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || PortableMaterial.IsAbsolutePath(path) || path.IndexOf('\0') >= 0)
        {
            throw new PlanningException("Repository paths must be non-empty, relative, and portable.");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new PlanningException("Repository paths cannot contain empty, current, or parent traversal segments.");
        }

        return normalized;
    }

    private static async Task<SymlinkResolution> ResolveSymlinkAsync(
        RepositoryContext context,
        string symlinkPath,
        string target,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { symlinkPath };
        return await ResolveTargetAsync(context, ParentSegments(symlinkPath), target, [], visited, cancellationToken);
    }

    private static async Task<SymlinkResolution> ResolveTargetAsync(
        RepositoryContext context,
        IReadOnlyList<string> parent,
        string target,
        IReadOnlyList<string> remaining,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(target) || PortableMaterial.IsAbsolutePath(target))
        {
            return SymlinkResolution.Escapes;
        }

        var resolved = parent.ToList();
        var pending = new Queue<string>(SplitTarget(target).Concat(remaining));
        while (pending.Count > 0)
        {
            var segment = pending.Dequeue();
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (resolved.Count == 0) return SymlinkResolution.Escapes;
                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            var candidate = string.Join('/', resolved.Append(segment));
            var entry = await TryReadTreeEntryAsync(context, candidate, cancellationToken);
            if (entry is null)
            {
                return SymlinkResolution.Missing;
            }

            if (entry.Mode == "120000")
            {
                if (!visited.Add(candidate))
                {
                    return SymlinkResolution.Cycle;
                }
                var nestedTarget = Encoding.UTF8.GetString(await ReadBlobAsync(context, entry.ObjectId, cancellationToken));
                if (string.IsNullOrEmpty(nestedTarget) || PortableMaterial.IsAbsolutePath(nestedTarget))
                {
                    return SymlinkResolution.Escapes;
                }

                pending = new Queue<string>(SplitTarget(nestedTarget).Concat(pending));
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

    private static IEnumerable<string> SplitTarget(string target) => target.Replace('\\', '/').Split('/');

    private static string[] ParentSegments(string path)
    {
        var segments = path.Split('/');
        return segments.Length == 1 ? [] : segments[..^1];
    }

    private static async Task<TreeEntry?> TryReadTreeEntryAsync(
        RepositoryContext context,
        string path,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            context.RepositoryRoot,
            cancellationToken,
            allowFailure: false,
            "ls-tree", "-z", "--end-of-options", context.Commit, "--", path);
        if (result.StandardOutput.Length == 0) return null;

        var metadata = Encoding.UTF8.GetString(result.StandardOutput).TrimEnd('\0');
        var tab = metadata.IndexOf('\t');
        var parts = (tab < 0 ? metadata : metadata[..tab]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 ? new TreeEntry(parts[0], parts[1], parts[2]) : null;
    }

    private static async Task<byte[]> ReadBlobAsync(
        RepositoryContext context,
        string objectId,
        CancellationToken cancellationToken) =>
        (await RunGitAsync(
            context.RepositoryRoot,
            cancellationToken,
            allowFailure: false,
            "cat-file", "blob", "--end-of-options", objectId)).StandardOutput;

    private static async Task<string> GitTextAsync(string root, CancellationToken cancellationToken, params string[] arguments) =>
        Encoding.UTF8.GetString((await RunGitAsync(root, cancellationToken, false, arguments)).StandardOutput);

    private static async Task<GitResult> RunGitAsync(
        string root,
        CancellationToken cancellationToken,
        bool allowFailure,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
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
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
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
}
