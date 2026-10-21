using System.Diagnostics;
using System.Text.Json;

namespace DeliveryForge.Evidence.Tests;

public sealed class InstalledCaptureFailureTimingTests
{
    [Fact(Timeout = 30000)]
    public async Task Actual_invalid_UTF8_capture_keeps_child_earliest_cleanup_and_raw_evidence()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Uses the actual bound Windows .NET helper byte stream; Linux capture remains separate.");
        var host = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DOTNET_HOST") ?? throw new IOException("Actual SDK host binding is missing.");
        var helper = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LIFECYCLE_HELPER_DLL") ?? throw new IOException("Actual lifecycle helper binding is missing.");
        if (!Path.IsPathFullyQualified(host) || !Path.IsPathFullyQualified(helper) || !File.Exists(host) || !File.Exists(helper))
            throw new IOException("Actual absolute SDK/helper inputs are unavailable.");
        var root = Path.Combine(Path.GetTempPath(), "forge-invalid-utf8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_ROOT"] = Path.GetDirectoryName(host)!,
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_PROCESSOR_COUNT"] = "2",
            ["DOTNET_GCHeapHardLimit"] = "0x100000000"
        };
        var owner = new InstalledToolGateProvider.ActualExecutor(files, environment, host);
        Exception? failure = null;
        try
        {
            await owner.ExecuteAsync(new(host,
                [helper, "invalid-utf8", root, Guid.NewGuid().ToString("N")], root),
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (Exception error) { failure = error; }
        var preliminary = JsonSerializer.Serialize(new { Calls = owner.Calls.Count, Files = files.TimingData, Failure = failure?.ToString() });
        File.WriteAllText(Path.Combine(root, "capture-attempt.json"), preliminary);
        TestContext.Current.TestOutputHelper!.WriteLine("Retained actual capture attempt: " + root + " " + preliminary);
        var actual = Assert.Single(owner.Calls);
        // Retain the bounded ACTUAL observation before any assertion can fail.
        File.WriteAllBytes(Path.Combine(root, "actual-stdout.bin"), actual.Capture.StandardOutputBytes);
        File.WriteAllBytes(Path.Combine(root, "actual-stderr.bin"), actual.Capture.StandardErrorBytes);
        var timing = JsonSerializer.Serialize(new
        {
            actual.Result.ExitCode,
            actual.Result.TimedOut,
            actual.Result.Cancelled,
            actual.Result.OwnedProcessQuiescent,
            actual.Result.LifecycleDiagnostics,
            actual.Capture.RootExitObserved,
            actual.Capture.StandardOutputDrainCompleted,
            actual.Capture.StandardErrorDrainCompleted,
            OutputBytes = actual.Capture.StandardOutputBytes.Length,
            ErrorBytes = actual.Capture.StandardErrorBytes.Length,
            Child = actual.Capture.Timing,
            Files = files.TimingData,
            Failure = failure?.ToString()
        });
        File.WriteAllText(Path.Combine(root, "timing.json"), timing);
        TestContext.Current.TestOutputHelper!.WriteLine("Retained actual invalid-byte observation: " + root + " " + timing);
        Assert.True(actual.Capture.RootExitObserved && actual.Capture.StandardOutputDrainCompleted && actual.Capture.StandardErrorDrainCompleted);
        Assert.True(actual.Result.OwnedProcessQuiescent);
        Assert.Equal(new byte[] { 255 }, actual.Capture.StandardOutputBytes);
        Assert.NotNull(failure);
        Assert.Contains("DecoderFallbackException", failure.ToString(), StringComparison.Ordinal);
        Assert.NotNull(actual.Capture.Timing?.CleanupStartedTimestamp);
        Assert.Equal(actual.Capture.Timing!.CleanupStartedTimestamp!.Value,
            JsonSerializer.SerializeToElement(files.TimingData).GetProperty("CleanupStarted").GetInt64());
    }
}
