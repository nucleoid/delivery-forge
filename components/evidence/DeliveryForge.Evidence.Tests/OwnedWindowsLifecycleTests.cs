using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeliveryForge.Evidence.Tests;

// Actual Windows creation-owned containment controls. Other platforms are explicitly unsupported evidence.
public sealed class OwnedWindowsLifecycleTests
{
    [Fact(Timeout = 120_000)]
    public async Task Caller_cancelled_during_real_argument_preparation_never_creates_or_resumes_command_body()
    {
        using var bound = Bound();
        if (!OperatingSystem.IsWindows()) { await AssertUnsupportedWindowsProof("root-hold", bound.Token); return; }
        var run = await TreeRun.CreateAsync("root-hold", bound.Token);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(bound.Token);
        var preparation = new PreparationArguments(run.Invocation.Arguments, () => caller.Cancel());
        // Setup has already recorded only the real ordinary args; enumeration of this wrapper happens inside Execute.
        var invocation = run.Invocation with { Arguments = preparation };
        var started = Stopwatch.GetTimestamp();
        var result = await new ProcessCommandExecutor().ExecuteAsync(invocation, TimeSpan.FromSeconds(15), caller.Token);
        await run.WriteObservationAsync(result, Stopwatch.GetElapsedTime(started), bound.Token);
        Assert.Equal(1, preparation.PreparationCalls);
        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.False(result.LifecycleDiagnostics?.OwnedTree?.CreationOwned ?? false);
        Assert.False(File.Exists(Path.Combine(invocation.WorkingDirectory, "child-entered-" + run.Nonce)));
        Assert.DoesNotContain("actual-root-entered:" + run.Nonce, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact(Timeout = 120_000)]
    public async Task Original_timeout_expired_during_real_argument_preparation_never_creates_or_resumes_command_body()
    {
        using var bound = Bound();
        if (!OperatingSystem.IsWindows()) { await AssertUnsupportedWindowsProof("root-hold", bound.Token); return; }
        var run = await TreeRun.CreateAsync("root-hold", bound.Token);
        var preparation = new PreparationArguments(run.Invocation.Arguments, () => Thread.Sleep(50));
        var invocation = run.Invocation with { Arguments = preparation };
        var started = Stopwatch.GetTimestamp();
        var result = await new ProcessCommandExecutor().ExecuteAsync(invocation, TimeSpan.FromMilliseconds(10), bound.Token);
        await run.WriteObservationAsync(result, Stopwatch.GetElapsedTime(started), bound.Token);
        Assert.Equal(1, preparation.PreparationCalls);
        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Null(result.ExitCode);
        Assert.False(result.LifecycleDiagnostics?.OwnedTree?.CreationOwned ?? false);
        Assert.False(File.Exists(Path.Combine(invocation.WorkingDirectory, "child-entered-" + run.Nonce)));
        Assert.DoesNotContain("actual-root-entered:" + run.Nonce, result.StandardOutput, StringComparison.Ordinal);
    }

    private sealed class PreparationArguments(IReadOnlyList<string> actual, Action duringPreparation) : IReadOnlyList<string>
    {
        public int PreparationCalls { get; private set; }
        public int Count => actual.Count;
        public string this[int index] => actual[index];
        public IEnumerator<string> GetEnumerator()
        {
            if (++PreparationCalls != 1) throw new InvalidOperationException("Fixture preparation wrapper was enumerated more than once.");
            duringPreparation();
            foreach (var argument in actual) yield return argument;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact(Timeout = 120_000)]
    public async Task Exited_root_with_real_descendant_holding_pipes_times_out_and_settles_whole_owned_tree()
    {
        using var bound = Bound();
        if (!OperatingSystem.IsWindows()) { await AssertUnsupportedWindowsProof("root-exit", bound.Token); return; }
        var run = await TreeRun.CreateAsync("root-exit", bound.Token);
        await ExecuteAndObserve(run, cancelAfterEntry: false, bound.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task Timeout_with_real_root_and_descendant_settles_whole_owned_tree_and_drains()
    {
        using var bound = Bound();
        if (!OperatingSystem.IsWindows()) { await AssertUnsupportedWindowsProof("root-hold", bound.Token); return; }
        var run = await TreeRun.CreateAsync("root-hold", bound.Token);
        await ExecuteAndObserve(run, cancelAfterEntry: false, bound.Token);
    }

    [Fact(Timeout = 120_000)]
    public async Task Caller_cancellation_after_real_child_entry_settles_whole_owned_tree_without_timeout()
    {
        using var bound = Bound();
        if (!OperatingSystem.IsWindows()) { await AssertUnsupportedWindowsProof("root-hold", bound.Token); return; }
        var run = await TreeRun.CreateAsync("root-hold", bound.Token);
        await ExecuteAndObserve(run, cancelAfterEntry: true, bound.Token);
    }

    private static async Task AssertUnsupportedWindowsProof(string mode, CancellationToken token)
    {
        var limitation = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => TreeRun.CreateAsync(mode, token));
        Assert.Contains("INCOMPLETE Windows creation-owned job lifecycle is not claimed", limitation.Message, StringComparison.Ordinal);
        // This path verifies typed fixture refusal only; it never counts as a Windows containment execution.
    }

    private static async Task ExecuteAndObserve(TreeRun run, bool cancelAfterEntry, CancellationToken outer)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var started = Stopwatch.GetTimestamp();
        var execution = new ProcessCommandExecutor().ExecuteAsync(run.Invocation, TimeSpan.FromSeconds(cancelAfterEntry ? 15 : 3), caller.Token);
        CommandResult? result = null;
        try
        {
            await run.WaitEnteredAsync(TimeSpan.FromSeconds(3), outer);
            if (cancelAfterEntry) caller.Cancel();
            result = await execution.WaitAsync(TimeSpan.FromSeconds(20), outer);
            await run.WriteObservationAsync(result, Stopwatch.GetElapsedTime(started), outer);
            Assert.Equal(cancelAfterEntry, result.Cancelled);
            Assert.Equal(!cancelAfterEntry, result.TimedOut);
            Assert.True(result.OwnedProcessQuiescent);
            var diagnostics = Assert.IsType<CommandLifecycleDiagnostics>(result.LifecycleDiagnostics);
            Assert.True(diagnostics.RootExited);
            Assert.True(diagnostics.ExitWaitCompleted);
            Assert.True(diagnostics.DrainWaitCompleted);
            Assert.True(diagnostics.StandardOutput.CompletedSuccessfully);
            Assert.True(diagnostics.StandardError.CompletedSuccessfully);
            Assert.Contains("actual-child-stdout:" + run.Nonce, result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("actual-child-stderr:" + run.Nonce, result.StandardError, StringComparison.Ordinal);
            var tree = Assert.IsType<CommandOwnedTreeObservation>(diagnostics.OwnedTree);
            Assert.True(tree.CreationOwned);
            Assert.True(tree.SettlementObserved);
            Assert.Equal(0, tree.ActiveProcesses);
        }
        finally
        {
            caller.Cancel();
            // Retain the real owner result and all files. Natural8s helper bounds old-code RED descendants;
            // no PID signal/adoption or marker-derived cleanup/settlement authority is used.
            if (result is null)
            {
                using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                result = await execution.WaitAsync(settle.Token);
                await run.WriteObservationAsync(result, Stopwatch.GetElapsedTime(started), settle.Token);
            }
        }
    }
    private static CancellationTokenSource Bound()
    {
        var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(30)); return bound;
    }

    private sealed class TreeRun(string root, string nonce, CommandInvocation invocation)
    {
        public string Nonce => nonce;
        public CommandInvocation Invocation => invocation;
        public static async Task<TreeRun> CreateAsync(string mode, CancellationToken token)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("INCOMPLETE Windows creation-owned job lifecycle is not claimed on this platform.");
            var host = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DOTNET_HOST") ?? throw new PlatformNotSupportedException("Root real SDK host is missing.");
            var helper = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LIFECYCLE_HELPER_DLL") ?? throw new PlatformNotSupportedException("Root-built real helper DLL is missing.");
            if (!Path.IsPathFullyQualified(host) || !Path.IsPathFullyQualified(helper) || !File.Exists(host) || !File.Exists(helper))
                throw new IOException("Actual absolute SDK/helper inputs are unavailable.");
            DirectoryInfo? source = new(AppContext.BaseDirectory);
            while (source is not null && !Directory.Exists(Path.Combine(source.FullName, "components", "end-to-end"))) source = source.Parent;
            if (source is null) throw new IOException("Source-owned retained artifact root unavailable.");
            var root = Path.Combine(source.FullName, "components", "end-to-end", "artifacts", "test-runs", "owned-tree-" + Guid.NewGuid().ToString("N"));
            for (DirectoryInfo? ancestor = new(root); ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Lifecycle artifact path is redirected.");
            if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Lifecycle leaf collision.");
            Directory.CreateDirectory(root);
            var nonce = Guid.NewGuid().ToString("N");
            var environment = new Dictionary<string, string>
            {
                ["DOTNET_ROOT"] = Path.GetDirectoryName(host)!,
                ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
                ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_PROCESSOR_COUNT"] = "2",
                ["DOTNET_GCHeapHardLimit"] = "0x100000000"
            };
            var invocation = new CommandInvocation(host, [helper, mode, root, nonce], root, environment);
            await WriteNew(root, "setup.json", new
            {
                fixture = true,
                productionCertified = false,
                nonce,
                invocation,
                helperSha256 = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(helper, token))),
                hostSha256 = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(host, token))),
                childNaturalLimitSeconds = 8,
                originalScenarioLimitSeconds = 120,
                retained = true
            }, token);
            return new(root, nonce, invocation);
        }
        public async Task WaitEnteredAsync(TimeSpan limit, CancellationToken token)
        {
            var started = Stopwatch.GetTimestamp(); var path = Path.Combine(root, "child-entered-" + nonce);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) >= limit) throw new TimeoutException("Actual helper child did not reach its nonce entry.");
                if (File.Exists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Child marker is redirected.");
                    try { if (await File.ReadAllTextAsync(path, token) == nonce) return; }
                    catch (IOException) { /* actual create-new writer may still hold the file */ }
                }
                await Task.Delay(10, token);
            }
        }
        public Task WriteObservationAsync(CommandResult result, TimeSpan elapsed, CancellationToken token) =>
            WriteNew(root, "actual-result.json", new
            {
                fixture = true,
                productionCertified = false,
                result,
                elapsedSeconds = elapsed.TotalSeconds,
                retained = true,
                limitation = "Child-entry bytes identify the scenario, never process ownership or quiescence authority."
            }, token);
        private static async Task WriteNew(string directory, string name, object value, CancellationToken token)
        {
            await using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value), token);
        }
    }
}
