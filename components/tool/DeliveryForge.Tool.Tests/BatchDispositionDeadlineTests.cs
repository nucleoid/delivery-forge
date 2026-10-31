namespace DeliveryForge.Tool.Tests;

// Actual Task/deadline controls only. No fixture Task issues a lease, native
// quiescence, reducer continuation or accepted production node completion.
public sealed class BatchDispositionDeadlineTests
{
    static readonly List<object> RetainedUnknownTaskControls = [];
    [Fact]
    public async Task Same_pending_disposition_can_settle_during_original_cleanup_after_WORK_closes()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var started = original.OriginalStarted; var whole = original.WholeLimit;
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.BeginFailureCleanup();
        await Assert.ThrowsAsync<TimeoutException>(() => ParentCoordinationHost.WaitActualDispositionTaskAsync(actual.Task, original, false));
        Assert.False(actual.Task.IsCompleted); Assert.False(original.HasExplicitUnknown);
        var sameCleanupWait = ParentCoordinationHost.WaitActualDispositionTaskAsync(actual.Task, original, true);
        Assert.False(sameCleanupWait.IsCompleted);
        actual.SetResult(); await sameCleanupWait;
        Assert.Equal(started, original.OriginalStarted); Assert.Equal(whole, original.WholeLimit);
        Assert.Throws<TimeoutException>(original.Check); Assert.False(original.HasExplicitUnknown);
    }

    [Fact]
    public async Task Late_batch_success_settles_original_task_but_preserves_exact_failed_WORK_exception()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var started = original.OriginalStarted;
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new TimeoutException("Original WORK failure.");
        var observing = StandaloneEntrypoint.SettleBatchTaskAfterWorkFailureAsync(actual.Task, original, primary);
        Assert.False(observing.IsCompleted); Assert.False(original.HasExplicitUnknown);
        actual.SetResult();
        Assert.Same(primary, await Assert.ThrowsAsync<TimeoutException>(() => observing));
        Assert.True(actual.Task.IsCompletedSuccessfully); Assert.False(original.HasExplicitUnknown);
        Assert.Equal(started, original.OriginalStarted); Assert.Throws<TimeoutException>(original.Check);
    }

    [Fact]
    public async Task Actual_batch_cleanup_failure_is_retained_with_WORK_primary_without_inventing_pending_task_unknown()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new TimeoutException("Original WORK failure."); var returned = new IOException("Actual batch failure after cleanup.");
        var observing = StandaloneEntrypoint.SettleBatchTaskAfterWorkFailureAsync(actual.Task, original, primary);
        actual.SetException(returned);
        var combined = await Assert.ThrowsAsync<AggregateException>(() => observing);
        Assert.Collection(combined.InnerExceptions, error => Assert.Same(primary, error), error => Assert.Same(returned, error));
        Assert.True(actual.Task.IsCompleted); Assert.False(original.HasExplicitUnknown);
    }

    [Fact]
    public async Task Already_failed_actual_batch_does_not_duplicate_the_same_primary_or_repeat_its_work()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var primary = new IOException("Actual batch primary.");
        var actual = Task.FromException(primary);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => StandaloneEntrypoint.SettleBatchTaskAfterWorkFailureAsync(actual, original, primary)));
        Assert.False(original.HasExplicitUnknown); Assert.Throws<TimeoutException>(original.Check);
    }

    [Fact(Timeout = 15000)]
    public async Task Pending_actual_batch_at_original_whole_expiry_is_retained_unknown_without_replay()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (RetainedUnknownTaskControls) { RetainedUnknownTaskControls.Add(original); RetainedUnknownTaskControls.Add(actual); }
        var primary = new TimeoutException("Original WORK failure.");
        var combined = await Assert.ThrowsAsync<AggregateException>(() => StandaloneEntrypoint.SettleBatchTaskAfterWorkFailureAsync(actual.Task, original, primary));
        Assert.Same(primary, combined.InnerExceptions[0]); Assert.IsType<TimeoutException>(combined.InnerExceptions[1]);
        Assert.False(actual.Task.IsCompleted); Assert.True(original.HasExplicitUnknown);
    }
    [Fact(Timeout = 15000)]
    public async Task Already_expired_original_preserves_WORK_primary_and_pending_actual_task()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (RetainedUnknownTaskControls) { RetainedUnknownTaskControls.Add(original); RetainedUnknownTaskControls.Add(actual); }
        // Expire this exact whole endpoint; do not create a replacement budget.
        await Task.Delay(original.WholeLimit + TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        var primary = new IOException("Original WORK primary before cleanup entry.");
        var combined = await Assert.ThrowsAsync<AggregateException>(() => StandaloneEntrypoint.SettleBatchTaskAfterWorkFailureAsync(actual.Task, original, primary));
        Assert.Collection(combined.InnerExceptions, error => Assert.Same(primary, error), error => Assert.IsType<TimeoutException>(error));
        Assert.False(actual.Task.IsCompleted); Assert.True(original.HasExplicitUnknown);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clamped_work_requires_forward_disposition_before_endpoint_and_cleanup_does_not_promote_late_success(bool observeAfterEndpoint)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = Directory.CreateTempSubdirectory("df-clamped-checkpoint-close-").FullName;
        TestContext.Current.TestOutputHelper!.WriteLine("Retained clamped-disposition fixture: " + root);
        var store = new DeliveryForge.Coordination.FileCoordinationCheckpointStore(root);
        try
        {
            var claim = await store.ClaimWriterAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken);
            Assert.Empty(await store.ReadLifecycleAsync(claim.LifecycleId, TestContext.Current.CancellationToken));
        }
        catch (Exception primary)
        {
            try { await store.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Actual checkpoint setup and first disposition failed.", primary, cleanup); }
            throw;
        }
        var started = original.OriginalStarted;
        var whole = original.WholeLimit;
        var token = original.Token;
        // This exercises the same real ToolLifetime and disposition waiter.
        // It does not invent a LocalSingle reservation, live lease, completed
        // worker or full HostBridge run; that wiring needs the selected golden.
        var endpoint = System.Diagnostics.Stopwatch.GetElapsedTime(started) + TimeSpan.FromSeconds(5);
        original.RestrictWork(endpoint);
        Assert.Equal(endpoint, original.WorkLimit);
        var actualClose = store.DisposeAsync().AsTask(); // Exactly one creator call.
        if (observeAfterEndpoint)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, wait.Token));
            Assert.True(token.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParentCoordinationHost.WaitActualDispositionTaskAsync(actualClose, original, false));
            Assert.False(original.HasExplicitUnknown);
            var firstCleanup = original.CleanupRemaining;
            await ParentCoordinationHost.WaitActualDispositionTaskAsync(actualClose, original, true);
            Assert.True(actualClose.IsCompletedSuccessfully);
            Assert.True(original.CleanupRemaining <= firstCleanup);
            Assert.ThrowsAny<OperationCanceledException>(original.CheckWork);
        }
        else
        {
            await ParentCoordinationHost.WaitActualDispositionTaskAsync(actualClose, original, false);
            original.CheckWork();
        }
        Assert.True(actualClose.IsCompletedSuccessfully);
        Assert.Equal(started, original.OriginalStarted);
        Assert.Equal(whole, original.WholeLimit);
        Assert.Equal(token, original.Token);
        Assert.False(original.HasExplicitUnknown);
        // Physical exclusive-writer first close, not just a completed fixture Task.
        using var reacquired = new FileStream(Path.Combine(root, "parent-writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(reacquired.SafeFileHandle.IsClosed);
    }
}
