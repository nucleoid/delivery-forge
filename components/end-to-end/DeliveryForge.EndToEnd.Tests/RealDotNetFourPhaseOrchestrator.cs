using System.Diagnostics;
using DeliveryForge.Evidence;

namespace DeliveryForge.EndToEnd.Tests;

public enum RealDotNetFixturePhase { FailingTest, ColdPassing, MissingCoverage, WarmNoBuild }
public sealed record RealDotNetPhaseSnapshot(GateRunResult Gate, string InvocationTrxPath, string ProvenancePath);

// Explicit trusted test composition only. Ordinary ScenarioRunner still accepts at most120s.
internal sealed class RealDotNetFixtureBudget
{
    private RealDotNetFixtureBudget() { }
    public static RealDotNetFixtureBudget ParentApproved { get; } = new();
    public string Id => "real-dotnet-fixture-v1";
    public int TotalSeconds => 540;
    public int SetupGitSeconds => 30;
    public int ProbeSeconds => 60;
    public int InitialRestoreSeconds => 90;
    public int GateSeconds => 75;
    public int CleanupReserveSeconds => 60;
    public int CpuCores => 2;
    public long ManagedHeapMaximumBytes => 4294967296;
}

internal static class RealDotNetFourPhaseOrchestrator
{
    public static Task<IReadOnlyDictionary<RealDotNetFixturePhase, RealDotNetPhaseSnapshot>> RunFourAsync(
        OwnedScenarioRun artifacts, string ownedSourceDirectory, RealDotNetFixtureClock budget, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyDictionary<RealDotNetFixturePhase, RealDotNetPhaseSnapshot>>(
            new NotImplementedException("Actual .NET protected-policy/probe/restore/four-phase orchestration awaits compiled RED."));
}

// Constructed before shared initialization, so source/artifact/Git setup consumes the same540s.
internal sealed class RealDotNetFixtureClock : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource _wall = new(TimeSpan.FromSeconds(540));
    public RealDotNetFixtureBudget Profile => RealDotNetFixtureBudget.ParentApproved;
    public CancellationToken WallToken => _wall.Token;
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);
    public TimeSpan Remaining => TimeSpan.FromSeconds(Profile.TotalSeconds) - Elapsed;
    public TimeSpan WorkBudget(int stageSeconds)
    {
        _wall.Token.ThrowIfCancellationRequested();
        var work = Remaining - TimeSpan.FromSeconds(Profile.CleanupReserveSeconds);
        if (work <= TimeSpan.Zero) throw new OperationCanceledException("Trusted real-tool work budget exhausted; cleanup reserve retained.", _wall.Token);
        return work < TimeSpan.FromSeconds(stageSeconds) ? work : TimeSpan.FromSeconds(stageSeconds);
    }
    public void Dispose() => _wall.Dispose();
}
