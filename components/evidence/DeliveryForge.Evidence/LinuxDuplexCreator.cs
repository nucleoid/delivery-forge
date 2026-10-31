using System.Diagnostics;
using System.Runtime.ExceptionServices;
namespace DeliveryForge.Evidence;

// This actual creating OS thread must outlive its Linux child: PDEATHSIG is
// attached to a creating thread, not to the lifetime of its containing process.
internal sealed class LinuxDuplexCreator : IDisposable
{
    readonly IOwnedDuplexBudget budget;
    readonly OwnedDuplexProcess session;
    readonly Thread thread;
    readonly object signalGate = new();
    readonly ManualResetEventSlim rootExited = new(false);
    readonly TaskCompletionSource<bool> birth = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Exception? birthFailure;
    int startReturned, birthReturned, creationEntered, creationReturned, created;
    bool joined, dispositionEntered, rootExitObserved;
    internal bool BirthReturned => Volatile.Read(ref birthReturned) != 0;
    internal LinuxDuplexCreator(IOwnedDuplexBudget actual, OwnedDuplexProcess owner, Action create)
    {
        budget = actual; session = owner;
        thread = new Thread(() => Run(create))
        {
            IsBackground = true,
            Name = "delivery-forge-linux-creator"
        };
    }
    internal void CreationEntered() => Volatile.Write(ref creationEntered, 1);
    internal void CreationReturned(bool actualCreated)
    {
        Volatile.Write(ref created, actualCreated ? 1 : 0);
        Volatile.Write(ref creationReturned, 1);
    }
    internal void ObserveRootExited()
    {
        lock (signalGate) { if (rootExitObserved) return; rootExitObserved = true; rootExited.Set(); }
    }
    internal void StartAndWait()
    {
        budget.CheckWork();
        // The session already retains this frame before the actual Start call.
        // A thrown/unreturned Start is not invented as a joined creator.
        thread.Start(); Volatile.Write(ref startReturned, 1);
        birth.Task.WaitAsync(budget.WorkLimit - Stopwatch.GetElapsedTime(budget.OriginalStarted), budget.Token).GetAwaiter().GetResult();
        if (birthFailure is not null) ExceptionDispatchInfo.Capture(birthFailure).Throw();
        budget.CheckWork();
    }
    void Run(Action create)
    {
        try { create(); }
        catch (Exception failure) { birthFailure = failure; session.RecordOwnerFailure(failure); }
        finally
        {
            Volatile.Write(ref birthReturned, 1);
            session.LinuxBirthReturned();
            birth.TrySetResult(true);
        }
        if (Volatile.Read(ref creationEntered) == 0 || Volatile.Read(ref creationReturned) != 0 && Volatile.Read(ref created) == 0) return;
        try
        {
            // WORK cancellation/cleanup does not end the creator's lifetime.
            // Re-read the containing absolute whole bound; never start a new one.
            while (!rootExited.IsSet)
            {
                var left = budget.WholeLimit - Stopwatch.GetElapsedTime(budget.OriginalStarted);
                if (left <= TimeSpan.Zero) throw new TimeoutException("Original Linux creator whole endpoint expired.");
                rootExited.Wait(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception failure)
        {
            session.RecordOwnerFailure(failure);
            if (budget is IOwnedBudgetUnknownRetention retained) retained.PreserveUnknown();
        }
    }
    internal bool Join(bool cleanup)
    {
        Check();
        if (Volatile.Read(ref startReturned) == 0 || !BirthReturned) return false;
        if (ReferenceEquals(thread, Thread.CurrentThread)) throw new IOException("Actual Linux creator cannot join itself.");
        var left = cleanup ? budget.CleanupRemaining : budget.WorkLimit - Stopwatch.GetElapsedTime(budget.OriginalStarted);
        joined = thread.Join(left); Check(); return joined;
        void Check() { if (cleanup) _ = budget.CleanupRemaining; else budget.CheckWork(); }
    }
    public void Dispose()
    {
        lock (signalGate)
        {
            if (!joined || dispositionEntered) throw new IOException("Actual Linux creator must be joined before one disposition.");
            dispositionEntered = true; rootExited.Dispose();
        }
    }
}
