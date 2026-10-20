using DeliveryForge.Evidence;
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
    static readonly List<WriterLease> RetainedWriterBirths = [];
    private readonly string _gitExecutable;
    private readonly ProcessCommandExecutor? _originalExecutor;
    private readonly IOwnedDuplexBudget? _originalBudget;
    public WorktreeManager(string gitExecutablePath, ProcessCommandExecutor actualExecutor, IOwnedDuplexBudget actualOriginal) : this(gitExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(actualExecutor); ArgumentNullException.ThrowIfNull(actualOriginal);
        actualOriginal.CheckWork(); _originalExecutor = actualExecutor; _originalBudget = actualOriginal;
    }
    public WorktreeManager(string? gitExecutablePath = null)
    {
        if (gitExecutablePath is not null)
        {
            if (!Path.IsPathFullyQualified(gitExecutablePath) || !File.Exists(gitExecutablePath)) throw new WorktreeBoundaryException("Configured Git executable must be an existing absolute trusted path.");
            _gitExecutable = Path.GetFullPath(gitExecutablePath);
            return;
        }
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        _gitExecutable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists)
            ?? throw new WorktreeBoundaryException("No absolute Git executable was discovered on the trusted parent PATH.");
    }
    readonly DeliveryForge.Coordination.ICoordinatedEffectAdmission? _effectAdmission;
    readonly string? _effectRunId;
    // A new immutable view retains the SAME executor/original/counter. No DTO issues authority.
    // Provisioning and the existing explicitly owned cleanup observation view stay unchanged.
    public WorktreeManager WithParentAdmission(DeliveryForge.Coordination.ICoordinatedEffectAdmission actualAdmission, string actualRunId)
    {
        ArgumentNullException.ThrowIfNull(actualAdmission);
        if (_originalExecutor is null || _originalBudget is null || _effectAdmission is not null || string.IsNullOrWhiteSpace(actualRunId) || actualRunId.Length > 128)
            throw new WorktreeBoundaryException("One actual original Git owner/parent binding required; no rebind.");
        return new WorktreeManager(this, actualAdmission, actualRunId);
    }
    WorktreeManager(WorktreeManager source, DeliveryForge.Coordination.ICoordinatedEffectAdmission admission, string runId)
    {
        _gitExecutable = source._gitExecutable; _originalExecutor = source._originalExecutor; _originalBudget = source._originalBudget;
        actualOwnedGitInvocations = source.actualOwnedGitInvocations; _effectAdmission = admission; _effectRunId = runId;
    }
    sealed class InvocationCounter { internal long Value; }
    InvocationCounter actualOwnedGitInvocations = new();
    public long ActualOwnedGitInvocations => Interlocked.Read(ref actualOwnedGitInvocations.Value);
    // Read-only same-original stopped-checkpoint path; never provisioning,
    // writer acquisition, completion or writable Git under cleanup.
    WorktreeManager OwnedCleanupObservationView()
    {
        if (_originalBudget is null || _originalExecutor is null) return this; // Existing legacy fixture realm only.
        var view = new WorktreeManager(_gitExecutable, _originalExecutor, new OwnedCleanupPhaseBudget(_originalBudget)); view.actualOwnedGitInvocations = actualOwnedGitInvocations; return view;
    }
    public Task<WorktreeObservation> ObserveOwnedCleanupAsync(string worktree, CancellationToken token = default) => OwnedCleanupObservationView().ObserveAsync(worktree, token);
    public Task EnsureDescendsFromOwnedCleanupAsync(string worktree, string baseCommit, string head, CancellationToken token = default) => OwnedCleanupObservationView().EnsureDescendsFromAsync(worktree, baseCommit, head, token);

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

    public async Task ValidateUnprovisionedPathsAsync(string parentCheckout, string worktree, string resultDirectory, CancellationToken cancellationToken = default)
    {
        ValidateDestination(parentCheckout, worktree);
        RejectSymlinkAncestors(resultDirectory);
        RejectRepositoryAncestors(resultDirectory);
        RejectParentOrNested(CanonicalPath(parentCheckout), CanonicalPath(resultDirectory));
        if (Directory.Exists(worktree) || File.Exists(worktree) || Directory.Exists(resultDirectory) || File.Exists(resultDirectory))
            throw new WorktreeBoundaryException("Unstarted cleanup refuses existing or ambiguous worktree/result paths; their absence alone is not closure proof.");
        var registered = await GitAsync(parentCheckout, cancellationToken, "worktree", "list", "--porcelain", "-z").ConfigureAwait(false);
        var paths = registered.Split('\0').Where(item => item.StartsWith("worktree ", StringComparison.Ordinal)).Select(item => item[9..]).ToArray();
        if (paths.Length == 0 || paths.Any(path => !Path.IsPathFullyQualified(path)) ||
            paths.Any(path => string.Equals(CanonicalPath(path), CanonicalPath(worktree), PathComparison())))
            throw new WorktreeBoundaryException("Git worktree registration is missing/ambiguous or still names the supposedly unstarted destination.");
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
        if (_effectAdmission is null) return AcquireWriterCore(worktree, runId, writerToken);
        if (_effectAdmission is not DeliveryForge.Coordination.ICoordinatedSynchronousEffectAdmission parent || runId != _effectRunId)
            throw new WorktreeBoundaryException("Writer birth requires the same synchronous original parent.");
        _originalBudget!.CheckWork(); parent.CheckCurrent(runId);
        WriterLease? lease = null;
        try
        {
            parent.EnterSynchronous(runId, () => AcquireWriterCore(worktree, runId, writerToken, born => lease = born));
            _originalBudget.CheckWork(); parent.CheckCurrent(runId);
            return lease ?? throw new WorktreeBoundaryException("Actual writer birth returned no lease.");
        }
        catch (Exception primary)
        {
            if (lease is null) throw;
            try { _ = _originalBudget.CleanupRemaining; lease.Dispose(); _ = _originalBudget.CleanupRemaining; }
            catch (Exception close)
            {
                lock (RetainedWriterBirths) RetainedWriterBirths.Add(lease);
                if (_originalBudget is IOwnedBudgetUnknownRetention retained) retained.PreserveUnknown();
                throw new AggregateException("Original writer birth and first disposition failed.", primary, close);
            }
            // Preserve its durable lock as failure evidence; no revoked delete.
            throw;
        }
    }
    WriterLease AcquireWriterCore(string worktree, string runId, string writerToken, Action<WriterLease>? captureBirth = null)
    {
        var path = WriterLockPath(worktree);
        try
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var lease = new WriterLease(path, stream, runId, writerToken);
            captureBirth?.Invoke(lease);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write($"{runId}\n{writerToken}\n");
            writer.Flush();
            stream.Flush(true);
            return lease;
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

    // Low-level cleanup mechanics only. This never authorizes deletion, dispatch or takeover;
    // callers still need current ownership, Git registration, quiescence and path proof.
    public WriterLease RecoverWriterWithDeleteSharing(string worktree, string runId, string writerToken)
    {
        FileStream? stream = null;
        try
        {
            RejectSymlinkAncestors(worktree);
            var metadata = Path.Combine(Path.GetFullPath(worktree), ".git");
            RejectSymlinkAncestors(metadata);
            if ((File.GetAttributes(metadata) & FileAttributes.ReparsePoint) != 0)
                throw new WorktreeBoundaryException("Cleanup writer refuses redirected Git metadata.");
            var path = WriterLockPath(worktree, requirePlainMetadata: true);
            RejectSymlinkAncestors(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new WorktreeBoundaryException("Cleanup writer refuses a redirected lock.");
            // Ordinary writer recovery stays FileShare.None. This separate low-level
            // handle permits unlink mechanics but gives no path/deletion authority.
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            LinuxCleanupWriterFence.RequireExclusive(stream.SafeFileHandle);
            if (stream.Length > 8192) throw new WorktreeBoundaryException("Cleanup writer identity exceeds the finite diagnostic bound.");
            using var reader = new StreamReader(stream, leaveOpen: true);
            if (reader.ReadLine() != runId || reader.ReadLine() != writerToken || reader.ReadLine() is not null)
                throw new WorktreeBoundaryException("Durable writer identity does not match the frozen request.");
            stream.Position = 0;
            return new WriterLease(path, stream, runId, writerToken);
        }
        catch (WorktreeBoundaryException) { stream?.Dispose(); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            throw new WorktreeBoundaryException("Cleanup writer could not be recovered exclusively.", exception);
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

    private async Task<string> GitAsync(string cwd, CancellationToken cancellationToken, params string[] arguments)
    {
        var (exitCode, stdout, stderr) = await RunGitAsync(cwd, cancellationToken, arguments).ConfigureAwait(false);
        if (exitCode != 0) throw new WorktreeBoundaryException($"Git failed: {stderr}");
        return stdout;
    }

    private async Task<int> GitExitCodeAsync(string cwd, CancellationToken cancellationToken, params string[] arguments)
    {
        var (exitCode, _, _) = await RunGitAsync(cwd, cancellationToken, arguments).ConfigureAwait(false);
        return exitCode;
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string cwd, CancellationToken cancellationToken, params string[] arguments) =>
        RunGitCoreAsync(cwd, cancellationToken, disableFilters: true, arguments);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunGitCoreAsync(
        string cwd, CancellationToken cancellationToken, bool disableFilters, params string[] arguments)
    {
        var filterNames = new HashSet<string>(StringComparer.Ordinal);
        if (disableFilters)
        {
            var configuration = await RunGitCoreAsync(cwd, cancellationToken, false, "config", "--includes", "--name-only", "--get-regexp", "^filter\\.").ConfigureAwait(false);
            if (configuration.ExitCode is not (0 or 1) || configuration.Stdout.Length > 65536)
                throw new WorktreeBoundaryException("Effective Git filters could not be inspected within the finite boundary.");
            foreach (var key in configuration.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(key => key.TrimEnd('\r')))
            {
                var separator = key.LastIndexOf('.');
                if (!key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) || separator <= 7 || key.Any(char.IsControl))
                    throw new WorktreeBoundaryException("Git filter configuration keys are ambiguous.");
                var name = key[7..separator];
                if (name.Length > 256 || name.Contains('=')) throw new WorktreeBoundaryException("Git filter name is unsupported.");
                filterNames.Add(name);
                if (filterNames.Count > 128) throw new WorktreeBoundaryException("Git filters exceed the finite configuration bound.");
            }
        }
        var start = new ProcessStartInfo(_gitExecutable)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_CONFIG", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT" }) start.Environment.Remove(name);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal) || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(name);
        var disabledHooks = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_GRAFT_FILE"] = disabledHooks;
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=" + disabledHooks);
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.fsmonitor=false");
        // Checkout/status use literal repository bytes, never external content-filter processes.
        foreach (var filter in filterNames)
        {
            foreach (var setting in new[] { "process=", "smudge=", "clean=", "required=false" })
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("filter." + filter + "." + setting);
            }
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (_originalExecutor is not null)
        {
            var actualBudget = _originalBudget ?? throw new WorktreeBoundaryException("Actual original Git budget is absent.");
            var actualExecutor = _originalExecutor;
            actualBudget.CheckWork();
            var env = start.Environment.ToDictionary(x => x.Key, x => x.Value ?? "", OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            Task<BoundedCommandCaptureResult> Birth()
            {
                actualBudget.CheckWork();
                Interlocked.Increment(ref actualOwnedGitInvocations.Value);
                return actualExecutor.ExecuteBoundedCaptureAsync(new(_gitExecutable, start.ArgumentList.ToArray(), cwd, env) { EnvironmentIsComplete = true }, TimeSpan.FromSeconds(30), 16 * 1024 * 1024, cancellationToken, new OwnedWorkPhaseBudget(actualBudget));
            }
            var actual = await (_effectAdmission is null ? Birth() : _effectAdmission.EnterAsync(_effectRunId!, Birth)).ConfigureAwait(false);
            actualBudget.CheckWork(); var command = actual.Command; var facts = command.LifecycleDiagnostics;
            if (actual.CaptureBoundExceeded || command.TimedOut || command.Cancelled || !command.OwnedProcessQuiescent || !actual.RootExitObserved || !actual.StandardOutputDrainCompleted || !actual.StandardErrorDrainCompleted || facts?.NativeFailure is not null || facts?.OwnedTree is not { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 } || OperatingSystem.IsLinux() && actual.OwnershipKind != BoundedCaptureOwnershipKind.LinuxCreationOwnedScope || command.ExitCode is null)
                throw new WorktreeBoundaryException("Actual original Git capture/group/stream closure is incomplete or failed.");
            return (command.ExitCode.Value, Encoding.UTF8.GetString(actual.StandardOutputBytes), Encoding.UTF8.GetString(actual.StandardErrorBytes));
        }
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

    private static string WriterLockPath(string worktree, bool requirePlainMetadata = false)
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

        if (requirePlainMetadata) RejectSymlinkAncestors(gitDirectory);
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

// Observation data only. No operation accepts this result as ownership or deletion authority.
public enum WriterNamespaceIdentityOutcome { Match, Changed, Unknown, Unsupported }
public sealed record WriterNamespaceIdentityObservation(WriterNamespaceIdentityOutcome Outcome, string Reason);

public sealed class WriterLease : IDisposable
{
    private readonly string _path;
    private readonly FileStream _stream;
    private readonly object _lifetime = new();
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
    public string DiagnosticLockPath => _path;
    public WriterNamespaceIdentityObservation ObserveCurrentNamespaceIdentity()
    {
        lock (_lifetime)
        {
            return _disposed ? new(WriterNamespaceIdentityOutcome.Unknown, "Owner lease is disposed; no native identity proof.")
                : WriterNamespaceIdentityReader.Observe(_stream.SafeFileHandle, _path);
        }
    }
    public IReadOnlyList<byte> GetDiagnosticBytes()
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream.Length > 8192) throw new WorktreeBoundaryException("Writer diagnostic bytes exceed the finite bound.");
            var bytes = new byte[checked((int)_stream.Length)];
            _stream.Position = 0;
            _stream.ReadExactly(bytes);
            _stream.Position = 0;
            return Array.AsReadOnly(bytes); // The caller never receives the mutable owner-held array or stream.
        }
    }

    internal void Abandon()
    {
        lock (_lifetime)
        {
            Dispose();
            File.Delete(_path);
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
        }
    }
}

public sealed class WorktreeBoundaryException : Exception
{
    public WorktreeBoundaryException(string message) : base(message) { }
    public WorktreeBoundaryException(string message, Exception inner) : base(message, inner) { }
}
