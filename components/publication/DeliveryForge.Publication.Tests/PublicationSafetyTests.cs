using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;
using DeliveryForge.Execution.Host;
using DeliveryForge.Publication.GitHost;

namespace DeliveryForge.Publication.Tests;

public sealed class PublicationSafetyTests(PublicationFixture fixture) : IClassFixture<PublicationFixture>
{
    [Theory]
    [InlineData(WorkflowState.IndependentReview, WorkflowState.PrAuthorized)]
    [InlineData(WorkflowState.PrAuthorized, WorkflowState.PrPublished)]
    [InlineData(WorkflowState.CiComplete, WorkflowState.HostReviewComplete)]
    [InlineData(WorkflowState.HostReviewComplete, WorkflowState.MergeAuthorized)]
    [InlineData(WorkflowState.MergeAuthorized, WorkflowState.Merged)]
    public void Real_public_issuer_chain_supports_all_five_fixture_consumers(WorkflowState from, WorkflowState to)
    {
        var evidence = fixture.Transition with
        {
            PublicationReceipt = fixture.PublicationReceipt(to == WorkflowState.Merged ? "MERGED" : "PR_PUBLISHED")
        };
        fixture.ReviewHost.EnsureContractFixtureTransition(from, to, evidence, fixture.Receiver);
    }

