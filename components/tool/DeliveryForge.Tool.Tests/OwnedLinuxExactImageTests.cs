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
        if (maximumExtent)
        {
            // Preserve the actual ELF prefix and zero-pad only this private
            // copy. The selected hash/extent cover EVERY padded byte; this
            // exercises the inclusive maximum without a new fixture row.
            original.CheckWork(); Assert.True(bytes.LongLength < OwnedDuplexProcess.MaximumExactLinuxImageBytes);
            Array.Resize(ref bytes, checked((int)OwnedDuplexProcess.MaximumExactLinuxImageBytes)); original.CheckWork();
        }
        var directory = Path.Combine(Path.GetTempPath(), "forge-exact-image-" + Guid.NewGuid().ToString("N"));
        original.CheckWork(); Directory.CreateDirectory(directory); original.CheckWork();
        var target = Path.Combine(directory, "actual-cat"); BoundedFiles.WriteNew(target, bytes, original);
        original.CheckWork(); File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); original.CheckWork();
        original.CheckWork(); var hash = BoundedFiles.Sha(bytes); original.CheckWork();
        return (directory, new(target, bytes.LongLength, hash));
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
            Assert.Equal(mode == "replace-before-ready" ? "Negative gate-build profile may never release a target." : "Actual private birth identity/clock differs.", refused.Message);
            Assert.False(session.HasCreationOwnedExactLinuxImage); Assert.False(File.Exists(marker));
            if (mode == "replace-before-ready") Assert.Equal("private pre-release replacement\n", ActualKernelText(selected.Image.Path, original, 4096, failurePhase: true));
            var retired = await session.RetireAsync(true);
            Assert.True(retired.KnownClosed && retired.RootExited && retired.OutputEof && retired.ErrorEof && retired.DispositionReturned, string.Join(";", retired.Failures)); Assert.Equal(0u, retired.ActiveProcesses);
            var drain = session.OriginalErrorDrainTask; Assert.NotNull(drain); await drain.WaitAsync(original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.False(session.ErrorQuotaExceeded);
            var lines = new UTF8Encoding(false, true).GetString(session.StandardError).Split('\n').Select(row => row.TrimEnd('\r')).ToArray();
            if (mode == "replace-before-ready") Assert.Single(lines, row => row == "DF_SCOPE_PRE_RELEASE_REPLACED|" + selected.Image.Bytes.ToString(CultureInfo.InvariantCulture) + "|" + selected.Image.Sha256);
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
