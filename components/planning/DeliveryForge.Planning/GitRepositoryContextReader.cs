using System.Diagnostics;
using System.Text;

namespace DeliveryForge.Planning;

public sealed class GitRepositoryContextReader
{
    private const int MaximumGitOutputBytes = 16 * 1024 * 1024;

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
        var commit = (await GitTextAsync(root, cancellationToken, "rev-parse", "--verify", $"{reference}^{{commit}}")).Trim();
        var tree = (await GitTextAsync(root, cancellationToken, "rev-parse", "--verify", $"{commit}^{{tree}}")).Trim();
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
        var treeEntry = await RunGitAsync(
            context.RepositoryRoot,
            cancellationToken,
            allowFailure: false,
            "ls-tree", "-z", context.Commit, "--", path);
        if (treeEntry.StandardOutput.Length == 0)
        {
            throw new PlanningException($"Path '{path}' does not exist at exact commit {context.Commit}.");
        }

        var metadata = Encoding.UTF8.GetString(treeEntry.StandardOutput).TrimEnd('\0');
        var tab = metadata.IndexOf('\t');
        var parts = (tab < 0 ? metadata : metadata[..tab]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[1] != "blob")
        {
            throw new PlanningException($"Path '{path}' is not a blob at exact commit {context.Commit}.");
        }

        var bytes = (await RunGitAsync(context.RepositoryRoot, cancellationToken, false, "cat-file", "blob", parts[2])).StandardOutput;
        var isSymlink = parts[0] == "120000";
        var escapes = isSymlink && SymlinkEscapes(context.RepositoryRoot, path, Encoding.UTF8.GetString(bytes));
        var generated = path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            ? "generated-by-convention"
            : "not-detected; generator metadata was not asserted";

        return new RepositoryFile(path, parts[2], parts[0], bytes, isSymlink, escapes, generated);
    }

    private static string NormalizeRepositoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.IndexOf('\0') >= 0)
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

    private static bool SymlinkEscapes(string root, string path, string target)
    {
        if (Path.IsPathRooted(target)) return true;
        var parent = Path.GetDirectoryName(Path.Combine(root, path)) ?? root;
        var resolved = Path.GetFullPath(Path.Combine(parent, target));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return !resolved.StartsWith(rootWithSeparator, StringComparison.Ordinal) && !string.Equals(resolved, root, StringComparison.Ordinal);
    }

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
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new PlanningException("Unable to start Git.");
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var result = new GitResult(process.ExitCode, await stdoutTask, await stderrTask);
        if (!allowFailure && result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError).Trim();
            throw new PlanningException($"Git could not resolve the requested ref/object: {error}");
        }

        return result;
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
}
