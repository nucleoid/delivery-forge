using DeliveryForge.Evidence;

namespace DeliveryForge.EndToEnd.Tests;

internal sealed record MiddleCrashPrerequisites(string RuntimeRoot, string RuntimeBaseCommit,
    string FrozenPlanContractPath, string? ActualNativeResultPath, string? ActualGateReceiptPath,
    string? ActualTrxPath, string? ActualReviewReceiptPath, string PrerequisiteObservationPath);
internal sealed record ActualMiddleCrashSnapshot(string ReadyPath, string CapturedReadyPath,
    string ParentObservationPath, string RecoveryReportPath, string ReleaseControlReportPath, string BoundaryArtifactPath);

internal static class ActualParentCrashMiddleOrchestrator
{
    internal static Task<ActualMiddleCrashSnapshot> RunMiddleAsync(OwnedScenarioRun artifacts,
        string stage, string nonce, string sentinelPath, MiddleCrashPrerequisites prerequisites,
        long originalStarted, RealDotNetFixtureClock? originalToolClock, CancellationToken token)
        => throw new NotImplementedException("RED actual middle durable boundary parent termination and fresh second-process recovery after genuine owner prerequisites.");
}
