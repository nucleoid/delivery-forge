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
    // These are actual managed stream/lease failure controls. The private DATA
    // budget/parent below never issues Git provisioning or native authority.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordinator_first_close_attempt_notifies_both_owners_even_when_observers_throw(bool parentThrows)
    {
        using var directory = new TemporaryDirectory();
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(directory.Path), new WorktreeManager(), processes);
        var originalFailure = new IOException("Original uncertainty observer failed.");
        var parentFailure = new IOException("Parent uncertainty observer failed.");
        var original = new Original { UnknownFailure = originalFailure };
        var parent = new Parent { UnknownFailure = parentThrows ? parentFailure : null };
        coordinator.BindCoordinationAdmission(parent);
        coordinator.BindOriginalScheduling(original, parent, "owned", new WorktreeManager());
        var path = Path.Combine(directory.Path, "owned-writer");
        var stream = new FaultAfterCloseStream(path); var lease = new WriterLease(path, stream, "owned", "token");
        var failure = Assert.Throws<AggregateException>(() => coordinator.DisposeOriginalWriter("owned", lease));
        var retained = failure.Flatten().InnerExceptions;
        Assert.Same(stream.CloseFailure, retained[0]); Assert.Same(originalFailure, retained[1]);
        Assert.Equal(parentThrows ? 3 : 2, retained.Count);
        if (parentThrows) Assert.Same(parentFailure, retained[2]);
        Assert.Equal(1, original.UnknownObservations); Assert.Equal(1, parent.UnknownObservations);
        Assert.Throws<CoordinationUncertainException>(() => parent.CheckCurrent("owned"));
        Assert.False(lease.FirstCloseReturned); Assert.Equal(1, stream.CloseAttempts);
        lease.Dispose(); Assert.Equal(1, stream.CloseAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Acquire_failure_path_keeps_birth_primary_and_both_uncertainty_observations(bool parentThrows)
    {
        using var directory = new TemporaryDirectory();
        var originalFailure = new IOException("Original uncertainty observer failed.");
        var parentFailure = new IOException("Parent uncertainty observer failed.");
        var original = new Original { UnknownFailure = originalFailure };
        var parent = new Parent { UnknownFailure = parentThrows ? parentFailure : null };
        var manager = new WorktreeManager(Environment.ProcessPath!, new ProcessCommandExecutor(), original).WithParentAdmission(parent, "owned");
        var path = Path.Combine(directory.Path, "actual-managed-writer");
        var stream = new FaultAfterCloseStream(path); var lease = new WriterLease(path, stream, "owned", "token");
        var primary = new IOException("Writer birth primary DATA; no Git binding is fabricated.");
        // Invoke the exact concrete catch routine called by AcquireWriter; no
        // fake production binding is constructed merely to reach its failure.
        var failure = Assert.Throws<AggregateException>(() => manager.RetireFailedWriterBirth(lease, primary));
        Assert.Same(primary, failure.InnerExceptions[0]);
        var retained = failure.Flatten().InnerExceptions;
        Assert.Contains(stream.CloseFailure, retained); Assert.Contains(originalFailure, retained);
        if (parentThrows) Assert.Contains(parentFailure, retained);
        Assert.Equal(parentThrows ? 4 : 3, retained.Count);
        Assert.Equal(1, original.UnknownObservations); Assert.Equal(1, parent.UnknownObservations);
        Assert.Throws<CoordinationUncertainException>(() => parent.CheckCurrent("owned"));
        Assert.False(lease.FirstCloseReturned); Assert.Equal(1, stream.CloseAttempts);
        lease.Dispose(); Assert.Equal(1, stream.CloseAttempts);
    }

    [Fact]
    public void Acquire_failure_known_first_close_with_expired_post_preserves_primary_without_false_unknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-acquire-known-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = new Original(); var parent = new Parent();
        var manager = new WorktreeManager(Environment.ProcessPath!, new ProcessCommandExecutor(), original).WithParentAdmission(parent, "owned");
        var path = Path.Combine(root, "writer-evidence"); var bytes = new byte[] { 2, 4, 6, 8 };
        var stream = new ReturnThenExpireStream(path, () => original.CleanupExpired = true);
        stream.Write(bytes); stream.Flush(true); var lease = new WriterLease(path, stream, "owned", "token");
        var primary = new IOException("Writer birth primary DATA.");
        var failure = Assert.Throws<AggregateException>(() => manager.RetireFailedWriterBirth(lease, primary));
        Assert.Collection(failure.InnerExceptions, error => Assert.Same(primary, error), error => Assert.IsType<TimeoutException>(error));
        Assert.True(lease.FirstCloseReturned); Assert.Equal(1, stream.CloseAttempts);
        Assert.False(original.Unknown); Assert.Equal(0, original.UnknownObservations); Assert.Equal(0, parent.UnknownObservations);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Cleanup_expired_before_close_retains_known_held_writer_and_blocks_parent_without_closing()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-expired-held-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); // Keep this actual still-held failure namespace.
        var original = new Original { CleanupExpired = true }; var parent = new Parent();
        var manager = new WorktreeManager(Environment.ProcessPath!, new ProcessCommandExecutor(), original).WithParentAdmission(parent, "owned");
        var path = Path.Combine(root, "writer-evidence"); var bytes = new byte[] { 1, 3, 5, 7 };
        var stream = new ReturnThenExpireStream(path, () => { });
        stream.Write(bytes); stream.Flush(true); var lease = new WriterLease(path, stream, "owned", "token");
        Assert.Throws<TimeoutException>(() => manager.RetireFailedPreparationWriter(lease));
        Assert.False(lease.FirstCloseReturned); Assert.Equal(0, stream.CloseAttempts);
        Assert.Equal(bytes, lease.GetDiagnosticBytes());
        Assert.True(original.Unknown); Assert.Equal(1, original.UnknownObservations); Assert.Equal(1, parent.UnknownObservations);
        Assert.Throws<CoordinationUncertainException>(() => parent.CheckCurrent("owned"));
        Assert.Contains("expired before", parent.LastUnknownReason!);
        Assert.Throws<IOException>(() => { using var duplicate = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        // Production's retained-writer list owns the same live lease. Do not
        // close outside its expired allowance or repair/delete the lock.
    }

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
    public void Failed_original_preparation_closes_once_but_preserves_complete_durable_writer_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-failed-preparation-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); // Retain this small real failure namespace.
        var original = new Original(); var parent = new Parent();
        var manager = new WorktreeManager(Environment.ProcessPath!, new ProcessCommandExecutor(), original)
            .WithParentAdmission(parent, "owned");
        var path = Path.Combine(root, "writer-evidence"); var bytes = new byte[] { 1, 2, 3, 4 };
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        stream.Write(bytes); stream.Flush(true); var lease = new WriterLease(path, stream, "owned", "token");
        manager.RetireFailedPreparationWriter(lease);
        Assert.True(lease.FirstCloseReturned);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(original.Unknown); Assert.Equal(0, parent.UnknownObservations);
        TestContext.Current.TestOutputHelper!.WriteLine("Failed preparation retained complete durable writer bytes after one known first close.");
    }

    [Fact]
    public void Known_failed_preparation_first_close_with_expired_post_keeps_bytes_without_false_unknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-known-preparation-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); // Preserve the actual refused-post evidence.
        var original = new Original(); var parent = new Parent();
        var manager = new WorktreeManager(Environment.ProcessPath!, new ProcessCommandExecutor(), original)
            .WithParentAdmission(parent, "owned");
        var path = Path.Combine(root, "writer-evidence"); var bytes = new byte[] { 4, 3, 2, 1 };
        var stream = new ReturnThenExpireStream(path, () => original.CleanupExpired = true);
        stream.Write(bytes); stream.Flush(true); var lease = new WriterLease(path, stream, "owned", "token");
        Assert.Throws<TimeoutException>(() => manager.RetireFailedPreparationWriter(lease));
        Assert.True(lease.FirstCloseReturned); Assert.Equal(1, stream.CloseAttempts);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(original.Unknown); Assert.Equal(0, parent.UnknownObservations);
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
        internal bool Unknown, CleanupExpired;
        internal int UnknownObservations;
        internal Exception? UnknownFailure;
        internal TimeSpan Limit = TimeSpan.FromSeconds(1100);
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(1200);
        public TimeSpan WorkLimit => Limit;
        public TimeSpan CleanupRemaining => CleanupExpired ? throw new TimeoutException("Existing original cleanup endpoint expired.") : TimeSpan.FromSeconds(60);
        public CancellationToken Token => CancellationToken.None;
        public void CheckWork() { }
        public void PreserveUnknown() { Unknown = true; UnknownObservations++; if (UnknownFailure is not null) throw UnknownFailure; }
    }
    sealed class Parent : ICoordinatedSynchronousEffectAdmission
    {
        internal int UnknownObservations;
        internal Exception? UnknownFailure;
        internal string? LastUnknownReason;
        public void CheckCurrent(string runId) { if (UnknownObservations != 0) throw new CoordinationUncertainException("Managed control parent has retained uncertainty."); }
        public T EnterSynchronous<T>(string runId, Func<T> invoke) => invoke();
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) => invoke();
        public void RetainUncertain(string runId, string reason) { UnknownObservations++; LastUnknownReason = reason; if (UnknownFailure is not null) throw UnknownFailure; }
    }
    sealed class ReturnThenExpireStream(string path, Action afterClose) : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
    {
        internal int CloseAttempts;
        protected override void Dispose(bool disposing)
        {
            CloseAttempts++; base.Dispose(disposing);
            if (disposing) afterClose();
        }
    }
    sealed class FaultAfterCloseStream(string path) : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
    {
        internal int CloseAttempts;
        internal readonly IOException CloseFailure = new("Controlled actual writer first-close return failure.");
        protected override void Dispose(bool disposing)
        {
            CloseAttempts++; base.Dispose(disposing);
            if (disposing) throw CloseFailure;
        }
    }
}
