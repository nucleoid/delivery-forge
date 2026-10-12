using System.Diagnostics;

namespace DeliveryForge.EndToEnd.Tests;

internal sealed record MiddleCleanupStage(string Name, Exception? Failure);

// Trusted MIDDLE fixture lifetime only. No JSON/PID adoption or production authority.
internal sealed class MiddleCrashLifetime
{
    private static readonly object RootsGate = new();
    private static readonly HashSet<MiddleCrashLifetime> Roots = [];
    private readonly object _gate = new();
    private readonly List<object> _owners = [];
    private long? _cleanupStarted;
    internal MiddleCrashLifetime(long started, int total)
    {
        if (total is not (120 or 540) || started <= 0 || started > Stopwatch.GetTimestamp()) throw new ArgumentException("Known original middle profile required.");
        Started = started; Total = total;
        lock (RootsGate) Roots.Add(this); // Attach original live objects before any setup can fail.
    }
    internal long Started { get; }
    internal int Total { get; }
    internal Exception? Primary { get; private set; }
    internal bool Cleaning { get { lock (_gate) return _cleanupStarted is not null; } }
    internal bool HasPendingTasks { get { lock (_gate) return _owners.OfType<Task>().Any(task => !task.IsCompleted); } }
    internal void Retain(object owner) { lock (_gate) if (!_owners.Any(item => ReferenceEquals(item, owner))) _owners.Add(owner); }
    internal void PrimaryFailure(Exception failure) { Primary ??= failure; Retain(failure); }
    internal async Task<T> WorkAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken caller)
    {
        var left = Remaining(); using var bound = CancellationTokenSource.CreateLinkedTokenSource(caller);
        bound.CancelAfter(left);
        var actual = operation(bound.Token); Track(actual);
        try { var value = await actual.WaitAsync(left, bound.Token); _ = Remaining(); return value; }
        catch (Exception failure) { PrimaryFailure(failure); throw; }
    }
    internal void Track(Task actual)
    {
        Retain(actual);
        _ = actual.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    internal void BeginCleanup() { lock (_gate) _cleanupStarted ??= Stopwatch.GetTimestamp(); }
    internal TimeSpan Remaining(bool cleanup = false)
    {
        var left = TimeSpan.FromSeconds(Total - (cleanup ? 0 : 20)) - Stopwatch.GetElapsedTime(Started);
        if (cleanup)
        {
            BeginCleanup();
            var reserve = TimeSpan.FromSeconds(20) - Stopwatch.GetElapsedTime(_cleanupStarted!.Value);
            if (reserve < left) left = reserve;
        }
        if (left <= TimeSpan.Zero) throw new TimeoutException("Original middle lifetime/reserve exhausted; live references retained.");
        return left;
    }
    internal async Task<IReadOnlyList<MiddleCleanupStage>> StagesAsync(params (string Name, object Owner, Func<CancellationToken, Task> Operation)[] stages)
    {
        BeginCleanup(); var observed = new List<MiddleCleanupStage>();
        foreach (var stage in stages)
        {
            Retain(stage.Owner);
            Exception? failure = null; Task? actual = null;
            try
            {
                var left = Remaining(true); using var bound = new CancellationTokenSource(left);
                actual = stage.Operation(bound.Token); Track(actual);
                await actual.WaitAsync(left, bound.Token);
                _ = Remaining(true);
            }
            catch (Exception error) { failure = error; Retain(stage.Owner); Retain(error); if (actual is not null) Retain(actual); }
            observed.Add(new(stage.Name, failure));
        }
        return observed.AsReadOnly();
    }
    internal void ReleaseKnownClosed(OwnedScenarioRun original)
    {
        lock (_gate)
            if (!_owners.Any(owner => ReferenceEquals(owner, original)) || _owners.OfType<Task>().Any(task => !task.IsCompleted))
                throw new InvalidOperationException("Original middle owner or actual task settlement is missing.");
        _ = Remaining(true);
        // A failed prerequisite may retain a different original tool/runtime owner. Never
        // discard that graph merely because this controller is now positively closed.
        if (Primary is not null) return;
        lock (RootsGate) Roots.Remove(this);
    }
}
