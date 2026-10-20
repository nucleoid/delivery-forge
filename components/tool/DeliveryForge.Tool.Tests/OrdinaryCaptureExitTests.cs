using System.Text;
using DeliveryForge.Evidence;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// Actual Windows creation exercises the common retirement classification. This
// does not manufacture or certify a Linux scope, producer, gate or host profile.
public sealed class OrdinaryCaptureExitTests
{
    [Theory(Timeout = 45000)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task Known_nonzero_command_outcome_keeps_work_only_for_ordinary_capture(
        bool ordinaryCapture, bool explicitFailureCleanup, bool workClosed)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows creator for common capture retirement; real Linux gate acceptance is separate.");
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var started = original.OriginalStarted; var work = original.WorkLimit; var whole = original.WholeLimit;
        OwnedDuplexProcess? session = null;
        try
        {
            var image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var invocation = new CommandInvocation(image,
                ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('ordinary-outcome'); [Console]::Error.Write('expected-nonzero'); exit 7"],
                Path.GetTempPath(), new Dictionary<string, string>
                {
                    ["DOTNET_PROCESSOR_COUNT"] = "2", ["DOTNET_GCHeapHardLimit"] = "0x100000000"
                });
            session = ordinaryCapture ? OwnedDuplexProcess.OpenCapture(invocation, original, null, 65536) : OwnedDuplexProcess.Open(invocation, original);
            Assert.Null(session.StartupFailure);
            session.CloseInputForCapture();
            using var output = new MemoryStream();
            var drain = session.Output.CopyToAsync(output, 256, original.Token);
            session.AttachOriginalOutputConsumer(drain);
            await drain.WaitAsync(original.Remaining, original.Token);
            await (session.OriginalErrorDrainTask ?? throw new IOException("Actual stderr task absent.")).WaitAsync(original.Remaining, original.Token);
            DuplexNativeObservation native;
            do
            {
                original.Check(); native = session.ObserveActualNative(); Assert.True(native.Known, native.Detail);
                if (!native.RootExited || native.ActiveProcesses != 0) await Task.Delay(10, original.Token);
            } while (!native.RootExited || native.ActiveProcesses != 0);
            Assert.Equal(7, native.ExitCode);
            var actual = session.RetireAsync(explicitFailureCleanup);
            Assert.Same(actual, session.RetireAsync(explicitFailureCleanup));
            var retired = await actual;
            Assert.True(retired.KnownClosed, string.Join(";", retired.Failures));
            Assert.True(retired.RootExited); Assert.Equal(0u, retired.ActiveProcesses);
            Assert.True(retired.OutputEof); Assert.True(retired.ErrorEof);
            Assert.True(retired.InputCloseReturned); Assert.True(retired.DispositionReturned);
            Assert.Equal(7, retired.ExitCode); Assert.Empty(retired.Failures); Assert.False(session.HasUnknownIo);
            Assert.Equal("ordinary-outcome", Encoding.UTF8.GetString(output.ToArray()));
            Assert.Equal("expected-nonzero", Encoding.UTF8.GetString(session.StandardError));
            Assert.Equal(started, original.OriginalStarted); Assert.Equal(work, original.WorkLimit); Assert.Equal(whole, original.WholeLimit);
            if (workClosed) Assert.Throws<TimeoutException>(original.Check);
            else original.Check();
        }
        finally
        {
            if (session is null) original.Dispose();
            else
            {
                // Observe the same once-only task; no second disposition or
                // disposal of an unknown actual process graph is attempted.
                var retirement = session.OriginalRetirementTask ?? session.RetireAsync(true);
                var closed = await retirement;
                if (closed.KnownClosed) original.Dispose();
            }
        }
    }
}
