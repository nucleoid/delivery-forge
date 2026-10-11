namespace DeliveryForge.EndToEnd.Tests;

internal static class ColdRecoveryOrchestrator
{
    public static Task ObserveAsync(OwnedScenarioRun artifacts, OwnedEphemeralRuntime runtime, string scenario,
        List<string> receipts, Dictionary<string, bool> invariants, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Public cold-lifetime orchestration awaits compiled RED: " + scenario);
}
