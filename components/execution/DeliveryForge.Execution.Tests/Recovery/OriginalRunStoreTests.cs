using System.Diagnostics;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Execution.Tests.Recovery;

public sealed class OriginalRunStoreTests
{
    [Fact]
    public async Task Revoked_parent_cannot_create_store_namespace()
    {
        using var directory = new TemporaryDirectory(); var root = Path.Combine(directory.Path, "absent");
        var original = new Original(); var parent = new Parent();
        var store = new RunStore(root).WithOriginalAdmission(original, parent, "owned"); parent.Revoked = true;
        await Assert.ThrowsAsync<IOException>(() => store.AppendAsync("owned", "prepared", new { value = 1 }, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task Revocation_after_temporary_birth_preserves_failure_file_and_closes_actual_handle()
    {
        using var directory = new TemporaryDirectory(); var original = new Original(); var parent = new Parent();
        var store = new RunStore(directory.Path).WithOriginalAdmission(original, parent, "owned");
        parent.After = () => { if (Directory.EnumerateFiles(directory.Path, ".record-*.tmp", SearchOption.AllDirectories).Any()) parent.Revoked = true; };
        await Assert.ThrowsAsync<IOException>(() => store.AppendAsync("owned", "prepared", new { value = 1 }, TestContext.Current.CancellationToken));
        var temporary = Assert.Single(Directory.EnumerateFiles(directory.Path, ".record-*.tmp", SearchOption.AllDirectories));
        using var exclusive = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*-sha256-*.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "current.json", SearchOption.AllDirectories));
        Assert.True(original.CleanupChecks > 0);
    }
    [Fact]
    public async Task Read_postrefusal_closes_exact_open_handle_with_same_original_cleanup()
    {
        using var directory = new TemporaryDirectory(); var original = new Original(); var parent = new Parent();
        var store = new RunStore(directory.Path).WithOriginalAdmission(original, parent, "owned");
        var record = await store.AppendAsync("owned", "prepared", new { value = 1 }, TestContext.Current.CancellationToken);
        parent.AfterValue = value => { if (value is byte[]) parent.Revoked = true; };
        await Assert.ThrowsAsync<IOException>(() => store.RecoverAsync("owned", TestContext.Current.CancellationToken));
        using var exclusive = new FileStream(record.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(original.CleanupChecks > 0); Assert.False(store.HasUnsettledOriginalFiles);
        Assert.True(File.Exists(record.Path));
    }
    [Fact]
    public async Task Existing_original_cleanup_view_records_stop_without_restoring_forward_authority()
    {
        using var directory = new TemporaryDirectory(); var original = new Original(); var parent = new Parent();
        var store = new RunStore(directory.Path).WithOriginalAdmission(original, parent, "owned");
        await store.AppendAsync("owned", "prepared", new { value = 1 }, TestContext.Current.CancellationToken);
        original.WorkClosed = true; parent.Revoked = true;
        var cleanup = store.WithOriginalCleanup();
        await cleanup.AppendAsync("owned", "stopped", new { quiescent = true }, TestContext.Current.CancellationToken);
        Assert.Equal("stopped", (await cleanup.RecoverAsync("owned", TestContext.Current.CancellationToken)).Latest!.Kind);
        await Assert.ThrowsAsync<TimeoutException>(() => store.AppendAsync("owned", "prepared", new { value = 2 }, TestContext.Current.CancellationToken));
        original.CleanupClosed = true;
        await Assert.ThrowsAsync<TimeoutException>(() => cleanup.RecoverAsync("owned", TestContext.Current.CancellationToken));
    }
    sealed class Original : IOwnedDuplexBudget
    {
        internal bool WorkClosed, CleanupClosed; internal int CleanupChecks;
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(1200);
        public TimeSpan WorkLimit => TimeSpan.FromSeconds(1100);
        public CancellationToken Token => CancellationToken.None;
        public TimeSpan CleanupRemaining { get { CleanupChecks++; if (CleanupClosed) throw new TimeoutException(); return TimeSpan.FromSeconds(60); } }
        public void CheckWork() { if (WorkClosed) throw new TimeoutException(); }
    }
    sealed class Parent : ICoordinatedSynchronousEffectAdmission
    {
        internal bool Revoked; internal Action? After; internal Action<object?>? AfterValue;
        public void CheckCurrent(string runId) { if (Revoked) throw new IOException("Actual parent revoked."); }
        public T EnterSynchronous<T>(string runId, Func<T> invoke) { CheckCurrent(runId); var value = invoke(); After?.Invoke(); AfterValue?.Invoke(value); CheckCurrent(runId); return value; }
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) { CheckCurrent(runId); return invoke(); }
        public void RetainUncertain(string runId, string reason) => throw new IOException(reason);
    }
}
