using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution.Host;
using DeliveryForge.Review;

namespace DeliveryForge.EndToEnd.Tests;

internal sealed record ProtectedReviewEdgesSetup(OwnedScenarioRun Run, ProtectedReviewFixtureBootstrap Bootstrap,
    ProtectedReviewFixtureBootstrapTests.SetupResult Source, PatchIdentity Patch,
    ReviewAcceptanceContext IndependentContext, ReviewAcceptanceContext HostedContext, ReviewTransitionReceiver PinnedReceiver,
    ValidatedContract PublicationInput, ValidatedContract CiInput, ValidatedContract MergeInput);
internal sealed record ReviewEdgeObservation(string Edge, bool Allowed, bool ProductionDenied, string Reason);
internal sealed record ReviewArtifactObservation(string Path, string BeforeSha256, string AfterSha256);
internal sealed record ProtectedReviewEdgesSnapshot(IReadOnlyList<ReviewEdgeObservation> Before,
    IReadOnlyList<ReviewEdgeObservation> After, IReadOnlyList<ReviewEdgeObservation> SecondaryOwn,
    IReadOnlyList<ReviewEdgeObservation> SecondaryAtOriginal, IReadOnlyList<ReviewEdgeObservation> OriginalStillValid,
    IReadOnlyList<ReviewArtifactObservation> ImmutableArtifacts, int ReviewerFixtureCalls, int PublicationCalls, int ModelCalls,
    string ObservationPath, string ReviewerLedgerPath, string? CurrentHeadAfterDrift);

internal static class ProtectedReviewEdgesOrchestrator
{
    internal static Task<ProtectedReviewEdgesSnapshot> RunAsync(ProtectedReviewEdgesSetup setup, string scenario,
        long originalStarted, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromException<ProtectedReviewEdgesSnapshot>(new NotImplementedException(
            "All five public protected review-authorizing edges/current dual-role receiver orchestration is RED at " + scenario));
    }
}
