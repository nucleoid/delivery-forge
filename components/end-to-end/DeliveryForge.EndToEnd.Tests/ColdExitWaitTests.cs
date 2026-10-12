using System.Diagnostics;
using System.Text.Json;
using DeliveryForge.Execution;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

[Collection(LocalSingleFixtureCollection.Name)]
public sealed class ColdExitWaitTests
{
    [Fact(Timeout = 120_000)]
    public async Task Permanently_unknown_fresh_controller_cannot_advance_and_exhausts_original_finite_wait()
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        await using var fresh = new ProcessControl();
        var child = runtime.StartBoundedChild();
        try
        {
            var live = runtime.Processes.Observe(child.Identity);
            Assert.True(live.Outcome == ProcessControlOutcome.LiveOwned, live.Reason);
            Assert.Equal(ProcessControlOutcome.IdentityUnknown, fresh.Observe(child.Identity).Outcome);
            var elapsed = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ColdRecoveryOrchestrator.WaitForActualExitAsync(artifacts, fresh, child.Identity, TestContext.Current.CancellationToken));
            Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12));
            using var poll = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(artifacts.Root, "cold-child-exit-poll.json"), TestContext.Current.CancellationToken));
            var observations = poll.RootElement.GetProperty("observations").EnumerateArray().ToArray();
            Assert.InRange(observations.Length, 1, 256);
            Assert.All(observations, observation => Assert.Equal((int)ProcessControlOutcome.IdentityUnknown, observation.GetProperty("Outcome").GetInt32()));
            Assert.Empty(fresh.GetAllOwnedIdentities());
            Assert.Equal(ProcessControlOutcome.LiveOwned, runtime.Processes.Observe(child.Identity).Outcome);
            Assert.Empty((await artifacts.Store.RecoverAsync(artifacts.RunId, TestContext.Current.CancellationToken)).Records);
            artifacts.MarkAssertionsPassed();
        }
        finally { await CloseActualChildAsync(runtime.Processes, child.Identity); }
    }

    [Fact(Timeout = 120_000)]
    public async Task Precancelled_unknown_wait_honors_caller_before_query_and_never_reports_success()
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        await using var fresh = new ProcessControl();
        var child = runtime.StartBoundedChild();
        try
        {
            var live = runtime.Processes.Observe(child.Identity);
            Assert.True(live.Outcome == ProcessControlOutcome.LiveOwned, live.Reason);
            Assert.Equal(ProcessControlOutcome.IdentityUnknown, fresh.Observe(child.Identity).Outcome);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ColdRecoveryOrchestrator.WaitForActualExitAsync(artifacts, fresh, child.Identity, cancelled.Token));
            using var poll = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(artifacts.Root, "cold-child-exit-poll.json"), TestContext.Current.CancellationToken));
            Assert.Empty(poll.RootElement.GetProperty("observations").EnumerateArray());
            Assert.Empty(fresh.GetAllOwnedIdentities());
            Assert.Equal(ProcessControlOutcome.LiveOwned, runtime.Processes.Observe(child.Identity).Outcome);
            Assert.Empty((await artifacts.Store.RecoverAsync(artifacts.RunId, TestContext.Current.CancellationToken)).Records);
            artifacts.MarkAssertionsPassed();
        }
        finally { await CloseActualChildAsync(runtime.Processes, child.Identity); }
    }

    private static async Task CloseActualChildAsync(ProcessControl original, ProcessIdentity identity)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var observed = original.Observe(identity);
        if (observed.Outcome == ProcessControlOutcome.LiveOwned)
        {
            var settled = await original.QuiesceAsync(identity, TimeSpan.FromSeconds(1), cleanup.Token);
            Assert.True(settled.Outcome is ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited, settled.Reason);
        }
        Assert.Equal(ProcessControlOutcome.AlreadyExited, original.Observe(identity).Outcome);
    }
}
