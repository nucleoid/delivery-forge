using DeliveryForge.Execution;
using DeliveryForge.Planning;
namespace DeliveryForge.Tool;

public sealed record ParentAssignmentRequest(FrozenPlan Plan, TrustedConfiguration Configuration, string? RunDirectory, string? ResultDirectory);
// Supplied by the actual containing owner. Select immutable paths/writer/run DATA
// only; never provision or dispatch here. ExecutionCoordinator is the sole owner
// that validates the assignment and performs those effects. No JSON factory.
public interface IWorkerAssignmentPort
{
    Task<FrozenWorkerPlan> SelectAsync(ParentAssignmentRequest request, TimeSpan remaining, CancellationToken cancellationToken);
}
public sealed class AssignmentObservation
{
    internal AssignmentObservation(ParentAssignmentRequest request, ToolLifetime original) { Request = request; Original = original; }
    public ParentAssignmentRequest Request { get; }
    internal ToolLifetime Original { get; }
    public bool Entered { get; internal set; }
    public bool InvocationReturned { get; internal set; }
    public Task<FrozenWorkerPlan>? ActualTask { get; internal set; }
    public FrozenWorkerPlan? Result { get; internal set; }
    public Exception? Primary { get; internal set; }
    public bool Unknown => Entered && (!InvocationReturned || ActualTask is null || !ActualTask.IsCompleted);
}
