namespace DeliveryForge.Evidence.Tests;

public sealed class GateRunnerTests
{
    [Fact]
    public async Task Captures_immutable_argv_artifacts_and_source_identity()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()));

        var result = await runner.RunAsync(TestEvidence.GateRequest(temp.Path));

        Assert.Equal(GateOutcome.Pass, result.Outcome);
        Assert.Equal(["test", "DeliveryForge.slnx", "--no-restore"], result.Invocation.Arguments);
        Assert.Equal(2, result.ArtifactHashes.Count);
        Assert.All(result.ArtifactHashes.Values, value => Assert.StartsWith("sha256:", value));
    }

    [Fact]
    public async Task Source_drift_is_error_and_append_only_output_is_not_overwritten()
    {
        using var temp = new TempDirectory();
        var changed = TestEvidence.Repository() with { TreeId = TestEvidence.Commit('d') };
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), changed));

        var result = await runner.RunAsync(TestEvidence.GateRequest(temp.Path));

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.True(result.SourceChanged);
        await Assert.ThrowsAsync<EvidenceWriteException>(() => runner.RunAsync(TestEvidence.GateRequest(temp.Path)));
    }

    [Fact]
    public async Task Timeout_never_passes_even_with_exit_zero()
    {
        using var temp = new TempDirectory();
        var execution = TestEvidence.CommandResult() with { TimedOut = true };
        var runner = new GateRunner(
            new StubCommandExecutor(execution),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()));

        var result = await runner.RunAsync(TestEvidence.GateRequest(temp.Path));

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
    }
}
