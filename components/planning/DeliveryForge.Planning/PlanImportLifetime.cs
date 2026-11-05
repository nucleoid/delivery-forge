using DeliveryForge.Evidence;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeliveryForge.Planning;

internal sealed class PlanImportLifetime : IDisposable
{
    private static readonly ConcurrentDictionary<Task, PlanImportLifetime> Retained = new();
    private readonly ConcurrentDictionary<Task, byte> _actual = new();
    internal PlanImportResourceCollector Resources { get; } = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _total;
    private readonly IOwnedDuplexBudget? _enclosing;
    private readonly CancellationTokenSource _wall;
    private readonly CancellationToken _token;
    internal PlanImportLifetime(TimeSpan total, CancellationToken token, IOwnedDuplexBudget? enclosing = null)
    { _enclosing = enclosing; _total = total; _wall = CancellationTokenSource.CreateLinkedTokenSource(token); _token = _wall.Token; _wall.CancelAfter(Remaining()); }
    internal CancellationToken Token => _token;
    internal TimeSpan Remaining()
    { _enclosing?.CheckWork(); Token.ThrowIfCancellationRequested(); var left = _total - Stopwatch.GetElapsedTime(_started); if (_enclosing is not null) { var parent = _enclosing.WorkLimit - Stopwatch.GetElapsedTime(_enclosing.OriginalStarted); if (parent < left) left = parent; } if (left <= TimeSpan.Zero) throw new TimeoutException("Original plan import allowance expired."); return left; }
    internal void Check() => _ = Remaining();
    internal async Task<T> Wait<T>(Task<T> actual)
    {
        _actual.TryAdd(actual, 0);
        _ = actual.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { var value = await actual.WaitAsync(Remaining(), Token).ConfigureAwait(false); Check(); return value; }
        catch { Retained.TryAdd(actual, this); throw; } // Original task/result, never timed-out-observation closure.
    }
    internal Task<T> Observe<T>(Func<T> operation)
    { Check(); return Wait(Task.Run(() => { Check(); return operation(); }, CancellationToken.None)); }
    internal Task<T> Run<T>(Func<Task<T>> operation)
    { Check(); return Wait(Task.Run(() => { Check(); return operation(); }, CancellationToken.None)); }
    internal bool HasPendingTasks => _actual.Keys.Any(task => !task.IsCompleted);
    public void Dispose()
    { try { _wall.Cancel(); } finally { _wall.Dispose(); } } // Request only, never native/source-settlement proof.
}
