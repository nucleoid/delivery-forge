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
    int startEntered, startReturned, birthReturned, creationEntered, creationReturned, created;
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
        try
        {
            budget.CheckWork();
            // The session already retains this frame before the actual Start call.
            Volatile.Write(ref startEntered, 1);
            thread.Start(); Volatile.Write(ref startReturned, 1);
            birth.Task.WaitAsync(budget.WorkLimit - Stopwatch.GetElapsedTime(budget.OriginalStarted), budget.Token).GetAwaiter().GetResult();
            if (birthFailure is not null) ExceptionDispatchInfo.Capture(birthFailure).Throw();
            budget.CheckWork();
        }
        catch (Exception primary)
        {
            if (Volatile.Read(ref startEntered) == 0)
            {
                // CheckWork refused before Thread.Start was entered: no creator
                // or native birth exists. This is not an invented successful Join.
                Volatile.Write(ref birthReturned, 1); session.LinuxBirthReturned(); birth.TrySetResult(true);
            }
            var secondary = new List<Exception>(2);
            try
            {
                if (budget is IOwnedFailureCleanup cleanup) cleanup.BeginFailureCleanup();
                else _ = budget.CleanupRemaining;
            }
            catch (Exception admission) { session.RecordOwnerFailure(admission); secondary.Add(admission); }
            // Never publish a failed Open while ordinary birth can still mutate
            // its streams, unless this SAME cleanup endpoint cannot settle it.
            try { WaitForBirthInCleanupAsync().GetAwaiter().GetResult(); }
            catch (Exception wait) { session.RecordOwnerFailure(wait); secondary.Add(wait); }
            if (secondary.Count != 0) throw new AggregateException("Actual Linux birth failure and same-original cleanup failures.", new[] { primary }.Concat(secondary));
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }
    internal async Task WaitForBirthInCleanupAsync()
    {
        await birth.Task.WaitAsync(budget.CleanupRemaining, CancellationToken.None).ConfigureAwait(false);
        _ = budget.CleanupRemaining;
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
    internal bool SettleCreator(bool cleanup)
    {
        Check();
        if (!BirthReturned) return false;
        if (Volatile.Read(ref startEntered) == 0) return true; // Known no Thread.Start, not joined.
        if (Volatile.Read(ref startReturned) == 0) return false;
        if (ReferenceEquals(thread, Thread.CurrentThread)) throw new IOException("Actual Linux creator cannot join itself.");
        var left = cleanup ? budget.CleanupRemaining : budget.WorkLimit - Stopwatch.GetElapsedTime(budget.OriginalStarted);
        joined = thread.Join(left); Check(); return joined;
        void Check() { if (cleanup) _ = budget.CleanupRemaining; else budget.CheckWork(); }
    }
    public void Dispose()
    {
        lock (signalGate)
        {
            if (!BirthReturned || Volatile.Read(ref startEntered) != 0 && !joined || dispositionEntered)
                throw new IOException("Actual Linux creator must be absent before Start or joined before one disposition.");
            dispositionEntered = true; rootExited.Dispose();
        }
    }
}
