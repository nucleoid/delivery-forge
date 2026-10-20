using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Execution.Tests;

// Actual semaphore/file-owner controls, not native-worker or stored authority.
public sealed class OriginalWriterDispositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writer_release_waits_for_the_same_existing_run_flow_lock(bool stopped)
    {
        using var directory = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(directory.Path), new WorktreeManager(), processes);
        var run = "flow-" + Guid.NewGuid().ToString("N"); var original = new Original(); var parent = new Parent();
        coordinator.BindCoordinationAdmission(parent);
        coordinator.BindOriginalScheduling(original, parent, run, new WorktreeManager());
        var locks = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(ExecutionCoordinator)
            .GetField("FlowLocks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var actualFlowLock = locks.GetOrAdd(run, _ => new SemaphoreSlim(1, 1));
        await actualFlowLock.WaitAsync(TestContext.Current.CancellationToken);
        Task release;
        try
        {
            release = stopped ? coordinator.ReleaseStoppedWriterAsync(run, () => true)
                : coordinator.ReleaseCompletedWriterAsync(run, null!, () => true, TestContext.Current.CancellationToken);
            Assert.False(release.IsCompleted); // No scheduling sleep: the actual semaphore is held.
        }
        finally { actualFlowLock.Release(); }
        // No saved completion or stop is fabricated to pass the next barrier.
        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => release);
    }

    [Fact]
    public async Task Unreturned_writer_close_retains_the_actual_lease_without_retry()
    {
        using var directory = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(directory.Path), new WorktreeManager(), processes);
        var original = new Original(); var parent = new Parent(); coordinator.BindCoordinationAdmission(parent);
        coordinator.BindOriginalScheduling(original, parent, "owned", new WorktreeManager());
        var path = Path.Combine(directory.Path, "owned-writer");
        var stream = new FaultAfterCloseStream(path); var lease = new WriterLease(path, stream, "owned", "token");
        Assert.Throws<IOException>(() => coordinator.DisposeOriginalWriter("owned", lease));
        Assert.True(original.Unknown); Assert.Equal(1, parent.UnknownObservations); Assert.Equal(1, stream.CloseAttempts);
        Assert.Equal(WriterNamespaceIdentityOutcome.Unknown, lease.ObserveCurrentNamespaceIdentity().Outcome);
        lease.Dispose(); Assert.Equal(1, stream.CloseAttempts); // Sticky lease never repeats the first close.
    }

    [Fact]
    public async Task Existing_expired_flow_endpoint_is_timeout_before_any_action()
    {
        using var directory = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(directory.Path), new WorktreeManager(), processes);
        var original = new Original(); var parent = new Parent(); coordinator.BindCoordinationAdmission(parent);
        coordinator.BindOriginalScheduling(original, parent, "owned", new WorktreeManager());
        original.Limit = TimeSpan.FromTicks(1);
        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.ReleaseCompletedWriterAsync("owned", null!, () => true, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    sealed class Original : IOwnedDuplexBudget, IOwnedBudgetUnknownRetention
    {
        internal bool Unknown;
        internal TimeSpan Limit = TimeSpan.FromSeconds(1100);
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(1200);
        public TimeSpan WorkLimit => Limit;
        public TimeSpan CleanupRemaining => TimeSpan.FromSeconds(60);
        public CancellationToken Token => CancellationToken.None;
        public void CheckWork() { }
        public void PreserveUnknown() => Unknown = true;
    }
    sealed class Parent : ICoordinatedSynchronousEffectAdmission
    {
        internal int UnknownObservations;
        public void CheckCurrent(string runId) { }
        public T EnterSynchronous<T>(string runId, Func<T> invoke) => invoke();
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) => invoke();
        public void RetainUncertain(string runId, string reason) => UnknownObservations++;
    }
    sealed class FaultAfterCloseStream(string path) : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
    {
        internal int CloseAttempts;
        protected override void Dispose(bool disposing)
        {
            CloseAttempts++; base.Dispose(disposing);
            if (disposing) throw new IOException("Controlled actual writer first-close return failure.");
        }
    }
}
