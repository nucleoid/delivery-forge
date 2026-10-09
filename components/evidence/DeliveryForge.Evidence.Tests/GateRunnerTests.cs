namespace DeliveryForge.Evidence.Tests;

public sealed class GateRunnerTests
{
    [Fact]
    public void Temp_cleanup_does_not_change_a_reparse_point_target()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var target = new TempDirectory();
        var targetFile = System.IO.Path.Combine(target.Path, "target.txt");
        File.WriteAllText(targetFile, "outside");
        File.SetUnixFileMode(targetFile, UnixFileMode.UserRead);
        var mode = File.GetUnixFileMode(targetFile);

        using (var cleanupRoot = new TempDirectory())
            File.CreateSymbolicLink(System.IO.Path.Combine(cleanupRoot.Path, "link.txt"), targetFile);

        Assert.True(File.Exists(targetFile));
        Assert.Equal(mode, File.GetUnixFileMode(targetFile));
    }

    [Fact]
    public async Task Captures_immutable_argv_artifacts_and_source_identity()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), temp.Path);

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.False(result.ProductionCapable);
        Assert.Equal(["test", "DeliveryForge.Tests.csproj", "--no-restore"], result.Invocation.Arguments);
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
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), changed), temp.Path);

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.True(result.SourceChanged);
        await Assert.ThrowsAsync<EvidenceWriteException>(() => runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Timeout_never_passes_even_with_exit_zero()
    {
        using var temp = new TempDirectory();
        var execution = TestEvidence.CommandResult() with { TimedOut = true };
        var runner = new GateRunner(
            new StubCommandExecutor(execution),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), temp.Path);

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
    }

    [Fact]
    public async Task Missing_executable_is_recorded_as_error_receipt()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path) with
        {
            Invocation = new CommandInvocation(
                System.IO.Path.Combine(temp.Path, "missing-executable"),
                [],
                temp.Path)
        };
        var runner = new GateRunner(
            new ProcessCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), temp.Path);

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Caller_cancellation_result_is_recorded_incomplete()
    {
        using var temp = new TempDirectory();
        var execution = TestEvidence.CommandResult() with { Cancelled = true };
        var runner = new GateRunner(
            new StubCommandExecutor(execution),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), temp.Path);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), cancellation.Token);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Repository_preflight_failure_is_recorded_as_error_receipt()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new ThrowingRepositoryIdentityReader(), temp.Path);

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.True(File.Exists(result.ReceiptPath));
        Assert.Contains("could not be read", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unexpected_executor_failure_is_recorded_as_error_receipt()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), temp.Path);

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.True(File.Exists(result.ReceiptPath));
        Assert.Null(result.Execution.ExitCode);
    }

    [Fact]
    public async Task Repository_postflight_failure_is_recorded_as_error_receipt()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new FirstThenThrowRepositoryIdentityReader(TestEvidence.Repository()), temp.Path);

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.True(result.SourceChanged);
        Assert.True(File.Exists(result.ReceiptPath));
        Assert.Contains("after evaluation", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adapter_tool_mismatch_is_rejected_before_execution()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path) with
        {
            Capability = TestEvidence.GateRequest(temp.Path).Capability with { Tool = "crap4csharp" }
        };
        var runner = new GateRunner(new QueueCommandExecutor(),
            new ThrowingRepositoryIdentityReader(), temp.Path);

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.Contains("adapter", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Executable_swap_during_run_is_an_orchestration_error()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path);
        var runner = new GateRunner(
            new MutatingCommandExecutor(request.Invocation.FileName, TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), temp.Path);

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.Contains("executable bytes changed", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Preexisting_unchanged_trx_cannot_be_reused()
    {
        using var temp = new TempDirectory();
        var trx = System.IO.Path.Combine(temp.Path, "stale.trx");
        await File.WriteAllTextAsync(
            trx,
            "<TestRun><ResultSummary><Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>",
            TestContext.Current.CancellationToken);
        var request = TestEvidence.GateRequest(temp.Path);
        request = request with
        {
            Invocation = request.Invocation with
            {
                Arguments = [.. request.Invocation.Arguments, "--logger", $"trx;LogFileName={trx}"]
            }
        };
        var execution = TestEvidence.CommandResult() with { StandardOutput = "Subject -> Subject.dll" };
        var runner = new GateRunner(
            new StubCommandExecutor(execution),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), temp.Path);

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.Contains("stale", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evidence.trx", result.ArtifactHashes.Keys);
    }
}
