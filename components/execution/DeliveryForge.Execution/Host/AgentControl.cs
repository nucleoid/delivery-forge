namespace DeliveryForge.Execution.Host;

public enum AgentControlAction { Pause, Stop, Resume }
public enum AgentActivityState { Quiescent, Live, Unknown }

public sealed record AgentRunBinding(
    string AdapterId,
    string AdapterVersion,
    string RunId,
    string RequestIdentity,
    string RuntimeRunIdentity,
    string RuntimeTaskIdentity);

public sealed record AgentControlResult(
    AgentRunBinding Binding,
    AgentControlAction Action,
    AgentActivityState Activity,
    bool CapabilitySupported,
    string EvidenceReference,
    string Limitation);

public interface IAgentControlPort
{
    // Separate fresh lookup for a request with no accepted runtime binding. Ordinary accepted-run
    // control support is not proof that this adapter can exclude an in-flight unstarted dispatch.
    Task<AgentControlResult> ObserveUnstartedAsync(
        AgentRunBinding binding, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentControlResult(binding, AgentControlAction.Stop, AgentActivityState.Unknown, false, "",
            "INCOMPLETE: adapter has no explicit fresh unstarted-request observation capability."));

    Task<AgentControlResult> ControlAsync(
        AgentRunBinding binding,
        AgentControlAction action,
        TimeSpan deadline,
        CancellationToken cancellationToken = default);
}

internal sealed class UnsupportedAgentControlPort : IAgentControlPort
{
    public Task<AgentControlResult> ControlAsync(
        AgentRunBinding binding,
        AgentControlAction action,
        TimeSpan deadline,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentControlResult(binding, action, AgentActivityState.Unknown, false, "",
            $"Adapter '{binding.AdapterId}' has no capability-proven {action.ToString().ToLowerInvariant()} control port; accepted activity remains unknown."));
}
