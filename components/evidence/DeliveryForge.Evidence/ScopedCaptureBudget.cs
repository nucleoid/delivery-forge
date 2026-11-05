using System.Diagnostics;
namespace DeliveryForge.Evidence;

// A restrictive view of the actual enclosing original. No incoming timestamps,
// actor callbacks, replacement owner or independently renewed clock are accepted.
internal sealed class ScopedCaptureBudget : IOwnedDuplexBudget, IOwnedFailureCleanup, IOwnedBudgetUnknownRetention, IDisposable
{
    readonly IOwnedDuplexBudget parent; readonly CancellationTokenSource linked; readonly long localStarted; readonly TimeSpan localWhole, localWork; readonly object gate = new(); long? cleanup;
    internal ScopedCaptureBudget(IOwnedDuplexBudget actualParent, TimeSpan localGrant, CancellationToken caller)
    {
        parent = actualParent; localStarted = Stopwatch.GetTimestamp();
        localWhole = localGrant; localWork = BoundedCaptureStream.WorkBudget(localGrant);
        linked = CancellationTokenSource.CreateLinkedTokenSource(parent.Token, caller);
        try { CheckWork(); }
        catch (Exception primary)
        {
            // No native birth exists yet, but the exact linked registration is
            // already owned. Its first close cannot disappear with a failed ctor.
            var retained = new BoundedCaptureRetention(); retained.Add(this); retained.Add(primary);
            try { linked.Dispose(); retained.ReleaseKnown(); }
            catch (Exception close)
            {
                retained.Add(close); var failures = new List<Exception> { primary, close };
                try { PreserveUnknown(); } catch (Exception observation) { retained.Add(observation); failures.Add(observation); }
                throw new AggregateException("Capture budget initial refusal and linked first disposition failed; exact owner retained.", failures);
            }
            throw;
        }
    }
    public long OriginalStarted => parent.OriginalStarted;
    TimeSpan Offset => Stopwatch.GetElapsedTime(parent.OriginalStarted, localStarted);
    public TimeSpan WholeLimit { get { var local = Offset + localWhole; return local < parent.WholeLimit ? local : parent.WholeLimit; } }
    public TimeSpan WorkLimit { get { var local = Offset + localWork; return local < parent.WorkLimit ? local : parent.WorkLimit; } }
    public CancellationToken Token => linked.Token;
    internal long? LocalCleanupEntered { get { lock (gate) return cleanup; } }
    public void CheckWork()
    {
        lock (gate) { if (cleanup is not null) throw new IOException("Original command work is closed after first cleanup."); }
        if (parent is IOwnedRestrictedWorkCheck restricted) restricted.CheckWork(localStarted, localWork);
        else parent.CheckWork();
        Token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(OriginalStarted) >= WorkLimit) throw new TimeoutException("Original MIN command work allowance expired.");
    }
    public TimeSpan CleanupRemaining { get { lock (gate) { cleanup ??= Stopwatch.GetTimestamp(); var actualParent = parent.CleanupRemaining; var whole = WholeLimit - Stopwatch.GetElapsedTime(OriginalStarted); var once = TimeSpan.FromSeconds(60) - Stopwatch.GetElapsedTime(cleanup.Value); var remaining = actualParent < whole ? actualParent : whole; if (once < remaining) remaining = once; if (remaining <= TimeSpan.Zero) throw new TimeoutException("SAME original command/parent whole/earliest cleanup endpoint expired."); return remaining; } } }
    internal TimeSpan NextObservationDelay()
    {
        var left = WorkLimit - Stopwatch.GetElapsedTime(OriginalStarted);
        if (left <= TimeSpan.Zero) throw new TimeoutException("Original MIN command work allowance expired during native observation.");
        // Fast commands need not consume a whole second before their actual
        // native exit is observed. Bound the faster cadence to this capture's
        // existing first second; long commands retain the original cadence.
        var cadence = Stopwatch.GetElapsedTime(localStarted) < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
        return left < cadence ? left : cadence;
    }
    internal (bool TimedOut, bool Cancelled) ClassifyFailure(Exception failure, bool callerCancellationRequested)
    {
        // An unrelated primary failure is not caused by a later deadline or a
        // concurrent cancellation. Its exact exception remains with the frame.
        if (failure is not (TimeoutException or OperationCanceledException)) return (false, false);
        // Parent CancelAfter(WORK) can race CheckWork/Delay. The SAME immutable
        // endpoint classifies cancellation only when its actual token belongs
        // to this budget or its parent; foreign cancellation is not that proof.
        var originalCancellation = failure is OperationCanceledException cancelled && cancelled.CancellationToken.CanBeCanceled &&
            (cancelled.CancellationToken == Token || cancelled.CancellationToken == parent.Token);
        var timedOut = !callerCancellationRequested &&
            (failure is TimeoutException || originalCancellation && Stopwatch.GetElapsedTime(OriginalStarted) >= WorkLimit);
        return (timedOut, !timedOut && (callerCancellationRequested || parent.Token.IsCancellationRequested));
    }
    internal bool CanStillFinish { get { try { if (LocalCleanupEntered is not null) _ = CleanupRemaining; else CheckWork(); return true; } catch (OperationCanceledException) { return false; } catch (TimeoutException) { return false; } catch (IOException) { return false; } } }
    public void BeginFailureCleanup() { if (parent is IOwnedFailureCleanup actual) actual.BeginFailureCleanup(); else _ = parent.CleanupRemaining; }
    public void PreserveUnknown() { if (parent is IOwnedBudgetUnknownRetention actual) actual.PreserveUnknown(); }
    public void Dispose() => linked.Dispose(); // Caller admits only after actual all-task/native closure.
}
