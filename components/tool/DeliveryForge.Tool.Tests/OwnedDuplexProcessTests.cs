using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution.Git;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;
// Genuine fixture-process controls. All require the actual build output binding;
// no skip, shell/model substitute or fake process/EOF/Job success.
public sealed class OwnedDuplexProcessTests
{
    internal sealed class Affinity : IDisposable
    {
        readonly Process actual; readonly IntPtr previous; bool set;
        static IntPtr ReadActualAffinity(Process actual)
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) return actual.ProcessorAffinity;
            throw new PlatformNotSupportedException("Duplex affinity is Windows/Linux only.");
        }
        static void WriteActualAffinity(Process actual, IntPtr value)
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) { actual.ProcessorAffinity = value; return; }
            throw new PlatformNotSupportedException("Duplex affinity is Windows/Linux only.");
        }
        public Affinity() { if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Duplex requires Windows/Linux."); actual = Process.GetCurrentProcess(); previous = ReadActualAffinity(actual); var mask = unchecked((ulong)previous.ToInt64()); if (BitOperations.PopCount(mask) < 2) throw new IOException("Two actual CPU bits are required."); var first = mask & (~mask + 1); var rest = mask & ~first; var two = first | (rest & (~rest + 1)); WriteActualAffinity(actual, unchecked((IntPtr)(long)two)); set = true; if (ReadActualAffinity(actual).ToInt64() != unchecked((long)two)) throw new IOException("Actual two-bit affinity set/readback failed."); }
        public void Dispose() { if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return; try { if (set) { actual.ProcessorAffinity = previous; Assert.Equal(previous, actual.ProcessorAffinity); } } finally { actual.Dispose(); } }
    }
    internal sealed class Run : IAsyncDisposable
    {
        public readonly ToolLifetime Original = new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); public readonly string DirectoryPath, Nonce = Guid.NewGuid().ToString("N"); public readonly OwnedDuplexProcess Session;
        public Run(string mode, bool missing = false, bool malformedGate = false)
        {
            var binding = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DUPLEX_FIXTURE_MANIFEST") ?? throw new IOException("Fresh actual compiled duplex fixture manifest is REQUIRED.");
            var selected = BoundedFiles.ReadJson<FixtureBinding>(binding, Original, 65536); if (selected.Files.Count != 5) throw new IOException("Complete five-file fixture build output is required.");
            var output = Path.GetDirectoryName(selected.Image)!; var actualFiles = System.IO.Directory.GetFiles(output).Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToArray(); if (!actualFiles.SequenceEqual(selected.Files.Select(x => Path.GetFullPath(x.Path)).Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new IOException("Actual fixture build membership differs.");
            foreach (var file in selected.Files) { var bytes = BoundedFiles.Read(file.Path, Original); if (bytes.LongLength != file.Bytes || BoundedFiles.Sha(bytes) != file.Sha256) throw new IOException("Actual fixture output changed."); }
            if (!selected.Files.Any(x => Path.GetFullPath(x.Path) == Path.GetFullPath(selected.Image))) throw new IOException("Selected fixture image is not in its actual build closure.");
            DirectoryPath = Path.Combine(Path.GetTempPath(), "delivery-forge-duplex-" + Nonce); System.IO.Directory.CreateDirectory(DirectoryPath);
            var env = new Dictionary<string, string> { ["DOTNET_PROCESSOR_COUNT"] = "2", ["DOTNET_GCHeapHardLimit"] = "0x100000000", ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1", ["MSBUILDDISABLENODEREUSE"] = "1" };
            LinuxScopeSelection? scope = null;
            if (OperatingSystem.IsLinux())
            {
                var scopePath = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LINUX_SCOPE_PROFILE") ?? throw new IOException("Fresh independently bound actual Linux scope profile is REQUIRED."); scope = BoundedFiles.ReadJson<LinuxScopeSelection>(scopePath, Original, 65536);
                if (malformedGate) { var closure = selected.Files.Select(file => new LinuxScopeImage(file.Path, file.Bytes, file.Sha256)).ToArray(); scope = scope with { BirthGate = closure.Single(file => Path.GetFullPath(file.Path) == Path.GetFullPath(selected.Image)), BirthGateClosure = Array.AsReadOnly(closure), GateBuildReceipt = selected.NegativeGateBuildReceipt ?? throw new IOException("Fresh genuine negative fixture compiled-source/PDB Root proof required.") }; }
            }
            else if (malformedGate) throw new PlatformNotSupportedException("Partial scope READY control is Linux-only.");
            Session = OwnedDuplexProcess.Open(new(missing ? Path.Combine(output, "explicit-absent-" + Nonce) : selected.Image, [mode, DirectoryPath, Nonce], DirectoryPath, env), Original, scope);
        }
        public async Task Ready() { Assert.Null(Session.StartupFailure); var bytes = new List<byte>(); var one = new byte[1]; while (true) { var count = await Session.Output.ReadAsync(one, 0, 1, Original.Token); if (count == 0) throw new IOException("Fixture EOF before actual READY."); if (one[0] == 10) break; if (bytes.Count >= 256) throw new IOException("Fixture READY exceeded."); if (one[0] != 13) bytes.Add(one[0]); } var values = Encoding.UTF8.GetString(bytes.ToArray()).Split('|'); Assert.Equal(4, values.Length); Assert.Equal("READY", values[0]); Assert.Equal(2, BitOperations.PopCount(unchecked((ulong)long.Parse(values[1], System.Globalization.CultureInfo.InvariantCulture)))); Assert.Equal("2", values[2]); Assert.Equal("0x100000000", values[3]); }
        public async ValueTask DisposeAsync() { var actual = await Session.RetireAsync(true); Preserve(actual); }
        // Unknown originals remain undisposed and are strongly held by the actual
        // OwnedDuplexProcess retained graph. Tests need no internal owner control.
        public void Preserve(DuplexRetirement result) { if (result.KnownClosed) Original.Dispose(); }
    }
    public sealed record FixturePin(string Path, long Bytes, string Sha256);
    public sealed record FixtureBinding(string Image, IReadOnlyList<FixturePin> Files, LinuxScopeImage? NegativeGateBuildReceipt = null);
    static void AssertPhysicalScope(DuplexRetirement actual) { Assert.True(actual.KnownClosed, string.Join(";", actual.Failures)); Assert.True(actual.RootExited); Assert.Equal(0u, actual.ActiveProcesses); Assert.True(actual.DispositionReturned); }
    [Fact(Timeout = 90000)]
    public async Task Actual_prompt_framing_and_original_graceful_retirement_observe_buffered_final_usage_with_phase_cancelled()
    {
        using var affinity = new Affinity(); await using var run = new Run("usage-on-eof"); await run.Ready();
        var slots = new DeliveryForge.Scheduling.ResourceSlots(1, 1, 64, 1, 1, 0);
        var budget = DeliveryForge.Scheduling.ResourceBudget.Measure(slots, new("actual-framing-control", true, slots, TimeSpan.FromSeconds(60), 100000, 0));
        var session = ReportedModelUsage.Bind(budget, 100000, 100000).Register(); var usage = new ClaudeReportedUsage(session);
        var codec = new DeliveryForge.Tool.Codex.CodexJsonlProtocol(run.Session.Input, run.Session.Output, run.Original);
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        async Task ReadFinal()
        {
            using var row = await codec.ReadAsync(run.Original.Token); Assert.NotNull(row); usage.Observe(row.RootElement, "fixture-session");
            Assert.Null(await codec.ReadAsync(run.Original.Token));
        }
        var pump = ReadFinal(); run.Session.AttachOriginalOutputConsumer(pump);
        await ReportedModelUsage.WritePromptAsync(session, codec, new { type = "user", message = new { role = "user", content = "fixed fixture" } }, phase.Token);
        phase.Cancel(); // Forward phase is stopped; original WORK observation still owns EOF.
        var closed = await run.Session.RetireAsync(false);
        await pump.WaitAsync(run.Original.Remaining, TestContext.Current.CancellationToken);
        session.RequireFinal(); session.Finish(); AssertPhysicalScope(closed);
        Assert.Equal(0, closed.ExitCode); Assert.True(closed.OutputEof && closed.ErrorEof); Assert.False(run.Session.HasUnknownIo);
        var actual = session.Evidence(); Assert.Equal(19, actual.AggregateTokens); Assert.True(actual.FinalUsageKnown); Assert.False(actual.Stopped);
        run.Preserve(closed);
    }
    [Fact(Timeout = 90000)]
    public async Task Actual_duplex_roundtrip_complete_EOF_and_same_first_retirement()
    {
        using var affinity = new Affinity(); await using var run = new Run("echo"); await run.Ready(); var sent = Encoding.ASCII.GetBytes("actual-duplex-16"); Assert.Equal(16, sent.Length); await run.Session.Input.WriteAsync(sent, 0, sent.Length, run.Original.Token); await run.Session.Input.FlushAsync(run.Original.Token); var actual = new byte[16]; await run.Session.Output.ReadExactlyAsync(actual, run.Original.Token); Assert.Equal(sent, actual); var eof = new byte[1]; Assert.Equal(0, await run.Session.Output.ReadAsync(eof, 0, 1, run.Original.Token)); var first = run.Session.RetireAsync(false); Assert.Same(first, run.Session.RetireAsync(false)); var closed = await first; AssertPhysicalScope(closed); Assert.Equal(0, closed.ExitCode); Assert.True(closed.OutputEof); Assert.All(run.Session.ActualIo, x => Assert.False(x.Unknown)); run.Preserve(closed);
    }
    [Fact(Timeout = 90000)]
    public async Task Actual_blocked_stdout_is_stopped_by_original_owner_not_terminal_text()
    {
        using var affinity = new Affinity(); await using var run = new Run("output-block"); await run.Ready(); var retirement = run.Session.RetireAsync(true); Assert.Throws<TimeoutException>(run.Original.Check); var closed = await retirement; AssertPhysicalScope(closed); Assert.True(closed.RootExited); Assert.False(run.Session.HasUnknownIo); run.Preserve(closed);
    }
    [Fact(Timeout = 90000)]
    public async Task Actual_blocked_stdin_records_original_task_and_cancellation_without_replay()
    {
        using var affinity = new Affinity(); await using var run = new Run("input-block"); await run.Ready(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var data = new byte[65536]; Task? originalWrite = null; for (var index = 0; index < 128; index++) { run.Original.Check(); originalWrite = run.Session.Input.WriteAsync(data, 0, data.Length, cancel.Token); if (!originalWrite.IsCompleted) break; await originalWrite; }
        Assert.NotNull(originalWrite); Assert.False(originalWrite.IsCompleted); Assert.True(run.Session.HasUnknownIo);
        var pendingWrite = Assert.Single(run.Session.ActualIo, x => x.Kind == "stdin-write" && x.Unknown);
        Assert.NotNull(pendingWrite.ActualTask);
        cancel.Cancel(); var retirement = run.Session.RetireAsync(true);
        Assert.Throws<TimeoutException>(run.Original.Check);
        // Retirement latches cleanup synchronously. Preserve the cancelled actual
        // write and its independent WORK post-check rather than dropping either.
        var failure = await Record.ExceptionAsync(async () => await originalWrite);
        Assert.NotNull(failure); Assert.True(cancel.IsCancellationRequested);
        Assert.True(pendingWrite.Primary is OperationCanceledException or IOException);
        if (failure is AggregateException combined)
        {
            Assert.Equal(2, combined.InnerExceptions.Count);
            Assert.Same(pendingWrite.Primary, combined.InnerExceptions[0]);
            Assert.Same(pendingWrite.Post, combined.InnerExceptions[1]);
            Assert.IsType<TimeoutException>(pendingWrite.Post);
        }
        else
        {
            Assert.Same(pendingWrite.Primary, failure);
            Assert.Null(pendingWrite.Post);
        }
        Assert.True(pendingWrite.ActualTask.IsCompleted); Assert.False(pendingWrite.Unknown);
        var closed = await retirement; Assert.True(closed.RootExited); Assert.Contains(run.Session.ActualIo, x => x.Kind == "stdin-write" && x.ActualTask is not null); Assert.Same(run.Session.RetireAsync(true), run.Session.RetireAsync(true)); if (closed.KnownClosed) Assert.True(closed.InputCloseReturned && closed.DispositionReturned); else Assert.True(run.Session.HasRetainedOriginalOwner); run.Preserve(closed);
    }
    [Fact(Timeout = 90000)]
    public async Task Actual_stderr_overflow_is_retained_failure_not_truncated_success()
    {
        using var affinity = new Affinity(); await using var run = new Run("stderr-overflow"); await run.Ready(); var codec = new DeliveryForge.Tool.Codex.CodexJsonlProtocol(run.Session.Input, run.Session.Output, run.Original); using var terminal = await codec.ReadAsync(TestContext.Current.CancellationToken); Assert.NotNull(terminal); DeliveryForge.Tool.Codex.CodexJsonlProtocol.RequireEnvelope(terminal.RootElement); Assert.Equal("completed", terminal.RootElement.GetProperty("params").GetProperty("turn").GetProperty("status").GetString()); Assert.NotNull(run.Session.OriginalErrorDrainTask); await Assert.ThrowsAsync<IOException>(async () => await run.Session.OriginalErrorDrainTask.WaitAsync(run.Original.Remaining, TestContext.Current.CancellationToken)); var retirement = run.Session.RetireAsync(false); Assert.Throws<TimeoutException>(run.Original.Check); var closed = await retirement; Assert.Throws<IOException>(() => HostTransportRetirement.RequireSuccessful(closed)); Assert.True(closed.RootExited); Assert.Contains(closed.Failures, x => x.Contains("stderr profile", StringComparison.Ordinal)); Assert.True(run.Session.StandardError.Length <= 1048576); run.Preserve(closed);
    }
    [Fact(Timeout = 90000)]
    public async Task Known_actual_creator_failure_retains_frame_and_never_claims_child_exit_zero()
    {
        using var affinity = new Affinity(); await using var run = new Run("echo", missing: true); Assert.True(run.Session.HasRetainedOriginalOwner);
        if (OperatingSystem.IsLinux()) { Assert.Null(run.Session.StartupFailure); var closed = await run.Session.RetireAsync(false); Assert.Throws<TimeoutException>(run.Original.Check); AssertPhysicalScope(closed); Assert.Equal(125, closed.ExitCode); Assert.Throws<IOException>(() => HostTransportRetirement.RequireSuccessful(closed)); run.Preserve(closed); }
        else { Assert.NotNull(run.Session.StartupFailure); var closed = await run.Session.RetireAsync(true); Assert.True(closed.RootExited); Assert.Null(closed.ExitCode); Assert.NotEmpty(closed.Failures); run.Preserve(closed); }
    }
    [Fact(Timeout = 90000)]
    public async Task Exit_zero_root_with_live_owned_descendant_retires_under_unchanged_work_then_verifies_actual_Git()
    {
        var gitImage = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DUPLEX_TEST_GIT") ?? throw new IOException("Explicit actual Git image required for post-retirement verification.");
        Assert.True(Path.IsPathFullyQualified(gitImage) && File.Exists(gitImage));
        using var affinity = new Affinity(); await using var run = new Run("orphan-parent-release");
        void Trace(string stage)
        {
            // Diagnostic only; the original budget and outcome remain authoritative.
            try { TestContext.Current.TestOutputHelper?.WriteLine($"{stage}: original elapsed {Stopwatch.GetElapsedTime(run.Original.OriginalStarted).TotalSeconds:F3}s"); }
            catch { } // Output failure must not replace the actual assertion or failure.
        }
        Trace("fixture opened"); await run.Ready(); Trace("fixture ready");
        var originalStarted = run.Original.OriginalStarted; var whole = run.Original.WholeLimit; var work = run.Original.WorkLimit;
        // Root completion is a native observation. An inherited pipe writer in
        // the surviving descendant can keep stdout open until actual retirement.
        DuplexNativeObservation before;
        do { run.Original.Check(); before = run.Session.ObserveActualNative(); if (!before.RootExited) await Task.Delay(5, run.Original.Token); } while (!before.RootExited);
        Assert.True(before.Known); Assert.Equal(0, before.ExitCode); Assert.True(before.ActiveProcesses > 0);
        Assert.True(File.Exists(Path.Combine(run.DirectoryPath, run.Nonce + "-child-entered")));
        var retiring = Stopwatch.GetTimestamp(); var first = run.Session.RetireAsync(false);
        Assert.Same(first, run.Session.RetireAsync(false));
        // RetireCore has entered its unchanged grace before the first await.
        // Only now release the actual waiting child: its four-second future
        // effect cannot already be consumed by root-exit/observation latency.
        var release = Path.Combine(run.DirectoryPath, run.Nonce + "-child-release");
        BoundedFiles.WriteNew(release + ".tmp", Encoding.UTF8.GetBytes(run.Nonce), run.Original);
        run.Original.Check(); File.Move(release + ".tmp", release); run.Original.Check();
        var armed = Path.Combine(run.DirectoryPath, run.Nonce + "-child-armed");
        while (!File.Exists(armed))
        {
            run.Original.Check();
            if (first.IsCompleted) { await first; throw new IOException("Actual held child retired before acknowledging its released future marker."); }
            await Task.Delay(5, run.Original.Token);
        }
        Assert.Equal(run.Nonce, Encoding.UTF8.GetString(BoundedFiles.Read(armed, run.Original, 64)));
        var armedObserved = Stopwatch.GetTimestamp();
        var closed = await first;
        Assert.True(Stopwatch.GetElapsedTime(retiring) >= TimeSpan.FromSeconds(2), "The actual live descendant must outlast the unchanged two-second root grace.");
        HostTransportRetirement.RequireSuccessful(closed); AssertPhysicalScope(closed);
        Assert.True(closed.InputCloseReturned && closed.OutputEof && closed.ErrorEof); Assert.False(run.Session.HasUnknownIo);
        Assert.All(run.Session.ActualIo, row => Assert.False(row.Unknown)); run.Original.Check();
        Assert.Equal(originalStarted, run.Original.OriginalStarted); Assert.Equal(whole, run.Original.WholeLimit); Assert.Equal(work, run.Original.WorkLimit);
        Trace("native retirement verified");
        Assert.False(File.Exists(Path.Combine(run.DirectoryPath, run.Nonce + "-child-returned")));
        var repository = Path.Combine(run.DirectoryPath, "post-retirement-git"); run.Original.Check(); Directory.CreateDirectory(repository); run.Original.Check();
        var executor = ActualOrdinaryCapture.Select(run.Original);
        async Task<string> Git(params string[] arguments)
        {
            run.Original.Check(); var disabled = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var env = ActualOrdinaryCapture.ResourceEnvironment(); env["GIT_CONFIG_NOSYSTEM"] = "1"; env["GIT_CONFIG_GLOBAL"] = disabled; env["GIT_CONFIG_SYSTEM"] = disabled; env["GIT_NO_REPLACE_OBJECTS"] = "1"; env["GIT_TERMINAL_PROMPT"] = "0";
            var argv = new[] { "--no-replace-objects", "-c", "core.hooksPath=" + disabled, "-c", "core.fsmonitor=false", "-c", "user.name=DuplexFixture", "-c", "user.email=fixture@example.invalid" }.Concat(arguments).ToArray();
            var actual = await executor.ExecuteBoundedCaptureAsync(new(gitImage, argv, repository, env), run.Original.Remaining, 65536, run.Original.Token, new OwnedWorkPhaseBudget(run.Original));
            ActualOrdinaryCapture.Require(actual); Assert.Equal(0, actual.Command.ExitCode); Assert.False(actual.Command.TimedOut || actual.Command.Cancelled); run.Original.Check();
            Trace("serial Git " + arguments[0] + " verified");
            return Encoding.UTF8.GetString(actual.StandardOutputBytes).Trim();
        }
        await Git("init", "-q", "-b", "main");
        BoundedFiles.WriteNew(Path.Combine(repository, "tracked.txt"), Encoding.UTF8.GetBytes("post-retirement " + run.Nonce + "\n"), run.Original);
        await Git("add", "--", "tracked.txt"); await Git("commit", "-q", "-m", "actual post-retirement fixture");
        var baseCommit = await Git("rev-parse", "HEAD");
        var worker = Path.Combine(run.DirectoryPath, "post-retirement-worker");
        var current = true;
        var parent = new ParentEffectAdmission(new object(),
            id => Volatile.Read(ref current) && id == run.Nonce && ParentCoordinationHost.OriginalCanAdmitEffects(run.Original),
            _ => Volatile.Write(ref current, false));
        var manager = new WorktreeManager(gitImage, executor, run.Original).WithParentAdmission(parent, run.Nonce);
        // Bind an actual fresh linked worktree to this SAME original parent;
        // existing repository metadata cannot be adopted as provisioning proof.
        Trace("linked worktree prepare entered");
        await manager.PrepareAsync(repository, worker, baseCommit, run.Original.Token);
        Trace("linked worktree prepare returned");
        var binding = manager.RequireOriginalBinding(worker);
        Assert.Equal(Path.GetFullPath(worker), binding.Worktree);
        Assert.True(File.Exists(Path.Combine(worker, ".git")));
        var beforeObserve = manager.ActualOwnedGitInvocations;
        Trace("linked worktree observe entered");
        var observed = await manager.ObserveAsync(worker, run.Original.Token); run.Original.Check();
        Trace("linked worktree observe returned");
        Assert.Equal(baseCommit, observed.HeadCommit); Assert.True(observed.IsClean);
        Assert.True(manager.ActualOwnedGitInvocations > beforeObserve);
        Assert.InRange(parent.EnteredCount, 1, 1023); Assert.True(parent.AllKnownSettled);
        Assert.False(run.Original.HasExplicitUnknown);
        // Git remains serial under the same original WORK while the already-armed
        // child's observation window elapses. Its paths cannot remove the marker.
        // The child's timer was started before its complete acknowledgement.
        // Observe more than four real seconds AFTER that acknowledgement;
        // root exit/EOF alone cannot pass while it still has this future effect.
        var markerWindow = TimeSpan.FromSeconds(4.5) - Stopwatch.GetElapsedTime(armedObserved);
        if (markerWindow > TimeSpan.Zero) await Task.Delay(markerWindow, run.Original.Token);
        run.Original.Check(); Assert.True(Stopwatch.GetElapsedTime(armedObserved) > TimeSpan.FromSeconds(4));
        Assert.False(File.Exists(Path.Combine(run.DirectoryPath, run.Nonce + "-child-returned")));
        Assert.Equal(originalStarted, run.Original.OriginalStarted); Assert.Equal(whole, run.Original.WholeLimit); Assert.Equal(work, run.Original.WorkLimit);
        Trace("final marker absence verified");
        run.Preserve(closed);
    }
    [Fact(Timeout = 90000)]
    public async Task Parent_exit_and_pipe_EOF_with_surviving_child_never_certify_Linux_group_zero()
    {
        using var affinity = new Affinity(); await using var run = new Run("orphan-parent"); await run.Ready(); var data = new byte[1]; Assert.Equal(0, await run.Session.Output.ReadAsync(data, 0, 1, run.Original.Token)); Assert.True(File.Exists(Path.Combine(run.DirectoryPath, run.Nonce + "-child-entered"))); var closed = await run.Session.RetireAsync(true);
        AssertPhysicalScope(closed);
        if (OperatingSystem.IsLinux())
        {
            // A naive root-exit/EOF implementation returns before this ordinary
            // child writes its four-second marker. Wait past that real effect:
            // only actual group termination prevents it. No fake native zero.
            _ = run.Original.CleanupRemaining; await Task.Delay(4500, TestContext.Current.CancellationToken); _ = run.Original.CleanupRemaining;
            Assert.False(File.Exists(Path.Combine(run.DirectoryPath, run.Nonce + "-child-returned")));
        }
        run.Preserve(closed);
    }
}
