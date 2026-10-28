using System.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using DeliveryForge.Evidence;
namespace DeliveryForge.Tool.Tests;

// Actual Linux native images and the existing creation-owned scope profile.
// These controls do not admit a reviewer, a CLI login, or a protected issuer.
// Acceptance requires BOTH positive rows to pass against the SAME independently
// selected scope/gate/profile before counting the three precise negative rows.
// Root joins actual TRX/profile hashes; no static test flag issues that proof.
[Trait("Category", "InstalledLocalRealAcceptance")]
public sealed class OwnedLinuxExactImageTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();
    [Theory(Timeout = 90000, Skip = "Exact Linux worker admission requires Linux.", SkipUnless = nameof(IsLinux))]
    [InlineData("codex.cli")]
    [InlineData("claude-code.cli")]
    [InlineData("pi.cli")]
    public async Task Actual_worker_port_refuses_oversize_before_path_hash_native_birth_or_protocol(string adapter)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var scope = Scope(original);
        var absent = Path.Combine(Path.GetTempPath(), "forge-worker-oversize-" + Guid.NewGuid().ToString("N"));
        Assert.False(File.Exists(absent)); Assert.False(Directory.Exists(absent));
        var request = DeliveryForge.Execution.WorkerEnvelope.PrepareWorker(new(
            "oversize-fixture", "sha256:" + new string('1', 64), new string('a', 40), "fixture/repo",
            Path.GetFullPath("."), absent + "-worktree", absent + "-results", "fixture-writer-token",
            ["source.cs"], [], adapter, "fixture-v1",
            [DeliveryForge.Execution.Host.AgentCapability.Launch, DeliveryForge.Execution.Host.AgentCapability.CompletionEvidence]));
        var profile = new DeliveryForge.Execution.Host.AgentAdapterProfile(adapter, "fixture-v1",
            new[] { DeliveryForge.Execution.Host.AgentCapability.Launch }.Select(capability =>
                new DeliveryForge.Execution.Host.AgentCapabilityEvidence(capability,
                    DeliveryForge.Execution.Host.AgentCapabilityStatus.Unverified, "negative fixture DATA only", "no authority")).ToArray());
        var git = new DeliveryForge.Execution.Git.WorktreeManager();
        DeliveryForge.Tool.IHostToolSessionPort port = adapter switch
        {
            "codex.cli" => new DeliveryForge.Tool.Codex.CodexAppServerPort(new(absent, OwnedDuplexProcess.MaximumExactLinuxImageBytes + 1, new string('b', 64), "fixture-v1"), git, profile, scope),
            "claude-code.cli" => new DeliveryForge.Tool.Claude.ClaudeStreamPort(new(absent, OwnedDuplexProcess.MaximumExactLinuxImageBytes + 1, new string('b', 64), "fixture-v1"), git, profile, scope),
            "pi.cli" => new DeliveryForge.Tool.Pi.PiRpcPort(new(absent, OwnedDuplexProcess.MaximumExactLinuxImageBytes + 1, new string('b', 64), "fixture-v1"), git, profile, scope),
            _ => throw new InvalidOperationException("Closed worker test selection required.")
        };
        ((IOriginalBoundHostPort)port).BindOriginal(original);
        ((IHostInstructionPort)port).BindInstructions(HostInstructionContext.FromParentSelection(request, "sha256:" + new string('c', 64),
            "Never dispatch this oversized negative fixture.", ["Refuse before image acquisition."], ["No native work."]));
        var refusal = await Assert.ThrowsAsync<ToolUsageException>(() => port.StartAsync(request, original.Remaining, original.Token));
        Assert.Equal("Exact Linux ELF image must fit the selected finite exact-image profile and original kernel cleanup reserve.", refusal.Message);
        switch (port)
        {
            case DeliveryForge.Tool.Codex.CodexAppServerPort codex:
                Assert.Null(codex.OriginalProcess); Assert.Empty(codex.OriginalTasks);
                var startup = codex.SnapshotStartupDiagnostics();
                Assert.Equal(DeliveryForge.Tool.Codex.CodexStartupStage.NativeImageVerification, startup.Stage);
                Assert.Empty(startup.Wire); Assert.Empty(startup.PendingIds); Assert.Null(startup.PumpStatus);
                Assert.False(startup.ThreadReceived); Assert.False(startup.TurnReceived);
                break;
            case DeliveryForge.Tool.Claude.ClaudeStreamPort claude:
                Assert.Null(claude.OriginalProcess); Assert.Empty(claude.OriginalTasks);
                break;
            case DeliveryForge.Tool.Pi.PiRpcPort pi:
                Assert.Null(pi.OriginalProcess); Assert.Empty(pi.ActualOperations);
                Assert.Same(refusal, pi.Primary); Assert.Null(pi.CleanupFailure);
                break;
        }
        Assert.All(port.Profile.Capabilities, capability => Assert.Equal(DeliveryForge.Execution.Host.AgentCapabilityStatus.Unverified, capability.Status));
        // The pathname never existed: this exact extent refusal, rather than a
        // missing-file error, proves the actual Port did not enter pathname hashing.
        Assert.False(File.Exists(absent)); Assert.False(Directory.Exists(absent));
    }
    // Private-method controls over a descriptor opened BY the real kernel owner.
    // No cgroup is bound, no process/descriptor is adopted, and KnownDisposed
    // must remain false: these are first-close facts, never scope authority.
    sealed class CloseFaultBudget(ToolLifetime parent) : DeliveryForge.Evidence.IOwnedDuplexBudget, DeliveryForge.Evidence.IOwnedFailureCleanup
    {
        int workTarget, workChecks;
        internal readonly TimeoutException WorkRefusal = new("Injected actual close-admission WORK refusal.");
        internal readonly IOException NotifyRefusal = new("Injected actual failure-cleanup notification refusal.");
        internal bool ThrowNotification;
        internal int Notifications;
        internal Action? BeforeWorkRefusal;
        internal void Arm(int target, Action witness) { workTarget = target; workChecks = 0; BeforeWorkRefusal = witness; }
        public long OriginalStarted => parent.OriginalStarted;
        public TimeSpan WholeLimit => parent.WholeLimit;
        public TimeSpan WorkLimit => parent.WorkLimit;
        public CancellationToken Token => parent.Token;
        public TimeSpan CleanupRemaining => parent.CleanupRemaining;
        public void CheckWork()
        {
            parent.CheckWork();
            if (workTarget > 0 && ++workChecks == workTarget)
            {
                workTarget = 0; BeforeWorkRefusal?.Invoke(); throw WorkRefusal;
            }
        }
        public void BeginFailureCleanup()
        {
            Notifications++;
            if (ThrowNotification) throw NotifyRefusal; // deterministic BEFORE forwarding/effect.
            parent.BeginFailureCleanup();
        }
    }
    sealed class ActualCloseFrame : IDisposable
    {
        static readonly List<ActualCloseFrame> Retained = [];
        const System.Reflection.BindingFlags Instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        static readonly Type KernelType = typeof(DeliveryForge.Evidence.OwnedDuplexProcess).Assembly.GetType("DeliveryForge.Evidence.LinuxScopeKernel", throwOnError: true)!;
        static readonly Type FdType = KernelType.GetNestedType("OwnedFd", System.Reflection.BindingFlags.NonPublic)!;
        static readonly System.Reflection.ConstructorInfo Constructor = KernelType.GetConstructor(Instance, null, [typeof(DeliveryForge.Evidence.IOwnedDuplexBudget)], null)!;
        static readonly System.Reflection.MethodInfo OpenFd = KernelType.GetMethod("OpenFd", Instance, null, [typeof(string), typeof(int), typeof(bool), FdType], null)!;
        static readonly System.Reflection.MethodInfo CloseTail = KernelType.GetMethod("CloseExactImage", Instance, null, [FdType, typeof(bool), typeof(Exception)], null)!;
        static readonly System.Reflection.MethodInfo FirstClose = KernelType.GetMethod("Close", Instance, null, [FdType, typeof(bool), typeof(bool)], null)!;
        internal readonly ToolLifetime Original = new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        internal readonly CloseFaultBudget Budget;
        internal readonly DeliveryForge.Evidence.IOwnedDuplexBudget ActualBudget;
        readonly object kernel;
        readonly object descriptor;
        internal ActualCloseFrame(bool subordinate = false)
        {
            Budget = new(Original);
            ActualBudget = subordinate ? new DeliveryForge.Evidence.OwnedWorkPhaseBudget(Budget) : Budget;
            Assert.NotNull(Constructor); Assert.NotNull(OpenFd); Assert.NotNull(CloseTail); Assert.NotNull(FirstClose);
            Assert.Equal(typeof(Exception), CloseTail.ReturnType);
            kernel = Constructor.Invoke([ActualBudget]);
            lock (Retained) Retained.Add(this);
            try
            {
                // Actual open/register/pre/post path, not an incoming fd or
                // manufactured cgroup/root identity. No namespace is modified.
                descriptor = Invoke(OpenFd, "/dev/null", 0x20000 | 0x80000, false, null)!;
                Assert.NotNull(descriptor); Assert.True(Unknown); Assert.False(KnownDisposed);
            }
            catch { Original.RetainUnknown(); throw; }
        }
        object? Invoke(System.Reflection.MethodInfo method, params object?[] arguments)
        {
            try { return method.Invoke(kernel, arguments); }
            catch (System.Reflection.TargetInvocationException failure) when (failure.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); throw;
            }
        }
        internal static object? Field(object actual, string name) => actual.GetType().GetField(name, Instance)!.GetValue(actual);
        internal bool Entered => (bool)Field(descriptor, "CloseEntered")!;
        internal bool Invoked => (bool)Field(descriptor, "CloseInvoked")!;
        internal bool Closed => (bool)Field(descriptor, "Closed")!;
        internal bool Unknown => (bool)KernelType.GetProperty("Unknown", Instance)!.GetValue(kernel)!;
        internal bool KnownDisposed => (bool)KernelType.GetProperty("KnownDisposed", Instance)!.GetValue(kernel)!;
        internal object[] CloseCalls => ((System.Collections.IEnumerable)KernelType.GetProperty("ActualCalls", Instance)!.GetValue(kernel)!).Cast<object>().Where(row => (string)Field(row, "Kind")! == "close-original-descriptor").ToArray();
        internal Exception? Finish(bool cleanup) => (Exception?)Invoke(CloseTail, descriptor, cleanup, null);
        internal void RefuseSecondClose() => _ = Invoke(FirstClose, descriptor, true, true);
        public void Dispose()
        {
            try
            {
                // Only a test-injected notification BEFORE any native close
                // may be disabled to finish this test's own first close. An
                // entered close is NEVER resent, even if it failed/was unknown.
                if (!Closed && !Invoked)
                {
                    Budget.ThrowNotification = false;
                    var post = Finish(true);
                    if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
                }
                if (!Closed || Unknown) throw new IOException("Actual test-owned descriptor remains UNKNOWN; retain original.");
                Original.Dispose(); lock (Retained) Retained.Remove(this);
            }
            catch { Original.RetainUnknown(); throw; }
        }
    }
    [Theory(Timeout = 90000, Skip = "Actual kernel first-close controls require Linux.", SkipUnless = nameof(IsLinux))]
    [InlineData(1)] // Close's check, BEFORE its attempt marker.
    [InlineData(2)] // Native's precheck, AFTER attempt marker, BEFORE row/syscall.
    public void Actual_kernel_uninvoked_first_close_continues_once_in_same_cleanup(int workCheck)
    {
        using var frame = new ActualCloseFrame();
        var originalStarted = frame.Original.OriginalStarted;
        bool? enteredAtRefusal = null, invokedAtRefusal = null;
        frame.Budget.Arm(workCheck, () => { enteredAtRefusal = frame.Entered; invokedAtRefusal = frame.Invoked; });
        var post = frame.Finish(false);
        Assert.Same(frame.Budget.WorkRefusal, post);
        Assert.Equal(workCheck == 2, enteredAtRefusal); Assert.False(invokedAtRefusal);
        Assert.True(frame.Entered); Assert.True(frame.Invoked); Assert.True(frame.Closed);
        Assert.False(frame.Unknown); Assert.False(frame.KnownDisposed);
        Assert.Equal(1, frame.Budget.Notifications);
        var close = Assert.Single(frame.CloseCalls);
        Assert.True((bool)ActualCloseFrame.Field(close, "Entered")!); Assert.True((bool)ActualCloseFrame.Field(close, "Returned")!);
        Assert.Equal(0L, ActualCloseFrame.Field(close, "Result"));
        Assert.Null(ActualCloseFrame.Field(close, "Primary")); Assert.Null(ActualCloseFrame.Field(close, "Post"));
        Assert.Throws<TimeoutException>(() => frame.Original.CheckWork());
        Assert.Equal(originalStarted, frame.Original.OriginalStarted);
        var repeat = Assert.Throws<IOException>(() => frame.RefuseSecondClose());
        Assert.Equal("Original scope descriptor first-close already entered; no retry.", repeat.Message);
        Assert.Single(frame.CloseCalls);
    }
    [Fact(Timeout = 90000, Skip = "Actual kernel first-close controls require Linux.", SkipUnless = nameof(IsLinux))]
    public void Actual_kernel_invoked_close_post_failure_never_retries_the_syscall()
    {
        using var frame = new ActualCloseFrame();
        bool? invokedAtRefusal = null, closedAtRefusal = null;
        frame.Budget.Arm(3, () => { invokedAtRefusal = frame.Invoked; closedAtRefusal = frame.Closed; });
        var post = frame.Finish(false);
        Assert.Same(frame.Budget.WorkRefusal, post);
        Assert.True(invokedAtRefusal); Assert.True(closedAtRefusal);
        Assert.True(frame.Closed); Assert.False(frame.Unknown); Assert.False(frame.KnownDisposed);
        Assert.Equal(0, frame.Budget.Notifications); // no fallback once close() entered.
        var close = Assert.Single(frame.CloseCalls);
        Assert.True((bool)ActualCloseFrame.Field(close, "Returned")!); Assert.Equal(0L, ActualCloseFrame.Field(close, "Result"));
        Assert.Same(frame.Budget.WorkRefusal, ActualCloseFrame.Field(close, "Post"));
        Assert.Equal("Original scope descriptor first-close already entered; no retry.", Assert.Throws<IOException>(() => frame.RefuseSecondClose()).Message);
        Assert.Single(frame.CloseCalls);
    }
    [Fact(Timeout = 90000, Skip = "Actual kernel first-close controls require Linux.", SkipUnless = nameof(IsLinux))]
    public void Actual_kernel_cleanup_notification_failure_retains_fd_and_subordinate_work_latch()
    {
        using var frame = new ActualCloseFrame(subordinate: true);
        var phase = Assert.IsType<DeliveryForge.Evidence.OwnedWorkPhaseBudget>(frame.ActualBudget);
        var started = phase.OriginalStarted;
        frame.Budget.ThrowNotification = true;
        var notification = frame.Finish(true);
        Assert.Same(frame.Budget.NotifyRefusal, notification);
        Assert.Equal(1, frame.Budget.Notifications); Assert.Empty(frame.CloseCalls);
        Assert.False(frame.Entered); Assert.False(frame.Invoked); Assert.False(frame.Closed);
        Assert.True(frame.Unknown); Assert.False(frame.KnownDisposed);
        Assert.Equal("Actual subordinate failure closed enclosing work.", Assert.Throws<IOException>(() => phase.CheckWork()).Message);
        Assert.Equal(frame.Original.OriginalStarted, phase.OriginalStarted);
        Assert.Equal(frame.Original.WholeLimit, phase.WholeLimit); Assert.Equal(frame.Original.WorkLimit, phase.WorkLimit);
        // Explicit test-only recovery of its OWN still-uninvoked first close;
        // preserve the first failure above and never assert disposed scope.
        frame.Budget.ThrowNotification = false;
        Assert.Null(frame.Finish(true)); Assert.Same(frame.Budget.NotifyRefusal, notification);
        Assert.True(frame.Closed); Assert.False(frame.Unknown); Assert.False(frame.KnownDisposed);
        Assert.Equal(2, frame.Budget.Notifications); Assert.Single(frame.CloseCalls);
        Assert.Equal(started, phase.OriginalStarted);
        Assert.InRange(phase.CleanupRemaining, TimeSpan.FromTicks(1), phase.WholeLimit);
        Assert.Equal("Actual subordinate failure closed enclosing work.", Assert.Throws<IOException>(() => phase.CheckWork()).Message);
        Assert.Throws<TimeoutException>(() => frame.Original.CheckWork());
    }
    static LinuxScopeSelection Scope(ToolLifetime original)
    {
        var path = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LINUX_SCOPE_PROFILE") ?? throw new IOException("Actual current compiled Linux scope/gate profile required.");
        return BoundedFiles.ReadJson<LinuxScopeSelection>(path, original, 65536);
    }
    static (string Directory, LinuxScopeImage Image) CopyNative(ToolLifetime original, bool maximumExtent = false)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Actual Linux native image required.");
        const string actual = "/usr/bin/cat";
        var bytes = BoundedFiles.Read(actual, original, 1048576);
        var directory = Path.Combine(Path.GetTempPath(), "forge-exact-image-" + Guid.NewGuid().ToString("N"));
        original.CheckWork(); Directory.CreateDirectory(directory); original.CheckWork();
        var target = Path.Combine(directory, "actual-cat");
        long length = bytes.LongLength; string hash;
        if (maximumExtent)
        {
            // Private fixture staging only: preserve the real ELF prefix and
            // extend this fresh file to the inclusive maximum without a whole
            // image array or Tool WriteNew's separate sixteen-MiB output path.
            // This finite test-owned write is not a product write-credit grant.
            original.CheckWork(); Assert.True(length < OwnedDuplexProcess.MaximumExactLinuxImageBytes);
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                original.CheckWork(); output.Write(bytes); original.CheckWork();
                output.SetLength(OwnedDuplexProcess.MaximumExactLinuxImageBytes); original.CheckWork();
                output.Flush(true); original.CheckWork();
            }
            original.CheckWork(); length = OwnedDuplexProcess.MaximumExactLinuxImageBytes;
            using var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long observed = 0;
            while (observed < length)
            {
                original.CheckWork(); var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - observed)); original.CheckWork();
                if (count == 0) throw new IOException("Complete private maximum-image fixture ended early.");
                digest.AppendData(buffer, 0, count); observed += count;
            }
            original.CheckWork(); Assert.Equal(-1, input.ReadByte()); original.CheckWork(); Assert.Equal(length, input.Length);
            hash = Convert.ToHexStringLower(digest.GetHashAndReset()); original.CheckWork();
        }
        else { BoundedFiles.WriteNew(target, bytes, original); hash = BoundedFiles.Sha(bytes); }
        original.CheckWork(); File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); original.CheckWork();
        return (directory, new(target, length, hash));
    }
    [Fact(Timeout = 90000, Skip = "Sealed descriptor execution requires Linux.", SkipUnless = nameof(IsLinux))]
    public async Task Actual_duplex_executes_sealed_image_and_survives_selected_path_replacement()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var selected = CopyNative(original, maximumExtent: true); var scope = Scope(original);
        Assert.Equal((long)OwnedDuplexProcess.MaximumExactLinuxImageBytes, selected.Image.Bytes);
        BoundedFiles.ReserveExactLinuxImageReads(new(selected.Image.Path, selected.Image.Bytes, selected.Image.Sha256), original);
        var session = OwnedDuplexProcess.Open(new(selected.Image.Path, ["/proc/self/cgroup", "/proc/self/stat", "-"], selected.Directory, ActualOrdinaryCapture.ResourceEnvironment()), original, scope, selected.Image);
        try
        {
            Assert.Null(session.StartupFailure); Assert.True(session.HasCreationOwnedExactLinuxImage);
            // After actual RELEASE, this same cat reports its own membership
            // and PID, then waits on original stdin. These rows are observation
            // DATA only: ownership still comes solely from the retained session.
            var membership = await ActualLfRow(session, original, 4096);
            var stat = await ActualLfRow(session, original, 8192);
            Assert.StartsWith("0::/", membership, StringComparison.Ordinal);
            var group = membership[3..]; var components = group[1..].Split('/');
            Assert.All(components, part => Assert.True(part.Length > 0 && part is not ("." or "..") && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' or ':')));
            var unit = components[^1]; const string prefix = "delivery-forge-";
            Assert.True(unit.StartsWith(prefix, StringComparison.Ordinal) && unit.EndsWith(".scope", StringComparison.Ordinal));
            var identifier = unit[prefix.Length..^6]; Assert.True(identifier.Length == 32 && identifier.All(char.IsAsciiHexDigitLower));
            var space = stat.IndexOf(' '); Assert.True(space > 0);
            Assert.True(int.TryParse(stat[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var actualPid) && actualPid > 0);
            original.CheckWork(); var linkBytes = new byte[4096];
            var linkLength = readlink("/proc/" + actualPid.ToString(CultureInfo.InvariantCulture) + "/exe", linkBytes, (nuint)linkBytes.Length); original.CheckWork();
            Assert.True(linkLength > 0 && linkLength < linkBytes.Length);
            Assert.Equal("/memfd:delivery-forge-exact-native (deleted)", new UTF8Encoding(false, true).GetString(linkBytes, 0, checked((int)linkLength)));
            var kernel = "/sys/fs/cgroup" + group;
            var current = ulong.Parse(ActualKernelText(kernel + "/memory.current", original, 64).TrimEnd('\n'), NumberStyles.None, CultureInfo.InvariantCulture);
            var maximum = ulong.Parse(ActualKernelText(kernel + "/memory.max", original, 64).TrimEnd('\n'), NumberStyles.None, CultureInfo.InvariantCulture);
            var memoryRows = ActualKernelText(kernel + "/memory.stat", original, 16384).Split('\n');
            var shmemRow = Assert.Single(memoryRows, row => row.StartsWith("shmem ", StringComparison.Ordinal));
            var shmem = ulong.Parse(shmemRow[6..], NumberStyles.None, CultureInfo.InvariantCulture);
            Assert.Equal(4294967296UL, maximum); Assert.InRange(current, 1UL, maximum); Assert.True(shmem >= (ulong)selected.Image.Bytes);
            TestContext.Current.TestOutputHelper!.WriteLine("sealed-image-observation|bytes=" + selected.Image.Bytes.ToString(CultureInfo.InvariantCulture) + "|memory.current=" + current.ToString(CultureInfo.InvariantCulture) + "|shmem=" + shmem.ToString(CultureInfo.InvariantCulture) + "|memory.max=" + maximum.ToString(CultureInfo.InvariantCulture));
            // Change only this fixture's selected pathname after actual sealed-fd
            // verification/release. No system image or other process is touched.
            original.CheckWork(); File.WriteAllBytes(selected.Image.Path, Encoding.ASCII.GetBytes("not an executable anymore\n")); original.CheckWork();
            var sent = Encoding.ASCII.GetBytes("same sealed native image\n");
            await session.Input.WriteAsync(sent, 0, sent.Length, original.Token); await session.Input.FlushAsync(original.Token);
            var received = new byte[sent.Length]; await session.Output.ReadExactlyAsync(received, original.Token); Assert.Equal(sent, received);
            session.CloseInputForCapture(); var end = new byte[1]; Assert.Equal(0, await session.Output.ReadAsync(end, 0, 1, original.Token));
            var retired = await session.RetireAsync(false); HostTransportRetirement.RequireSuccessful(retired);
            Assert.True(retired.RootExited && retired.KnownClosed && retired.OutputEof && retired.ErrorEof && retired.DispositionReturned); Assert.Equal(0u, retired.ActiveProcesses);
        }
        finally { var closed = await session.RetireAsync(true); if (closed.KnownClosed) original.Dispose(); }
        // Retain the exact changed fixture namespace as evidence; no recursive deletion.
    }
    [Fact(Timeout = 90000, Skip = "Sealed descriptor execution requires Linux.", SkipUnless = nameof(IsLinux))]
    public async Task Actual_ordinary_capture_uses_same_exact_image_scope_and_complete_retirement()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity(); using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var selected = CopyNative(original); var scope = Scope(original); var executor = new ProcessCommandExecutor(scope, original);
        BoundedFiles.ReserveExactLinuxImageReads(new(selected.Image.Path, selected.Image.Bytes, selected.Image.Sha256), original);
        var actual = await executor.ExecuteBoundedCaptureAsync(new(selected.Image.Path, [], selected.Directory, ActualOrdinaryCapture.ResourceEnvironment()), TimeSpan.FromSeconds(15), 65536, original.Token, new OwnedWorkPhaseBudget(original), scope, selected.Image);
        ActualOrdinaryCapture.Require(actual); Assert.Equal(0, actual.Command.ExitCode); Assert.Empty(actual.StandardOutputBytes); Assert.Equal(BoundedCaptureOwnershipKind.LinuxCreationOwnedScope, actual.OwnershipKind);
    }
    [Theory(Timeout = 90000, Skip = "Sealed descriptor execution requires Linux.", SkipUnless = nameof(IsLinux))]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("script")]
    public async Task Actual_unmatched_or_script_image_never_enters_target_or_path_fallback(string mode)
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var selected = CopyNative(original); var marker = Path.Combine(selected.Directory, "target-entered"); var pin = selected.Image;
        var expectedCode = mode switch { "hash" => "hash", "length" => "extent", "script" => "elf", _ => throw new ArgumentException("Closed negative image case.", nameof(mode)) };
        if (mode == "hash") pin = pin with { Sha256 = new string('0', 64) };
        else if (mode == "length") pin = pin with { Bytes = pin.Bytes + 1 };
        else
        {
            var bytes = Encoding.ASCII.GetBytes("#!/bin/sh\nprintf forbidden > \"$1\"\n#" + new string('x', 128) + "\n");
            original.CheckWork(); File.WriteAllBytes(pin.Path, bytes); original.CheckWork(); pin = pin with { Bytes = bytes.LongLength, Sha256 = BoundedFiles.Sha(bytes) };
        }
        var session = OwnedDuplexProcess.Open(new(pin.Path, [marker], selected.Directory, ActualOrdinaryCapture.ResourceEnvironment()), original, Scope(original), pin);
        try
        {
            Assert.NotNull(session.StartupFailure); Assert.False(session.HasCreationOwnedExactLinuxImage); Assert.False(File.Exists(marker));
            var retired = await session.RetireAsync(true); Assert.False(File.Exists(marker));
            var actualErrorDrain = session.OriginalErrorDrainTask;
            Assert.NotNull(actualErrorDrain);
            await actualErrorDrain.WaitAsync(original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.False(session.ErrorQuotaExceeded);
            var refusalRows = new UTF8Encoding(false, true).GetString(session.StandardError).Split('\n')
                .Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("DF_SCOPE_EXACT_IMAGE_REFUSAL|", StringComparison.Ordinal)).ToArray();
            Assert.Equal(["DF_SCOPE_EXACT_IMAGE_REFUSAL|" + expectedCode], refusalRows);
            // The fixed NONREADY refusal preserves the actual group until
            // its original parent binds it; UNKNOWN never counts as this row.
            Assert.True(retired.KnownClosed && retired.RootExited && retired.OutputEof && retired.ErrorEof && retired.DispositionReturned);
            Assert.Equal(0u, retired.ActiveProcesses); Assert.NotEqual(0, retired.ExitCode);
        }
        finally { var closed = await session.RetireAsync(true); if (closed.KnownClosed) original.Dispose(); }
    }

    [Fact(Timeout = 90000, Skip = "Parent descriptor seals require Linux.", SkipUnless = nameof(IsLinux))]
    public async Task Actual_unsealed_descriptor_is_refused_by_parent_kernel_check_before_release()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var selected = CopyNative(original); var marker = Path.Combine(selected.Directory, "target-entered");
        var binding = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DUPLEX_FIXTURE_MANIFEST") ?? throw new IOException("Fresh actual compiled duplex fixture manifest is REQUIRED.");
        var fixture = BoundedFiles.ReadJson<OwnedDuplexProcessTests.FixtureBinding>(binding, original, 65536);
        if (fixture.Files.Count != 5) throw new IOException("Complete five-file negative fixture build output is required.");
        var directory = Path.GetDirectoryName(fixture.Image)!;
        var actualFiles = Directory.GetFiles(directory).Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToArray();
        if (!actualFiles.SequenceEqual(fixture.Files.Select(file => Path.GetFullPath(file.Path)).Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new IOException("Actual negative fixture build membership differs.");
        foreach (var file in fixture.Files)
        {
            var bytes = BoundedFiles.Read(file.Path, original);
            if (bytes.LongLength != file.Bytes || BoundedFiles.Sha(bytes) != file.Sha256) throw new IOException("Actual negative fixture output changed.");
        }
        var closure = fixture.Files.Select(file => new LinuxScopeImage(file.Path, file.Bytes, file.Sha256)).ToArray();
        var scope = Scope(original) with
        {
            BirthGate = closure.Single(file => Path.GetFullPath(file.Path) == Path.GetFullPath(fixture.Image)),
            BirthGateClosure = Array.AsReadOnly(closure),
            GateBuildReceipt = fixture.NegativeGateBuildReceipt ?? throw new IOException("Fresh genuine negative fixture compiled-source/PDB Root proof required.")
        };
        BoundedFiles.ReserveExactLinuxImageReads(new(selected.Image.Path, selected.Image.Bytes, selected.Image.Sha256), original);
        var session = OwnedDuplexProcess.Open(new(selected.Image.Path, [marker], selected.Directory, ActualOrdinaryCapture.ResourceEnvironment()), original, scope, selected.Image);
        try
        {
            var refusal = Assert.IsType<IOException>(session.StartupFailure);
            // The generic negative-purpose barrier, wrong fd/READY, early EOF,
            // setup failure or UNKNOWN must never satisfy this kernel witness.
            Assert.Equal("Complete actual immutable content/extent/exec/seal-set required.", refusal.Message);
            Assert.False(session.HasCreationOwnedExactLinuxImage); Assert.False(File.Exists(marker));
            var retired = await session.RetireAsync(true);
            Assert.True(retired.KnownClosed && retired.RootExited && retired.OutputEof && retired.ErrorEof && retired.DispositionReturned, string.Join(";", retired.Failures));
            Assert.Equal(0u, retired.ActiveProcesses); Assert.NotEqual(0, retired.ExitCode); Assert.False(File.Exists(marker));
        }
        finally { var closed = await session.RetireAsync(true); if (closed.KnownClosed) original.Dispose(); }
        // Keep the native input/marker namespace. Parent exact refusal plus
        // releaseReturned=false proves no release; marker absence alone does not.
    }

    [Fact(Timeout = 90000, Skip = "Sealed descriptor execution requires Linux.", SkipUnless = nameof(IsLinux))]
    public Task Actual_pre_release_path_replacement_preserves_original_sealed_descriptor_before_negative_release_barrier() => ActualCopyFixture("replace-before-ready");
    [Fact(Timeout = 90000, Skip = "Sealed descriptor execution requires Linux.", SkipUnless = nameof(IsLinux))]
    public Task Actual_copy_cancellation_after_partial_memfd_never_publishes_ready_or_enters_target() => ActualCopyFixture("cancel-mid-copy");
    [Fact(Timeout = 90000, Skip = "Parent sealed descriptor identity requires Linux.", SkipUnless = nameof(IsLinux))]
    public Task Actual_forged_ready_fd_with_other_fully_sealed_identity_is_refused_before_release() => ActualCopyFixture("forged-sealed-ready");
    static async Task ActualCopyFixture(string mode)
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var selected = CopyNative(original, maximumExtent: mode == "cancel-mid-copy"); var marker = Path.Combine(selected.Directory, "target-entered");
        var production = Scope(original);
        foreach (var row in production.BirthGateClosure)
        {
            var bytes = BoundedFiles.Read(row.Path, original);
            if (bytes.LongLength != row.Bytes || BoundedFiles.Sha(bytes) != row.Sha256) throw new IOException("Actual production gate bytes differ before fixture SUT call.");
        }
        var dll = Assert.Single(production.BirthGateClosure, row => Path.GetFileName(row.Path) == "DeliveryForge.LinuxScopeBirthGate.dll");
        var path = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DUPLEX_FIXTURE_MANIFEST") ?? throw new IOException("Current actual negative fixture binding required.");
        var fixture = BoundedFiles.ReadJson<OwnedDuplexProcessTests.FixtureBinding>(path, original, 65536);
        if (fixture.Files.Count != 5) throw new IOException("Complete actual negative fixture5 required.");
        var actual = Directory.GetFiles(Path.GetDirectoryName(fixture.Image)!).Select(Path.GetFullPath).Order(StringComparer.Ordinal);
        if (!actual.SequenceEqual(fixture.Files.Select(row => Path.GetFullPath(row.Path)).Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new IOException("Whole actual negative fixture membership differs.");
        foreach (var row in fixture.Files)
        {
            var bytes = BoundedFiles.Read(row.Path, original);
            if (bytes.LongLength != row.Bytes || BoundedFiles.Sha(bytes) != row.Sha256) throw new IOException("Actual negative fixture bytes differ.");
        }
        var closure = fixture.Files.Select(row => new LinuxScopeImage(row.Path, row.Bytes, row.Sha256)).ToArray();
        var scope = production with { BirthGate = closure.Single(row => row.Path == fixture.Image), BirthGateClosure = Array.AsReadOnly(closure), GateBuildReceipt = fixture.NegativeGateBuildReceipt ?? throw new IOException("Actual negative fixture source/PDB proof required.") };
        BoundedFiles.ReserveExactLinuxImageReads(new(selected.Image.Path, selected.Image.Bytes, selected.Image.Sha256), original);
        var session = OwnedDuplexProcess.Open(new(selected.Image.Path, [marker, mode, dll.Path, dll.Bytes.ToString(CultureInfo.InvariantCulture), dll.Sha256], selected.Directory, ActualOrdinaryCapture.ResourceEnvironment()), original, scope, selected.Image);
        try
        {
            var refused = Assert.IsType<IOException>(session.StartupFailure);
            Assert.Equal(mode switch { "replace-before-ready" => "Negative gate-build profile may never release a target.", "forged-sealed-ready" => "Actual sealed image identity/extent/mode differs.", _ => "Actual private birth identity/clock differs." }, refused.Message);
            Assert.False(session.HasCreationOwnedExactLinuxImage); Assert.False(File.Exists(marker));
            if (mode == "replace-before-ready") Assert.Equal("private pre-release replacement\n", ActualKernelText(selected.Image.Path, original, 4096, failurePhase: true));
            var retired = await session.RetireAsync(true);
            Assert.True(retired.KnownClosed && retired.RootExited && retired.OutputEof && retired.ErrorEof && retired.DispositionReturned, string.Join(";", retired.Failures)); Assert.Equal(0u, retired.ActiveProcesses);
            var drain = session.OriginalErrorDrainTask; Assert.NotNull(drain); await drain.WaitAsync(original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.False(session.ErrorQuotaExceeded);
            var lines = new UTF8Encoding(false, true).GetString(session.StandardError).Split('\n').Select(row => row.TrimEnd('\r')).ToArray();
            if (mode == "replace-before-ready") Assert.Single(lines, row => row == "DF_SCOPE_PRE_RELEASE_REPLACED|" + selected.Image.Bytes.ToString(CultureInfo.InvariantCulture) + "|" + selected.Image.Sha256);
            else if (mode == "forged-sealed-ready")
            {
                var line = Assert.Single(lines, row => row.StartsWith("DF_SCOPE_FORGED_SEALED_READY|", StringComparison.Ordinal)); var fields = line.Split('|'); Assert.Equal(15, fields.Length);
                Assert.All(fields.Skip(1), field => Assert.True(field.Length is > 0 and <= 20 && field.All(char.IsAsciiDigit)));
                Assert.True(int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var firstFd) && firstFd > 2);
                Assert.True(int.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out var secondFd) && secondFd > 2); Assert.NotEqual(firstFd, secondFd);
                Assert.True(ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var firstInode) && firstInode > 0);
                Assert.True(ulong.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var secondInode) && secondInode > 0);
                Assert.True(fields[2] != fields[9] || fields[3] != fields[10] || fields[4] != fields[11]);
                foreach (var start in new[] { 1, 8 })
                {
                    Assert.True(uint.TryParse(fields[start + 2], NumberStyles.None, CultureInfo.InvariantCulture, out _)); Assert.True(uint.TryParse(fields[start + 3], NumberStyles.None, CultureInfo.InvariantCulture, out _));
                    Assert.Equal(selected.Image.Bytes.ToString(CultureInfo.InvariantCulture), fields[start + 4]);
                    Assert.True(ushort.TryParse(fields[start + 5], NumberStyles.None, CultureInfo.InvariantCulture, out var actualMode) && (actualMode & 0xf000) == 0x8000 && (actualMode & 0x1ff) == 0x140);
                    Assert.True(int.TryParse(fields[start + 6], NumberStyles.None, CultureInfo.InvariantCulture, out var actualSeals) && (actualSeals & 0x2f) == 0x2f);
                }
                Assert.NotEqual(0, retired.ExitCode); Assert.False(session.HasUnknownIo);
                Assert.All(session.ActualIo, row => { Assert.True(row.InvocationReturned); Assert.False(row.Unknown); if (row.ActualTask is { } task) Assert.True(task.IsCompleted); });
            }
            else
            {
                var line = Assert.Single(lines, row => row.StartsWith("DF_SCOPE_COPY_CANCELLED|", StringComparison.Ordinal)); var fields = line.Split('|'); Assert.Equal(5, fields.Length);
                Assert.True(long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var partial) && partial > 0 && partial < selected.Image.Bytes);
                Assert.Equal(selected.Image.Bytes.ToString(CultureInfo.InvariantCulture), fields[2]); Assert.Equal(selected.Image.Sha256, fields[3]); Assert.Equal("routine-callback", fields[4]);
            }
            Assert.False(File.Exists(marker));
        }
        finally { var retired = await session.RetireAsync(true); if (retired.KnownClosed) original.Dispose(); }
        // Native process retirement closes any descriptor retained after the
        // canceled check; no assertion claims Create's failed first-close ran.
    }

    [Fact(Timeout = 90000, Skip = "Actual ordinary creation-owned scope requires Linux.", SkipUnless = nameof(IsLinux))]
    public async Task Actual_ordinary_scope_preserves_original_kernel_ledger_for_one_thousand_live_observations()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var selected = CopyNative(original); var scope = Scope(original);
        // Deliberately use the ordinary public overload: no exact-image pin.
        // This is compressed native observation coverage, not a change to the
        // product polling cadence or a fifteen-minute wall-clock claim.
        var session = OwnedDuplexProcess.Open(new(selected.Image.Path, [], selected.Directory, ActualOrdinaryCapture.ResourceEnvironment()), original, scope);
        try
        {
            Assert.Null(session.StartupFailure); Assert.False(session.HasCreationOwnedExactLinuxImage);
            for (var index = 0; index < 1000; index++)
            {
                original.CheckWork(); var actual = session.ObserveActualNative(); original.CheckWork();
                Assert.True(actual.Known, "Ordinary observation " + index.ToString(CultureInfo.InvariantCulture) + ": " + actual.Detail);
                Assert.False(actual.RootExited); Assert.True(actual.ActiveProcesses > 0);
            }
            session.CloseInputForCapture(); var end = new byte[1]; Assert.Equal(0, await session.Output.ReadAsync(end, 0, 1, original.Token));
            var retired = await session.RetireAsync(false); HostTransportRetirement.RequireSuccessful(retired);
            Assert.True(retired.KnownClosed && retired.RootExited && retired.OutputEof && retired.ErrorEof && retired.DispositionReturned); Assert.Equal(0u, retired.ActiveProcesses);
        }
        finally { var retired = await session.RetireAsync(true); if (retired.KnownClosed) original.Dispose(); }
    }

    public static bool CodexSelfReexecDiagnosticSelected => OperatingSystem.IsLinux() &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DELIVERY_FORGE_DIAGNOSTIC_CODEX_IMAGE"));

    [Fact(Timeout = 180000, Skip = "Explicit Linux official-image diagnostic selection required.", SkipUnless = nameof(CodexSelfReexecDiagnosticSelected))]
    public async Task Diagnostic_official_Codex_sandbox_reexec_matches_path_and_sealed_image()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux diagnostic only.");
        using var original = new ToolLifetime(TimeSpan.FromSeconds(180), TestContext.Current.CancellationToken);
        var status = ActualKernelText("/proc/self/status", original, 16384);
        Assert.Equal("NoNewPrivs:\t1", Assert.Single(status.Split('\n'), row => row.StartsWith("NoNewPrivs:", StringComparison.Ordinal)));
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        var scope = Scope(original);
        // Explicit DIAGNOSTIC ordinary profile, never silently clear a v3 policy.
        // This comparison issues no runtime trust, production admission or model turn.
        Assert.Null(scope.RestrictiveChildPolicy);
        var path = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DIAGNOSTIC_CODEX_IMAGE")!;
        Assert.True(Path.IsPathFullyQualified(path));
        var image = new LinuxScopeImage(path, 296885960, "70ea33b7094a4c20c007d09b83bb50e33debb9bbf99bf066709f9eb85bad524a");
        var directory = Path.Combine(Path.GetTempPath(), "forge-codex-reexec-" + Guid.NewGuid().ToString("N"));
        original.CheckWork(); Assert.False(Directory.Exists(directory)); Assert.False(File.Exists(directory));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); original.CheckWork();
        var environment = ActualOrdinaryCapture.ResourceEnvironment();
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        Assert.True(!string.IsNullOrWhiteSpace(dotnetRoot) && Path.IsPathFullyQualified(dotnetRoot));
        environment["DOTNET_ROOT"] = dotnetRoot!; // Existing framework-dependent birth-gate runtime only.
        foreach (var key in new[] { "HOME", "CODEX_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "TMPDIR" }) environment[key] = directory;
        environment["PATH"] = "/usr/bin:/bin"; environment["LANG"] = "C.UTF-8"; environment["LC_ALL"] = "C.UTF-8";
        environment["XDG_RUNTIME_DIR"] = "/run/user/1000"; environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus";
        var executor = new ProcessCommandExecutor(scope, original);
        async Task<BoundedCommandCaptureResult> Capture(string label, string[] arguments, bool sealedImage)
        {
            // Four fresh source hashes plus two gate-copy/parent-verification pairs:
            // 8*(296885960+1)=2375087688 image-read bytes, same original credit.
            BoundedFiles.VerifyNativeImage(new(image.Path, image.Bytes, image.Sha256), original);
            if (sealedImage) BoundedFiles.ReserveExactLinuxImageReads(new(image.Path, image.Bytes, image.Sha256), original);
            var invocation = new CommandInvocation(path, arguments, directory, environment, InheritEnvironment: false) { EnvironmentIsComplete = true };
            var actual = await executor.ExecuteBoundedCaptureAsync(invocation, TimeSpan.FromSeconds(30), 16384,
                TestContext.Current.CancellationToken, new OwnedWorkPhaseBudget(original), scope, sealedImage ? image : null);
            // Emit bounded raw captures and observed closure before any outcome assertion.
            TestContext.Current.TestOutputHelper!.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                Kind = "DiagnosticCodexSelfReexecCapture-v1", Label = label, SealedImageRequested = sealedImage,
                actual.Command.ExitCode, actual.Command.TimedOut, actual.Command.Cancelled, actual.Command.OwnedProcessQuiescent,
                actual.RootExitObserved, actual.StandardOutputDrainCompleted, actual.StandardErrorDrainCompleted,
                actual.CaptureBoundExceeded, actual.OwnershipKind, actual.Timing,
                NativeFailure = actual.Command.LifecycleDiagnostics?.NativeFailure,
                OwnedTree = actual.Command.LifecycleDiagnostics?.OwnedTree,
                StandardOutputBase64 = Convert.ToBase64String(actual.StandardOutputBytes),
                StandardErrorBase64 = Convert.ToBase64String(actual.StandardErrorBytes)
            }, BoundedFiles.Json));
            // Unknown native/IO disposition or exceeded capture stops all later calls.
            ActualOrdinaryCapture.Require(actual); original.CheckWork(); return actual;
        }
        var pathHelp = await Capture("path-help", ["sandbox", "linux", "--help"], false);
        var sealedHelp = await Capture("sealed-help", ["sandbox", "linux", "--help"], true);
        foreach (var help in new[] { pathHelp, sealedHelp })
        {
            Assert.Equal(0, help.Command.ExitCode);
            Assert.Contains("Usage:", help.Command.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("sandbox linux", help.Command.StandardOutput, StringComparison.Ordinal);
            Assert.True(help.Command.StandardOutput.Contains("[COMMAND]", StringComparison.Ordinal) ||
                help.Command.StandardOutput.Contains("<COMMAND>", StringComparison.Ordinal));
        }
        // Actual help must establish the grammar before either helper-spawning call.
        var pathCommand = await Capture("path-sandbox-true", ["sandbox", "linux", "--", "/usr/bin/true"], false);
        var sealedCommand = await Capture("sealed-sandbox-true", ["sandbox", "linux", "--", "/usr/bin/true"], true);
        Assert.Equal(0, pathCommand.Command.ExitCode); Assert.Equal(0, sealedCommand.Command.ExitCode);
        // Retain this fresh private directory as diagnostic evidence; no recursive cleanup.
    }

    static async Task<string> ActualLfRow(OwnedDuplexProcess session, ToolLifetime original, int maximum)
    {
        var bytes = new byte[maximum]; var one = new byte[1]; var used = 0;
        while (used < bytes.Length)
        {
            original.CheckWork(); var count = await session.Output.ReadAsync(one, 0, 1, original.Token); original.CheckWork();
            Assert.Equal(1, count);
            if (one[0] == 10) return new UTF8Encoding(false, true).GetString(bytes, 0, used);
            bytes[used++] = one[0];
        }
        throw new IOException("Actual observation row exceeds its finite width.");
    }
    // Fixed bounded proc/sysfs observations cannot use File.Length (pseudo
    // files report zero). No PID adoption, signaling or ownership is exposed.
    sealed class KernelTextRead(ToolLifetime original, int maximum)
    {
        internal readonly ToolLifetime Original = original;
        internal readonly byte[] Bytes = new byte[maximum + 1];
        internal int Descriptor = -1;
        internal bool Entered, Returned, CloseEntered, Closed;
    }
    static readonly List<KernelTextRead> UnknownKernelReads = [];
    static string ActualKernelText(string path, ToolLifetime original, int maximum, bool failurePhase = false)
    {
        void Check() { if (failurePhase) _ = original.CleanupRemaining; else original.CheckWork(); }
        Check(); var frame = new KernelTextRead(original, maximum);
        Exception? primary = null, disposition = null, post = null; var used = 0; var cleanup = failurePhase;
        try
        {
            Check(); frame.Entered = true; frame.Descriptor = open(path, 0x20000 | 0x80000); frame.Returned = true; Check();
            if (frame.Descriptor < 0) throw new IOException("Actual kernel observation open refused.");
            var chunk = new byte[Math.Min(4096, frame.Bytes.Length)];
            while (used < frame.Bytes.Length)
            {
                var requested = Math.Min(chunk.Length, frame.Bytes.Length - used);
                Check(); var count = read(frame.Descriptor, chunk, (nuint)requested); Check();
                if (count < 0 || count > requested) throw new IOException("Actual kernel observation read refused.");
                if (count == 0) break;
                Buffer.BlockCopy(chunk, 0, frame.Bytes, used, checked((int)count)); used += checked((int)count);
            }
            if (used > maximum) throw new IOException("Actual complete kernel observation exceeded its finite width.");
            Check();
        }
        catch (Exception failure) { primary = failure; cleanup = true; }
        if (frame.Descriptor >= 0)
        {
            try
            {
                if (!cleanup) { try { Check(); } catch (Exception admission) { disposition = admission; cleanup = true; } }
                if (cleanup) { original.BeginFailureCleanup(); _ = original.CleanupRemaining; }
                frame.CloseEntered = true; frame.Closed = close(frame.Descriptor) == 0;
                if (!frame.Closed) throw new IOException("Actual kernel observation first-close refused; no retry.");
            }
            catch (Exception failure) { disposition = disposition is null ? failure : new AggregateException(disposition, failure); cleanup = true; }
        }
        try { if (cleanup) { original.BeginFailureCleanup(); _ = original.CleanupRemaining; } else Check(); }
        catch (Exception failure) { post = failure; }
        if (frame.Entered && !frame.Returned || frame.Descriptor >= 0 && !frame.Closed)
        {
            original.RetainUnknown(); lock (UnknownKernelReads) UnknownKernelReads.Add(frame);
        }
        var failures = new[] { primary, disposition, post }.OfType<Exception>().ToArray();
        if (failures.Length > 1) throw new AggregateException("Actual kernel observation, first-close and original endpoint post.", failures);
        if (failures.Length == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        return new UTF8Encoding(false, true).GetString(frame.Bytes, 0, used);
    }
    [DllImport("libc", SetLastError = true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, [Out] byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] static extern nint readlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer, nuint count);
}
