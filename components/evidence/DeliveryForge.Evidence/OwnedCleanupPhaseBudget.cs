using System.Diagnostics;
namespace DeliveryForge.Evidence;
// Restrictive phase view of an existing original. No incoming time/issuer grant.
// It cannot extend original whole/earliest-once cleanup or restart its QPC.
internal sealed class OwnedCleanupPhaseBudget : IOwnedDuplexBudget,IOwnedFailureCleanup,IOwnedBudgetUnknownRetention
{
    internal IOwnedDuplexBudget Parent {get;}
    readonly TimeSpan ceiling;
    internal OwnedCleanupPhaseBudget(IOwnedDuplexBudget actualOriginal)
    {
        ArgumentNullException.ThrowIfNull(actualOriginal);Parent=actualOriginal;
        var before=Stopwatch.GetElapsedTime(Parent.OriginalStarted);
        var remaining=Parent.CleanupRemaining; // Existing earliest cleanup, never a new grant.
        var cap=before+remaining;
        ceiling=TimeSpan.FromTicks(Math.Min(cap.Ticks,Math.Min(TimeSpan.FromSeconds(1100).Ticks,Parent.WholeLimit.Ticks-1)));
        CheckWork();
    }
    public long OriginalStarted=>Parent.OriginalStarted;
    public TimeSpan WholeLimit=>Parent.WholeLimit;
    public TimeSpan WorkLimit=>ceiling;
    // Existing cleanup intentionally ignores WORK cancellation, within exactly
    // the actual original whole/once endpoint used by same-owned Stop.
    public CancellationToken Token=>CancellationToken.None;
    public void CheckWork(){_=Parent.CleanupRemaining;if(Stopwatch.GetElapsedTime(OriginalStarted)>=ceiling)throw new TimeoutException("Original cleanup observation endpoint expired.");}
    public TimeSpan CleanupRemaining {get {CheckWork();var left=ceiling-Stopwatch.GetElapsedTime(OriginalStarted);var original=Parent.CleanupRemaining;return left<original?left:original;}}
    public void BeginFailureCleanup(){if(Parent is IOwnedFailureCleanup failure)failure.BeginFailureCleanup();else _=Parent.CleanupRemaining;}
    public void PreserveUnknown(){if(Parent is IOwnedBudgetUnknownRetention retention)retention.PreserveUnknown();}
}
