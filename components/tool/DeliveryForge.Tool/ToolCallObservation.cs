namespace DeliveryForge.Tool;
// Strong references to entered original Tasks and containing clock remain after
// timeout. Completion/exception DATA never reconstructs private owner authority.
public sealed class ToolCallObservation
{
    internal ToolCallObservation(string kind, ToolLifetime original) { Kind = kind; Original = original; }
    public string Kind { get; }
    internal ToolLifetime Original { get; }
    public bool Entered { get; internal set; }
    public bool InvocationReturned { get; internal set; }
    public Task? ActualTask { get; internal set; }
    public Exception? Primary { get; internal set; }
    public Exception? Post { get; internal set; }
    public bool Unknown => Entered && (!InvocationReturned || ActualTask is null || !ActualTask.IsCompleted);
}
