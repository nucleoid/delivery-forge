using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Planning;

namespace DeliveryForge.EndToEnd.Tests;

// Real file/Git/process boundaries. This harness issues no worker, reviewer, lease or publication authority.
public sealed class OwnedScenarioRun : IAsyncDisposable
{
    public const string InitialIntegratedBase = "8c2db2b2041f458de92f1f39b31aa1c244ad1083";
    private readonly string _ownershipToken = Guid.NewGuid().ToString("N");
    private readonly string _git;
    private int _commandSequence;
    private bool _commandsQuiescent = true;
    private bool _assertionsPassed;
    private string? _retentionReason;
    private MiddleCrashLifetime? _middle;
    internal MiddleCrashLifetime MiddleLifetime => _middle ?? throw new InvalidOperationException("Original middle lifetime is missing.");

    private OwnedScenarioRun(string sourceRoot, string git)
    {
        SourceRoot = sourceRoot;
        _git = git;
        ArtifactsRoot = Path.Combine(sourceRoot, "components", "end-to-end", "artifacts", "test-runs");
        RunId = "e2e-" + Guid.NewGuid().ToString("N");
        Root = Path.Combine(ArtifactsRoot, RunId);
        Checkout = Path.Combine(Root, "repository");
        Store = new RunStore(Path.Combine(Root, "receipts"));
    }

    public string SourceRoot { get; }
    public string ArtifactsRoot { get; }
    public string RunId { get; }
    public string Root { get; }
    public string Checkout { get; }
    public RunStore Store { get; }
    public ProcessControl Processes { get; } = new();
    public RepositoryContext IntegratedSource { get; private set; } = null!;
    public GitRepositoryContextReader Reader => new(_git);

    public static async Task<OwnedScenarioRun> CreateAsync(CancellationToken cancellationToken)
        => await CreateCoreAsync(null, cancellationToken);

    internal static async Task<OwnedScenarioRun> CreateMiddleAsync(long originalStarted, int total, CancellationToken cancellationToken)
    {
        var lifetime = new MiddleCrashLifetime(originalStarted, total);
        return await lifetime.WorkAsync(current => CreateCoreAsync(lifetime, current), cancellationToken);
    }

    private static async Task<OwnedScenarioRun> CreateCoreAsync(MiddleCrashLifetime? lifetime, CancellationToken cancellationToken)
    {
        var sourceRoot = FindSourceRoot();
        var run = new OwnedScenarioRun(sourceRoot, FindExecutable(OperatingSystem.IsWindows() ? "git.exe" : "git"));
        run._middle = lifetime;
        lifetime?.Retain(run);
        lifetime?.Retain(run.Processes);
        try
        {
            RequirePlainAncestors(run.Root);
            if (Directory.Exists(run.Root) || File.Exists(run.Root)) throw new IOException("Owned run leaf already exists.");
            Directory.CreateDirectory(run.Checkout);
            await run.WriteNewJsonAsync("owner.json", new { run.RunId, run.Root, token = run._ownershipToken }, cancellationToken);
            // Read the real integrated object and current implementation HEAD through the public exact Git reader.
            run.IntegratedSource = await run.Reader.ReadAsync(sourceRoot, InitialIntegratedBase, cancellationToken);
            await run.WriteNewJsonAsync("source.json", new
            {
                integratedBaseCommit = run.IntegratedSource.Commit,
                integratedBaseTree = run.IntegratedSource.Tree,
                implementationHead = run.IntegratedSource.HeadCommit,
                implementationTree = run.IntegratedSource.HeadTree,
                fixture = true,
                productionCertified = false
            }, cancellationToken);
            await run.GitAsync(cancellationToken, "init", "-q", "-b", "main");
            await File.WriteAllTextAsync(Path.Combine(run.Checkout, "subject.txt"), "committed fixture\n", cancellationToken);
            await run.GitAsync(cancellationToken, "add", "--", "subject.txt");
            await run.GitAsync(cancellationToken, "-c", "user.name=EndToEndFixture", "-c", "user.email=fixture@example.invalid",
                "commit", "-q", "-m", "initial fixture");
            return run;
        }
        catch (Exception primary) when (lifetime is not null)
        {
            lifetime.PrimaryFailure(primary);
            try { await run.DisposeMiddleAsync(); }
            catch (Exception secondary) { lifetime.Retain(secondary); primary.Data["MiddleSetupCleanupFailure"] = secondary; }
            throw;
        }
    }

