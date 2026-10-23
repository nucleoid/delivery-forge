using System.Text;
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
    static (string Directory, LinuxScopeImage Image) CopyNative(ToolLifetime original)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Actual Linux native image required.");
        const string actual = "/usr/bin/cat";
        var bytes = BoundedFiles.Read(actual, original, 1048576);
        var directory = Path.Combine(Path.GetTempPath(), "forge-exact-image-" + Guid.NewGuid().ToString("N"));
        original.CheckWork(); Directory.CreateDirectory(directory); original.CheckWork();
        var target = Path.Combine(directory, "actual-cat"); BoundedFiles.WriteNew(target, bytes, original);
        original.CheckWork(); File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); original.CheckWork();
        return (directory, new(target, bytes.LongLength, BoundedFiles.Sha(bytes)));
    }
    [Fact(Timeout = 90000, Skip = "Sealed descriptor execution requires Linux.", SkipUnless = nameof(IsLinux))]
    public async Task Actual_duplex_executes_sealed_image_and_survives_selected_path_replacement()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var selected = CopyNative(original); var scope = Scope(original);
        BoundedFiles.ReserveExactLinuxImageReads(new(selected.Image.Path, selected.Image.Bytes, selected.Image.Sha256), original);
        var session = OwnedDuplexProcess.Open(new(selected.Image.Path, [], selected.Directory, ActualOrdinaryCapture.ResourceEnvironment()), original, scope, selected.Image);
        try
        {
            Assert.Null(session.StartupFailure); Assert.True(session.HasCreationOwnedExactLinuxImage);
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
}
