using System.Diagnostics;

namespace DeliveryForge.Evidence.Tests;

// UNEXECUTED. Predicate/file controls do not certify process ownership. The separately
// admitted native case below exercises the actual default and restrictive owners.
public sealed class RestrictiveCaptureBudgetTests
{
    [Fact]
    public void Actual_file_positive_then_same_original_cleanup_stops_new_work()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(30));
        var path = Path.Combine(directory, "genuine.txt");
        files.WriteNew(path, "actual-file-positive"u8.ToArray());
        Assert.Equal("actual-file-positive"u8.ToArray(), files.Read(path, 1024));
        files.CheckWork();
        var first = files.CleanupRemaining;
        Assert.Throws<TimeoutException>(files.CheckWork);
        var second = files.CleanupRemaining;
        Assert.True(second <= first);
        // Read-only closing evidence uses the same original endpoint, not CheckWork renewal.
        Assert.Equal("actual-file-positive"u8.ToArray(), files.Read(path, 1024));
        files.ReleaseKnown(); // Real files are preserved for diagnosis.
    }

    [Fact]
    public void Shared_cleanup_latch_is_synchronous_and_never_expands_local_remaining()
    {
        var actual = new InstalledToolFiles(TimeSpan.FromSeconds(30));
        actual.CheckWork();
        var retained = new BoundedCaptureRetention();
        var local = new RestrictiveCaptureBudget(Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5), actual, retained);
        Assert.True(local.WorkAvailable);
        local.BeginCleanup();
        Assert.NotNull(local.CleanupStarted);
        Assert.True(actual.IsClosing);
        Assert.False(local.WorkAvailable);
        Assert.InRange(local.CleanupRemaining, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        retained.ReleaseKnown();
        actual.ReleaseKnown();
    }

    [Fact]
    [Trait("Category", "InstalledToolAcceptance")]
    public async Task Actual_default_host_capture_precedes_restrictive_no_birth_refusal()
    {
        var host = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DOTNET_HOST");
        if (host is null || !Path.IsPathFullyQualified(host) || !File.Exists(host))
            Assert.Skip("Requires Root-admitted selected real SDK host; no fixture executable substitutes.");
        var invocation = new CommandInvocation(host!, ["--version"], AppContext.BaseDirectory);
        var executor = new ProcessCommandExecutor();
        var ordinary = await executor.ExecuteBoundedCaptureAsync(invocation, TimeSpan.FromSeconds(15),
            65536, TestContext.Current.CancellationToken);
        Assert.Equal(0, ordinary.Command.ExitCode);
        Assert.True(ordinary.Command.OwnedProcessQuiescent);
        Assert.True(ordinary.RootExitObserved);
        Assert.NotEmpty(ordinary.StandardOutputBytes);
        Assert.NotNull(ordinary.Timing);
        Assert.Equal(Stopwatch.Frequency, ordinary.Timing!.StopwatchFrequency);
        var actual = new InstalledToolFiles(TimeSpan.FromSeconds(30));
        _ = actual.CleanupRemaining;
        var refused = await executor.ExecuteBoundedCaptureAsync(invocation, TimeSpan.FromSeconds(15),
            65536, TestContext.Current.CancellationToken, actual);
        Assert.Null(refused.Command.ExitCode);
        Assert.Empty(refused.StandardOutputBytes);
        Assert.False(refused.RootExitObserved);
        actual.ReleaseKnown();
    }
}
