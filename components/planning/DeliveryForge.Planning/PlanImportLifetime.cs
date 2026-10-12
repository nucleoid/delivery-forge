using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeliveryForge.Planning;

internal sealed class PlanImportLifetime : IDisposable
{
    private static readonly ConcurrentDictionary<Task, PlanImportLifetime> Retained = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _total;
    private readonly CancellationTokenSource _wall;
    private readonly CancellationToken _token;
    internal PlanImportLifetime(TimeSpan total, CancellationToken token)
    { _total = total; _wall = CancellationTokenSource.CreateLinkedTokenSource(token); _token = _wall.Token; _wall.CancelAfter(Remaining()); }
    internal CancellationToken Token => _token;
    internal TimeSpan Remaining()
    { Token.ThrowIfCancellationRequested(); var left = _total - Stopwatch.GetElapsedTime(_started); if (left <= TimeSpan.Zero) throw new TimeoutException("Original plan import allowance expired."); return left; }
    internal void Check() => _ = Remaining();
    internal async Task<T> Wait<T>(Task<T> actual)
    {
        _ = actual.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { var value = await actual.WaitAsync(Remaining(), Token).ConfigureAwait(false); Check(); return value; }
        catch { Retained.TryAdd(actual, this); throw; } // Original task/result, never timed-out-observation closure.
    }
    internal Task<T> Observe<T>(Func<T> operation)
    { Check(); return Wait(Task.Run(() => { Check(); return operation(); }, CancellationToken.None)); }
    internal Task<T> Run<T>(Func<Task<T>> operation)
    { Check(); return Wait(Task.Run(() => { Check(); return operation(); }, CancellationToken.None)); }
    public void Dispose()
    { try { _wall.Cancel(); } finally { _wall.Dispose(); } } // Request only, never native/source-settlement proof.
}
