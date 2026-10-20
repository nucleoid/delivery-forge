namespace DeliveryForge.Evidence.Tests;

public sealed class GateExecutorExceptionTests
{
    [Fact]
    public async Task Executor_exception_after_dispatch_is_INCOMPLETE_without_positive_owned_quiescence()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path);
        var executor = new ThrowAfterDispatch();
        var runner = new GateRunner(executor,
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            request.Invocation.WorkingDirectory, TestEvidence.Commit('a'));
        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);
        Assert.True(executor.WasDispatched);
        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Null(result.Execution.ExitCode);
        Assert.False(result.Execution.OwnedProcessQuiescent);
        Assert.Contains("owner-test injected post-dispatch exception", result.Execution.StandardError, StringComparison.Ordinal);
    }

    // Owner fault test only; not an actual SDK execution or native settlement certificate.
    private sealed class ThrowAfterDispatch : ICommandExecutor
    {
        public bool WasDispatched { get; private set; }
        public Task<CommandResult> ExecuteAsync(CommandInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken)
        {
            WasDispatched = true;
            throw new InvalidOperationException("owner-test injected post-dispatch exception");
        }
    }
}
