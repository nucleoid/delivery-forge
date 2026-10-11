namespace DeliveryForge.EndToEnd.Tests;

public sealed record ActualParentCrashSnapshot(string ReadyPath, string CapturedReadyPath, string RecoveryReportPath,
    string ReleaseControlReportPath, string PlanContractPath, string ParentObservationPath);

internal static class ActualParentCrashOrchestrator
{
    internal static Task<ActualParentCrashSnapshot> RunEarlyAsync(OwnedScenarioRun artifacts, OwnedEphemeralRuntime runtime,
        string stage, string nonce, string sentinelPath, string frozenBaseCommit, long originalStarted, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stage is not ("plan-frozen" or "worker-prepared" or "host-accepted" or "owned-command-started"))
            throw new ArgumentException("This compiled RED slice covers only the four early boundaries.", nameof(stage));
        return Task.FromException<ActualParentCrashSnapshot>(new NotImplementedException(
            "Actual force-terminated owned driver and independent second recovery driver orchestration is RED at " + stage + "; lifetime disposal is not parent crash evidence."));
    }
}
