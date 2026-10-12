using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Evidence;

namespace DeliveryForge.EndToEnd.Tests;

// Separate retained actual-tool leaf; existing native-worktree cleanup guards are untouched.
internal sealed class OwnedQualityInterruptionRuntime : ICommandExecutor
{
    private readonly OwnedScenarioRun _artifacts;
    private readonly QualityInterruptionClock _clock;
    private readonly CancellationToken _outer;
    private readonly string _git;
    private readonly byte[] _marker;
    private readonly ProcessCommandExecutor _actual = new();
    private long _stageStarted;
    private TimeSpan _stageLimit;
    private int _command;
    private int _activeSdk;
    public Action<CommandResult>? AfterSdkResult { get; set; }
    public int SdkCommands { get; private set; }
    public string Root { get; }
    public string Repository { get; }
    public string Dotnet { get; }
    public string PackageCache { get; }
    public bool AllCommandsQuiescent { get; private set; } = true;
    public IReadOnlyDictionary<string, string> DotnetEnvironment { get; }

    public OwnedQualityInterruptionRuntime(OwnedScenarioRun artifacts, QualityInterruptionClock clock, CancellationToken outer)
    {
        _artifacts = artifacts; _clock = clock; _outer = outer;
        Dotnet = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DOTNET_HOST") ?? throw new PlatformNotSupportedException("INCOMPLETE root-provided absolute real SDK host is absent.");
        if (!Path.IsPathFullyQualified(Dotnet) || !File.Exists(Dotnet)) throw new PlatformNotSupportedException("INCOMPLETE trusted SDK host is unavailable.");
        Dotnet = Path.GetFullPath(Dotnet);
        _git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "git.exe" : "git")).FirstOrDefault(File.Exists)
            ?? throw new PlatformNotSupportedException("INCOMPLETE absolute Git unavailable.");
        PackageCache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (!Path.IsPathFullyQualified(PackageCache) || !Directory.Exists(PackageCache)) throw new PlatformNotSupportedException("INCOMPLETE existing package cache unavailable.");
        Root = Path.Combine(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "delivery-forge-e2e")), Guid.NewGuid().ToString("N"));
        Repository = Path.Combine(Root, "repository");
        _marker = JsonSerializer.SerializeToUtf8Bytes(new { fixture = true, Root, artifactRunId = artifacts.RunId, nonce = Guid.NewGuid().ToString("N") });
        DotnetEnvironment = new Dictionary<string, string>
        {
            ["DOTNET_ROOT"] = Path.GetDirectoryName(Dotnet)!,
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_GCHeapHardLimit"] = "0x100000000",
            ["NUGET_PACKAGES"] = PackageCache,
            ["DOTNET_PROCESSOR_COUNT"] = "2"
        };
    }
    public void BeginStage(TimeSpan limit)
    {
        if (limit <= TimeSpan.Zero) throw new OperationCanceledException("Trusted stage budget already exhausted.", _clock.WallToken);
        _stageStarted = System.Diagnostics.Stopwatch.GetTimestamp(); _stageLimit = limit;
    }
    public TimeSpan CommandBudget(TimeSpan requested)
    {
        _outer.ThrowIfCancellationRequested(); _clock.WallToken.ThrowIfCancellationRequested();
        var stage = _stageLimit - System.Diagnostics.Stopwatch.GetElapsedTime(_stageStarted);
        var global = _clock.Remaining - TimeSpan.FromSeconds(QualityInterruptionClock.CleanupSeconds);
        var allowed = new[] { requested, stage, global }.Min();
        if (allowed <= TimeSpan.Zero) throw new OperationCanceledException("Original real-tool stage/work budget exhausted.", _clock.WallToken);
        return allowed;
    }
    public async Task InitializeAsync(string source, CancellationToken cancellationToken)
    {
        RequirePlain(Root);
        if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Actual tool runtime leaf collision.");
        Directory.CreateDirectory(Root);
        await WriteNewAsync(Path.Combine(Root, "owner.json"), _marker, cancellationToken);
        Directory.CreateDirectory(Repository);
        var pending = new Stack<string>(); pending.Push(source);
        var entries = 0; long bytes = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Tool source has reparse directory.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > 128 || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Tool source inventory exceeds its owned bound.");
                if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                if ((bytes += new FileInfo(entry).Length) > 1024 * 1024) throw new IOException("Tool source byte bound exceeded.");
                var relative = Path.GetRelativePath(source, entry);
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) throw new IOException("Build outputs cannot seed actual tool runtime.");
                var destination = Path.Combine(Repository, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await WriteNewAsync(destination, await File.ReadAllBytesAsync(entry, cancellationToken), cancellationToken);
            }
        }
        await _artifacts.WriteNewJsonAsync("quality-interruption-runtime.json", new
        {
            fixture = true,
            productionCertified = false,
            Root,
            Repository,
            Dotnet,
            PackageCache,
            markerSha256 = Hash(_marker),
            retained = true
        }, cancellationToken);
    }
    public async Task<CommandResult> ExecuteAsync(CommandInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        EnsureOwner();
        if (!string.Equals(Path.GetFullPath(invocation.WorkingDirectory), Repository, PathComparison())) throw new InvalidOperationException("Tool invocation escaped its exact owned repo.");
        var git = invocation.FileName == "git" || string.Equals(invocation.FileName, _git, PathComparison());
        if (!git && (!string.Equals(invocation.FileName, Dotnet, PathComparison()) || invocation.Arguments.Count == 0 ||
            invocation.Arguments[0] is not ("--version" or "--help" or "restore" or "test"))) throw new InvalidOperationException("Actual tool invocation is outside approved Git/SDK scope.");
        var environment = new Dictionary<string, string>(invocation.Environment ?? new Dictionary<string, string>());
        if (git)
        {
            var directory = Path.Combine(Repository, ".git");
            RequirePlainPath(directory); RequirePlainPath(Path.Combine(directory, "objects"));
            Directory.CreateDirectory(Path.Combine(directory, "objects"));
            var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            foreach (var pair in new Dictionary<string, string>
            {
                ["GIT_DIR"] = directory,
                ["GIT_WORK_TREE"] = Repository,
                ["GIT_COMMON_DIR"] = directory,
                ["GIT_OBJECT_DIRECTORY"] = Path.Combine(directory, "objects"),
                ["GIT_INDEX_FILE"] = Path.Combine(directory, "index"),
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "",
                ["GIT_CONFIG"] = Path.Combine(directory, "config"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_SYSTEM"] = nullDevice,
                ["GIT_CONFIG_GLOBAL"] = nullDevice,
                ["GIT_CONFIG_PARAMETERS"] = "",
                ["GIT_CONFIG_COUNT"] = "0",
                ["GIT_NO_REPLACE_OBJECTS"] = "1",
                ["GIT_GRAFT_FILE"] = nullDevice,
                ["GIT_NO_LAZY_FETCH"] = "1",
                ["GIT_TERMINAL_PROMPT"] = "0"
            }) environment[pair.Key] = pair.Value;
            invocation = new(_git, ["-c", "core.hooksPath=" + nullDevice, "-c", "core.fsmonitor=false", .. invocation.Arguments], Repository, environment);
        }
        else
        {
            foreach (var pair in DotnetEnvironment) environment[pair.Key] = pair.Value;
            invocation = invocation with { Environment = environment };
        }
        if (!git) SdkCommands++;
        var allowed = CommandBudget(timeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _outer, _clock.WallToken);
        // Actual executor owns its timeout; caller/original wall cancellation stays distinct.
        CommandResult result;
        var sdkEntered = !git;
        if (sdkEntered && Interlocked.CompareExchange(ref _activeSdk, 1, 0) != 0)
            throw new InvalidOperationException("A second SDK command cannot overlap an actual owned execution.");
        try { result = await _actual.ExecuteAsync(invocation, allowed, deadline.Token); }
        catch { AllCommandsQuiescent = false; throw; }
        finally { if (sdkEntered) Volatile.Write(ref _activeSdk, 0); }
        AllCommandsQuiescent &= result.OwnedProcessQuiescent;
        if (!git) AfterSdkResult?.Invoke(result);
        using var diagnostic = CancellationTokenSource.CreateLinkedTokenSource(_outer, _clock.WallToken);
        diagnostic.CancelAfter(TimeSpan.FromSeconds(2));
        await _artifacts.WriteNewJsonAsync($"interruption-tool-command-{Interlocked.Increment(ref _command):D4}.json", new { fixture = true, invocation, allowedSeconds = allowed.TotalSeconds, result }, diagnostic.Token);
        return result;
    }
    public async Task<string> GitAsync(CancellationToken token, params string[] arguments)
    {
        var result = await ExecuteAsync(new("git", arguments, Repository), TimeSpan.FromSeconds(15), token);
        RequireSuccessful(result, "Git"); return result.StandardOutput.Trim();
    }
    public void DeleteOwnedBuildOutputs()
    {
        EnsureOwner();
        if (!AllCommandsQuiescent || Volatile.Read(ref _activeSdk) != 0) throw new IOException("Owned tool command quiescence is unproved; no deletion.");
        foreach (var project in new[] { "Subject", "Subject.Tests" })
            foreach (var leaf in new[] { "bin", "obj" })
            {
                var path = Path.GetFullPath(Path.Combine(Repository, project, leaf));
                if (!path.StartsWith(Repository + Path.DirectorySeparatorChar, PathComparison())) throw new IOException("Build output escaped owned runtime.");
                if (!Directory.Exists(path)) continue;
                RequirePlainPath(path);
                RequirePlainTree(path);
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(path, recursive: true);
            }
    }
    public void EnsureOwner()
    {
        RequirePlain(Root); RequirePlainPath(Repository);
        if ((File.GetAttributes(Path.Combine(Root, "owner.json")) & FileAttributes.ReparsePoint) != 0 ||
            !File.ReadAllBytes(Path.Combine(Root, "owner.json")).SequenceEqual(_marker)) throw new IOException("Actual tool owner marker changed.");
    }
    private static void RequirePlain(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && ((current.Attributes & FileAttributes.ReparsePoint) != 0 || Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))))
                throw new IOException("Tool runtime crosses a redirected/repository ancestor.");
    }
    private static void RequirePlainTree(string root)
    {
        RequirePlainPath(root);
        var pending = new Stack<string>(); pending.Push(root); var count = 0;
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 4096 || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Owned build-output tree is uncertain.");
                if (Directory.Exists(entry)) pending.Push(entry);
            }
    }
    private static void RequirePlainPath(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Owned tool path has a redirected ancestor.");
    }
    public static void RequireSuccessful(CommandResult result, string stage)
    {
        if (result.ExitCode != 0 || result.TimedOut || result.Cancelled || !result.OwnedProcessQuiescent)
            throw new InvalidOperationException("INCOMPLETE actual " + stage + " failed/uncertain: " + result.StandardError);
    }
    public static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true);
    }
    public static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string HashFile(string path) { using var stream = File.OpenRead(path); return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

