using System.Diagnostics;
using DeliveryForge.Evidence;

namespace DeliveryForge.EndToEnd.Tests;

public enum QualityInterruptionPhase { ColdControl, SourceDrift, Timeout, LateArtifact, CallerCancellation }
public sealed record QualityInterruptionSnapshot(GateRunResult Gate, string EnteredPath, string Nonce,
    string InvocationTrxPath, string ProvenancePath, string CapturedReceiptPath);

// Trusted fixture-only subprofile; does not change ordinary120s or the separate real-dotnet540s profile.
internal sealed class QualityInterruptionClock : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource _wall = new(TimeSpan.FromSeconds(720));
    public const string ProfileId = "real-dotnet-interruption-fixture-v1";
    public const int TotalSeconds = 720;
    public const int CleanupSeconds = 60;
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);
    public CancellationToken WallToken => _wall.Token;
    public TimeSpan WorkBudget(int stageSeconds)
    {
        _wall.Token.ThrowIfCancellationRequested();
        var remaining = TimeSpan.FromSeconds(TotalSeconds - CleanupSeconds) - Elapsed;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Interruption fixture work budget exhausted; cleanup reserve retained.");
        return remaining < TimeSpan.FromSeconds(stageSeconds) ? remaining : TimeSpan.FromSeconds(stageSeconds);
    }
    public void Dispose() => _wall.Dispose();
}

internal static class QualityInterruptionOrchestrator
{
    public static Task<IReadOnlyDictionary<QualityInterruptionPhase, QualityInterruptionSnapshot>> RunFiveAsync(
        OwnedScenarioRun artifacts, string ownedSourceDirectory, QualityInterruptionClock clock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        clock.WorkBudget(30);
        return Task.FromException<IReadOnlyDictionary<QualityInterruptionPhase, QualityInterruptionSnapshot>>(
            new NotImplementedException("Actual public-owner .NET interruption sequence is RED: compiled-entry barrier, source drift, timeout, sequential late producer and caller cancellation are not implemented."));
    }
}
