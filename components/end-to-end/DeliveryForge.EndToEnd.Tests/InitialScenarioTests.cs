using DeliveryForge.Evidence;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class InitialScenarioTests
{
    private static ScenarioBudget Budget => new(TimeSpan.FromSeconds(90), 2, 4096, 1000, 0m);
    private static ScenarioOptions Options(ScenarioProfile profile) => new(profile, [], Budget, 11017);

    [Theory(Timeout = 120_000)]
    [InlineData("worker")]
    [InlineData("independent-reviewer")]
    [InlineData("dotnet-tools")]
    public async Task Local_real_missing_configured_capability_is_incomplete(string unavailable)
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var options = Options(ScenarioProfile.LocalReal) with
        {
            Capabilities = [new(unavailable, false, false, "not-configured")]
        };
        var result = await new ScenarioRunner(run).RunAsync("profile.local-real.required-capability-missing", options,
            TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.False(result.FullRealProfileCertified);
        Assert.NotEmpty(result.Limitations);
        Assert.True(result.Invariants["no-publication-writes"]);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Production_without_deployed_coordination_is_incomplete()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var result = await new ScenarioRunner(run).RunAsync("profile.production.coordination-unavailable",
            Options(ScenarioProfile.ProductionCoordinated), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.False(result.FullRealProfileCertified);
        Assert.NotEmpty(result.Limitations);
        Assert.True(result.Invariants["no-publication-writes"]);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Caller_claims_cannot_certify_an_actual_real_profile()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var options = Options(ScenarioProfile.LocalReal) with
        {
            Capabilities = [new("worker", true, false, "caller-claim"),
                new("independent-reviewer", true, false, "caller-claim"), new("dotnet-tools", true, false, "caller-claim")]
        };
        var result = await new ScenarioRunner(run).RunAsync("profile.claims-are-not-authority", options,
            TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.False(result.FullRealProfileCertified);
        Assert.True(result.Invariants["no-publication-writes"]);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Local_contract_recovery_binds_real_durable_receipts_to_integrated_source_and_budget()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var options = Options(ScenarioProfile.LocalContract);
        var result = await new ScenarioRunner(run).RunAsync("recovery.torn-run-pointer", options,
            TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Pass, result.Outcome);
        Assert.True(result.Fixture);
        Assert.False(result.FullRealProfileCertified);
        Assert.Equal(run.IntegratedSource.Commit, result.IntegratedBaseCommit);
        Assert.Equal(run.IntegratedSource.HeadCommit, result.ImplementationHead);
        Assert.Equal(options.Seed, result.Seed);
        Assert.Equal(options.Budget, result.Budget);
        Assert.True(result.Invariants["durable-pointer-recovered"]);
        Assert.True(result.Invariants["no-publication-writes"]);
        Assert.NotEmpty(result.ReceiptIds);
        var recovery = await run.Store.RecoverAsync(run.RunId, TestContext.Current.CancellationToken);
        Assert.All(result.ReceiptIds, id => Assert.Contains(recovery.Records, record => record.Sha256 == id));
        Assert.Contains(recovery.Records, record => record.Kind == "scenario-result");
        run.MarkAssertionsPassed();
    }

    [Theory(Timeout = 120_000)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Nonfinite_or_missing_budget_is_rejected_before_scenario_work(int seconds)
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var options = Options(ScenarioProfile.LocalContract) with { Budget = Budget with { WallTime = TimeSpan.FromSeconds(seconds) } };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ScenarioRunner(run).RunAsync(
            "recovery.torn-run-pointer", options, TestContext.Current.CancellationToken));
        Assert.Empty((await run.Store.RecoverAsync(run.RunId, TestContext.Current.CancellationToken)).Records);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Undefined_profile_cannot_fall_back_to_local_contract()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ScenarioRunner(run).RunAsync(
            "recovery.torn-run-pointer", Options((ScenarioProfile)999), TestContext.Current.CancellationToken));
        Assert.Empty((await run.Store.RecoverAsync(run.RunId, TestContext.Current.CancellationToken)).Records);
        run.MarkAssertionsPassed();
    }
}
