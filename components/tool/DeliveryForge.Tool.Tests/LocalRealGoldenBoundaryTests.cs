using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Coordination;
using DeliveryForge.Publication;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

public sealed class LocalRealGoldenBoundaryTests
{
    [Theory]
    [InlineData("Ready")]
    [InlineData("AcceptedReview")]
    [InlineData("BaselineApproved")]
    [InlineData("Adapter")]
    public void Local_real_selection_accepts_locators_only_and_cannot_import_success(string claim)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-golden-config-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var git = Path.Combine(root, "data-not-executable"); File.WriteAllText(git, "DATA");
        var output = Path.Combine(root, "not-created");
        var config = new TrustedConfiguration("fixture/repo", root, "refs/heads/main", git, LocalRealGolden: new(output));
        var path = Path.Combine(root, "config.json"); File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config));
        using var original = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(config.LocalRealGolden, TrustedConfiguration.Load(path, original).LocalRealGolden);
        var node = JsonNode.Parse(File.ReadAllBytes(path))!; node["LocalRealGolden"]![claim] = true;
        File.WriteAllText(path, node.ToJsonString());
        Assert.ThrowsAny<Exception>(() => TrustedConfiguration.Load(path, original)); Assert.False(Directory.Exists(output));
    }
    [Fact]
    public async Task Pending_actual_local_real_phase_retains_same_original_without_ready_data_or_retry()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "forge-golden-phase-" + Guid.NewGuid().ToString("N"))), new WorktreeManager(), processes);
        var original = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("unit-golden", original, coordinator, () => false, false);
        var pending = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        frame.EnterRoute(); frame.LocalReal.Start(original, () => pending.Task); frame.LeaveRoute(); original.Dispose();
        Assert.False(frame.CloseWhenSettled()); Assert.True(original.HasRetainedUnknown); Assert.Same(pending.Task, frame.LocalReal.Actual);
        var observe = frame.LocalReal.ObserveAsync(original); var incomplete = ToolResult.Incomplete("UNIT_ONLY", "No real pipeline was invoked.");
        pending.SetResult(incomplete); Assert.Same(incomplete, await observe); Assert.True(frame.CloseWhenSettled());
        Assert.Throws<ObjectDisposedException>(original.Check);
    }

    [Fact]
    public void First_closed_journal_file_with_failed_postcheck_retains_durable_uncertainty_and_primary()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-first-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var admission = new ParentEffectAdmission(new object(), _ => true, _ => { });
        var primary = new IOException("Injected actual post-write refusal."); var checks = 0;
        void Current() { original.Check(); if (++checks == 2) throw primary; }
        var journal = new LocalRealGoldenOwner.LocalJournal(root, original, admission, "unit-journal", Current);
        var intent = new PublicationIntent("unit-key", "unit-journal", "unit-revision", PublicationAction.CreateOrUpdatePr,
            new("local-real/no-network", "refs/heads/unit", "refs/heads/base"), new('a', 40), new('b', 40), new('c', 40), PublicationIntentState.Prepared);
        var failure = Assert.Throws<IOException>(() => { _ = journal.AppendAdmittedAsync(intent, original.Token); });
        Assert.Same(primary, failure);
        var file = Assert.Single(Directory.GetFiles(root));
        Assert.Equal("00-intent.json", Path.GetFileName(file));
        // This exclusive open proves the actual first file is already closed.
        using (var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var document = JsonDocument.Parse(read)) Assert.Equal("unit-key", document.RootElement.GetProperty("IdempotencyKey").GetString());
        Assert.False(admission.AllKnownSettled); // durable admission remains uncertain
        Assert.False(original.HasRetainedUnknown); // no invented native/file uncertainty
        Assert.ThrowsAny<Exception>(() => admission.EnterSynchronous("unit-journal", () => true));
        Assert.Single(Directory.GetFiles(root));
    }

    [Fact(Timeout = 10_000)]
    public async Task Actual_self_admitted_journal_prefix_leaves_revocation_free_and_refuses_later_file_birth()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-golden-journal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var gate = new object(); var current = true;
        var admission = new ParentEffectAdmission(gate, _ => current, _ => { });
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var once = 0;
        void Current()
        {
            original.Check(); Assert.False(Monitor.IsEntered(gate));
            if (Interlocked.Exchange(ref once, 1) == 0)
            {
                entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            }
            admission.CheckCurrent("unit-journal");
        }
        var journal = new LocalRealGoldenOwner.LocalJournal(root, original, admission, "unit-journal", Current);
        var intent = new PublicationIntent("unit-key", "unit-journal", "unit-revision", PublicationAction.CreateOrUpdatePr,
            new("local-real/no-network", "refs/heads/unit", "refs/heads/base"), new('a', 40), new('b', 40), new('c', 40), PublicationIntentState.Prepared);
        // This is the exact self-admitted journal call used by the publication
        // route, with a controlled slow synchronous prefix. No review authority
        // or native-product success is manufactured by this file-I/O control.
        var write = Task.Run(() => journal.AppendAdmittedAsync(intent, original.Token), TestContext.Current.CancellationToken);
        Task? revoked = null; Exception? writeFailure = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.False(admission.AllKnownSettled);
            revoked = Task.Run(() => { lock (gate) current = false; }, TestContext.Current.CancellationToken);
            await revoked.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.Set();
            try { await write.WaitAsync(original.Remaining, TestContext.Current.CancellationToken); } catch (Exception error) { writeFailure = error; }
            if (revoked is not null) await revoked.WaitAsync(original.Remaining, TestContext.Current.CancellationToken);
        }
        Assert.IsType<CoordinationUncertainException>(writeFailure);
        Assert.Empty(Directory.GetFiles(root));
    }
}
