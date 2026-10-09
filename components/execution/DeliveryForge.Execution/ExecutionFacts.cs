using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Execution;

public sealed record ExecutionOwnedFacts(
    string RequestIdentity,
    string PlanIdentity,
    string BaseCommit,
    string AuthorizationCeiling,
    IReadOnlyList<string> RequiredArtifactPaths);

public interface IExecutionFactSource
{
    Task<ExecutionOwnedFacts> ObserveAsync(
        AgentRequest request,
        WorktreeObservation worktree,
        CancellationToken cancellationToken = default);
}

internal sealed class RequestExecutionFactSource : IExecutionFactSource
{
    public Task<ExecutionOwnedFacts> ObserveAsync(
        AgentRequest request,
        WorktreeObservation worktree,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ExecutionOwnedFacts(
            WorkerEnvelope.ComputeRequestIdentity(request),
            request.PlanIdentity,
            request.BaseCommit,
            request.AuthorizationCeiling,
            []));
}
