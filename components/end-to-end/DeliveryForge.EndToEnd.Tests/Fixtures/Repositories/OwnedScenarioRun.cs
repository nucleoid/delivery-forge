using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Planning;

namespace DeliveryForge.EndToEnd.Tests;

// Real file/Git/process boundaries. This harness issues no worker, reviewer, lease or publication authority.
public sealed class OwnedScenarioRun : IAsyncDisposable
{
    public const string InitialIntegratedBase = "0ec6c9d18c660d399c9b171aba4e6c09952f731a";
    private readonly string _ownershipToken = Guid.NewGuid().ToString("N");
    private readonly string _git;
    private int _commandSequence;
    private bool _commandsQuiescent = true;
    private bool _assertionsPassed;
    private string? _retentionReason;

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
    {
        var sourceRoot = FindSourceRoot();
        var run = new OwnedScenarioRun(sourceRoot, FindExecutable(OperatingSystem.IsWindows() ? "git.exe" : "git"));
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

    public async Task<string> GitAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var gitDirectory = Path.Combine(Checkout, ".git");
        Directory.CreateDirectory(Path.Combine(gitDirectory, "objects"));
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var environment = new Dictionary<string, string>
        {
            ["GIT_DIR"] = gitDirectory, ["GIT_WORK_TREE"] = Checkout, ["GIT_COMMON_DIR"] = gitDirectory,
            ["GIT_OBJECT_DIRECTORY"] = Path.Combine(gitDirectory, "objects"), ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "",
            ["GIT_INDEX_FILE"] = Path.Combine(gitDirectory, "index"), ["GIT_CONFIG_COUNT"] = "0",
            ["GIT_CONFIG_PARAMETERS"] = "", ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_SYSTEM"] = nullDevice,
            ["GIT_CONFIG_GLOBAL"] = nullDevice, ["GIT_CONFIG"] = Path.Combine(gitDirectory, "config"),
            ["GIT_NO_REPLACE_OBJECTS"] = "1", ["GIT_GRAFT_FILE"] = nullDevice,
            ["GIT_NO_LAZY_FETCH"] = "1", ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_OPTIONAL_LOCKS"] = "0"
        };
        CommandResult result;
        try
        {
            result = await new ProcessCommandExecutor().ExecuteAsync(new(_git,
                ["-c", "core.hooksPath=" + nullDevice, "-c", "core.fsmonitor=false", .. arguments], Checkout, environment),
                TimeSpan.FromSeconds(15), cancellationToken);
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
            var executable = FindWindowsPowerShell();
            launch = new(executable, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]);
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
        await using var stream = new FileStream(Path.Combine(Root, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var quiescent = _commandsQuiescent;
        foreach (var identity in Processes.GetOwnedIdentities(RunId))
        {
            try
            {
                var observation = Processes.Observe(identity);
                if (observation.Outcome == ProcessControlOutcome.LiveOwned)
                    await Processes.QuiesceAsync(identity, TimeSpan.FromSeconds(2), CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
                quiescent &= Processes.Observe(identity).Outcome == ProcessControlOutcome.AlreadyExited;
            }
            catch (Exception) { quiescent = false; }
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
                new { assertionsPassed = _assertionsPassed, quiescent, fixture = true, reason = _retentionReason ?? "Failure, timeout or ambiguous cleanup retains owned artifacts." }, CancellationToken.None);
        if (_assertionsPassed && (!quiescent || !owned))
            throw new InvalidOperationException("Cleanup was not positively owned and quiescent; retained run: " + Root);
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

    private static string FindWindowsPowerShell()
    {
        try { return FindExecutable("pwsh.exe"); }
        catch (FileNotFoundException)
        {
            var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(candidate) ? candidate : throw new FileNotFoundException("Bounded native fixture PowerShell host is unavailable.");
        }
    }
}
