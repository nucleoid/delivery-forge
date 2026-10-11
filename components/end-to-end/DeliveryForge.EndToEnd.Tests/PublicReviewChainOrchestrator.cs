using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Review;

namespace DeliveryForge.EndToEnd.Tests;

public sealed record PublicReviewSetup(OwnedScenarioRun Artifacts, string BaseCommit, string HeadCommit, string HeadTree,
    FrozenPlan Plan, string EvidencePolicyIdentity, string GitExecutable, PatchIdentity ActualPatch, string RawIndexSha256);
public sealed record PublicReviewChainSnapshot(string ObservationPath, string? RawReviewerPath, string? CanonicalReceiptPath);

internal static class PublicReviewChainOrchestrator
{
    internal static Task<PublicReviewChainSnapshot> RunAsync(PublicReviewSetup setup, string scenario, long originalStarted, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (scenario is not ("marked-positive" or "strict-same-family" or "tracked-drift" or "head-drift" or "raw-index-drift" or "revoked-capability" or "foreign-host" or "distinct-request"))
            throw new ArgumentException("Undefined initial public review-chain scenario.", nameof(scenario));
        return Task.FromException<PublicReviewChainSnapshot>(new NotImplementedException(
            "Full public registration/issuer/execution/normalizer/receipt/lookup/pinned receiver review composition is RED at " + scenario));
    }
}
