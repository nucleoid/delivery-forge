namespace DeliveryForge.Evidence.Tests;

// Acceptance boundary DATA only: these executors do not own native processes,
// issue installed proof, or establish that a real Git invocation has settled.
public sealed class GitCaptureRefusalTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public async Task Every_identity_command_requires_quiescence_before_accepting_its_output(int uncertainIndex, bool bounded)
    {
        using var root = new TempDirectory();
        var complete = IdentityResults();
        var accepted = Execute(complete, bounded);
        Assert.Equal(TestEvidence.Repository(), await new GitRepositoryIdentityReader(
            accepted, root.Path, TestEvidence.Commit('a')).ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(5, accepted.Calls);

        var uncertain = IdentityResults();
        uncertain[uncertainIndex] = uncertain[uncertainIndex] with { OwnedProcessQuiescent = false };
        var refused = Execute(uncertain, bounded);
        await Assert.ThrowsAsync<EvidenceRepositoryException>(() => new GitRepositoryIdentityReader(
            refused, root.Path, TestEvidence.Commit('a')).ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(uncertainIndex + 1, refused.Calls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(128, false)]
    [InlineData(0, true)]
    [InlineData(128, true)]
    public async Task Optional_protected_ignore_read_requires_quiescence_even_for_missing_blob(int exit, bool bounded)
    {
        using var root = new TempDirectory();
        // No JavaScript inputs: the third operation is precisely the optional
        // protected ignore read. An uncertain missing result is not absence.
        var executor = Execute([
            Result(TestEvidence.Commit('b')), Result(string.Empty),
            Result(string.Empty) with { ExitCode = exit, OwnedProcessQuiescent = false }
        ], bounded);
        await Assert.ThrowsAsync<EvidenceRepositoryException>(() => new GitRepositoryIdentityReader(
            executor, root.Path, TestEvidence.Commit('a')).ReadEvaluationScopeAsync(
                TestEvidence.Commit('b'), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(3, executor.Calls);
    }

    private static CommandResult[] IdentityResults() => [
        Result(TestEvidence.Commit('b')), Result(TestEvidence.Commit('a')),
        Result(string.Empty), Result(TestEvidence.Commit('c')), Result(string.Empty)
    ];
    private static CommandResult Result(string output)
    {
        var now = DateTimeOffset.UtcNow;
        return new(0, output, string.Empty, false, false, now, now);
    }
    private static PlainExecutor Execute(CommandResult[] results, bool bounded) =>
        bounded ? new BoundedExecutor(results) : new PlainExecutor(results);
    private class PlainExecutor(CommandResult[] results) : ICommandExecutor
    {
        private readonly Queue<CommandResult> remaining = new(results);
        internal int Calls { get; private set; }
        public Task<CommandResult> ExecuteAsync(CommandInvocation invocation, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(remaining.Dequeue());
        }
    }
    private sealed class BoundedExecutor(CommandResult[] results) : PlainExecutor(results), IBoundedGateFixtureExecutor
    {
        public bool SupportsCreationOwnedBoundedCapture => true;
        public async Task<BoundedCommandCaptureResult> ExecuteBoundedCaptureAsync(CommandInvocation invocation,
            TimeSpan timeout, int maximumBytesPerStream, CancellationToken token)
        {
            var result = await ExecuteAsync(invocation, timeout, token);
            return new(result, System.Text.Encoding.UTF8.GetBytes(result.StandardOutput), [], false, true, true, true);
        }
    }
}
