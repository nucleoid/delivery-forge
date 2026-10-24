using System.Diagnostics;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Scheduling;

public sealed partial class LocalSingleScheduler
{
    IOwnedDuplexBudget? original;
    ICoordinatedSynchronousEffectAdmission? parent;
    Func<Task?>? actualPipeline;
    Func<bool>? actualSettled;
    Func<bool>? actualOriginalUnknown;
    bool provisioningEntered, preparationEntered;
    internal bool HasRetainedReservation => _laneHeld || _ticket is not null || _resources is not null;
    internal void BindOriginal(IOwnedDuplexBudget owner, ICoordinatedSynchronousEffectAdmission admission, Func<Task?> pipeline, Func<bool> settled, Func<bool> originalUnknown)
    {
        ArgumentNullException.ThrowIfNull(pipeline); ArgumentNullException.ThrowIfNull(settled); ArgumentNullException.ThrowIfNull(originalUnknown);
        if (original is not null || _laneHeld || _ticket is not null || _timeBudget is not null) throw new SchedulingException("Original scheduling binds once before reservation.");
        owner.CheckWork(); admission.CheckCurrent(_worker.RunId);
        original = owner; parent = admission; actualPipeline = pipeline; actualSettled = settled; actualOriginalUnknown = originalUnknown;
    }
    internal bool CanReleaseUnprovisioned => original is not null && _laneHeld && _ticket is not null && !_prepared && !provisioningEntered && !preparationEntered && _nativeQuiescenceKnown && !_uncertain && !_store.HasUnsettledOriginalFiles && actualOriginalUnknown?.Invoke() == false;
    internal async Task ReleaseCompletedUnprovisionedObservedAsync(LocalDispatchReservation ticket, Func<bool> actualNoWorkerStart, CancellationToken token)
    {
        await WaitSerialAsync(token).ConfigureAwait(false);
        try
        {
            CheckOriginal();
            if (!ReferenceEquals(ticket, _ticket) || !CanReleaseUnprovisioned || actualPipeline?.Invoke() is not { IsCompletedSuccessfully: true } ||
                actualSettled?.Invoke() != true || !actualNoWorkerStart() || _execution.HasPendingControlOperations(_worker.RunId))
                throw new SchedulingException("Only this settled original gate-only pipeline can release its unused reservation.");
            // No writer, preparation or native worker exists to stop. The same
            // original WORK endpoint remains in force for parent finalization.
            CheckOriginal(); MarkObservedClosed(); Release();
        }
        finally { _serial.Release(); }
    }
    internal async Task ReleaseUnprovisionedObservedAsync(LocalDispatchReservation ticket, Func<bool> actualNoWorkerStart)
    {
        if (original is null) throw new SchedulingException("No retained original unstarted owner.");
        using var deadline = new CancellationTokenSource(original.CleanupRemaining);
        await _serial.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            _ = original.CleanupRemaining;
            if (!ReferenceEquals(ticket, _ticket) || !CanReleaseUnprovisioned || actualPipeline?.Invoke() is not { IsCompleted: true } || !actualNoWorkerStart() || _execution.HasPendingControlOperations(_worker.RunId))
                throw new SchedulingException("Unprovisioned release requires exact settled original no-start/no-provision evidence.");
            // No writer/preparation/provisioning was entered, so there is no
            // worker to stop or dispose. Retain all original refusal evidence.
            _ = original.CleanupRemaining; MarkObservedClosed(); Release();
        }
        finally { _serial.Release(); }
    }
    void CheckOriginal() { original?.CheckWork(); parent?.CheckCurrent(_worker.RunId); }
    TimeSpan OriginalRemaining()
    {
        CheckOriginal(); var remaining = original!.WorkLimit - Stopwatch.GetElapsedTime(original.OriginalStarted);
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Original scheduling WORK endpoint expired.");
        return remaining;
    }
    static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
    CancellationTokenSource OriginalDeadline(CancellationToken caller)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, original?.Token ?? default);
        try { if (original is not null) linked.CancelAfter(OriginalRemaining()); return linked; }
        catch { linked.Dispose(); throw; }
    }
    async Task WaitSerialAsync(CancellationToken token)
    {
        using var linked = OriginalDeadline(token);
        await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try { CheckOriginal(); } catch { _serial.Release(); throw; }
    }
    // Private same-process terminal observation. Never stop an already completed
    // native worker, and never release from a saved terminal checkpoint.
    internal async Task CompleteObservedAsync(LocalDispatchReservation ticket, Func<Task> releaseActualWriter, CancellationToken token)
    {
        await WaitSerialAsync(token).ConfigureAwait(false);
        var terminalEntered = false;
        try
        {
            CheckOriginal();
            if (original is null || !ReferenceEquals(ticket, _ticket) || !_laneHeld || !_prepared || _uncertain || !_nativeQuiescenceKnown ||
                _store.HasUnsettledOriginalFiles || actualOriginalUnknown?.Invoke() != false ||
                actualPipeline?.Invoke() is not { IsCompletedSuccessfully: true } || actualSettled?.Invoke() != true || _execution.HasPendingControlOperations(_worker.RunId))
                throw new SchedulingException("Only the exact settled original pipeline can release its actual completed reservation.");
            terminalEntered = true;
            await PersistAsync("completed", token).ConfigureAwait(false);
            CheckOriginal();
            if (actualSettled!.Invoke() != true) throw new SchedulingException("Original settlement changed before writer disposition.");
            await releaseActualWriter().ConfigureAwait(false);
            CheckOriginal();
            if (_store.HasUnsettledOriginalFiles || actualOriginalUnknown?.Invoke() != false || actualSettled?.Invoke() != true || _execution.HasPendingControlOperations(_worker.RunId))
                throw new SchedulingException("Original completed disposition acquired uncertainty; capacity remains retained.");
            MarkObservedClosed(); Release();
        }
        catch { if (terminalEntered) _uncertain = true; throw; }
        finally { _serial.Release(); }
    }
    internal Task ReleaseStoppedObservedAsync(LocalDispatchReservation ticket, Func<bool> actualStopped, Func<Task> releaseActualWriter) =>
        ReleaseTerminalObservedAsync(ticket, actualStopped, releaseActualWriter);
    // The actual completed worker has already settled. This callback must also
    // join the retained failed evidence Task; no terminal DATA or new owner is
    // enough. This path closes WORK and cannot dispatch another node.
    internal Task ReleaseCompletedFailureObservedAsync(LocalDispatchReservation ticket, Func<bool> actualCompletedFailure, Func<Task> releaseActualWriter) =>
        ReleaseTerminalObservedAsync(ticket, actualCompletedFailure, releaseActualWriter);
    async Task ReleaseTerminalObservedAsync(LocalDispatchReservation ticket, Func<bool> actualStopped, Func<Task> releaseActualWriter)
    {
        if (original is null) throw new SchedulingException("No retained original cleanup owner.");
        using var deadline = new CancellationTokenSource(original.CleanupRemaining);
        await _serial.WaitAsync(deadline.Token).ConfigureAwait(false);
        var writerDispositionEntered = false;
        try
        {
            _ = original.CleanupRemaining;
            if (!ReferenceEquals(ticket, _ticket) || !_laneHeld || !StoppedOriginalKnown() || actualPipeline?.Invoke() is not { IsCompleted: true } || !actualStopped())
                throw new SchedulingException("Cleanup release requires the exact settled pipeline and fresh same-owner stopped closure.");
            writerDispositionEntered = true;
            await releaseActualWriter().ConfigureAwait(false);
            _ = original.CleanupRemaining;
            if (!StoppedOriginalKnown() || !actualStopped()) throw new SchedulingException("Stopped closure or original uncertainty changed during cleanup; capacity remains retained.");
            // No forward checkpoint write or new dispatch authority under cleanup.
            // A fresh process must still reconcile the retained journal itself.
            MarkObservedClosed(); Release();
        }
        catch { if (writerDispositionEntered) _uncertain = true; throw; }
        finally { _serial.Release(); }
    }
    bool StoppedOriginalKnown() => _nativeQuiescenceKnown && !_uncertain && !_store.HasUnsettledOriginalFiles &&
        actualOriginalUnknown?.Invoke() == false && !_execution.HasPendingControlOperations(_worker.RunId);
}
