using DeliveryForge.Coordination;

namespace DeliveryForge.Tool;

// Internal implementation used by the real parent, with its SAME revocation lock and private current-authority check.
internal sealed class ParentEffectAdmission(object originalGate, Func<string, bool> current, Action<Exception> revoke,
    ParentRenewalHandoff? renewal = null) : ICoordinatedSynchronousEffectAdmission
{
    sealed class OriginalInvocation { internal bool Entered, Returned, Synchronous; internal Task? Task; internal Task? Observation; internal Exception? Failure; }
    readonly List<OriginalInvocation> originals = [];
    bool retainedUncertain;
    bool returnedFailure;
    internal bool AllKnownSettled { get { lock (originalGate) { ObserveReturnedFailures(); return !retainedUncertain && originals.All(row => row.Returned && row.Task is { IsCompleted: true } && row.Observation is { IsCompletedSuccessfully: true } && (row.Synchronous || row.Task.IsCompletedSuccessfully && row.Failure is null)); } } }
    internal IReadOnlyList<Task> OriginalObservers { get { lock (originalGate) return originals.Where(row => row.Observation is not null).Select(row => row.Observation!).ToArray(); } }
    internal int EnteredCount { get { lock (originalGate) return originals.Count; } }
    public void RetainUncertain(string runId, string reason) { lock (originalGate) { retainedUncertain = true; revoke(new CoordinationUncertainException("Original parent effect requires explicit reconciliation.")); } }
    void ObserveReturnedFailures()
    {
        foreach (var row in originals) if (row.Returned && row.Task is not null && (row.Task.IsFaulted || row.Task.IsCanceled))
        {
            row.Failure ??= row.Task.Exception ?? (Exception)new OperationCanceledException("Original effect Task cancelled.");
            returnedFailure = true;
            if (!row.Synchronous) retainedUncertain = true;
            revoke(row.Failure);
        }
    }
    public void CheckCurrent(string runId)
    {
        while (true)
        {
            ParentRenewalHandoff.Pending? pending;
            lock (originalGate) { pending = PendingOrCurrent(runId); if (pending is null) return; }
            WaitForRenewal(pending);
        }
    }
    ParentRenewalHandoff.Pending? PendingOrCurrent(string runId)
    {
        ObserveReturnedFailures();
        if (!retainedUncertain && !returnedFailure)
        {
            ParentRenewalHandoff.Pending? pending;
            try { pending = renewal?.Snapshot(runId); }
            catch (Exception error) { revoke(error); throw; }
            if (pending is not null) return pending;
            if (current(runId)) return null;
        }
        var denied = new CoordinationUncertainException("No current original parent effect authority.");
        revoke(denied); throw denied;
    }
    void WaitForRenewal(ParentRenewalHandoff.Pending pending)
    {
        try { renewal!.Wait(pending); }
        catch (Exception error) { lock (originalGate) revoke(error); throw; }
    }
    public T EnterSynchronous<T>(string runId, Func<T> invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        var completion = new TaskCompletionSource<T>();
        while (true)
        {
            ParentRenewalHandoff.Pending? pending;
            lock (originalGate)
            {
                pending = PendingOrCurrent(runId);
                if (pending is null)
                {
                    if (originals.Count >= 1024)
                    {
                        var exhausted = new IOException("Finite original parent effect ledger exhausted.");
                        revoke(exhausted); throw exhausted;
                    }
                    // The actual completion exists before leaving admission. Renewal and
                    // revocation can proceed while I/O runs; quiescence sees this pending Task.
                    var row = new OriginalInvocation { Entered = true, Returned = true, Synchronous = true, Task = completion.Task };
                    originals.Add(row);
                    row.Observation = completion.Task.ContinueWith(_ => { lock (originalGate) ObserveReturnedFailures(); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    break;
                }
            }
            WaitForRenewal(pending);
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
        while (true)
        {
            ParentRenewalHandoff.Pending? pending;
            lock (originalGate)
            {
                pending = PendingOrCurrent(runId);
                if (pending is null)
                {
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
            WaitForRenewal(pending);
        }
    }
}
