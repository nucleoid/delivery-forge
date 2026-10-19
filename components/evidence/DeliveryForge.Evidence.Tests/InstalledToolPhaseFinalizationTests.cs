using System.Diagnostics;
namespace DeliveryForge.Evidence.Tests;

// Actual phase-accounting tests. No process result, native owner or capability
// is fabricated: real Linux closure remains a separate required integration test.
public sealed class InstalledToolPhaseFinalizationTests
{
    sealed class Parent : IOwnedDuplexBudget, IOwnedFailureCleanup
    {
        internal bool WorkOpen = true;
        internal int FailureNotifications;
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(120);
        public TimeSpan WorkLimit => TimeSpan.FromSeconds(110);
        public CancellationToken Token => TestContext.Current.CancellationToken;
        public void CheckWork() { Token.ThrowIfCancellationRequested(); if (!WorkOpen) throw new IOException("Original parent WORK is closed."); }
        public TimeSpan CleanupRemaining => TimeSpan.FromSeconds(10) - Stopwatch.GetElapsedTime(OriginalStarted);
        public void BeginFailureCleanup() { FailureNotifications++; WorkOpen = false; }
    }
    [Fact]
    public void Settled_work_starts_only_local_finalization_and_next_phase_keeps_parent_work()
    {
        var parent = new Parent(); var credits = new InstalledToolHostScope.Credits();
        var files = new InstalledToolFiles(parent, TimeSpan.FromSeconds(30), credits);
        var observation = new BoundedCaptureTimingObservation(parent.OriginalStarted, null, Stopwatch.Frequency);
        files.FinishSettledWorkCapture(observation);
        Assert.True(files.IsClosing); Assert.True(files.CleanupRemaining > TimeSpan.Zero);
        Assert.Equal(0, parent.FailureNotifications); parent.CheckWork();
        var first = System.Text.Json.JsonSerializer.SerializeToElement(files.TimingData).GetProperty("CleanupStarted").GetInt64();
        files.FinishSettledWorkCapture(observation);
        Assert.Equal(first, System.Text.Json.JsonSerializer.SerializeToElement(files.TimingData).GetProperty("CleanupStarted").GetInt64());
        Assert.True(files.IsClosing); Assert.Equal(0, parent.FailureNotifications);
        var next = new InstalledToolFiles(parent, TimeSpan.FromSeconds(30), credits);
        next.CheckWork(); Assert.False(next.IsClosing);
    }
    [Fact]
    public void Closed_parent_cannot_gain_a_new_local_finalization_allowance()
    {
        var parent = new Parent(); var files = new InstalledToolFiles(parent, TimeSpan.FromSeconds(30), new InstalledToolHostScope.Credits());
        parent.WorkOpen = false;
        Assert.Throws<IOException>(() => files.FinishSettledWorkCapture(new(parent.OriginalStarted, null, Stopwatch.Frequency)));
        Assert.False(files.IsClosing); Assert.Equal(0, parent.FailureNotifications);
    }
    [Theory]
    [InlineData("origin")]
    [InlineData("frequency")]
    [InlineData("child-cleanup")]
    public void Foreign_or_already_cleanup_observation_cannot_start_the_settled_work_route(string changed)
    {
        var parent = new Parent(); var files = new InstalledToolFiles(parent, TimeSpan.FromSeconds(30), new InstalledToolHostScope.Credits());
        var timing = new BoundedCaptureTimingObservation(parent.OriginalStarted, null, Stopwatch.Frequency);
        timing = changed switch
        {
            "origin" => timing with { OriginalStartedTimestamp = parent.OriginalStarted + 1 },
            "frequency" => timing with { StopwatchFrequency = Stopwatch.Frequency + 1 },
            _ => timing with { CleanupStartedTimestamp = Stopwatch.GetTimestamp() }
        };
        Assert.Throws<EvidenceWriteException>(() => files.FinishSettledWorkCapture(timing));
        Assert.False(files.IsClosing); files.CheckWork(); Assert.Equal(0, parent.FailureNotifications);
    }
}