    [Fact]
    public void Real_issued_chain_can_use_only_its_own_fixture_bridge()
    {
        fixture.ReviewHost.EnsureContractFixtureTransition(WorkflowState.IndependentReview,
            WorkflowState.PrAuthorized, fixture.Transition, fixture.Receiver);
        var foreign = ReviewAuthorityHost.CreateIsolatedTest(fixture.Root, PublicationFixture.Time);
        Assert.Throws<InvalidOperationException>(() => foreign.EnsureContractFixtureTransition(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized, fixture.Transition, fixture.Receiver));
        var production = ReviewAuthorityHost.CreateProduction(new("production-root", fixture.Root));
        Assert.Throws<InvalidOperationException>(() => production.EnsureContractFixtureTransition(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized, fixture.Transition, fixture.Receiver));
        Assert.Throws<InvalidOperationException>(() => production.CreateContractFixturePolicy(
            [new("gpt", "claude")], ["source"], false, new(1, TimeSpan.FromMinutes(1), 100, 1m)));
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized, fixture.Transition, fixture.Receiver));
    }

    [Fact]
    public void Fixture_policy_factory_refuses_a_contradictory_root()
    {
        var host = ReviewAuthorityHost.CreateIsolatedTest(fixture.Root with
        {
            ReviewPolicyContentIdentity = PublicationFixture.Id('9')
        });
        Assert.Throws<InvalidOperationException>(() => host.CreateContractFixturePolicy(
            [new("gpt", "claude")], ["source"], false, new(1, TimeSpan.FromMinutes(1), 100, 1m)));
    }

    [Theory]
    [InlineData(PublicationMode.Plan)]
    [InlineData(PublicationMode.Implement)]
    public async Task Plan_and_implement_have_zero_external_mutations(PublicationMode mode)
    {
        var lane = Lane();
        await lane.Service.ExecuteAsync(lane.Run, new(mode, "forbidden", DryRun: false), TestContext.Current.CancellationToken);
        Assert.Equal(0, lane.Host.Mutations);
        Assert.Empty(lane.Journal.Receipts);
    }

    [Fact]
    public async Task Valid_dry_run_returns_the_intent_without_host_mutation_or_publication_receipt()
    {
        var lane = Lane();
        var result = await lane.Service.ExecuteAsync(lane.Run, new(PublicationMode.Pr, "dry-run"), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationOutcome.DryRun, result.Outcome);
        Assert.Equal(PublicationAction.CreateOrUpdatePr, result.Action);
        Assert.Equal(PublicationRealm.Fixture, result.Realm);
        Assert.Equal(0, lane.Host.Mutations);
        Assert.Empty(lane.Journal.Receipts);
    }

    [Theory]
    [InlineData(PublicationMode.Pr, PublicationAction.CreateOrUpdatePr)]
    [InlineData(PublicationMode.Merge, PublicationAction.MergeNormal)]
    public async Task Valid_parent_fixture_publishes_exact_action_and_v1_receipt(PublicationMode mode, PublicationAction action)
    {
        var lane = Lane();
        var result = await lane.Service.ExecuteAsync(lane.Run, new(mode, "valid-" + mode, false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationOutcome.PublishedFixture, result.Outcome);
        Assert.Equal(PublicationRealm.Fixture, result.Realm);
        Assert.Equal(new[] { action }, lane.Host.Actions);
        Assert.True(lane.Facts.Reads >= 2, "Local authority must be refreshed after remote readback and before mutation.");
        Assert.NotNull(result.Receipt);
        Assert.Equal("publication-receipt", result.Receipt.SchemaName);
        Assert.Single(lane.Journal.Receipts);
    }

    [Theory]
    [InlineData(AuthorizationCeiling.Plan)]
    [InlineData(AuthorizationCeiling.Implement)]
    [InlineData(AuthorizationCeiling.Pr)]
    public async Task Requested_merge_cannot_raise_an_existing_runs_ceiling(AuthorizationCeiling ceiling)
    {
        var lane = Lane(fixture.Definition(ceiling));
        var result = await lane.Service.ExecuteAsync(lane.Run, new(PublicationMode.Merge, "ceiling", false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationDenial.Ceiling, result.Denial);
        Assert.Equal(0, lane.Host.Mutations);
        Assert.Equal(ceiling, lane.Run.Definition.Ceiling);
    }

    [Fact]
    public async Task Later_explicit_authority_creates_new_reconciled_revision_without_mutating_prior_run()
    {
        var lane = Lane(fixture.Definition(AuthorizationCeiling.Implement));
        var revision = fixture.Authority.IssueReconciledRevision(lane.Run, fixture.Definition() with
        {
            RevisionIdentity = "revision-2",
            PriorRevisionIdentity = lane.Run.Definition.RevisionIdentity
        });
        Assert.Equal(AuthorizationCeiling.Implement, lane.Run.Definition.Ceiling);
        Assert.Equal(AuthorizationCeiling.Merge, revision.Definition.Ceiling);
        var result = await lane.Service.ExecuteAsync(revision, new(PublicationMode.Pr, "new-revision", false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationOutcome.PublishedFixture, result.Outcome);
    }

    [Fact]
    public async Task Another_parent_issuer_cannot_lend_a_run_token()
    {
        var lane = Lane();
        var other = PublicationAuthorityHost.CreateIsolatedFixture(fixture.ReviewHost);
        var borrowed = other.IssueRun(fixture.Definition());
        var result = await lane.Service.ExecuteAsync(borrowed, new(PublicationMode.Pr, "foreign", false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationDenial.Authority, result.Denial);
        Assert.Equal(0, lane.Host.Mutations);
    }

    [Theory]
    [InlineData("dirty", PublicationDenial.LocalIdentity)]
    [InlineData("head", PublicationDenial.LocalIdentity)]
    [InlineData("tree", PublicationDenial.LocalIdentity)]
    [InlineData("patch", PublicationDenial.LocalIdentity)]
    [InlineData("artifact", PublicationDenial.LocalIdentity)]
    [InlineData("ownership", PublicationDenial.Ownership)]
    [InlineData("expired", PublicationDenial.Ownership)]
    [InlineData("stopped", PublicationDenial.Stopped)]
    [InlineData("revision", PublicationDenial.Revision)]
    public async Task Stale_or_unowned_local_facts_block_before_any_host_mutation(string fault, PublicationDenial expected)
    {
        var lane = Lane();
        lane.Facts.Observe = (run, _) => CorruptLocal(run, fault);
        var result = await lane.Service.ExecuteAsync(lane.Run, new(PublicationMode.Pr, fault, false), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Denial);
        Assert.Equal(0, lane.Host.Mutations);
    }

    [Fact]
    public async Task Local_head_drift_after_first_readback_blocks_the_actual_send()
    {
        var lane = Lane();
        lane.Facts.Observe = (run, read) => CorruptLocal(run, read == 1 ? "valid" : "head");
        var result = await lane.Service.ExecuteAsync(lane.Run, new(PublicationMode.Pr, "late-local", false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationDenial.LocalIdentity, result.Denial);
        Assert.True(lane.Facts.Reads >= 2);
        Assert.Equal(0, lane.Host.Mutations);
    }

    [Theory]
    [InlineData("head", PublicationDenial.RemoteIdentity)]
    [InlineData("pr-head", PublicationDenial.RemoteIdentity)]
    [InlineData("checks", PublicationDenial.Checks)]
    [InlineData("protection", PublicationDenial.Protection)]
    public async Task Remote_drift_or_unproved_merge_protection_blocks_normal_merge(string fault, PublicationDenial expected)
    {
        var lane = Lane();
        lane.Host.Read = _ => fault switch
        {
            "head" => lane.Host.State with { SourceHead = PublicationFixture.Commit('9') },
            "pr-head" => lane.Host.State with { PrHead = PublicationFixture.Commit('9') },
            "checks" => lane.Host.State with { Checks = GitHostChecks.Pending },
            _ => lane.Host.State with { Protection = GitHostProtection.Unknown }
        };
        var result = await lane.Service.ExecuteAsync(lane.Run, new(PublicationMode.Merge, fault, false), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Denial);
        Assert.Equal(0, lane.Host.Mutations);
    }

    [Theory]
    [InlineData("missing-gate", PublicationDenial.PolicyGate)]
    [InlineData("incomplete-gate", PublicationDenial.PolicyGate)]
    [InlineData("review", PublicationDenial.Review)]
    [InlineData("hosted", PublicationDenial.Review)]
    public async Task Missing_or_incomplete_issued_evidence_never_publishes(string fault, PublicationDenial expected)
    {
        var definition = fixture.Definition();
        definition = fault switch
        {
            "missing-gate" => definition with { GateReceipts = [] },
            "incomplete-gate" => definition with { GateReceipts = [fixture.Gate("build", "INCOMPLETE"), fixture.Gate("test", "PASS")] },
            "review" => definition with { ReviewReceipt = null },
            _ => definition with { HostedReviewReceipt = null }
        };
        var lane = Lane(definition);
        var result = await lane.Service.ExecuteAsync(lane.Run, new(PublicationMode.Merge, fault, false), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Denial);
        Assert.Equal(0, lane.Host.Mutations);
    }

    [Fact]
    public async Task Lost_response_is_read_back_and_same_key_never_blindly_retries()
    {
        var lane = Lane();
        lane.Host.LoseResponse = true;
        var request = new PublicationRequest(PublicationMode.Pr, "lost-response", false);
        var first = await lane.Service.ExecuteAsync(lane.Run, request, TestContext.Current.CancellationToken);
        var replay = await lane.Service.ExecuteAsync(lane.Run, request, TestContext.Current.CancellationToken);
        Assert.Equal(PublicationOutcome.ReconciledFixture, first.Outcome);
        Assert.Equal(PublicationOutcome.ReconciledFixture, replay.Outcome);
        Assert.Equal(1, lane.Host.Mutations);
        Assert.True(lane.Host.Reads >= 3);
        Assert.Single(lane.Journal.Receipts);
        Assert.Contains(lane.Journal.Records, record => record.State == PublicationIntentState.Sent);
    }

    [Fact]
    public async Task Per_ref_serialization_spans_two_service_instances()
    {
        var lane = Lane();
        lane.Host.MutationDelay = TimeSpan.FromMilliseconds(50);
        var second = new PublicationService(fixture.Authority, lane.Facts, lane.Host, lane.Journal);
        await Task.WhenAll(
            lane.Service.ExecuteAsync(lane.Run, new(PublicationMode.Pr, "serialized-1", false), TestContext.Current.CancellationToken),
            second.ExecuteAsync(lane.Run, new(PublicationMode.Pr, "serialized-2", false), TestContext.Current.CancellationToken));
        Assert.Equal(2, lane.Host.Mutations);
        Assert.Equal(1, lane.Host.MaximumConcurrentMutations);
    }

    [Fact]
    public async Task Uncertain_action_with_absent_readback_never_automatically_resends()
    {
        var lane = Lane();
        var absent = lane.Host.State;
        lane.Host.LoseResponse = true;
        lane.Host.Read = _ => absent;
        var request = new PublicationRequest(PublicationMode.Pr, "unresolved", false);
        var first = await lane.Service.ExecuteAsync(lane.Run, request, TestContext.Current.CancellationToken);
        var retry = await lane.Service.ExecuteAsync(lane.Run, request, TestContext.Current.CancellationToken);
        Assert.Equal(PublicationDenial.Uncertain, first.Denial);
        Assert.Equal(PublicationDenial.Uncertain, retry.Denial);
        Assert.Equal(1, lane.Host.Mutations);
        Assert.Empty(lane.Journal.Receipts);
    }

    [Fact]
    public async Task Existing_key_for_another_revision_blocks_before_mutation()
    {
        var lane = Lane();
        await lane.Journal.AppendAsync(new("collision", fixture.Root.RunIdentity, "different-revision",
            PublicationAction.CreateOrUpdatePr, fixture.Target, fixture.Patch.BaseCommit,
            fixture.Patch.HeadCommit, fixture.Patch.HeadTree, PublicationIntentState.Sent), TestContext.Current.CancellationToken);
        var result = await lane.Service.ExecuteAsync(lane.Run, new(PublicationMode.Pr, "collision", false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationDenial.Idempotency, result.Denial);
        Assert.Equal(0, lane.Host.Mutations);
    }

    [Fact]
    public async Task Send_intent_is_recorded_before_the_external_action()
    {
        var lane = Lane();
        var observed = false;
        lane.Host.BeforeMutation = mutation =>
        {
            Assert.Contains(lane.Journal.Records, record => record.IdempotencyKey == mutation.Intent.IdempotencyKey &&
                record.State == PublicationIntentState.Sent && record.HeadCommit == fixture.Patch.HeadCommit);
            observed = true;
        };
        var result = await lane.Service.ExecuteAsync(lane.Run, new(PublicationMode.Pr, "send-barrier", false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationOutcome.PublishedFixture, result.Outcome);
        Assert.True(observed);
    }

    [Fact]
    public async Task Review_revocation_at_final_local_refresh_blocks_the_send()
    {
        var scenario = new PublicationFixture();
        var facts = new FixtureFacts(scenario);
        facts.Observe = (run, read) =>
        {
            if (read > 1) scenario.ReviewHost.RevokeCapability("claude-proof");
            return new(scenario.Patch, true, PublicationOwnership.ReconciledFixture, false, run.Definition.RevisionIdentity);
        };
        var host = new FixtureGitHost(scenario);
        var service = new PublicationService(scenario.Authority, facts, host, new FixtureJournal());
        var result = await service.ExecuteAsync(scenario.Authority.IssueRun(scenario.Definition()),
            new(PublicationMode.Pr, "late-revocation", false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationDenial.Review, result.Denial);
        Assert.True(facts.Reads >= 2);
        Assert.Equal(0, host.Mutations);
    }

    [Fact]
    public void Valid_preflight_positive_control_accepts_exact_issued_fixture_facts()
    {
        var lane = Lane();
        var result = PublicationPreflight.Evaluate(fixture.Authority, lane.Run, new(PublicationMode.Pr, "preflight", false),
            CorruptLocal(lane.Run, "valid"), lane.Host.State);
        Assert.True(result.Allowed, result.Reason);
    }

    [Fact]
    public void Issued_ceiling_and_nested_patch_inputs_are_frozen()
    {
        var artifacts = new Dictionary<string, string> { ["tests"] = PublicationFixture.Id('1') };
        var input = fixture.Definition() with { Patch = fixture.Patch with { ArtifactIdentities = artifacts } };
        var run = fixture.Authority.IssueRun(input);
        artifacts["tests"] = PublicationFixture.Id('9');
        Assert.Equal(PublicationFixture.Id('1'), run.Definition.Patch.ArtifactIdentities["tests"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)run.Definition.Patch.ArtifactIdentities)["tests"] = "changed");
    }

    [Fact]
    public void Production_publication_is_explicitly_unavailable()
    {
        Assert.Throws<PublicationUnavailableException>(() => PublicationAuthorityHost.CreateProduction());
    }

    private PublicationLocalObservation CorruptLocal(PublicationRun run, string fault)
    {
        var patch = fixture.Patch;
        patch = fault switch
        {
            "head" => patch with { HeadCommit = PublicationFixture.Commit('9') },
            "tree" => patch with { HeadTree = PublicationFixture.Commit('9') },
            "patch" => patch with { PatchSha256 = PublicationFixture.Id('9') },
            "artifact" => patch with { ArtifactIdentities = new Dictionary<string, string> { ["tests"] = PublicationFixture.Id('9') } },
            _ => patch
        };
        return new(patch, fault != "dirty", fault switch
        {
            "ownership" => PublicationOwnership.Uncertain,
            "expired" => PublicationOwnership.Expired,
            _ => PublicationOwnership.ReconciledFixture
        }, fault == "stopped", fault == "revision" ? "stale-revision" : run.Definition.RevisionIdentity);
    }

    private (PublicationService Service, PublicationRun Run, FixtureFacts Facts, FixtureGitHost Host, FixtureJournal Journal) Lane(
        PublicationRunDefinition? definition = null)
    {
        var facts = new FixtureFacts(fixture);
        var host = new FixtureGitHost(fixture);
        var journal = new FixtureJournal();
        var run = fixture.Authority.IssueRun(definition ?? fixture.Definition());
        return (new PublicationService(fixture.Authority, facts, host, journal), run, facts, host, journal);
    }
}
