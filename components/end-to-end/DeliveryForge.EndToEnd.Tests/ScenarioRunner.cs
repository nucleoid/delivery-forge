using DeliveryForge.Evidence;

namespace DeliveryForge.EndToEnd.Tests;

public enum ScenarioProfile { LocalContract, LocalReal, ProductionCoordinated }

// Capability declarations are diagnostic data. They cannot mint owner-issued capability/authority receipts.
public sealed record ScenarioCapabilityClaim(string Name, bool Available, bool Fixture, string EvidenceReference);
public sealed record ScenarioBudget(TimeSpan WallTime, int CpuCores, long MemoryMiB, long Tokens, decimal Cost);
public sealed record ScenarioOptions(ScenarioProfile Profile, IReadOnlyList<ScenarioCapabilityClaim> Capabilities,
    ScenarioBudget Budget, int Seed);
public sealed record ScenarioResult(GateOutcome Outcome, ScenarioProfile Profile, bool Fixture, bool FullRealProfileCertified,
    string IntegratedBaseCommit, string ImplementationHead, int Seed, ScenarioBudget Budget,
    IReadOnlyDictionary<string, bool> Invariants, IReadOnlyList<string> ReceiptIds, IReadOnlyList<string> Limitations);

// Initial public-interface RED seam. No scenario behavior is implemented before the parent's compiled RED receipt.
// The integrated publication owner's review-authority bridge is deliberately not duplicated here.
public sealed class ScenarioRunner(OwnedScenarioRun run)
{
    public Task<ScenarioResult> RunAsync(string name, ScenarioOptions options, CancellationToken cancellationToken = default)
    {
        _ = run;
        _ = name;
        _ = options;
        _ = cancellationToken;
        throw new NotImplementedException("Issue 11 initial scenario specification awaits compiled RED before behavior implementation.");
    }
}
