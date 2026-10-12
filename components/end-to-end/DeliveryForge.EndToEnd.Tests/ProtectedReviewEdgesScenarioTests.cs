using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution.Host;
using DeliveryForge.Review;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class ProtectedReviewEdgesScenarioTests
{
    private static readonly string[] Edges = ["IndependentReview->PrAuthorized", "PrAuthorized->PrPublished",
        "CiComplete->HostReviewComplete", "HostReviewComplete->MergeAuthorized", "MergeAuthorized->Merged"];

    [Theory(Timeout = 120_000)]
    [InlineData("all-five-positive")]
    [InlineData("hosted-revoked")]
    [InlineData("independent-revoked")]
    [InlineData("hosted-reuses-independent")]
    [InlineData("foreign-protected-host")]
    [InlineData("same-host-distinct-pair")]
    [InlineData("real-head-drift")]
    [InlineData("original-pr-ceiling")]
    public async Task All_five_protected_review_edges_require_current_dual_role_issuer_and_immutable_ceiling(string scenario)
    {
        var started = Stopwatch.GetTimestamp();
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wall.CancelAfter(TimeSpan.FromSeconds(120));
        var token = wall.Token;
        await using var run = await OwnedScenarioRun.CreateAsync(token);
        run.RetainArtifacts("All-five protected review lookup controls retained; publication/CI inputs are fixtures, no publisher executed.");
        var ceiling = scenario == "original-pr-ceiling" ? "pr" : "merge";
        var source = await ProtectedReviewFixtureBootstrapTests.Setup(run, token, initialCeiling: ceiling, includeHostedPolicy: true);
        using var bootstrap = await ReviewAuthorityHost.CreateProtectedGitContractFixtureAsync(source.Configuration, token);
        using var gitBound = CancellationTokenSource.CreateLinkedTokenSource(token); gitBound.CancelAfter(TimeSpan.FromSeconds(15));
        var patch = await GitPatchIdentity.ComputeAsync(bootstrap.ObjectView, source.BaseCommit, source.HeadCommit, source.Plan.ContractIdentity,
            new Dictionary<string, string> { ["plan-contract"] = Hash(source.Plan.PlanContractBytes), ["protected-policy-bytes"] = source.ProtectedPolicySha256 }, gitBound.Token);
        Assert.Equal(source.Plan.BaseCommit, patch.BaseCommit);
        Assert.Equal(source.ProtectedPolicySha256, bootstrap.Policy.ContentIdentity);
        Assert.Equal(ReviewAuthorityRealm.Test, bootstrap.Policy.SourceRealm);
        Assert.Contains(new FamilyPair("gpt", "claude"), bootstrap.Policy.AllowedFamilies);
        Assert.Contains(new FamilyPair("gpt", "github-hosted"), bootstrap.Policy.AllowedFamilies);
        var root = bootstrap.RootContext;
        ReviewAcceptanceContext Context(string role) => new(root.RunIdentity, role + "-request-" + Guid.NewGuid().ToString("N"), root.PlanIdentity,
            root.EvidencePolicyIdentity, root.ReviewPolicyContentIdentity, root.ReviewPolicySourceRevision, root.ReviewPolicyAuthorityKind,
            root.SourceAuthorityIdentity, root.SourceRoot, root.ProtectedBaseCommit, root.ProtectedObjectViewIdentity);
        var independent = Context("independent"); var hosted = Context("hosted");
        var receiver = bootstrap.Host.CreateTransitionReceiver(independent, hosted); // Both pinned BEFORE reviewer raw artifacts/receipts.
        var publication = await Prerequisite(run, source, patch, "PR_PUBLISHED", token);
        var ci = await Prerequisite(run, source, patch, "ci", token);
        var merge = await Prerequisite(run, source, patch, "MERGED", token);
        await run.WriteNewJsonAsync("review-all-edges-public-setup.json", new
        {
            fixture = true,
            scenario,
            seed = 11019,
            initialCeiling = ceiling,
            source.BaseCommit,
            source.HeadCommit,
            patch.HeadTree,
            planIdentity = source.Plan.ContractIdentity,
            protectedPolicySha256 = bootstrap.Policy.ContentIdentity,
            independentRequestIdentity = independent.RequestIdentity,
            hostedRequestIdentity = hosted.RequestIdentity,
            publicationInputIdentity = publication.Identity,
            ciInputIdentity = ci.Identity,
            mergeInputIdentity = merge.Identity,
            publicationAndCiArePrerequisiteInputsOnly = true,
            publicationOwnerExecuted = false,
            actualModelCalls = 0,
            integratedBaseCommit = OwnedScenarioRun.InitialIntegratedBase,
            implementationHead = run.IntegratedSource.HeadCommit,
            wallSeconds = 120,
            gitSeconds = 15,
            maximumCaptureBytes = 1024 * 1024,
            cpu = 2,
            memoryMiB = 4096,
            limitation = "Public review lookup edges only; no #10 bridge/golden/production/real independent model certification."
        }, token);
        var actual = await ProtectedReviewEdgesOrchestrator.RunAsync(new(run, bootstrap, source, patch, independent, hosted, receiver,
            publication, ci, merge), scenario, started, token);
        Assert.Equal(Edges, actual.Before.Select(edge => edge.Edge));
        Assert.Equal(Edges, actual.After.Select(edge => edge.Edge));
        Assert.All(actual.Before, edge => Assert.True(edge.ProductionDenied));
        Assert.All(actual.After, edge => Assert.True(edge.ProductionDenied));
        if (scenario == "original-pr-ceiling")
        {
            Assert.All(actual.Before.Take(3), edge => Assert.True(edge.Allowed));
            Assert.All(actual.Before.Skip(3), edge => Assert.False(edge.Allowed));
            Assert.Equal(actual.Before.Select(edge => edge.Allowed), actual.After.Select(edge => edge.Allowed));
        }
        else
        {
            Assert.All(actual.Before, edge => Assert.True(edge.Allowed));
            if (scenario is "hosted-revoked" or "hosted-reuses-independent")
            {
                Assert.All(actual.After.Take(2), edge => Assert.True(edge.Allowed));
                Assert.All(actual.After.Skip(2), edge => Assert.False(edge.Allowed));
            }
            else if (scenario is "independent-revoked" or "real-head-drift") Assert.All(actual.After, edge => Assert.False(edge.Allowed));
            else Assert.All(actual.After, edge => Assert.True(edge.Allowed));
        }
        if (scenario is "foreign-protected-host" or "same-host-distinct-pair")
        {
            Assert.Equal(Edges, actual.SecondaryOwn.Select(edge => edge.Edge)); Assert.All(actual.SecondaryOwn, edge => Assert.True(edge.Allowed));
            Assert.Equal(Edges, actual.SecondaryAtOriginal.Select(edge => edge.Edge)); Assert.All(actual.SecondaryAtOriginal, edge => Assert.False(edge.Allowed));
            Assert.Equal(Edges, actual.OriginalStillValid.Select(edge => edge.Edge)); Assert.All(actual.OriginalStillValid, edge => Assert.True(edge.Allowed));
        }
        if (scenario == "real-head-drift")
        { Assert.NotNull(actual.CurrentHeadAfterDrift); Assert.NotEqual(patch.HeadCommit, actual.CurrentHeadAfterDrift); }
        Assert.True(actual.ReviewerFixtureCalls >= 2);
        Assert.Equal(0, actual.PublicationCalls); Assert.Equal(0, actual.ModelCalls);
        Assert.True(File.Exists(actual.ObservationPath)); Assert.True(File.Exists(actual.ReviewerLedgerPath));
        Assert.True(actual.ImmutableArtifacts.Count >= 4);
        foreach (var artifact in actual.ImmutableArtifacts)
        {
            Assert.Equal(artifact.BeforeSha256, artifact.AfterSha256);
            Assert.Equal(artifact.AfterSha256, Hash(await File.ReadAllBytesAsync(artifact.Path, token)));
        }
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(120));
    }

    private static async Task<ValidatedContract> Prerequisite(OwnedScenarioRun run, ProtectedReviewFixtureBootstrapTests.SetupResult source,
        PatchIdentity patch, string kind, CancellationToken token)
    {
        var now = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var node = new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["kind"] = kind == "ci" ? "gate-receipt" : "publication-receipt",
            ["baseCommit"] = patch.BaseCommit,
            ["headCommit"] = patch.HeadCommit,
            ["treeId"] = patch.HeadTree
        };
        if (kind == "ci")
        {
            node["gateId"] = "ci"; node["policyIdentity"] = source.EvidencePolicy.Identity;
            node["command"] = "CONTRACT-FIXTURE-INPUT: CI was not executed"; node["startedAt"] = now; node["completedAt"] = now;
            node["exitCode"] = 0; node["outcome"] = "PASS";
            node["reason"] = "Schema-valid CI prerequisite fixture for review lookup only; no producer/gate execution.";
            node["artifactHashes"] = new JsonObject(); node["sourceChanged"] = false;
            node["limitations"] = new JsonArray("PREREQUISITE_FIXTURE_INPUT_ONLY", "NO_ACTUAL_CI_OR_DOTNET_PRODUCER");
        }
        else
        {
            node["action"] = kind; node["remoteRef"] = "contract-fixture-input:refs/heads/protected-review-candidate";
            node["resultId"] = "contract-fixture-input:" + kind; node["createdAt"] = now;
        }
        node["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(node.ToJsonString()));
        var bytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(node.ToJsonString()));
        var validated = ContractValidator.ParseAndValidate(bytes);
        await new AppendOnlyContractStore(Path.Combine(run.Root, "review-prerequisite-fixture-inputs")).WriteImmutableAsync(bytes, token);
        return validated;
    }
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
