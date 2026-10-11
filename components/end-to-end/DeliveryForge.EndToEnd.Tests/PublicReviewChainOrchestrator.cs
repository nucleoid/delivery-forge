using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Review;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using System.Diagnostics;
using System.Security.Cryptography;

namespace DeliveryForge.EndToEnd.Tests;

public sealed record PublicReviewSetup(OwnedScenarioRun Artifacts, string BaseCommit, string HeadCommit, string HeadTree,
    FrozenPlan Plan, string EvidencePolicyIdentity, string GitExecutable, PatchIdentity ActualPatch, string RawIndexSha256);
public sealed record PublicReviewChainSnapshot(string ObservationPath, string? RawReviewerPath, string? CanonicalReceiptPath);

internal static class PublicReviewChainOrchestrator
{
    internal static async Task<PublicReviewChainSnapshot> RunAsync(PublicReviewSetup setup, string scenario, long originalStarted, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (scenario is not ("marked-positive" or "strict-same-family" or "tracked-drift" or "head-drift" or "raw-index-drift" or "revoked-capability" or "foreign-host" or "distinct-request"))
            throw new ArgumentException("Undefined initial public review-chain scenario.", nameof(scenario));
        var artifacts = setup.Artifacts;
        async Task<string> WriteObservation(string name, object value)
        {
            await artifacts.WriteNewJsonAsync(name, value, token);
            return Path.Combine(artifacts.Root, name);
        }
        TimeSpan Remaining()
        {
            token.ThrowIfCancellationRequested();
            var remaining = TimeSpan.FromSeconds(120) - Stopwatch.GetElapsedTime(originalStarted);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Original public review-chain budget expired.");
            return remaining < TimeSpan.FromSeconds(15) ? remaining : TimeSpan.FromSeconds(15);
        }
        var port = new ReviewFixturePort();
        var policyPath = Path.Combine(artifacts.Root, "fixture-evidence-policy-inputs", "sha256-" + setup.EvidencePolicyIdentity[7..] + ".json");
        var evidencePolicy = ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(policyPath, token));
        if (evidencePolicy.Identity != setup.EvidencePolicyIdentity) throw new InvalidOperationException("Immutable evidence policy input changed.");
        // Descriptive pure-fixture policy binding; not a protected-source resolver certificate.
        var rootBinding = new ReviewAuthorityRootContext(artifacts.RunId, setup.Plan.ContractIdentity, setup.EvidencePolicyIdentity,
            "sha256:" + new string('f', 64), new string('f', 40), ReviewPolicyAuthorityKind.Caller,
            "fixture-owned-source:" + artifacts.RunId, artifacts.Checkout, setup.BaseCommit, "fixture-contract-view:" + artifacts.RunId);
        var host = ReviewAuthorityHost.CreateIsolatedTest(rootBinding);
        ReviewAcceptanceContext Context(string requestIdentity) => new(rootBinding.RunIdentity, requestIdentity,
            rootBinding.PlanIdentity, rootBinding.EvidencePolicyIdentity, rootBinding.ReviewPolicyContentIdentity,
            rootBinding.ReviewPolicySourceRevision, rootBinding.ReviewPolicyAuthorityKind, rootBinding.SourceAuthorityIdentity,
            rootBinding.SourceRoot, rootBinding.ProtectedBaseCommit, rootBinding.ProtectedObjectViewIdentity);
        var primaryContext = Context("request-primary-" + Guid.NewGuid().ToString("N"));
        var primaryReceiver = host.CreateTransitionReceiver(primaryContext); // Pin before any per-call receipt exists.
        var preparation = await host.PrepareIsolatedTestGitEvidenceAsync(artifacts.Checkout, Path.Combine(artifacts.Checkout, ".git"),
            setup.GitExecutable, 1024 * 1024, Remaining(), token);
        if (preparation.Observation.Outcome != ReviewGitPreparationOutcome.Prepared || preparation.Context is null)
            throw new InvalidOperationException("Actual Git owner preparation failed: " + preparation.Observation.Reason);
        using var controlled = preparation.Context;
        var sourcePath = Path.Combine(artifacts.Checkout, "subject.txt");
        var sourceBefore = Hash(await File.ReadAllBytesAsync(sourcePath, token));
        async Task<string> Git(params string[] arguments)
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(token); bound.CancelAfter(Remaining());
            return await artifacts.GitAsync(bound.Token, arguments);
        }
        async Task<string> GitObservation(string name)
        {
            var head = await Git("rev-parse", "HEAD"); var tree = await Git("rev-parse", "HEAD^{tree}");
            var index = Hash(await File.ReadAllBytesAsync(Path.Combine(artifacts.Checkout, ".git", "index"), token));
            return await WriteObservation(name, new
            { fixture = true, head, tree, rawIndexSha256 = index, sourceSha256 = Hash(await File.ReadAllBytesAsync(sourcePath, token)) });
        }
        var originalGitPath = await GitObservation("review-original-git-observation.json");
        if (scenario is "tracked-drift" or "head-drift" or "raw-index-drift")
        {
            // Mutate only the owned real repository after frozen preparation and before reviewer preflight.
            if (scenario == "tracked-drift") await File.WriteAllTextAsync(sourcePath, "real unstaged drift after preparation\n", token);
            if (scenario == "head-drift") await Git("-c", "user.name=ReviewFixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-q", "-m", "real later HEAD");
            if (scenario == "raw-index-drift") await Git("update-index", "--assume-unchanged", "subject.txt");
            var changedGitPath = await GitObservation("review-drifted-git-observation.json");
            ReviewPreflightException? refused = null;
            try
            {
                using var bound = CancellationTokenSource.CreateLinkedTokenSource(token); bound.CancelAfter(Remaining());
                _ = await GitPatchIdentity.ComputeAsync(controlled, setup.BaseCommit, setup.HeadCommit, setup.Plan.ContractIdentity,
                    setup.ActualPatch.ArtifactIdentities, bound.Token);
            }
            catch (ReviewPreflightException exception) { refused = exception; }
            if (refused is null) throw new InvalidOperationException("Actual source/index/HEAD drift did not refuse preflight.");
            var changedHead = await Git("rev-parse", "HEAD");
            var currentIndex = Hash(await File.ReadAllBytesAsync(Path.Combine(artifacts.Checkout, ".git", "index"), token));
            var observation = Common();
            observation["actualPreflightException"] = true; observation["exceptionType"] = refused.GetType().Name;
            observation["exceptionReason"] = refused.Message; observation["reviewerPortCalls"] = port.Calls.Count;
            observation["originalRawIndexSha256"] = setup.RawIndexSha256; observation["currentRawIndexSha256"] = currentIndex;
            observation["sourceBeforeSha256"] = sourceBefore; observation["sourceAfterSha256"] = Hash(await File.ReadAllBytesAsync(sourcePath, token));
            observation["actualHeadAfterDrift"] = changedHead; observation["originalGitObservationPath"] = originalGitPath;
            observation["driftedGitObservationPath"] = changedGitPath; observation["acceptanceIssued"] = false;
            observation["transitionAllowed"] = false;
            // A direct production call with no receipt cannot authorize this fixture/preflight refusal.
            observation["productionTransitionDenied"] = Denied(() => WorkflowTransition.EnsureAllowed(
                WorkflowState.IndependentReview, WorkflowState.PrAuthorized, new(AuthorizationCeiling.Pr,
                    setup.BaseCommit, setup.HeadCommit, setup.HeadTree, evidencePolicy), primaryReceiver));
            _ = Remaining();
            var path = await WriteObservation("public-review-chain-observation.json", observation);
            return new(path, null, null);
        }
        PatchIdentity actualPatch;
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            bound.CancelAfter(Remaining());
            actualPatch = await GitPatchIdentity.ComputeAsync(controlled, setup.BaseCommit, setup.HeadCommit,
                setup.Plan.ContractIdentity, setup.ActualPatch.ArtifactIdentities, bound.Token);
        }
        if (actualPatch.PatchSha256 != setup.ActualPatch.PatchSha256 || actualPatch.HeadTree != setup.HeadTree)
            throw new InvalidOperationException("Current real patch no longer matches the setup snapshot.");
        var original = await Chain(host, primaryContext, primaryReceiver, "primary", scenario == "strict-same-family" ? "gpt" : "claude", actualPatch);
        var resultObservation = Common();
        resultObservation["fullRegistrationIssuerExecutionChain"] = true; resultObservation["externalReviewerExplicitlyFixture"] = true;
        resultObservation["normalizerOutcome"] = original.Normalized.Outcome.ToString(); resultObservation["authorFamily"] = original.Normalized.AuthorFamily;
        resultObservation["normalizerReason"] = original.Normalized.Reason;
        resultObservation["reviewerFamily"] = original.Normalized.ReviewerFamily; resultObservation["rawArtifactSha256"] = original.Raw.RawSha256;
        resultObservation["authorityAccepted"] = original.Normalized.AuthorityAccepted; resultObservation["acceptanceIssued"] = original.AcceptanceIssued;
        resultObservation["transitionAllowed"] = original.Allowed; resultObservation["originalPinnedReceiverAllowedBeforeNegative"] = original.Allowed;
        resultObservation["originalRequestIdentity"] = primaryContext.RequestIdentity;
        resultObservation["receiptIdentity"] = original.Receipt.Identity;
        resultObservation["canonicalReceiptSha256"] = Hash(await File.ReadAllBytesAsync(original.ReceiptPath, token));
        resultObservation["productionTransitionDenied"] = Denied(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.IndependentReview, WorkflowState.PrAuthorized, original.Evidence, primaryReceiver));
        if (scenario == "revoked-capability")
        {
            resultObservation["receiptSha256BeforeRevocation"] = Hash(await File.ReadAllBytesAsync(original.ReceiptPath, token));
            resultObservation["rawSha256BeforeRevocation"] = Hash(await File.ReadAllBytesAsync(original.Raw.RawPath, token));
            host.RevokeCapability(original.Capability.ProofIdentity);
            resultObservation["capabilityRevoked"] = true;
            resultObservation["transitionAllowedAfterRevocation"] = Allowed(host, primaryReceiver, original.Evidence);
        }
        if (scenario is "foreign-host" or "distinct-request")
        {
            var secondaryHost = scenario == "foreign-host" ? ReviewAuthorityHost.CreateIsolatedTest(rootBinding) : host;
            var secondaryContext = Context("request-secondary-" + Guid.NewGuid().ToString("N"));
            var secondaryReceiver = secondaryHost.CreateTransitionReceiver(secondaryContext);
            var secondary = await Chain(secondaryHost, secondaryContext, secondaryReceiver, "secondary", "claude", actualPatch);
            resultObservation["secondaryRequestIdentity"] = secondaryContext.RequestIdentity;
            resultObservation["secondaryChainIntactAndOwnReceiverAllowed"] = secondary.AcceptanceIssued && secondary.Allowed;
            resultObservation["secondaryAcceptanceAllowedByOriginalReceiver"] = Allowed(host, primaryReceiver, secondary.Evidence);
            resultObservation["originalReceiverStillAllowsOriginal"] = Allowed(host, primaryReceiver, original.Evidence);
            resultObservation["secondaryReceiptPath"] = secondary.ReceiptPath; resultObservation["secondaryRawPath"] = secondary.Raw.RawPath;
        }
        var ledgerPath = await WriteObservation("public-review-fixture-ledger.json", new
        { fixture = true, modelCalls = 0, publicationCalls = 0, calls = port.Calls });
        resultObservation["reviewerPortCalls"] = port.Calls.Count; resultObservation["reviewerLedgerPath"] = ledgerPath;
        _ = Remaining();
        var observationPath = await WriteObservation("public-review-chain-observation.json", resultObservation);
        return new(observationPath, original.Raw.RawPath, original.ReceiptPath);

        Dictionary<string, object?> Common() => new()
        {
            ["scenario"] = scenario, ["fixture"] = true, ["productionAccepted"] = false, ["firstFullRealRunCertified"] = false,
            ["integratedBaseCommit"] = OwnedScenarioRun.InitialIntegratedBase, ["implementationHead"] = artifacts.IntegratedSource.HeadCommit,
            ["baseCommit"] = setup.BaseCommit, ["headCommit"] = setup.HeadCommit, ["headTree"] = setup.HeadTree,
            ["patchSha256"] = setup.ActualPatch.PatchSha256, ["gitVersion"] = setup.ActualPatch.GitVersion,
            ["policyComposition"] = "pure-contract-fixture-policy", ["protectedPolicyCompositionStillRequired"] = true,
            ["publicationCalls"] = 0, ["modelCalls"] = 0, ["seed"] = 11019,
            ["rootBinding"] = rootBinding, ["initialPlanCeiling"] = "pr", ["initialEvidencePolicyCeiling"] = "pr"
        };
        async Task<ChainResult> Chain(ReviewAuthorityHost issuer, ReviewAcceptanceContext context, ReviewTransitionReceiver receiver,
            string name, string family, PatchIdentity patch)
        {
            _ = Remaining();
            var policy = issuer.CreateContractFixturePolicy([new("gpt", "claude")], ["source"], false,
                new ReviewBudget(2, TimeSpan.FromSeconds(30), 4096, 1m));
            var capabilityData = new ReviewerCapability("explicit-external-review-fixture", "1.0.0", family, true, true, false, ["source"]);
            var request = new ReviewRequest(patch, "gpt", capabilityData, "source", policy.PolicyIdentity);
            var registration = issuer.RegisterRequest(context.RequestIdentity, request, context);
            var now = DateTimeOffset.UtcNow;
            var capability = issuer.IssueCapability(capabilityData, "fixture-capability-" + Guid.NewGuid().ToString("N"), now.AddSeconds(-1), now.AddSeconds(120));
            var raw = await port.ExecuteAsync(registration, Path.Combine(artifacts.Root, "review-" + name + "-raw.json"), token);
            var execution = issuer.RecordExecution(registration, capability, "fixture-execution-" + Guid.NewGuid().ToString("N"),
                true, true, raw.RawSha256, raw.Verdict, raw.CompletedAt);
            // The external raw vocabulary remains verbatim in its artifact/execution. The public
            // normalizer expects a mapped result verdict and independently checks this correspondence.
            var mappedVerdict = raw.Verdict.Trim().ToLowerInvariant() switch
            { "ship" => "PASS", "needs-attention" or "do-not-ship" => "FAIL", _ => "ERROR" };
            var normalized = ReviewNormalizer.Normalize(request, ReviewResult.Complete(patch, family, mappedVerdict, [], raw.RawSha256, raw.CompletedAt), policy, capability, execution);
            if (!normalized.Fixture || normalized.ProductionAccepted) throw new InvalidOperationException("External fixture review promoted to production.");
            var receiptBytes = ReviewReceiptSerializer.Serialize(ReviewReceiptSerializer.Create(normalized));
            var receipt = ContractValidator.ParseAndValidate(receiptBytes);
            var path = await new AppendOnlyContractStore(Path.Combine(artifacts.Root, "review-canonical-contracts")).WriteImmutableAsync(receiptBytes, token);
            var accepted = false;
            try { issuer.IssueAcceptance(receipt, normalized, context, ReviewEvidenceRole.Independent); accepted = true; }
            catch (InvalidOperationException) when (normalized.Outcome != ReviewOutcome.Pass) { }
            var evidence = new TransitionEvidence(AuthorizationCeiling.Pr, setup.BaseCommit, setup.HeadCommit, setup.HeadTree,
                evidencePolicy, ReviewReceipt: receipt);
            return new(raw, normalized, receipt, path, capability, evidence, accepted, Allowed(issuer, receiver, evidence));
        }
    }

    private sealed record ChainResult(ReviewFixtureObservation Raw, NormalizedReview Normalized, ValidatedContract Receipt,
        string ReceiptPath, IssuedReviewerCapability Capability, TransitionEvidence Evidence, bool AcceptanceIssued, bool Allowed);
    private static bool Allowed(ReviewAuthorityHost host, ReviewTransitionReceiver receiver, TransitionEvidence evidence)
    {
        try { host.EnsureContractFixtureTransition(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, evidence, receiver); return true; }
        catch (InvalidWorkflowTransitionException) { return false; }
    }
    private static bool Denied(Action action)
    {
        try { action(); return false; } catch (InvalidWorkflowTransitionException) { return true; }
    }
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
