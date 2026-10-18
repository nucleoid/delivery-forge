using System.Diagnostics;

namespace DeliveryForge.Evidence;

// MIN only: never renews or enlarges the legacy local owner's original lifetime. An optional
// actual same-origin budget synchronously owns its private earliest cleanup latch. Scalars
// exported after return remain DATA; no incoming timestamp/DTO can create a process owner.
internal sealed class RestrictiveCaptureBudget(long localStarted, TimeSpan localWhole, TimeSpan localWork,
    IOwnedDuplexBudget? enclosing, BoundedCaptureRetention retention)
{
    private long? _cleanup;
    internal long? CleanupStarted => _cleanup;
    internal bool WorkAvailable
    {
        get
        {
            if (!BoundedCaptureStream.Within(localStarted, localWork)) return false;
            if (enclosing is null) return true;
            try { enclosing.CheckWork(); return true; }
            catch (Exception failure) { retention.Add(failure); return false; }
        }
    }
    internal void BeginFailureCleanup()
    {
        _cleanup ??= Stopwatch.GetTimestamp();
        if (enclosing is IOwnedFailureCleanup actual)
        {
            try { actual.BeginFailureCleanup(); }
            catch (Exception error) { retention.Add(error); }
        }
    }
    internal void BeginCleanup()
    {
        _cleanup ??= Stopwatch.GetTimestamp();
        _ = CleanupRemaining; // Actual synchronous shared-latch entry, before termination/waits/first close.
    }
    internal TimeSpan CleanupRemaining
    {
        get
        {
            var remaining = localWhole - Stopwatch.GetElapsedTime(localStarted);
            if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
            if (enclosing is null) return remaining;
            try
            {
                var actual = enclosing.CleanupRemaining;
                if (_cleanup is { } first)
                {
                    var localOnce = TimeSpan.FromSeconds(60) - Stopwatch.GetElapsedTime(first);
                    if (localOnce < actual) actual = localOnce;
                }
                return actual <= TimeSpan.Zero ? TimeSpan.Zero : actual < remaining ? actual : remaining;
            }
            catch (Exception failure) { retention.Add(failure); return TimeSpan.Zero; }
        }
    }
    internal bool CleanupAvailable => CleanupRemaining > TimeSpan.Zero;
}