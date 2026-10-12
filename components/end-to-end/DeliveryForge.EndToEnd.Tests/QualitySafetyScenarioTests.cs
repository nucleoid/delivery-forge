using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Evidence;
using DeliveryForge.Evidence.Adapters;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class QualitySafetyScenarioTests
{
    private static ScenarioOptions Options => new(ScenarioProfile.LocalContract, [],
        new(TimeSpan.FromSeconds(120), 2, 4096, 1000, 0m), 11017);

    [Fact(Timeout = 120_000)]
    public async Task Worker_branch_policy_and_exemption_relaxation_cannot_replace_protected_requirements()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var data = await PrepareProtectedPolicy(run);
        await WriteShadow(run, data);
        var result = await new ScenarioRunner(run).RunAsync("quality.worker-policy-shadow", Options, TestContext.Current.CancellationToken);
        AssertNonpass(result, "protected-policy-retained", "worker-policy-not-authority");
        await AssertObservation(run, result, data);
        Assert.NotEqual(data.PolicyIdentity, ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(
            Path.Combine(run.Checkout, data.PolicyPath), TestContext.Current.CancellationToken)).Identity);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Caller_baseline_with_exact_source_and_nonfixture_claim_cannot_hide_missing_issuance()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var data = await PrepareProtectedPolicy(run);
        await File.WriteAllTextAsync(Path.Combine(run.Checkout, data.CandidatePath), JsonSerializer.Serialize(new
        {
            baselineIdentity = "caller-rebased-debt",
            policyIdentity = data.PolicyIdentity,
            sourceRevision = data.ProtectedBaseCommit,
            fixture = false,
            metrics = new Dictionary<string, decimal> { ["debt"] = 1000m },
            current = new Dictionary<string, decimal> { ["debt"] = 10m }
        }), TestContext.Current.CancellationToken);
        var result = await new ScenarioRunner(run).RunAsync("quality.caller-baseline-gaming", Options, TestContext.Current.CancellationToken);
        AssertNonpass(result, "caller-baseline-not-authority", "claimed-nonfixture-not-authority");
        await AssertObservation(run, result, data);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Untrusted_mutation_preview_success_bytes_cannot_become_installed_outcome_certification()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var data = await PrepareProtectedPolicy(run);
        await File.WriteAllTextAsync(Path.Combine(run.Checkout, data.CandidatePath),
            "{\"kind\":\"untrusted-mutation-preview\",\"outcome\":\"PASS\",\"complete\":true,\"fixture\":false}", TestContext.Current.CancellationToken);
        var result = await new ScenarioRunner(run).RunAsync("quality.incomplete-mutation-preview", Options, TestContext.Current.CancellationToken);
        AssertNonpass(result, "mutation-certification-unavailable", "preview-success-not-authority");
        await AssertObservation(run, result, data);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Real_protected_Git_resolution_control_ignores_committed_worker_shadow_and_na_exemption()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var data = await PrepareProtectedPolicy(run);
        await WriteShadow(run, data);
        var observed = await new ProtectedGitPolicyResolver(new OwnedPolicyGitExecutor(run), run.Checkout, data.ProtectedBaseCommit)
            .ResolveAsync(data.PolicyPath, TestContext.Current.CancellationToken);
        Assert.Equal(data.PolicyIdentity, observed.PolicyIdentity);
        Assert.Equal(data.ProtectedBaseCommit, observed.SourceRevision);
        Assert.Equal(new[] { "test", "coverage", "mutation" }, observed.RequiredGates);
        Assert.Equal(PolicyAuthorityKind.ProtectedGitBase, observed.AuthorityKind);
        Assert.Equal(0m, observed.MaximumChangedCodeDebtIncrease);
        Assert.NotEqual(data.ProtectedBaseCommit, await run.GitAsync(TestContext.Current.CancellationToken, "rev-parse", "HEAD"));
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Actual_malformed_mutation_file_control_is_error_and_never_rewritten_as_success()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        run.RetainArtifacts("Malformed untrusted mutation file is retained negative evidence, not an installed tool result.");
        var path = Path.Combine(run.Root, "malformed-mutation.json");
        await File.WriteAllTextAsync(path, "{not-a-complete-report", TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var result = new Mutate4CSharpAdapter().Normalize(before, ToolCapability.Unsupported("mutate4csharp", "No installed-outcome certificate."));
        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.False(result.ProductionCapable);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(before)), Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken))));
        run.MarkAssertionsPassed();
    }

    private static void AssertNonpass(ScenarioResult result, params string[] invariants)
    {
        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.True(result.Fixture);
        Assert.False(result.FullRealProfileCertified);
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, result.IntegratedBaseCommit);
        Assert.Equal(11017, result.Seed);
        foreach (var invariant in invariants) Assert.True(result.Invariants[invariant]);
        Assert.True(result.Invariants["no-publication-writes"]);
        Assert.NotEmpty(result.ReceiptIds);
    }

    private static async Task AssertObservation(OwnedScenarioRun run, ScenarioResult result, QualityInput data)
    {
        var record = Assert.Single((await run.Store.RecoverAsync(run.RunId, TestContext.Current.CancellationToken)).Records,
            record => record.Kind == "scenario-quality-observation");
        Assert.Contains(record.Sha256, result.ReceiptIds);
        Assert.Equal(data.ProtectedBaseCommit, record.Payload.GetProperty("protectedBaseCommit").GetString());
        Assert.Equal(data.PolicyIdentity, record.Payload.GetProperty("policyIdentity").GetString());
        var candidate = await File.ReadAllBytesAsync(Path.Combine(run.Checkout, data.CandidatePath), TestContext.Current.CancellationToken);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(candidate)), record.Payload.GetProperty("candidateSha256").GetString());
        Assert.True(record.Payload.GetProperty("fixture").GetBoolean());
        Assert.False(record.Payload.GetProperty("productionCertified").GetBoolean());
    }

    private sealed record QualityInput(string ProtectedBaseCommit, string PolicyPath, string CandidatePath, string PolicyIdentity);
    private static async Task<QualityInput> PrepareProtectedPolicy(OwnedScenarioRun run)
    {
        const string policyPath = ".forge/evidence-policy.json";
        const string candidatePath = ".forge/candidate-quality.json";
        Directory.CreateDirectory(Path.Combine(run.Checkout, ".forge"));
        var strict = Policy(["test", "coverage", "mutation"], []);
        await File.WriteAllBytesAsync(Path.Combine(run.Checkout, policyPath), strict, TestContext.Current.CancellationToken);
        await run.GitAsync(TestContext.Current.CancellationToken, "add", "--", policyPath);
        await run.GitAsync(TestContext.Current.CancellationToken, "-c", "user.name=EndToEndFixture", "-c", "user.email=fixture@example.invalid",
            "commit", "-q", "-m", "strict fixture protected policy");
        var protectedBase = await run.GitAsync(TestContext.Current.CancellationToken, "rev-parse", "HEAD");
        var data = new QualityInput(protectedBase, policyPath, candidatePath, ContractValidator.ParseAndValidate(strict).Identity);
        await run.WriteNewJsonAsync("quality-input.json", data, TestContext.Current.CancellationToken);
        var source = await File.ReadAllBytesAsync(Path.Combine(run.Root, "source.json"), TestContext.Current.CancellationToken);
        await run.WriteNewJsonAsync("quality-spec.json", new
        {
            fixture = true,
            productionCertified = false,
            firstFullRealRunCertified = false,
            sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source)),
            seed = 11017,
            budget = Options.Budget,
            limitation = "Untrusted input/policy admission only; no fake executor, synthetic compiler counts, installed producer or quality PASS."
        }, TestContext.Current.CancellationToken);
        return data;
    }

    private static async Task WriteShadow(OwnedScenarioRun run, QualityInput data)
    {
        var shadow = Policy(["test"], ["coverage", "mutation"]);
        await File.WriteAllBytesAsync(Path.Combine(run.Checkout, data.PolicyPath), shadow, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(run.Checkout, data.CandidatePath), shadow, TestContext.Current.CancellationToken);
        await run.GitAsync(TestContext.Current.CancellationToken, "add", "--", data.PolicyPath, data.CandidatePath);
        await run.GitAsync(TestContext.Current.CancellationToken, "-c", "user.name=EndToEndFixture", "-c", "user.email=fixture@example.invalid",
            "commit", "-q", "-m", "attempted worker policy and exemption relaxation");
    }

    private static byte[] Policy(string[] required, string[] exemptions)
    {
        var node = new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["kind"] = "evidence-policy",
            ["identity"] = "sha256:" + new string('0', 64),
            ["policyId"] = "strict-e2e-fixture",
            ["requiredGates"] = JsonSerializer.SerializeToNode(required),
            ["authorizedCeiling"] = "implement",
            ["requireIndependentReview"] = true,
            ["allowedNotApplicable"] = JsonSerializer.SerializeToNode(exemptions),
            ["createdAt"] = "2026-10-11T00:00:01Z"
        };
        node["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(node.ToJsonString()));
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        ContractValidator.ParseAndValidate(bytes);
        return bytes;
    }
}