    public async Task<string> GitAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var gitDirectory = Path.Combine(Checkout, ".git");
        Directory.CreateDirectory(Path.Combine(gitDirectory, "objects"));
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var environment = new Dictionary<string, string>
        {
            ["GIT_DIR"] = gitDirectory,
            ["GIT_WORK_TREE"] = Checkout,
            ["GIT_COMMON_DIR"] = gitDirectory,
            ["GIT_OBJECT_DIRECTORY"] = Path.Combine(gitDirectory, "objects"),
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "",
            ["GIT_INDEX_FILE"] = Path.Combine(gitDirectory, "index"),
            ["GIT_CONFIG_COUNT"] = "0",
            ["GIT_CONFIG_PARAMETERS"] = "",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_SYSTEM"] = nullDevice,
            ["GIT_CONFIG_GLOBAL"] = nullDevice,
            ["GIT_CONFIG"] = Path.Combine(gitDirectory, "config"),
            ["GIT_NO_REPLACE_OBJECTS"] = "1",
            ["GIT_GRAFT_FILE"] = nullDevice,
            ["GIT_NO_LAZY_FETCH"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0"
        };
        CommandResult result;
        try
        {
            var executor = new ProcessCommandExecutor();
            _middle?.Retain(executor);
            var timeout = TimeSpan.FromSeconds(15);
            if (_middle is not null) { var left = _middle.Remaining(_middle.Cleaning); if (left < timeout) timeout = left; }
            var actual = executor.ExecuteAsync(new(_git,
                ["-c", "core.hooksPath=" + nullDevice, "-c", "core.fsmonitor=false", .. arguments], Checkout, environment),
                timeout, cancellationToken);
            _middle?.Track(actual);
            result = _middle is null ? await actual : await actual.WaitAsync(timeout, cancellationToken);
            if (_middle is not null) _ = _middle.Remaining(_middle.Cleaning);
        }
        catch { _commandsQuiescent = false; throw; }
        _commandsQuiescent &= result.OwnedProcessQuiescent;
        await WriteNewJsonAsync($"command-{++_commandSequence:D4}.json", new { executable = _git, arguments, result }, CancellationToken.None);
        if (result.ExitCode != 0 || result.TimedOut || result.Cancelled || !result.OwnedProcessQuiescent)
            throw new InvalidOperationException($"Owned Git command failed or remained uncertain; retained artifacts: {Root}\n{result.StandardError}");
        return result.StandardOutput.Trim();
    }

    public OwnedProcess StartBoundedChild()
        => Processes.StartOwned(BoundedChildLaunch(), RunId);

