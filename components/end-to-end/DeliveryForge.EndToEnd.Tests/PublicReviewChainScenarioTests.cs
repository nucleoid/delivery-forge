using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Review;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class PublicReviewChainScenarioTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("marked-positive")]
    [InlineData("strict-same-family")]
    [InlineData("tracked-drift")]
    [InlineData("head-drift")]
    [InlineData("raw-index-drift")]
    [InlineData("revoked-capability")]
    [InlineData("foreign-host")]
    [InlineData("distinct-request")]
    public async Task Full_public_fixture_review_chain_requires_current_issuer_git_and_pinned_receiver(string scenario)
    {
        var started = Stopwatch.GetTimestamp();
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wall.CancelAfter(TimeSpan.FromSeconds(120));
        var token = wall.Token;
        await using var artifacts = await OwnedScenarioRun.CreateAsync(token);
        artifacts.RetainArtifacts("Public review-chain actual Git/raw receipt/refusal evidence retained; no cleanup authority from data.");
        var setup = await Setup(artifacts, started, token);
        var result = await PublicReviewChainOrchestrator.RunAsync(setup, scenario, started, token);
        using var observed = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ObservationPath, token));
        var root = observed.RootElement;
        Assert.Equal(scenario, root.GetProperty("scenario").GetString());
        Assert.True(root.GetProperty("fixture").GetBoolean());
        Assert.False(root.GetProperty("productionAccepted").GetBoolean());
        Assert.False(root.GetProperty("firstFullRealRunCertified").GetBoolean());
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, root.GetProperty("integratedBaseCommit").GetString());
        Assert.Equal(artifacts.IntegratedSource.HeadCommit, root.GetProperty("implementationHead").GetString());
        Assert.Equal(setup.BaseCommit, root.GetProperty("baseCommit").GetString());
        Assert.Equal(setup.HeadCommit, root.GetProperty("headCommit").GetString());
        Assert.Equal(setup.HeadTree, root.GetProperty("headTree").GetString());
        Assert.Equal(setup.ActualPatch.PatchSha256, root.GetProperty("patchSha256").GetString());
        Assert.Equal(setup.ActualPatch.GitVersion, root.GetProperty("gitVersion").GetString());
        Assert.Equal("pure-contract-fixture-policy", root.GetProperty("policyComposition").GetString());
        Assert.True(root.GetProperty("protectedPolicyCompositionStillRequired").GetBoolean());
        Assert.Equal(0, root.GetProperty("publicationCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("modelCalls").GetInt32());
        Assert.True(root.GetProperty("productionTransitionDenied").GetBoolean());
        if (scenario is "tracked-drift" or "head-drift" or "raw-index-drift")
        {
            Assert.True(root.GetProperty("actualPreflightException").GetBoolean());
            Assert.Equal(0, root.GetProperty("reviewerPortCalls").GetInt32());
            Assert.Equal("ReviewPreflightException", root.GetProperty("exceptionType").GetString());
            Assert.Equal(setup.RawIndexSha256, root.GetProperty("originalRawIndexSha256").GetString());
            Assert.True(File.Exists(root.GetProperty("originalGitObservationPath").GetString()));
            Assert.True(File.Exists(root.GetProperty("driftedGitObservationPath").GetString()));
            Assert.False(root.GetProperty("acceptanceIssued").GetBoolean());
            Assert.False(root.GetProperty("transitionAllowed").GetBoolean());
            if (scenario == "tracked-drift") Assert.NotEqual(root.GetProperty("sourceBeforeSha256").GetString(), root.GetProperty("sourceAfterSha256").GetString());
            if (scenario == "head-drift") Assert.NotEqual(setup.HeadCommit, root.GetProperty("actualHeadAfterDrift").GetString());
            if (scenario == "raw-index-drift") Assert.NotEqual(setup.RawIndexSha256, root.GetProperty("currentRawIndexSha256").GetString());
        }
        else
        {
            Assert.True(root.GetProperty("fullRegistrationIssuerExecutionChain").GetBoolean());
            Assert.True(root.GetProperty("externalReviewerExplicitlyFixture").GetBoolean());
            Assert.True(root.GetProperty("reviewerPortCalls").GetInt32() >= 1);
            Assert.True(File.Exists(root.GetProperty("reviewerLedgerPath").GetString()));
            Assert.NotNull(result.RawReviewerPath);
            Assert.Equal(root.GetProperty("rawArtifactSha256").GetString(), Hash(await File.ReadAllBytesAsync(result.RawReviewerPath, token)));
            if (scenario == "strict-same-family")
            {
                Assert.Equal("gpt", root.GetProperty("authorFamily").GetString());
                Assert.Equal("gpt", root.GetProperty("reviewerFamily").GetString());
                Assert.Equal("Incomplete", root.GetProperty("normalizerOutcome").GetString());
                Assert.False(root.GetProperty("acceptanceIssued").GetBoolean());
                Assert.False(root.GetProperty("transitionAllowed").GetBoolean());
            }
            else
            {
                Assert.Equal("Pass", root.GetProperty("normalizerOutcome").GetString());
                Assert.True(root.GetProperty("authorityAccepted").GetBoolean());
                Assert.True(root.GetProperty("acceptanceIssued").GetBoolean());
                Assert.NotNull(result.CanonicalReceiptPath);
                var bytes = await File.ReadAllBytesAsync(result.CanonicalReceiptPath, token);
                var document = ContractValidator.ParseAndValidate(bytes);
                Assert.Equal("review-receipt", document.SchemaName);
                Assert.Equal(document.Identity, root.GetProperty("receiptIdentity").GetString());
                Assert.Equal(Hash(bytes), root.GetProperty("canonicalReceiptSha256").GetString());
                Assert.True(root.GetProperty("originalPinnedReceiverAllowedBeforeNegative").GetBoolean());
                if (scenario == "marked-positive") Assert.True(root.GetProperty("transitionAllowed").GetBoolean());
                if (scenario == "revoked-capability")
                {
                    Assert.True(root.GetProperty("capabilityRevoked").GetBoolean());
                    Assert.False(root.GetProperty("transitionAllowedAfterRevocation").GetBoolean());
                    Assert.Equal(root.GetProperty("receiptSha256BeforeRevocation").GetString(), Hash(bytes));
                    Assert.Equal(root.GetProperty("rawSha256BeforeRevocation").GetString(), Hash(await File.ReadAllBytesAsync(result.RawReviewerPath, token)));
                }
                if (scenario is "foreign-host" or "distinct-request")
                {
                    Assert.True(root.GetProperty("secondaryChainIntactAndOwnReceiverAllowed").GetBoolean());
                    Assert.False(root.GetProperty("secondaryAcceptanceAllowedByOriginalReceiver").GetBoolean());
                    Assert.True(root.GetProperty("originalReceiverStillAllowsOriginal").GetBoolean());
                    Assert.NotEqual(root.GetProperty("originalRequestIdentity").GetString(), root.GetProperty("secondaryRequestIdentity").GetString());
                }
            }
        }
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(120));
    }

    private static async Task<PublicReviewSetup> Setup(OwnedScenarioRun artifacts, long started, CancellationToken token)
    {
        var baseCommit = await artifacts.GitAsync(token, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(artifacts.Checkout, "subject.txt"), "actual candidate review patch\n", token);
        await artifacts.GitAsync(token, "add", "--", "subject.txt");
        await artifacts.GitAsync(token, "-c", "user.name=ReviewFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "actual review candidate");
        var context = await artifacts.Reader.ReadAsync(artifacts.Checkout, "refs/heads/main", token);
        var file = await artifacts.Reader.ReadFileAsync(context, "subject.txt", token);
        var now = DateTimeOffset.UtcNow;
        var request = new PlanningRequest("fixture/delivery-forge", "#11", "implement", "Public review-chain fixture",
            ["subject.txt"], ["excluded"], ["Pure fixture review policy, no production provenance"], "implement");
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, now, [])];
        var plan = PlanFreezer.Freeze(new(request, context, evidence, [new("subject.txt", "fixture", "review candidate")], [new("#11", [], "root")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], IntakePlanner.Assess(request, evidence)), "public-review-fixture", now);
        await new AppendOnlyContractStore(Path.Combine(artifacts.Root, "plan-contracts")).WriteImmutableAsync(plan.PlanContractBytes, token);
        var policyNode = new JsonObject
        {
            ["schemaVersion"] = "1.0.0", ["kind"] = "evidence-policy", ["policyId"] = "issue11-public-review-fixture",
            ["requiredGates"] = new JsonArray("test"), ["authorizedCeiling"] = "implement", ["requireIndependentReview"] = true,
            ["allowedNotApplicable"] = new JsonArray(), ["createdAt"] = now.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
        policyNode["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(policyNode.ToJsonString()));
        var policyBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(policyNode.ToJsonString()));
        var policyDocument = ContractValidator.ParseAndValidate(policyBytes);
        await new AppendOnlyContractStore(Path.Combine(artifacts.Root, "fixture-evidence-policy-inputs")).WriteImmutableAsync(policyBytes, token);
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "git.exe" : "git")).First(File.Exists);
        var host = ReviewAuthorityHost.CreateIsolatedTest();
        var preparation = await host.PrepareIsolatedTestGitEvidenceAsync(artifacts.Checkout, Path.Combine(artifacts.Checkout, ".git"), git,
            1024 * 1024, TimeSpan.FromSeconds(15), token);
        if (preparation.Observation.Outcome != ReviewGitPreparationOutcome.Prepared || preparation.Context is null || !preparation.Observation.OwnedProcessQuiescent)
            throw new InvalidOperationException("Actual bounded owner Git preparation did not issue a positively settled context: " + preparation.Observation.Reason);
        using var controlled = preparation.Context;
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(token); capture.CancelAfter(TimeSpan.FromSeconds(15));
        var actualPatch = await GitPatchIdentity.ComputeAsync(controlled, baseCommit, context.Commit, plan.ContractIdentity,
            new Dictionary<string, string> { ["plan-contract"] = Hash(plan.PlanContractBytes), ["fixture-evidence-policy-input"] = Hash(policyBytes) }, capture.Token);
        var rawIndex = Hash(await File.ReadAllBytesAsync(Path.Combine(artifacts.Checkout, ".git", "index"), token));
        await artifacts.WriteNewJsonAsync("public-review-real-git-setup.json", new
        {
            fixture = true, productionAccepted = false, firstFullRealRunCertified = false, seed = 11019,
            integratedBaseCommit = artifacts.IntegratedSource.Commit, implementationHead = artifacts.IntegratedSource.HeadCommit,
            baseCommit, headCommit = context.Commit, headTree = context.Tree, actualPatch, rawIndexSha256 = rawIndex,
            wallSeconds = 120, ordinaryGitSeconds = 15, maximumCaptureBytes = 1024 * 1024, cpu = 2, memoryMiB = 4096,
            setupElapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
            limitation = "Pure fixture policy input only. Bounded owner preparation has its own proper RED/GREEN prerequisite before distinct chain RED. No synchronous unbounded fallback. Strong protected-policy composition remains required."
        }, token);
        return new(artifacts, baseCommit, context.Commit, context.Tree, plan, policyDocument.Identity, git, actualPatch, rawIndex);
    }
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
