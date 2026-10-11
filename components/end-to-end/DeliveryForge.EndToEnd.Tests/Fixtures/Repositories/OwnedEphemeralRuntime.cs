using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;

namespace DeliveryForge.EndToEnd.Tests;

// Live fixture ownership is this reference/private nonce; archived JSON never recreates it.
public sealed class OwnedEphemeralRuntime : IAsyncDisposable
{
    private readonly OwnedScenarioRun _artifacts;
    private readonly string _git;
    private readonly byte[] _markerBytes;
    private int _commandSequence;
    private bool _removed;
    private bool _nativeUnknown;
    private string? _writerRunId;
    private string? _writerToken;
    private ExecutionCoordinator? _execution;
    private NativeFixtureControl? _control;
    private AgentRunBinding? _binding;
    private HashSet<string> _ownedEntries = new(StringComparer.Ordinal);
    private OwnedEphemeralRuntime(OwnedScenarioRun artifacts, string git)
    {
        _artifacts = artifacts; _git = git;
        Prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "delivery-forge-e2e"));
        RuntimeId = Guid.NewGuid().ToString("N");
        Root = Path.Combine(Prefix, RuntimeId);
        Repository = Path.Combine(Root, "repository");
        Worktree = Path.Combine(Root, "worktree");
        Results = Path.Combine(Root, "results");
        Store = new RunStore(Path.Combine(Root, "receipts"));
        _markerBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            fixture = true, Root, RuntimeId, artifactRunId = artifacts.RunId, nonce = Guid.NewGuid().ToString("N"),
            integratedBaseCommit = artifacts.IntegratedSource.Commit
        });
    }
    public string Prefix { get; }
    public string Root { get; }
    public string RuntimeId { get; }
    public string Repository { get; }
    public string Worktree { get; }
    public string Results { get; }
    public string MarkerPath => Path.Combine(Root, "owner.json");
    public string ExpectedMarkerSha256 => Convert.ToHexStringLower(SHA256.HashData(_markerBytes));
    public RunStore Store { get; }
    public ProcessControl Processes { get; } = new();
    // Explicit live fixture instrumentation; never imported from stored observations.
    public Func<CancellationToken, Task>? FixtureArchiveEntryBarrier { get; set; }
    public GitRepositoryContextReader Reader => new(_git);
    public WorktreeManager Worktrees => new(_git);
    internal bool BelongsTo(OwnedScenarioRun artifacts) => ReferenceEquals(_artifacts, artifacts);

    public static async Task<OwnedEphemeralRuntime> CreateAsync(OwnedScenarioRun artifacts, CancellationToken cancellationToken)
    {
        var runtime = new OwnedEphemeralRuntime(artifacts, FindExecutable(OperatingSystem.IsWindows() ? "git.exe" : "git"));
        RequirePlainOutsideRepositories(runtime.Root);
        if (Path.GetRelativePath(runtime.Prefix, runtime.Root) != runtime.RuntimeId)
            throw new IOException("Runtime escaped its exact fresh GUID prefix.");
        if (Directory.Exists(runtime.Root) || File.Exists(runtime.Root)) throw new IOException("Fresh runtime GUID leaf collided.");
        Directory.CreateDirectory(runtime.Root);
        if (Directory.EnumerateFileSystemEntries(runtime.Root).Any()) throw new IOException("Runtime leaf was not newly empty.");
        await using (var marker = new FileStream(runtime.MarkerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await marker.WriteAsync(runtime._markerBytes, cancellationToken);
            await marker.FlushAsync(cancellationToken);
            marker.Flush(true);
        }
        Directory.CreateDirectory(runtime.Repository);
        await runtime.GitAsync(cancellationToken, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(runtime.Repository, "subject.txt"), "owned native fixture\n", cancellationToken);
        await runtime.GitAsync(cancellationToken, "add", "--", "subject.txt");
        await runtime.GitAsync(cancellationToken, "-c", "user.name=EndToEndFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "owned runtime base");
        await artifacts.WriteNewJsonAsync("ephemeral-runtime.json", new
        {
            fixture = true, productionCertified = false, runtime.Root, runtime.RuntimeId, runtime.Repository, runtime.Worktree,
            expectedMarkerSha256 = runtime.ExpectedMarkerSha256, integratedBaseCommit = artifacts.IntegratedSource.Commit,
            limitation = "Owned platform Temp live runtime only; durable artifacts stay in source-owned ignored leaf. Cleanup needs fresh ownership/quiescence proof."
        }, cancellationToken);
        runtime.CaptureOwnedManifest();
        return runtime;
    }

    public async Task<string> GitAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var gitDirectory = Path.Combine(Repository, ".git");
        Directory.CreateDirectory(Path.Combine(gitDirectory, "objects"));
        var environment = new Dictionary<string, string>
        {
            ["GIT_DIR"] = gitDirectory, ["GIT_WORK_TREE"] = Repository, ["GIT_COMMON_DIR"] = gitDirectory,
            ["GIT_OBJECT_DIRECTORY"] = Path.Combine(gitDirectory, "objects"), ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "",
            ["GIT_INDEX_FILE"] = Path.Combine(gitDirectory, "index"), ["GIT_CONFIG"] = Path.Combine(gitDirectory, "config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_SYSTEM"] = nullDevice, ["GIT_CONFIG_GLOBAL"] = nullDevice,
            ["GIT_CONFIG_PARAMETERS"] = "", ["GIT_CONFIG_COUNT"] = "0", ["GIT_NO_REPLACE_OBJECTS"] = "1",
            ["GIT_GRAFT_FILE"] = nullDevice, ["GIT_NO_LAZY_FETCH"] = "1", ["GIT_TERMINAL_PROMPT"] = "0"
        };
        var result = await new ProcessCommandExecutor().ExecuteAsync(new(_git,
            ["-c", "core.hooksPath=" + nullDevice, "-c", "core.fsmonitor=false", .. arguments], Repository, environment),
            TimeSpan.FromSeconds(15), cancellationToken);
        await _artifacts.WriteNewJsonAsync($"runtime-command-{++_commandSequence:D4}.json", new { fixture = true, Root, arguments, result }, CancellationToken.None);
        if (result.ExitCode != 0 || result.TimedOut || result.Cancelled || !result.OwnedProcessQuiescent)
        {
            _nativeUnknown |= !result.OwnedProcessQuiescent;
            throw new InvalidOperationException("Owned runtime Git failed or remained uncertain: " + result.StandardError);
        }
        return result.StandardOutput.Trim();
    }

    public OwnedProcess StartBoundedChild()
    {
        var executable = OperatingSystem.IsWindows() ? FindWindowsPowerShell() : FindExecutable("sleep");
        var arguments = OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" } : new[] { "30" };
        return Processes.StartOwned(new(executable, arguments), RuntimeId);
    }

    public void RecordWriterLease(WriterLease writer)
    {
        _writerRunId = writer.RunId; _writerToken = writer.WriterToken;
        CaptureOwnedManifest();
    }
    internal void BindSession(ExecutionCoordinator execution, NativeFixtureControl control, AgentRunBinding binding, AgentRequest request)
    {
        if (_binding is not null || binding.RunId != request.RunId || binding.RequestIdentity != request.RequestIdentity ||
            request.Worktree != Worktree || request.RequestIdentity != WorkerEnvelope.ComputeRequestIdentity(request) ||
            execution.StoreRootPath != Store.RootPath) throw new InvalidOperationException("Native fixture session binding differs.");
        _execution = execution; _control = control; _binding = binding;
        _writerRunId = request.RunId; _writerToken = request.WriterToken;
        CaptureOwnedManifest();
    }

    public async Task<bool> TryCleanupAsync(CancellationToken cancellationToken = default)
    {
        if (_removed) return true;
        try
        {
            RequirePlainOutsideRepositories(Root);
            if (Path.GetRelativePath(Prefix, Path.GetFullPath(Root)) != RuntimeId || _nativeUnknown ||
                !File.Exists(MarkerPath) || !File.ReadAllBytes(MarkerPath).SequenceEqual(_markerBytes)) return false;
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "owner.json", "repository", "worktree", "results", "receipts" };
            if (Directory.EnumerateFileSystemEntries(Root).Any(path => !allowed.Contains(Path.GetFileName(path)))) return false;
            var pending = new Stack<string>(); pending.Push(Root);
            while (pending.TryPop(out var directory))
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                    if (Directory.Exists(path)) pending.Push(path);
                }
            var allowedEntries = new HashSet<string>(_ownedEntries, StringComparer.Ordinal);
            foreach (var runId in await Store.DiscoverRunIdsAsync(cancellationToken))
            {
                var history = await Store.RecoverAsync(runId, cancellationToken);
                foreach (var item in history.Records)
                {
                    AddWithParents(allowedEntries, item.Path);
                    AddWithParents(allowedEntries, Path.Combine(Path.GetDirectoryName(item.Path)!, "current.json"));
                }
            }
            if (Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
                .Any(path => !allowedEntries.Contains(Path.GetRelativePath(Root, path)))) return false;
            var identities = Processes.GetOwnedIdentities(RuntimeId).Concat(_binding is null ? [] : Processes.GetOwnedIdentities(_binding.RunId)).Distinct().ToArray();
            if (identities.Any(identity => Processes.Observe(identity).Outcome != ProcessControlOutcome.AlreadyExited)) return false;
            if (_binding is not null)
            {
                if (_execution is null || _control is null || _execution.HasPendingControlOperations(_binding.RunId)) return false;
                var observed = await _control.ControlAsync(_binding, AgentControlAction.Stop, TimeSpan.FromSeconds(2), cancellationToken);
                if (observed.Binding != _binding || !observed.CapabilitySupported || observed.Activity != AgentActivityState.Quiescent ||
                    observed.Action != AgentControlAction.Stop || string.IsNullOrWhiteSpace(observed.EvidenceReference)) return false;
            }
            else if ((await Store.DiscoverRunIdsAsync(cancellationToken)).Count != 0) return false;
            var registered = (await GitAsync(cancellationToken, "worktree", "list", "--porcelain", "-z")).Split('\0')
                .Where(item => item.StartsWith("worktree ", StringComparison.Ordinal)).Select(item => Path.GetFullPath(item[9..])).ToArray();
            var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (registered.Length is < 1 or > 2 || registered.Any(path => !comparison.Equals(path, Repository) && !comparison.Equals(path, Worktree))) return false;
            var repository = await Reader.ReadAsync(Repository, "refs/heads/main", cancellationToken);
            if (repository.Dirty || repository.Shallow) return false;
            if (Directory.Exists(Worktree))
            {
                if (!registered.Contains(Worktree, comparison) || _writerRunId is null || _writerToken is null) return false;
                var observation = await Worktrees.ObserveAsync(Worktree, cancellationToken);
                if (!observation.IsClean) return false;
                using var exclusive = Worktrees.RecoverWriter(Worktree, _writerRunId, _writerToken);
            }
            else if (registered.Contains(Worktree, comparison)) return false;
            await ArchiveAsync(cancellationToken);
            if (!File.ReadAllBytes(MarkerPath).SequenceEqual(_markerBytes) || identities.Any(identity => Processes.Observe(identity).Outcome != ProcessControlOutcome.AlreadyExited)) return false;
            if (Directory.Exists(Worktree)) await GitAsync(cancellationToken, "worktree", "remove", "--force", Worktree);
            var finalRegistered = await GitAsync(cancellationToken, "worktree", "list", "--porcelain", "-z");
            if (finalRegistered.Replace('\\', '/').Contains(Worktree.Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            RequirePlainOutsideRepositories(Root);
            if (!File.ReadAllBytes(MarkerPath).SequenceEqual(_markerBytes)) return false;
            if (Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
                .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    !allowedEntries.Contains(Path.GetRelativePath(Root, path)))) return false;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(Root, recursive: true);
            _removed = true;
            await _artifacts.WriteNewJsonAsync("runtime-cleanup.json", new { fixture = true, Root, RuntimeId, actualOwnedQuiescence = true, gitRegistrationRemoved = true, archivedBeforeDeletion = true }, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WorktreeBoundaryException or InvalidOperationException)
        {
            _artifacts.RetainArtifacts("Runtime cleanup remains uncertain: " + exception.Message);
            return false;
        }
    }

    private void CaptureOwnedManifest()
    {
        _ownedEntries = Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path)).ToHashSet(StringComparer.Ordinal);
    }
    private void AddWithParents(HashSet<string> allowed, string path)
    {
        for (var current = Path.GetFullPath(path); current != Root; current = Path.GetDirectoryName(current)!)
        {
            var relative = Path.GetRelativePath(Root, current);
            if (Path.IsPathFullyQualified(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                throw new IOException("Owned store record escaped its runtime root.");
            allowed.Add(relative);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_removed) { await Processes.DisposeAsync(); return; }
        _artifacts.RetainArtifacts("Native runtime retained after nonpass or unresolved cleanup: " + Root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var observations = new List<object>();
        foreach (var identity in Processes.GetOwnedIdentities(RuntimeId).Concat(_binding is null ? [] : Processes.GetOwnedIdentities(_binding.RunId)).Distinct())
        {
            var before = Processes.Observe(identity);
            ProcessControlResult? controlled = null;
            if (before.Outcome == ProcessControlOutcome.LiveOwned)
                controlled = await Processes.QuiesceAsync(identity, TimeSpan.FromSeconds(1), deadline.Token);
            observations.Add(new { identity, before, controlled, after = Processes.Observe(identity) });
        }
        await Processes.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8), deadline.Token);
        await _artifacts.WriteNewJsonAsync("retained-runtime-" + Guid.NewGuid().ToString("N") + ".json", new
        {
            fixture = true, Root, RuntimeId, observations, expectedMarkerSha256 = ExpectedMarkerSha256,
            actualMarkerSha256 = File.Exists(MarkerPath) ? Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(MarkerPath, deadline.Token))) : null,
            retained = true, limitation = "Unresolved or nonpassing live runtime retained; archived data does not grant cleanup authority."
        }, CancellationToken.None);
        await ArchiveAsync(deadline.Token);
    }

    private async Task ArchiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FixtureArchiveEntryBarrier is { } barrier) await barrier(cancellationToken);
        var archive = Path.Combine(_artifacts.Root, "runtime-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        var stack = new Stack<string>(); stack.Push(Root);
        var count = 0; long bytes = 0;
        while (stack.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue; // Retain in place; never follow a foreign link.
                if (Directory.Exists(entry)) { stack.Push(entry); continue; }
                if (++count > 512 || (bytes += new FileInfo(entry).Length) > 16 * 1024 * 1024)
                    throw new IOException("Runtime archive exceeded its finite fixture bound; live leaf retained.");
                var target = Path.Combine(archive, Path.GetRelativePath(Root, entry));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry, target, overwrite: false);
            }
        await _artifacts.WriteNewJsonAsync("runtime-archive-index-" + Guid.NewGuid().ToString("N") + ".json",
            new { fixture = true, Root, archive, count, bytes }, cancellationToken);
    }

    private static void RequirePlainOutsideRepositories(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Runtime crosses a reparse ancestor.");
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                throw new IOException("Runtime must stay outside repository ancestors.");
        }
    }
    private static string FindExecutable(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException("Actual fixture executable unavailable: " + name);
    private static string FindWindowsPowerShell()
    {
        try { return FindExecutable("pwsh.exe"); }
        catch (FileNotFoundException)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(path) ? path : throw new FileNotFoundException("Native fixture PowerShell is unavailable.");
        }
    }
}
