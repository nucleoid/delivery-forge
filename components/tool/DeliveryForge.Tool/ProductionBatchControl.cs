namespace DeliveryForge.Tool;

// Additional node-birth restriction only. This never issues host/lease authority.
internal sealed class ProductionBatchControl
{
    readonly object gate = new();
    bool paused, stopped;
    internal bool Paused { get { lock (gate) return paused; } }
    internal bool Stopped { get { lock (gate) return stopped; } }
    internal void Pause() { lock (gate) paused = true; }
    internal void Stop() { lock (gate) { stopped = true; paused = true; } }
    internal T EnterNodeStart<T>(Func<T> actualInvocation)
    {
        lock (gate)
        {
            if (paused || stopped) throw new ToolUsageException("Production batch pause/stop prevents a new native node start.");
            return actualInvocation(); // Return the untouched actual invocation Task.
        }
    }
}