    public async Task<CommandResult> ExecuteBoundedChildCommandAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var launch = BoundedChildLaunch();
        CommandResult result;
        try
        {
            result = await new ProcessCommandExecutor().ExecuteAsync(new(launch.Executable, launch.Arguments, Root), timeout, cancellationToken);
        }
        catch { _commandsQuiescent = false; throw; }
        _commandsQuiescent &= result.OwnedProcessQuiescent;
        await WriteNewJsonAsync($"command-{++_commandSequence:D4}.json", new { launch, result }, CancellationToken.None);
        return result;
    }

    private static ProcessLaunch BoundedChildLaunch()
    {
        ProcessLaunch launch;
        if (OperatingSystem.IsWindows())
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
            if (!File.Exists(executable))
                throw new FileNotFoundException("Bounded native fixture ping host is unavailable.", executable);
            launch = new(executable, ["-n", "30", "127.0.0.1"]);
        }
        else if (OperatingSystem.IsLinux())
        {
            launch = new(FindExecutable("sleep"), ["30"]);
        }
        else throw new PlatformNotSupportedException("The owned process fixture supports Windows and Linux only.");
        return launch;
    }

    public void MarkAssertionsPassed() => _assertionsPassed = true;
    public void RetainArtifacts(string reason) => _retentionReason = string.IsNullOrWhiteSpace(reason) ? "Non-pass evidence retained." : reason;

    public async Task WriteNewJsonAsync<T>(string name, T value, CancellationToken cancellationToken)
    {
        if (Path.GetFileName(name) != name) throw new ArgumentException("Artifact names must be a single owned path segment.", nameof(name));
        if (_middle is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            if (bytes.Length > 1024 * 1024) throw new IOException("Middle diagnostic exceeds one MiB.");
            var left = _middle.Remaining(_middle.Cleaning);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); bound.CancelAfter(left);
            var middleStream = new FileStream(Path.Combine(Root, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _middle.Retain(middleStream);
            var actual = WriteMiddleAsync(middleStream, bytes, bound.Token); _middle.Track(actual);
            await actual.WaitAsync(left, bound.Token);
            _ = _middle.Remaining(_middle.Cleaning);
            return;
        }
        await using var stream = new FileStream(Path.Combine(Root, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken);
    }

    private static async Task WriteMiddleAsync(FileStream stream, byte[] bytes, CancellationToken token)
    {
        await using (stream) { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_middle is not null) { await DisposeMiddleAsync(); return; }
        var quiescent = _commandsQuiescent;
        var cleanupObservations = new List<object>();
        foreach (var identity in Processes.GetOwnedIdentities(RunId))
        {
            try
            {
                var observation = Processes.Observe(identity);
                ProcessControlResult? controlled = null;
                if (observation.Outcome == ProcessControlOutcome.LiveOwned)
                    controlled = await Processes.QuiesceAsync(identity, TimeSpan.FromSeconds(2), CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
                var settled = Processes.Observe(identity);
                cleanupObservations.Add(new { identity, initial = observation, controlled, settled });
                quiescent &= settled.Outcome == ProcessControlOutcome.AlreadyExited;
            }
            catch (Exception exception)
            {
                cleanupObservations.Add(new { identity, failureType = exception.GetType().FullName, exception.Message });
                quiescent = false;
            }
        }
        try { await Processes.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None); }
        catch (Exception) { quiescent = false; }

        var owned = OwnsPlainLeaf();
        if (_assertionsPassed && quiescent && owned && _retentionReason is null)
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(Root, recursive: true);
            return;
        }
        if (Directory.Exists(Root) && owned)
            await WriteNewJsonAsync("retained-" + Guid.NewGuid().ToString("N") + ".json",
                new { assertionsPassed = _assertionsPassed, quiescent, fixture = true, cleanupObservations, reason = _retentionReason ?? "Failure, timeout or ambiguous cleanup retains owned artifacts." }, CancellationToken.None);
        if (_assertionsPassed && (!quiescent || !owned))
            throw new InvalidOperationException("Cleanup was not positively owned and quiescent; retained run: " + Root);
    }

    private async Task DisposeMiddleAsync()
    {
        var lifetime = MiddleLifetime;
        lifetime.BeginCleanup();
        var failures = new List<Exception>();
        var observations = new List<object>();
        var known = _commandsQuiescent;
        // Only original controller members. Never adopt a journal identity or an imported PID.
        foreach (var identity in Processes.GetAllOwnedIdentities())
        {
            var stages = await lifetime.StagesAsync(("original-native:" + identity.ProcessId, Processes, async current =>
            {
                var first = Processes.Observe(identity);
                ProcessControlResult? controlled = null;
                if (first.Outcome == ProcessControlOutcome.LiveOwned)
                {
                    var remaining = lifetime.Remaining(true);
                    // Public quiescence includes its own five-second force-exit bound.
                    if (remaining <= TimeSpan.FromSeconds(7)) throw new TimeoutException("Original reserve cannot contain public native quiescence.");
                    controlled = await Processes.QuiesceAsync(identity, TimeSpan.FromSeconds(2), current);
                }
                var fresh = Processes.Observe(identity);
                observations.Add(new { identity, first, controlled, fresh });
                if (fresh.Outcome != ProcessControlOutcome.AlreadyExited) throw new InvalidOperationException("Original middle handle remains unknown.");
            }
            ));
            failures.AddRange(stages.Where(stage => stage.Failure is not null).Select(stage => stage.Failure!));
        }
        known &= failures.Count == 0;
        // A failed stage must not prevent independent observations of the other original members.
        foreach (var identity in Processes.GetAllOwnedIdentities())
        {
            try { known &= Processes.Observe(identity).Outcome == ProcessControlOutcome.AlreadyExited; }
            catch (Exception failure) { known = false; failures.Add(failure); lifetime.Retain(failure); }
        }
        known &= !lifetime.HasPendingTasks;
        if (known)
        {
            var stages = await lifetime.StagesAsync(("original-controller-dispose", Processes,
                _ => Processes.DisposeAsync().AsTask()));
            failures.AddRange(stages.Where(stage => stage.Failure is not null).Select(stage => stage.Failure!));
            known &= failures.Count == 0;
        }
        var owned = OwnsPlainLeaf();
        if (Directory.Exists(Root) && owned)
        {
            var stages = await lifetime.StagesAsync(("retained-middle-diagnostics", this,
                current => WriteNewJsonAsync("retained-middle-" + Guid.NewGuid().ToString("N") + ".json", new
                {
                    fixture = true,
                    productionCertified = false,
                    assertionsPassed = _assertionsPassed,
                    quiescent = known,
                    originalStarted = lifetime.Started,
                    originalTotalSeconds = lifetime.Total,
                    sharedCleanupSeconds = 20,
                    observations,
                    primaryType = lifetime.Primary?.GetType().FullName,
                    primaryMessage = lifetime.Primary?.Message,
                    secondary = failures.Select(failure => new { type = failure.GetType().FullName, failure.Message }).ToArray(),
                    reason = _retentionReason
                }, current)));
            failures.AddRange(stages.Where(stage => stage.Failure is not null).Select(stage => stage.Failure!));
        }
        if (known && owned && failures.Count == 0)
        {
            try { lifetime.ReleaseKnownClosed(this); }
            catch (Exception failure) { failures.Add(failure); lifetime.Retain(failure); }
        }
        else lifetime.Retain(this);
        if (failures.Count != 0)
        {
            if (lifetime.Primary is not null) lifetime.Primary.Data["MiddleFixtureSecondaryFailures"] = failures.ToArray();
            else throw new AggregateException("Original middle cleanup/diagnostics did not complete within its shared lifetime.", failures);
        }
        if (!known || !owned)
        {
            var failure = new InvalidOperationException("Original middle owners remain rooted; no later lane readiness is inferred.");
            lifetime.Retain(failure);
            if (lifetime.Primary is not null) lifetime.Primary.Data["MiddleFixtureUnknownOwners"] = failure;
            else throw failure;
        }
    }

    private bool OwnsPlainLeaf()
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(ArtifactsRoot), Path.GetFullPath(Root));
        if (relative != RunId || relative.Contains(Path.DirectorySeparatorChar) || relative is "" or "." or "..") return false;
        try
        {
            RequirePlainAncestors(Root);
            var pending = new Stack<string>();
            pending.Push(Root);
            while (pending.TryPop(out var directory))
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            using var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "owner.json")));
            return marker.RootElement.GetProperty("RunId").GetString() == RunId &&
                marker.RootElement.GetProperty("Root").GetString() == Root && marker.RootElement.GetProperty("token").GetString() == _ownershipToken;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        { return false; }
    }

    private static void RequirePlainAncestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Owned runtime artifacts cannot traverse a reparse/symlink ancestor.");
    }

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DeliveryForge.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The source checkout owning this test assembly could not be located.");
    }

    private static string FindExecutable(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException("Required explicit/absolute owned fixture executable is unavailable: " + name);

}
