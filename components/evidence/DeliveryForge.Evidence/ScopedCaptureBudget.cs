using System.Diagnostics;
namespace DeliveryForge.Evidence;

// A restrictive view of the actual enclosing original. No incoming timestamps,
// actor callbacks, replacement owner or independently renewed clock are accepted.
internal sealed class ScopedCaptureBudget : IOwnedDuplexBudget, IOwnedFailureCleanup, IOwnedBudgetUnknownRetention, IDisposable
{
    readonly IOwnedDuplexBudget parent; readonly CancellationTokenSource linked; readonly long localStarted; readonly TimeSpan localWhole, localWork; readonly object gate = new(); long? cleanup;
    internal ScopedCaptureBudget(IOwnedDuplexBudget actualParent, TimeSpan localGrant, CancellationToken caller) { parent = actualParent; parent.CheckWork(); localStarted = Stopwatch.GetTimestamp(); localWhole = localGrant; localWork = BoundedCaptureStream.WorkBudget(localGrant); linked = CancellationTokenSource.CreateLinkedTokenSource(parent.Token, caller); CheckWork(); }
    public long OriginalStarted => parent.OriginalStarted;
    TimeSpan Offset => Stopwatch.GetElapsedTime(parent.OriginalStarted, localStarted);
    public TimeSpan WholeLimit { get { var local = Offset + localWhole; return local < parent.WholeLimit ? local : parent.WholeLimit; } }
    public TimeSpan WorkLimit { get { var local = Offset + localWork; return local < parent.WorkLimit ? local : parent.WorkLimit; } }
    public CancellationToken Token => linked.Token;
    internal long? LocalCleanupEntered { get { lock (gate) return cleanup; } }
    public void CheckWork() { lock (gate) { if (cleanup is not null) throw new IOException("Original command work is closed after first cleanup."); } parent.CheckWork(); Token.ThrowIfCancellationRequested(); if (Stopwatch.GetElapsedTime(OriginalStarted) >= WorkLimit) throw new TimeoutException("Original MIN command work allowance expired."); }
    public TimeSpan CleanupRemaining { get { lock (gate) { cleanup ??= Stopwatch.GetTimestamp(); var actualParent = parent.CleanupRemaining; var whole = WholeLimit - Stopwatch.GetElapsedTime(OriginalStarted); var once = TimeSpan.FromSeconds(60) - Stopwatch.GetElapsedTime(cleanup.Value); var remaining = actualParent < whole ? actualParent : whole; if (once < remaining) remaining = once; if (remaining <= TimeSpan.Zero) throw new TimeoutException("SAME original command/parent whole/earliest cleanup endpoint expired."); return remaining; } } }
    internal bool CanStillFinish { get { try { if (LocalCleanupEntered is not null) _ = CleanupRemaining; else CheckWork(); return true; } catch (OperationCanceledException) { return false; } catch (TimeoutException) { return false; } catch (IOException) { return false; } } }
    public void BeginFailureCleanup() { if (parent is IOwnedFailureCleanup actual) actual.BeginFailureCleanup(); else _ = parent.CleanupRemaining; }
    public void PreserveUnknown() { if (parent is IOwnedBudgetUnknownRetention actual) actual.PreserveUnknown(); }
    public void Dispose() => linked.Dispose(); // Caller admits only after actual all-task/native closure.
}
