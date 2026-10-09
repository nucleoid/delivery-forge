using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeliveryForge.Execution.Git;

public sealed record CommittedRevision(string HeadCommit, string TreeId);
public sealed record FileSnapshot(IReadOnlyDictionary<string, string> Files);
public sealed record WorktreeObservation(
    string RootPath, string HeadCommit, string TreeId, bool IsClean,
    IReadOnlyList<string> StatusEntries, FileSnapshot Snapshot);

public sealed class WorktreeManager
{
    internal bool IsSameDirectory(string left, string right) =>
        string.Equals(CanonicalPath(left), CanonicalPath(right), PathComparison());

    public void ValidateDestination(string parentCheckout, string destination)
    {
        var parent = CanonicalPath(parentCheckout);
        var target = CanonicalPath(destination);
        RejectParentOrNested(parent, target);
        RejectSymlinkAncestors(destination);
        RejectRepositoryAncestors(destination);
        if (Directory.Exists(Path.Combine(destination, ".git")) || File.Exists(Path.Combine(destination, ".git")))
            throw new WorktreeBoundaryException("Destination already contains a repository or worktree.");
    }

    public async Task ValidateAssignedWorktreeAsync(string parentCheckout, string worktree, CancellationToken cancellationToken = default)
    {
        var parent = CanonicalPath(parentCheckout);
        var target = CanonicalPath(worktree);
        RejectParentOrNested(parent, target);
        RejectSymlinkAncestors(worktree);
        if (!Directory.Exists(worktree)) throw new WorktreeBoundaryException("Assigned worktree does not exist.");
        var observedRoot = CanonicalPath((await GitAsync(worktree, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Trim());
        if (!string.Equals(target, observedRoot, PathComparison()))
            throw new WorktreeBoundaryException("Assigned path is not the exact Git worktree root.");
    }

    public async Task PrepareAsync(string parentCheckout, string destination, string baseCommit, CancellationToken cancellationToken = default)
    {
        ValidateDestination(parentCheckout, destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new WorktreeBoundaryException("Worker destination must not contain existing files.");
        await GitAsync(parentCheckout, cancellationToken, "worktree", "add", "--detach", destination, baseCommit).ConfigureAwait(false);
        var actual = (await GitAsync(destination, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        if (actual != baseCommit) throw new WorktreeBoundaryException("Provisioned worktree does not match the frozen base commit.");
    }

    public WriterLease AcquireWriter(string worktree, string runId, string writerToken)
    {
        var path = WriterLockPath(worktree);
        try
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write($"{runId}\n{writerToken}\n");
            writer.Flush();
            stream.Flush(true);
            return new WriterLease(path, stream, runId, writerToken);
        }
        catch (IOException exception)
        {
            throw new WorktreeBoundaryException("A writer already owns this worktree.", exception);
        }
    }

    public WriterLease RecoverWriter(string worktree, string runId, string writerToken)
    {
        var path = WriterLockPath(worktree);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new StreamReader(stream, leaveOpen: true);
            var storedRunId = reader.ReadLine();
            var storedWriterToken = reader.ReadLine();
            if (storedRunId != runId || storedWriterToken != writerToken || reader.ReadLine() is not null)
                throw new WorktreeBoundaryException("Durable writer identity does not match the frozen request.");
            stream.Position = 0;
            return new WriterLease(path, stream, runId, writerToken);
        }
        catch (WorktreeBoundaryException)
        {
            stream?.Dispose();
            throw;
        }
        catch (IOException exception)
        {
            stream?.Dispose();
            throw new WorktreeBoundaryException("Durable writer ownership could not be recovered exclusively.", exception);
        }
    }

    public async Task<WorktreeObservation> ObserveAsync(string worktree, CancellationToken cancellationToken = default)
    {
        var root = CanonicalPath(worktree);
        var observedRoot = CanonicalPath((await GitAsync(worktree, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false)).Trim());
        if (!string.Equals(root, observedRoot, PathComparison()))
            throw new WorktreeBoundaryException("Observed path is not the exact assigned worktree root.");
        var statusText = await GitAsync(worktree, cancellationToken, "status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching").ConfigureAwait(false);
        var status = statusText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var head = (await GitAsync(worktree, cancellationToken, "rev-parse", "HEAD^{commit}").ConfigureAwait(false)).Trim();
        var tree = (await GitAsync(worktree, cancellationToken, "rev-parse", "HEAD^{tree}").ConfigureAwait(false)).Trim();
        return new WorktreeObservation(root, head, tree, status.Length == 0, status, CaptureSnapshot(worktree));
    }

    public async Task<CommittedRevision> CaptureCommittedRevisionAsync(string worktree, CancellationToken cancellationToken = default)
    {
        var observation = await ObserveAsync(worktree, cancellationToken).ConfigureAwait(false);
        if (!observation.IsClean)
            throw new WorktreeBoundaryException("LOCAL_COMPLETE requires a clean worktree with no untracked or ignored files.");
        return new CommittedRevision(observation.HeadCommit, observation.TreeId);
    }

    public async Task EnsureDescendsFromAsync(string worktree, string baseCommit, string headCommit, CancellationToken cancellationToken = default)
    {
        var result = await GitExitCodeAsync(worktree, cancellationToken, "merge-base", "--is-ancestor", baseCommit, headCommit).ConfigureAwait(false);
        if (result != 0) throw new WorktreeBoundaryException("Completed head is not a descendant of the frozen base commit.");
    }

    public FileSnapshot CaptureSnapshot(string worktree)
    {
        var root = Path.GetFullPath(worktree);
        RejectSymlinkTree(root);
        var nestedRepositories = Directory.EnumerateFileSystemEntries(root, ".git", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetDirectoryName(path), root, PathComparison()))
            .ToArray();
        if (nestedRepositories.Length > 0)
            throw new WorktreeBoundaryException($"Nested repositories are outside the worker boundary: {string.Join(", ", nestedRepositories)}.");
        var gitMetadata = Path.Combine(root, ".git");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !IsWithin(path, gitMetadata))
            .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                HashFile, StringComparer.Ordinal);
        if (File.Exists(gitMetadata))
            files[".git"] = HashFile(gitMetadata);
        return new FileSnapshot(files);
    }

