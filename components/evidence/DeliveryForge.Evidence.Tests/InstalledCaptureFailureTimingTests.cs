using System.Diagnostics;
using System.Text.Json;

namespace DeliveryForge.Evidence.Tests;

public sealed class InstalledCaptureFailureTimingTests
{
    [Fact(Timeout = 30000)]
    public async Task Actual_invalid_UTF8_capture_keeps_child_earliest_cleanup_and_raw_evidence()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Uses the actual installed Windows PowerShell byte stream; Linux capture remains separate.");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var root = Path.Combine(Path.GetTempPath(), "forge-invalid-utf8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var owner = new InstalledToolGateProvider.ActualExecutor(files, new Dictionary<string, string>(), shell);
        await Assert.ThrowsAnyAsync<Exception>(() => owner.ExecuteAsync(new(shell,
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::OpenStandardOutput().WriteByte(255); exit 1"], root),
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var actual = Assert.Single(owner.Calls);
        Assert.True(actual.Capture.RootExitObserved && actual.Capture.StandardOutputDrainCompleted && actual.Capture.StandardErrorDrainCompleted);
        Assert.True(actual.Result.OwnedProcessQuiescent);
        Assert.Equal(new byte[] { 255 }, actual.Capture.StandardOutputBytes);
        File.WriteAllBytes(Path.Combine(root, "actual-stdout.bin"), actual.Capture.StandardOutputBytes);
        File.WriteAllText(Path.Combine(root, "timing.json"), JsonSerializer.Serialize(new { Child = actual.Capture.Timing, Files = files.TimingData }));
        Assert.NotNull(actual.Capture.Timing?.CleanupStartedTimestamp);
        Assert.Equal(actual.Capture.Timing!.CleanupStartedTimestamp!.Value,
            JsonSerializer.SerializeToElement(files.TimingData).GetProperty("CleanupStarted").GetInt64());
    }
}
