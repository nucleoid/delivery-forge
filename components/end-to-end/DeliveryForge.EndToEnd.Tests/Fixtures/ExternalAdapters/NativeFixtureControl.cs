using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.EndToEnd.Tests;

// Explicit native-task fixture edge, never a configured model-worker adapter.
internal sealed class NativeFixtureControl(OwnedEphemeralRuntime runtime, AgentRunBinding binding, bool initiallyUnknown) : IAgentControlPort
{
    private bool _unknown = initiallyUnknown;
    public int Observations { get; private set; }
    public void EnableFreshOwnedFixtureObservations() => _unknown = false; // Capability script only; activity is always physically observed below.
    public Task<AgentControlResult> ControlAsync(AgentRunBinding supplied, AgentControlAction action, TimeSpan deadline, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Observations++;
        var outcomes = runtime.Processes.ObserveOwned(binding.RunId);
        var quiet = outcomes.All(item => item.Outcome == ProcessControlOutcome.AlreadyExited);
        var activity = _unknown || supplied != binding ? AgentActivityState.Unknown : quiet ? AgentActivityState.Quiescent : AgentActivityState.Live;
        return Task.FromResult(new AgentControlResult(supplied, action, activity, !_unknown && supplied == binding,
            "fixture-native-owned-observation:" + Guid.NewGuid().ToString("N"),
            "FIXTURE native task only; exact public process observations, no model worker or production host capability."));
    }
}
