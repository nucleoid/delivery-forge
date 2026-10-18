namespace DeliveryForge.Evidence;
// Optional outgoing notification from an ACTUAL failing owner before its
// termination/disposition effects. No incoming stamp/DTO/issuer or time grant.
public interface IOwnedFailureCleanup { void BeginFailureCleanup(); }

// A subordinate preflight's successful native disposal remains enclosing WORK.
// Real failure synchronously closes that actual enclosing main phase, once.
public sealed class OwnedWorkPhaseBudget : IOwnedDuplexBudget,IOwnedFailureCleanup,IOwnedBudgetUnknownRetention {
    internal IOwnedDuplexBudget Parent {get;}
    readonly object gate=new();bool failure;
    public OwnedWorkPhaseBudget(IOwnedDuplexBudget actualParent){ArgumentNullException.ThrowIfNull(actualParent);actualParent.CheckWork();Parent=actualParent;}
    public long OriginalStarted=>Parent.OriginalStarted;public TimeSpan WholeLimit=>Parent.WholeLimit;public TimeSpan WorkLimit=>Parent.WorkLimit;public CancellationToken Token=>Parent.Token;
    public void CheckWork(){lock(gate){if(failure)throw new IOException("Actual subordinate failure closed enclosing work.");Parent.CheckWork();}}
    public TimeSpan CleanupRemaining {get {lock(gate){if(failure)return Parent.CleanupRemaining;Parent.CheckWork();var left=Parent.WorkLimit-System.Diagnostics.Stopwatch.GetElapsedTime(Parent.OriginalStarted);if(left<=TimeSpan.Zero)throw new TimeoutException("Actual enclosing work endpoint expired.");return left;}}}
    public void BeginFailureCleanup(){lock(gate){failure=true;if(Parent is IOwnedFailureCleanup actual)actual.BeginFailureCleanup();else _=Parent.CleanupRemaining;}}
    public void PreserveUnknown(){if(Parent is IOwnedBudgetUnknownRetention actual)actual.PreserveUnknown();}
}
