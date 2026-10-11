using DeliveryForge.Execution.Git;

namespace DeliveryForge.Execution.Tests.Repositories;

public sealed class WriterNamespaceIdentityTests
{
    [Fact(Timeout = 120_000)]
    public async Task Live_cleanup_handle_matches_current_namespace_but_disposed_handle_is_fail_closed()
    {
        await using var repository = await GitFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var area = new TemporaryDirectory();
        var worktree = Path.Combine(area.Path, "assigned");
        var manager = new WorktreeManager();
        await manager.PrepareAsync(repository.Path, worktree, repository.Head, TestContext.Current.CancellationToken);
        using (manager.AcquireWriter(worktree, "same-run", "same-token")) { }
        using var held = manager.RecoverWriterWithDeleteSharing(worktree, "same-run", "same-token");
        var live = held.ObserveCurrentNamespaceIdentity();
        Assert.Equal(WriterNamespaceIdentityOutcome.Match, live.Outcome);
        Assert.NotEmpty(live.Reason);
        held.Dispose();
        var disposed = held.ObserveCurrentNamespaceIdentity();
        Assert.Equal(WriterNamespaceIdentityOutcome.Unknown, disposed.Outcome);
        Assert.NotEmpty(disposed.Reason);
    }

    [Fact(Timeout = 120_000)]
    public async Task Same_path_run_token_and_bytes_do_not_hide_a_replaced_native_file_identity()
    {
        await using var repository = await GitFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var area = new TemporaryDirectory();
        var worktree = Path.Combine(area.Path, "assigned");
        var manager = new WorktreeManager();
        await manager.PrepareAsync(repository.Path, worktree, repository.Head, TestContext.Current.CancellationToken);
        using (manager.AcquireWriter(worktree, "same-run", "same-token")) { }
        using var held = manager.RecoverWriterWithDeleteSharing(worktree, "same-run", "same-token");
        var originalBytes = held.GetDiagnosticBytes().ToArray();
        File.Delete(held.DiagnosticLockPath);
        using var replacement = manager.AcquireWriter(worktree, "same-run", "same-token");
        Assert.Equal(Path.GetFullPath(held.DiagnosticLockPath), Path.GetFullPath(replacement.DiagnosticLockPath));
        Assert.Equal(originalBytes, replacement.GetDiagnosticBytes().ToArray());
        var observed = held.ObserveCurrentNamespaceIdentity();
        Assert.Equal(WriterNamespaceIdentityOutcome.Changed, observed.Outcome);
        Assert.NotEmpty(observed.Reason);
    }
}
