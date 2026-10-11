using System.Text;
using DeliveryForge.Execution;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class HarnessPositiveControlTests
{
    [Fact(Timeout = 120_000)]
    public async Task Real_git_reads_committed_bytes_after_mutable_fixture_changes()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var context = await run.Reader.ReadAsync(run.Checkout, "refs/heads/main", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(run.Checkout, "subject.txt"), "dirty mutable file\n", TestContext.Current.CancellationToken);
        var file = await run.Reader.ReadFileAsync(context, "subject.txt", TestContext.Current.CancellationToken);
        var dirty = await run.Reader.ReadAsync(run.Checkout, "refs/heads/main", TestContext.Current.CancellationToken);

        Assert.Equal("committed fixture\n", Encoding.UTF8.GetString(file.Bytes));
        Assert.Equal(context.Commit, file.Commit);
        Assert.Equal(context.Tree, file.Tree);
        Assert.True(dirty.Dirty);
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, run.IntegratedSource.Commit);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Public_run_store_recovers_a_torn_pointer_from_real_immutable_records()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var first = await run.Store.AppendAsync(run.RunId, "fixture-checkpoint", new { fixture = true, sequence = "before" }, TestContext.Current.CancellationToken);
        var second = await run.Store.AppendAsync(run.RunId, "fixture-checkpoint", new { fixture = true, sequence = "after" }, TestContext.Current.CancellationToken);
        var pointer = Path.Combine(Path.GetDirectoryName(second.Path)!, "current.json");
        await File.WriteAllTextAsync(pointer, "{torn", TestContext.Current.CancellationToken);

        var recovery = await new RunStore(Path.Combine(run.Root, "receipts")).RecoverAsync(run.RunId, TestContext.Current.CancellationToken);

        Assert.True(recovery.PointerWasRecovered);
        Assert.Equal(second.Sha256, recovery.Latest!.Sha256);
        Assert.Equal([first.Sha256, second.Sha256], recovery.Records.Select(record => record.Sha256));
        Assert.False((await run.Store.RecoverAsync(run.RunId, TestContext.Current.CancellationToken)).PointerWasRecovered);
        run.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Stale_process_record_cannot_signal_the_real_owned_child()
    {
        await using var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var child = run.StartBoundedChild();
        var stale = child.Identity with { PlatformStartIdentity = checked(child.Identity.PlatformStartIdentity + 1) };
        var denied = await run.Processes.QuiesceAsync(stale, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(ProcessControlOutcome.IdentityUnknown, denied.Outcome);
        Assert.Equal(ProcessControlOutcome.LiveOwned, run.Processes.Observe(child.Identity).Outcome);
        var stopped = await run.Processes.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(stopped.Outcome is ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited, stopped.Reason);
        Assert.Equal(ProcessControlOutcome.AlreadyExited, run.Processes.Observe(child.Identity).Outcome);
        await run.Store.AppendAsync(run.RunId, "fixture-process-observation", new { fixture = true, child.Identity, denied, stopped }, TestContext.Current.CancellationToken);
        run.MarkAssertionsPassed();
    }
}