    public void EnsureAllowedChanges(
        FileSnapshot before, FileSnapshot after, IEnumerable<string> allowedPaths, IEnumerable<string> exclusions)
    {
        var allowed = allowedPaths.Select(NormalizeScope).ToArray();
        var excluded = exclusions.Select(NormalizeScope).ToArray();
        var changed = before.Files.Keys.Union(after.Files.Keys, StringComparer.Ordinal)
            .Where(path => !before.Files.TryGetValue(path, out var oldHash) || !after.Files.TryGetValue(path, out var newHash) || oldHash != newHash)
            .ToArray();
        if (changed.Contains(".git", StringComparer.Ordinal))
            throw new WorktreeBoundaryException("Assigned worktree Git metadata changed during worker execution.");
        var excludedChanges = changed.Where(path => excluded.Any(scope => InScope(path, scope))).ToArray();
        if (excludedChanges.Length > 0)
            throw new WorktreeBoundaryException($"Excluded-path writes detected: {string.Join(", ", excludedChanges)}.");
        var outside = changed.Where(path => !allowed.Any(scope => InScope(path, scope))).ToArray();
        if (outside.Length > 0)
            throw new WorktreeBoundaryException($"Out-of-scope writes detected: {string.Join(", ", outside)}.");
    }

    private static async Task<string> GitAsync(string cwd, CancellationToken cancellationToken, params string[] arguments)
    {
        var (exitCode, stdout, stderr) = await RunGitAsync(cwd, cancellationToken, arguments).ConfigureAwait(false);
        if (exitCode != 0) throw new WorktreeBoundaryException($"Git failed: {stderr}");
        return stdout;
    }

