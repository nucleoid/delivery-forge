using DeliveryForge.Contracts.State;
using DeliveryForge.Execution.Host;
using DeliveryForge.Review;

namespace DeliveryForge.Execution.Tests;

public sealed class ReviewAuthorityHostTests
{
    [Fact]
    public void Production_host_requires_frozen_source_and_rejects_fixture_capability()
    {
        var root = new ReviewAuthorityRootContext(
            "run", Identity('1'), Identity('2'), Identity('3'), Commit('4'),
            ReviewPolicyAuthorityKind.ProtectedGitBase,
            "parent-source", Path.GetTempPath(), Commit('5'), "object-view");
        var host = ReviewAuthorityHost.CreateProduction(
            new ProductionReviewAuthorityConfiguration("root", root));
        var fixture = new ReviewerCapability(
            "adapter", "1", "claude", true, true, false, ["source"]);

        Assert.Throws<InvalidOperationException>(() => host.IssueCapability(
            fixture,
            "fixture-proof",
            DateTimeOffset.Parse("2026-10-10T00:00:00Z"),
            DateTimeOffset.Parse("2026-10-10T00:05:00Z")));
    }

    [Fact]
    public void Test_acceptance_is_role_specific_and_cannot_be_issued_by_production_host()
    {
        var root = new ReviewAuthorityRootContext(
            "run", Identity('1'), Identity('2'), Identity('3'), Commit('4'),
            ReviewPolicyAuthorityKind.ProtectedGitBase,
            "parent-source", Path.GetTempPath(), Commit('5'), "object-view");
        var context = new ReviewAcceptanceContext(
            root.RunIdentity, "request", root.PlanIdentity, root.EvidencePolicyIdentity,
            root.ReviewPolicyContentIdentity, root.ReviewPolicySourceRevision,
            root.ReviewPolicyAuthorityKind, root.SourceAuthorityIdentity, root.SourceRoot,
            root.ProtectedBaseCommit, root.ProtectedObjectViewIdentity);
        var test = ReviewAuthorityHost.CreateIsolatedTest(root);
        test.IssueTestAcceptance(
            Identity('a'), ReviewEvidenceRole.Independent, context, "execution");

        var production = ReviewAuthorityHost.CreateProduction(
            new ProductionReviewAuthorityConfiguration("root", root));
        Assert.Throws<InvalidOperationException>(() => production.IssueTestAcceptance(
            Identity('a'), ReviewEvidenceRole.Independent, context, "execution"));
    }

    private static string Identity(char value) => $"sha256:{new string(value, 64)}";
    private static string Commit(char value) => new(value, 40);
}
