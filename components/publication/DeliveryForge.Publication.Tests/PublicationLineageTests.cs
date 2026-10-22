using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Publication.Tests;

public sealed class PublicationLineageTests
{
    [Fact]
    public async Task New_root_revision_publishes_fresh_chain_without_adopting_the_prior_token()
    {
        var prior = new PublicationFixture();
        var oldRun = prior.Authority.IssueRun(prior.Definition(AuthorizationCeiling.Implement));
        var next = PublicationFixture.NewRoot();
        var host = PublicationAuthorityHost.CreateReconciledFixture(prior.Authority, oldRun, next.ReviewHost,
            GateReceiptConsumerPolicy.CreateIsolatedFixture(
                TrustedGatePluginRegistry.BuiltIns, next.GateReceiptProvenance));
        var revision = host.IssueReconciledRevision(oldRun, next.Definition(AuthorizationCeiling.Pr) with
        {
            PriorRevisionIdentity = oldRun.Definition.RevisionIdentity
        });
        var adapter = new FixtureGitHost(next);
        var service = new PublicationService(host, new FixtureFacts(next), adapter, new FixtureJournal());
        var borrowed = await service.ExecuteAsync(oldRun, new(PublicationMode.Pr, "old-token", false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationDenial.Authority, borrowed.Denial);
        Assert.Equal(0, adapter.Mutations);
        var result = await service.ExecuteAsync(revision, new(PublicationMode.Pr, "new-root", false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationOutcome.PublishedFixture, result.Outcome);
        Assert.Equal(AuthorizationCeiling.Implement, oldRun.Definition.Ceiling);
        Assert.NotEqual(oldRun.Definition.Policy.Identity, revision.Definition.Policy.Identity);
        Assert.NotEqual(oldRun.Definition.Patch.PlanIdentity, revision.Definition.Patch.PlanIdentity);
    }

    [Fact]
    public void Same_named_foreign_prior_host_cannot_lend_registered_membership()
    {
        var prior = new PublicationFixture();
        var foreign = new PublicationFixture();
        var oldRun = prior.Authority.IssueRun(prior.Definition(AuthorizationCeiling.Implement));
        Assert.Throws<InvalidOperationException>(() => PublicationAuthorityHost.CreateReconciledFixture(
            foreign.Authority, oldRun, PublicationFixture.NewRoot().ReviewHost));
    }

    [Fact]
    public void Owned_source_scope_cannot_change_during_reconciliation()
    {
        var prior = new PublicationFixture();
        var oldRun = prior.Authority.IssueRun(prior.Definition(AuthorizationCeiling.Implement));
        var moved = PublicationFixture.NewRoot(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "different-publication-scope")));
        Assert.Throws<InvalidOperationException>(() => PublicationAuthorityHost.CreateReconciledFixture(prior.Authority, oldRun, moved.ReviewHost));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prior_review_bytes_do_not_replace_new_root_fresh_acceptance(bool hosted)
    {
        var prior = new PublicationFixture();
        var oldRun = prior.Authority.IssueRun(prior.Definition(AuthorizationCeiling.Implement));
        var next = PublicationFixture.NewRoot();
        var host = PublicationAuthorityHost.CreateReconciledFixture(prior.Authority, oldRun, next.ReviewHost,
            GateReceiptConsumerPolicy.CreateIsolatedFixture(
                TrustedGatePluginRegistry.BuiltIns, next.GateReceiptProvenance));
        var definition = next.Definition(AuthorizationCeiling.Pr) with
        {
            PriorRevisionIdentity = oldRun.Definition.RevisionIdentity,
            ReviewReceipt = hosted ? next.Review : prior.Review,
            HostedReviewReceipt = hosted ? prior.HostedReview : next.HostedReview
        };
        var revision = host.IssueReconciledRevision(oldRun, definition);
        var adapter = new FixtureGitHost(next);
        var service = new PublicationService(host, new FixtureFacts(next), adapter, new FixtureJournal());
        // PR needs only independent acceptance; hosted binding is exercised through the
        // actual hosted consumer at the new root's PR ceiling, without production promotion.
        if (hosted)
        {
            next.ReviewHost.EnsureContractFixtureTransition(WorkflowState.CiComplete, WorkflowState.HostReviewComplete,
                next.Transition, next.Receiver);
            Assert.Throws<DeliveryForge.Contracts.State.InvalidWorkflowTransitionException>(() => next.ReviewHost.EnsureContractFixtureTransition(
                WorkflowState.CiComplete, WorkflowState.HostReviewComplete,
                next.Transition with { HostReviewReceipt = prior.HostedReview }, next.Receiver));
        }
        else
        {
            var result = await service.ExecuteAsync(revision, new(PublicationMode.Pr, "old-review", false), TestContext.Current.CancellationToken);
            Assert.Equal(PublicationDenial.Review, result.Denial);
        }
        Assert.Equal(0, adapter.Mutations);
    }
}
