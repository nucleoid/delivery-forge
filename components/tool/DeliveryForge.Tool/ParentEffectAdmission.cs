using DeliveryForge.Coordination;

namespace DeliveryForge.Tool;

// Internal implementation used by the real parent, with its SAME revocation lock and private current-authority check.
internal sealed class ParentEffectAdmission(object originalGate, Func<string, bool> current, Action<Exception> revoke) : ICoordinatedSynchronousEffectAdmission
{
    sealed class OriginalInvocation { internal bool Entered, Returned; internal Task? Task; internal Task? Observation; internal Exception? Failure; }
    readonly List<OriginalInvocation> originals = [];
    bool retainedUncertain;
    internal bool AllKnownSettled { get { lock (originalGate) { ObserveReturnedFailures(); return !retainedUncertain && originals.All(row => row.Returned && row.Task is { IsCompletedSuccessfully: true } && row.Observation is { IsCompletedSuccessfully: true } && row.Failure is null); } } }
    internal IReadOnlyList<Task> OriginalObservers { get { lock (originalGate) return originals.Where(row => row.Observation is not null).Select(row => row.Observation!).ToArray(); } }
    internal int EnteredCount { get { lock (originalGate) return originals.Count; } }
    public void RetainUncertain(string runId, string reason) { lock (originalGate) { retainedUncertain = true; revoke(new CoordinationUncertainException("Original parent effect requires explicit reconciliation.")); } }
    void ObserveReturnedFailures()
    {
        foreach (var row in originals) if (row.Returned && row.Task is not null && (row.Task.IsFaulted || row.Task.IsCanceled))
        {
            row.Failure ??= row.Task.Exception ?? (Exception)new OperationCanceledException("Original effect Task cancelled.");
            retainedUncertain = true; revoke(row.Failure);
        }
    }
    public void CheckCurrent(string runId)
    {
        lock (originalGate)
        {
            ObserveReturnedFailures();
            if (retainedUncertain || !current(runId))
            {
                var denied = new CoordinationUncertainException("No current original parent effect authority.");
                revoke(denied); throw denied;
            }
        }
    }
    public T EnterSynchronous<T>(string runId, Func<T> invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        var completion = new TaskCompletionSource<T>();
        lock (originalGate)
        {
            CheckCurrent(runId);
            if (originals.Count >= 1024)
            {
                var exhausted = new IOException("Finite original parent effect ledger exhausted.");
                revoke(exhausted); throw exhausted;
            }
            // The actual completion exists before leaving admission. Renewal and
            // revocation can proceed while I/O runs; quiescence sees this pending Task.
            var row = new OriginalInvocation { Entered = true, Returned = true, Task = completion.Task };
            originals.Add(row);
            row.Observation = completion.Task.ContinueWith(_ => { lock (originalGate) ObserveReturnedFailures(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        T result;
        try { CheckCurrent(runId); result = invoke(); }
        catch (Exception error) { completion.TrySetException(error); _ = completion.Task.Exception; throw; }
        completion.SetResult(result);
        CheckCurrent(runId);
        return result;
    }
    public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        lock (originalGate)
        {
            ObserveReturnedFailures();
            if (retainedUncertain || !current(runId))
            {
                var denied = new CoordinationUncertainException("No current original parent effect authority."); revoke(denied); throw denied;
            }
            if (originals.Count >= 1024)
            {
                var exhausted = new IOException("Finite original parent effect ledger exhausted."); revoke(exhausted); throw exhausted;
            }
            var row = new OriginalInvocation { Entered = true }; originals.Add(row);
            try
            {
                var actual = invoke(); row.Task = actual; row.Returned = true;
                if (actual is null) throw new IOException("Actual original effect returned no task.");
                row.Observation = actual.ContinueWith(_ => { lock (originalGate) ObserveReturnedFailures(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return actual;
            }
            catch (Exception error) { row.Failure = error; retainedUncertain = true; revoke(error); throw; }
        }
    }
}