    private static async Task<int> GitExitCodeAsync(string cwd, CancellationToken cancellationToken, params string[] arguments)
    {
        var (exitCode, _, _) = await RunGitAsync(cwd, cancellationToken, arguments).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string cwd, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment.Remove("GIT_DIR");
        start.Environment.Remove("GIT_WORK_TREE");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new WorktreeBoundaryException("Could not start Git.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void RejectParentOrNested(string parent, string target)
    {
        if (string.Equals(parent, target, PathComparison()) || IsWithin(target, parent))
            throw new WorktreeBoundaryException("Worker destination cannot be the parent checkout or nested inside it.");
    }

    private static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) return full;
        if (OperatingSystem.IsWindows()) return WindowsFinalPath(full);
        var info = new DirectoryInfo(full);
        return Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
    }

    private static string WriterLockPath(string worktree)
    {
        var marker = Path.Combine(Path.GetFullPath(worktree), ".git");
        string gitDirectory;
        if (Directory.Exists(marker))
        {
            gitDirectory = marker;
        }
        else if (File.Exists(marker))
        {
            var lines = File.ReadAllLines(marker);
            const string prefix = "gitdir:";
            if (lines.Length != 1 || !lines[0].StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(lines[0][prefix.Length..]))
                throw new WorktreeBoundaryException("Assigned worktree has invalid Git metadata.");
            gitDirectory = Path.GetFullPath(lines[0][prefix.Length..].Trim(), Path.GetDirectoryName(marker)!);
        }
        else
        {
            throw new WorktreeBoundaryException("Assigned worktree has no Git metadata.");
        }

        if (!Directory.Exists(gitDirectory))
            throw new WorktreeBoundaryException("Assigned worktree Git directory does not exist.");
        return Path.Combine(CanonicalPath(gitDirectory), "delivery-forge-writer.lock");
    }

    private static string WindowsFinalPath(string path)
    {
        const uint fileFlagBackupSemantics = 0x02000000;
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, fileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new WorktreeBoundaryException("Could not resolve the assigned Windows directory identity.",
                new Win32Exception(Marshal.GetLastWin32Error()));

        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                throw new WorktreeBoundaryException("Could not resolve the assigned Windows directory identity.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            if (length < buffer.Capacity)
            {
                var resolved = buffer.ToString();
                if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    resolved = @"\\" + resolved[8..];
                else if (resolved.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                    resolved = resolved[4..];
                return Path.GetFullPath(resolved);
            }

            capacity = checked((int)length + 1);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file, StringBuilder filePath, uint filePathLength, uint flags);

    private static void RejectSymlinkAncestors(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new WorktreeBoundaryException($"Symlink/reparse-point escape at '{current.FullName}'.");
    }

    private static void RejectRepositoryAncestors(string path)
    {
        var current = Directory.Exists(path) ? new DirectoryInfo(path) : new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
        for (; current is not null; current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                throw new WorktreeBoundaryException("Worker destination cannot be nested in another repository or worktree.");
        }
    }

    private static void RejectSymlinkTree(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new WorktreeBoundaryException($"Symlink/reparse-point content is outside the enforceable boundary: '{entry}'.");
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative);
    }

    private static string NormalizeScope(string value) => value.Replace('\\', '/').Trim('/');
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static bool InScope(string path, string scope) => path == scope || path.StartsWith(scope + "/", StringComparison.Ordinal);
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public sealed class WriterLease : IDisposable
{
    private readonly string _path;
    private readonly FileStream _stream;
    private bool _disposed;

    internal WriterLease(string path, FileStream stream, string runId, string writerToken)
    {
        _path = path;
        _stream = stream;
        RunId = runId;
        WriterToken = writerToken;
    }

    public string RunId { get; }
    public string WriterToken { get; }

    internal void Abandon()
    {
        Dispose();
        File.Delete(_path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}

public sealed class WorktreeBoundaryException : Exception
{
    public WorktreeBoundaryException(string message) : base(message) { }
    public WorktreeBoundaryException(string message, Exception inner) : base(message, inner) { }
}
