using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Execution.Host;
using DeliveryForge.Review;
using System.Diagnostics;
using System.Security.Cryptography;

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
    internal static async Task<ProtectedReviewEdgesSnapshot> RunAsync(ProtectedReviewEdgesSetup setup, string scenario,
        long originalStarted, CancellationToken token)
    {
        TimeSpan Remaining()
        {
            token.ThrowIfCancellationRequested();
            var left = TimeSpan.FromSeconds(120) - Stopwatch.GetElapsedTime(originalStarted);
            if (left <= TimeSpan.Zero) throw new TimeoutException("Original all-edge fixture lifetime expired.");
            return left < TimeSpan.FromSeconds(15) ? left : TimeSpan.FromSeconds(15);
        }
        if (scenario is not ("all-five-positive" or "hosted-revoked" or "independent-revoked" or "hosted-reuses-independent" or
            "foreign-protected-host" or "same-host-distinct-pair" or "real-head-drift" or "original-pr-ceiling"))
            throw new ArgumentException("Unknown all-five review edge scenario.", nameof(scenario));
        _ = Remaining();
        var port = new ReviewFixturePort();
        var artifacts = new List<(string Path, string Sha256)>();
        var ceiling = scenario == "original-pr-ceiling" ? AuthorizationCeiling.Pr : AuthorizationCeiling.Merge;
        var original = await Pair(setup.Bootstrap, setup.Patch, setup.IndependentContext, setup.HostedContext, "primary");
        var originalEvidence = Evidence(original);
        var before = Observe(setup.Bootstrap.Host, setup.PinnedReceiver, originalEvidence);
        var changed = originalEvidence;
        string? currentHead = null;
        if (scenario == "hosted-revoked") setup.Bootstrap.Host.RevokeCapability(original.Hosted.Capability.ProofIdentity);
        if (scenario == "independent-revoked") setup.Bootstrap.Host.RevokeCapability(original.Independent.Capability.ProofIdentity);
        if (scenario == "hosted-reuses-independent") changed = changed with { HostReviewReceipt = original.Independent.Receipt };
        if (scenario == "real-head-drift")
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(token); bound.CancelAfter(Remaining());
            await setup.Run.GitAsync(bound.Token, "-c", "user.name=ReviewFixture", "-c", "user.email=fixture@example.invalid",
                "commit", "--allow-empty", "-q", "-m", "actual post-review head drift");
            currentHead = await setup.Run.GitAsync(bound.Token, "rev-parse", "HEAD");
            var tree = await setup.Run.GitAsync(bound.Token, "rev-parse", "HEAD^{tree}");
            changed = changed with { HeadCommit = currentHead, TreeId = tree };
        }
        var after = Observe(setup.Bootstrap.Host, setup.PinnedReceiver, changed);
        IReadOnlyList<ReviewEdgeObservation> secondaryOwn = [], secondaryAtOriginal = [], originalStillValid = [];
        ProtectedReviewFixtureBootstrap? other = null;
        try
        {
            if (scenario is "foreign-protected-host" or "same-host-distinct-pair")
            {
                var issuer = setup.Bootstrap;
                var patch = setup.Patch;
                if (scenario == "foreign-protected-host")
                {
                    var input = setup.Source.Configuration;
                    other = await ReviewAuthorityHost.CreateProtectedGitContractFixtureAsync(new(input.RunIdentity, input.FrozenPlanContract,
                        input.EvidencePolicyContract, input.SourceRoot, input.WorkerGitDirectory, input.TrustedGitExecutable,
                        input.RelativePolicyPath, input.MaximumEvidenceBytes, Remaining()), token);
                    issuer = other;
                    using var bound = CancellationTokenSource.CreateLinkedTokenSource(token); bound.CancelAfter(Remaining());
                    patch = await GitPatchIdentity.ComputeAsync(issuer.ObjectView, setup.Source.BaseCommit, setup.Source.HeadCommit,
                        setup.Source.Plan.ContractIdentity, setup.Patch.ArtifactIdentities, bound.Token);
                    if (patch.PatchSha256 != setup.Patch.PatchSha256 || patch.HeadTree != setup.Patch.HeadTree)
                        throw new InvalidOperationException("Foreign host's actual patch facts differ from the intact original.");
                }
                var independent = Context(issuer.RootContext, "secondary-independent");
                var hosted = Context(issuer.RootContext, "secondary-hosted");
                var receiver = issuer.Host.CreateTransitionReceiver(independent, hosted); // Pin BOTH expected references before secondary raw artifacts.
                var secondary = await Pair(issuer, patch, independent, hosted, "secondary");
                var evidence = Evidence(secondary);
                secondaryOwn = Observe(issuer.Host, receiver, evidence);
                secondaryAtOriginal = Observe(setup.Bootstrap.Host, setup.PinnedReceiver, evidence);
                originalStillValid = Observe(setup.Bootstrap.Host, setup.PinnedReceiver, originalEvidence);
            }
            var immutable = new List<ReviewArtifactObservation>();
            foreach (var artifact in artifacts)
                immutable.Add(new(artifact.Path, artifact.Sha256, Hash(await File.ReadAllBytesAsync(artifact.Path, token))));
            await setup.Run.WriteNewJsonAsync("review-all-edges-fixture-ledger.json", new
            { fixture = true, publicationCalls = 0, modelCalls = 0, reviewerFixtureCalls = port.Calls }, token);
            _ = Remaining();
            await setup.Run.WriteNewJsonAsync("review-all-edges-current-observation.json", new
            {
                fixture = true, productionAccepted = false, firstFullRealRunCertified = false, scenario, seed = 11019,
                setup.Patch, rootBinding = setup.Bootstrap.RootContext,
                independentExpected = setup.IndependentContext.RequestIdentity, hostedExpected = setup.HostedContext.RequestIdentity,
                before, after, secondaryOwn, secondaryAtOriginal, originalStillValid, immutable, currentHead,
                publicationCalls = 0, modelCalls = 0, reviewerFixtureCalls = port.Calls.Count,
                publicationAndCiPrerequisitesAreFixtureInputsOnly = true, publicationOwnerExecuted = false,
                limitation = "Five public protected review acceptance lookups only; actual #10 bridge/golden and genuine model/production profiles remain separate."
            }, token);
            _ = Remaining();
            return new(before, after, secondaryOwn, secondaryAtOriginal, originalStillValid, immutable.AsReadOnly(), port.Calls.Count, 0, 0,
                Path.Combine(setup.Run.Root, "review-all-edges-current-observation.json"), Path.Combine(setup.Run.Root, "review-all-edges-fixture-ledger.json"), currentHead);
        }
        finally { other?.Dispose(); } // Owned fixture object-view lifetime, not physical command closure proof.

        async Task<ReviewPair> Pair(ProtectedReviewFixtureBootstrap issuer, PatchIdentity patch,
            ReviewAcceptanceContext independent, ReviewAcceptanceContext hosted, string name)
        {
            var first = await Issue(issuer, patch, independent, "claude", "source", name + "-independent", ReviewEvidenceRole.Independent);
            var second = await Issue(issuer, patch, hosted, "github-hosted", "pull-request", name + "-hosted", ReviewEvidenceRole.Hosted);
            return new(first, second);
        }
        async Task<IssuedRole> Issue(ProtectedReviewFixtureBootstrap issuer, PatchIdentity patch, ReviewAcceptanceContext context,
            string family, string scope, string name, ReviewEvidenceRole role)
        {
            _ = Remaining();
            var data = new ReviewerCapability("explicit-external-review-fixture", "1.0.0", family, true, true, false, [scope]);
            var request = new ReviewRequest(patch, "gpt", data, scope, issuer.Policy.PolicyIdentity);
            var registration = issuer.Host.RegisterRequest(context.RequestIdentity, request, context);
            var now = DateTimeOffset.UtcNow;
            var capability = issuer.Host.IssueCapability(data, "all-edge-capability-" + Guid.NewGuid().ToString("N"), now.AddSeconds(-1), now.AddSeconds(120));
            var raw = await port.ExecuteAsync(registration, Path.Combine(setup.Run.Root, "all-edge-" + name + "-raw.json"), token);
            artifacts.Add((raw.RawPath, raw.RawSha256));
            var execution = issuer.Host.RecordExecution(registration, capability, "all-edge-execution-" + Guid.NewGuid().ToString("N"),
                true, true, raw.RawSha256, raw.Verdict, raw.CompletedAt);
            var mapped = raw.Verdict.Trim().ToLowerInvariant() switch { "ship" => "PASS", "needs-attention" or "do-not-ship" => "FAIL", _ => "ERROR" };
            var normalized = ReviewNormalizer.Normalize(request, ReviewResult.Complete(patch, family, mapped, [], raw.RawSha256, raw.CompletedAt),
                issuer.Policy, capability, execution);
            if (normalized.Outcome != ReviewOutcome.Pass || !normalized.AuthorityAccepted || !normalized.Fixture || normalized.ProductionAccepted)
                throw new InvalidOperationException("Actual public role issuance refused or promoted fixture evidence: " + normalized.Reason);
            var bytes = ReviewReceiptSerializer.Serialize(ReviewReceiptSerializer.Create(normalized));
            var receipt = ContractValidator.ParseAndValidate(bytes);
            var path = await new AppendOnlyContractStore(Path.Combine(setup.Run.Root, "all-edge-review-contracts")).WriteImmutableAsync(bytes, token);
            artifacts.Add((path, Hash(await File.ReadAllBytesAsync(path, token))));
            issuer.Host.IssueAcceptance(receipt, normalized, context, role);
            _ = Remaining(); return new(receipt, capability);
        }
        TransitionEvidence Evidence(ReviewPair pair) => new(ceiling, setup.Patch.BaseCommit, setup.Patch.HeadCommit, setup.Patch.HeadTree,
            setup.Source.EvidencePolicy, CiReceipt: setup.CiInput, ReviewReceipt: pair.Independent.Receipt,
            HostReviewReceipt: pair.Hosted.Receipt);
        IReadOnlyList<ReviewEdgeObservation> Observe(ReviewAuthorityHost host, ReviewTransitionReceiver receiver, TransitionEvidence evidence)
        {
            (WorkflowState From, WorkflowState To)[] edges = [(WorkflowState.IndependentReview, WorkflowState.PrAuthorized),
                (WorkflowState.PrAuthorized, WorkflowState.PrPublished), (WorkflowState.CiComplete, WorkflowState.HostReviewComplete),
                (WorkflowState.HostReviewComplete, WorkflowState.MergeAuthorized), (WorkflowState.MergeAuthorized, WorkflowState.Merged)];
            var results = new List<ReviewEdgeObservation>();
            foreach (var edge in edges)
            {
                _ = Remaining();
                var input = evidence with { PublicationReceipt = edge.To == WorkflowState.Merged ? setup.MergeInput : setup.PublicationInput };
                var allowed = true; var reason = "Actual pinned fixture authority lookup allowed.";
                try { host.EnsureContractFixtureTransition(edge.From, edge.To, input, receiver); }
                catch (InvalidWorkflowTransitionException refusal) { allowed = false; reason = refusal.Message; }
                var productionDenied = false;
                try { WorkflowTransition.EnsureAllowed(edge.From, edge.To, input, receiver); }
                catch (InvalidWorkflowTransitionException) { productionDenied = true; }
                results.Add(new(edge.From + "->" + edge.To, allowed, productionDenied, reason));
            }
            return results.AsReadOnly();
        }
    }

    private sealed record IssuedRole(ValidatedContract Receipt, IssuedReviewerCapability Capability);
    private sealed record ReviewPair(IssuedRole Independent, IssuedRole Hosted);
    private static ReviewAcceptanceContext Context(ReviewAuthorityRootContext root, string role) => new(root.RunIdentity,
        role + "-request-" + Guid.NewGuid().ToString("N"), root.PlanIdentity, root.EvidencePolicyIdentity, root.ReviewPolicyContentIdentity,
        root.ReviewPolicySourceRevision, root.ReviewPolicyAuthorityKind, root.SourceAuthorityIdentity, root.SourceRoot, root.ProtectedBaseCommit, root.ProtectedObjectViewIdentity);
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
