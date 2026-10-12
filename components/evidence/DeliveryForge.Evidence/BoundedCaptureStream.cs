using System.Diagnostics;

namespace DeliveryForge.Evidence;

// A finite raw-byte prefix, while discarding the remainder until actual EOF. No decoder buffering.
internal sealed class BoundedCaptureStream(int maximumBytes)
{
    private readonly object _sync = new();
    private readonly MemoryStream _prefix = new();
    private int _exceeded;
    internal bool Exceeded => Volatile.Read(ref _exceeded) != 0;
    internal byte[] Snapshot() { lock (_sync) return _prefix.ToArray(); }
    internal async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) return;
            lock (_sync)
            {
                var take = Math.Min(count, maximumBytes - (int)_prefix.Length);
                _prefix.Write(buffer, 0, take);
                if (take != count) Volatile.Write(ref _exceeded, 1);
            }
        }
    }

    internal static Task Observe(Task task)
    {
        _ = task.ContinueWith(fault => _ = fault.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    internal static TimeSpan WorkBudget(TimeSpan total) => total - TimeSpan.FromTicks(Math.Min(total.Ticks / 4, TimeSpan.FromSeconds(2).Ticks));
    internal static bool Within(long started, TimeSpan budget) => Stopwatch.GetElapsedTime(started) < budget;
    internal static bool Eof(Task? task) => task?.Status == TaskStatus.RanToCompletion;
}
