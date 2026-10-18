using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
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
    public async Task Actual_duplex_roundtrip_complete_EOF_and_same_first_retirement()
    {
        using var affinity = new Affinity(); await using var run = new Run("echo"); await run.Ready(); var sent = Encoding.ASCII.GetBytes("actual-duplex-16"); Assert.Equal(16, sent.Length); await run.Session.Input.WriteAsync(sent, 0, sent.Length, run.Original.Token); await run.Session.Input.FlushAsync(run.Original.Token); var actual = new byte[16]; await run.Session.Output.ReadExactlyAsync(actual, run.Original.Token); Assert.Equal(sent, actual); var eof = new byte[1]; Assert.Equal(0, await run.Session.Output.ReadAsync(eof, 0, 1, run.Original.Token)); var first = run.Session.RetireAsync(false); Assert.Same(first, run.Session.RetireAsync(false)); var closed = await first; AssertPhysicalScope(closed); Assert.Equal(0, closed.ExitCode); Assert.True(closed.OutputEof); Assert.All(run.Session.ActualIo, x => Assert.False(x.Unknown)); run.Preserve(closed);
    }
    [Fact(Timeout = 90000)]
    public async Task Actual_blocked_stdout_is_stopped_by_original_owner_not_terminal_text()
    {
        using var affinity = new Affinity(); await using var run = new Run("output-block"); await run.Ready(); var closed = await run.Session.RetireAsync(true); AssertPhysicalScope(closed); Assert.True(closed.RootExited); Assert.False(run.Session.HasUnknownIo); run.Preserve(closed);
    }
    [Fact(Timeout = 90000)]
    public async Task Actual_blocked_stdin_records_original_task_and_cancellation_without_replay()
    {
        using var affinity = new Affinity(); await using var run = new Run("input-block"); await run.Ready(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var data = new byte[65536]; Task? originalWrite = null; for (var index = 0; index < 128; index++) { run.Original.Check(); originalWrite = run.Session.Input.WriteAsync(data, 0, data.Length, cancel.Token); if (!originalWrite.IsCompleted) break; await originalWrite; }
        Assert.NotNull(originalWrite); Assert.False(originalWrite.IsCompleted); Assert.True(run.Session.HasUnknownIo); cancel.Cancel(); var retirement = run.Session.RetireAsync(true); try { await originalWrite; } catch (OperationCanceledException) { } catch (IOException) { }
        var closed = await retirement; Assert.True(closed.RootExited); Assert.Contains(run.Session.ActualIo, x => x.Kind == "stdin-write" && x.ActualTask is not null); Assert.Same(run.Session.RetireAsync(true), run.Session.RetireAsync(true)); if (closed.KnownClosed) Assert.True(closed.InputCloseReturned && closed.DispositionReturned); else Assert.True(run.Session.HasRetainedOriginalOwner); run.Preserve(closed);
    }
    [Fact(Timeout = 90000)]
    public async Task Actual_stderr_overflow_is_retained_failure_not_truncated_success()
    {
        using var affinity = new Affinity(); await using var run = new Run("stderr-overflow"); await run.Ready(); var codec = new DeliveryForge.Tool.Codex.CodexJsonlProtocol(run.Session.Input, run.Session.Output, run.Original); using var terminal = await codec.ReadAsync(TestContext.Current.CancellationToken); Assert.NotNull(terminal); DeliveryForge.Tool.Codex.CodexJsonlProtocol.RequireEnvelope(terminal.RootElement); Assert.Equal("completed", terminal.RootElement.GetProperty("params").GetProperty("turn").GetProperty("status").GetString()); Assert.NotNull(run.Session.OriginalErrorDrainTask); await Assert.ThrowsAsync<IOException>(async () => await run.Session.OriginalErrorDrainTask.WaitAsync(run.Original.Remaining, TestContext.Current.CancellationToken)); var closed = await run.Session.RetireAsync(true); Assert.Throws<IOException>(() => HostTransportRetirement.RequireSuccessful(closed)); Assert.True(closed.RootExited); Assert.Contains(closed.Failures, x => x.Contains("stderr profile", StringComparison.Ordinal)); Assert.True(run.Session.StandardError.Length <= 1048576); run.Preserve(closed);
    }
    [Fact(Timeout = 90000)]
    public async Task Known_actual_creator_failure_retains_frame_and_never_claims_child_exit_zero()
    {
        using var affinity = new Affinity(); await using var run = new Run("echo", missing: true); Assert.True(run.Session.HasRetainedOriginalOwner);
        if (OperatingSystem.IsLinux()) { Assert.Null(run.Session.StartupFailure); var closed = await run.Session.RetireAsync(false); AssertPhysicalScope(closed); Assert.Equal(125, closed.ExitCode); Assert.Throws<IOException>(() => HostTransportRetirement.RequireSuccessful(closed)); run.Preserve(closed); }
        else { Assert.NotNull(run.Session.StartupFailure); var closed = await run.Session.RetireAsync(true); Assert.True(closed.RootExited); Assert.Null(closed.ExitCode); Assert.NotEmpty(closed.Failures); run.Preserve(closed); }
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
