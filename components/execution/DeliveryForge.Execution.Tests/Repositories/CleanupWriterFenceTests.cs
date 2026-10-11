using System.Text;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Execution.Tests.Repositories;

public sealed class CleanupWriterFenceTests
{
    [Fact(Timeout = 120_000)]
    public async Task Cleanup_lease_rejects_competing_public_writers_and_returns_bound_immutable_diagnostics()
    {
        await using var repository = await GitFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var area = new TemporaryDirectory();
        var worktree = Path.Combine(area.Path, "assigned");
        var manager = new WorktreeManager();
        await manager.PrepareAsync(repository.Path, worktree, repository.Head, TestContext.Current.CancellationToken);
        var gitDirectory = (await GitFixture.RunAsync(worktree, TestContext.Current.CancellationToken, "rev-parse", "--absolute-git-dir")).Trim();
        using (manager.AcquireWriter(worktree, "owned-run", "owned-token")) { }
        using var cleanup = manager.RecoverWriterWithDeleteSharing(worktree, "owned-run", "owned-token");
        Assert.Throws<WorktreeBoundaryException>(() => manager.AcquireWriter(worktree, "foreign-run", "foreign-token"));
        Assert.Throws<WorktreeBoundaryException>(() => manager.RecoverWriter(worktree, "owned-run", "owned-token"));
        Assert.Throws<WorktreeBoundaryException>(() => manager.RecoverWriterWithDeleteSharing(worktree, "owned-run", "owned-token"));
        Assert.Equal(Path.GetFullPath(Path.Combine(gitDirectory, "delivery-forge-writer.lock")), Path.GetFullPath(cleanup.DiagnosticLockPath));
        var diagnostics = cleanup.GetDiagnosticBytes();
        Assert.Equal("owned-run\nowned-token\n", Encoding.UTF8.GetString(diagnostics.ToArray()));
        Assert.False(diagnostics is byte[], "A caller must not mutate the owner-held diagnostic byte array.");
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)diagnostics)[0] = 0);
        Assert.Equal("owned-run\nowned-token\n", Encoding.UTF8.GetString(cleanup.GetDiagnosticBytes().ToArray()));
        cleanup.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cleanup.GetDiagnosticBytes());
    }

    [Fact(Timeout = 120_000)]
    public async Task Ordinary_writer_retains_no_delete_sharing_while_cleanup_lease_permits_checked_unlink_mechanics()
    {
        await using var repository = await GitFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var area = new TemporaryDirectory();
        var worktree = Path.Combine(area.Path, "assigned");
        var manager = new WorktreeManager();
        await manager.PrepareAsync(repository.Path, worktree, repository.Head, TestContext.Current.CancellationToken);
        var gitDirectory = (await GitFixture.RunAsync(worktree, TestContext.Current.CancellationToken, "rev-parse", "--absolute-git-dir")).Trim();
        var path = Path.Combine(gitDirectory, "delivery-forge-writer.lock");
        using (var ordinary = manager.AcquireWriter(worktree, "owned-run", "owned-token"))
        {
            if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => File.Delete(path));
            Assert.True(File.Exists(path));
        }
        using (var ordinary = manager.RecoverWriter(worktree, "owned-run", "owned-token"))
        {
            if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<WorktreeBoundaryException>(() => manager.RecoverWriter(worktree, "owned-run", "owned-token"));
        }
        using var cleanup = manager.RecoverWriterWithDeleteSharing(worktree, "owned-run", "owned-token");
        Assert.Equal("owned-run\nowned-token\n", Encoding.UTF8.GetString(cleanup.GetDiagnosticBytes().ToArray()));
        File.Delete(path); // Actual owned fixture mechanics, not a production deletion policy.
        Assert.False(File.Exists(path));
        Assert.Equal("owned-run\nowned-token\n", Encoding.UTF8.GetString(cleanup.GetDiagnosticBytes().ToArray()));
    }

    [Fact(Timeout = 120_000)]
    public async Task Cleanup_recovery_rejects_actual_mismatched_or_malformed_durable_identity()
    {
        await using var repository = await GitFixture.CreateAsync(TestContext.Current.CancellationToken);
        using var area = new TemporaryDirectory();
        var worktree = Path.Combine(area.Path, "assigned");
        var manager = new WorktreeManager();
        await manager.PrepareAsync(repository.Path, worktree, repository.Head, TestContext.Current.CancellationToken);
        var gitDirectory = (await GitFixture.RunAsync(worktree, TestContext.Current.CancellationToken, "rev-parse", "--absolute-git-dir")).Trim();
        using (manager.AcquireWriter(worktree, "owned-run", "owned-token")) { }
        Assert.Throws<WorktreeBoundaryException>(() => manager.RecoverWriter(worktree, "owned-run", "wrong-token"));
        Assert.Throws<WorktreeBoundaryException>(() => manager.RecoverWriterWithDeleteSharing(worktree, "owned-run", "wrong-token"));
        Assert.Throws<WorktreeBoundaryException>(() => manager.RecoverWriterWithDeleteSharing(worktree, "wrong-run", "owned-token"));
        var path = Path.Combine(gitDirectory, "delivery-forge-writer.lock");
        await File.AppendAllTextAsync(path, "unexpected-third-line\n", TestContext.Current.CancellationToken);
        Assert.Throws<WorktreeBoundaryException>(() => manager.RecoverWriter(worktree, "owned-run", "owned-token"));
        Assert.Throws<WorktreeBoundaryException>(() => manager.RecoverWriterWithDeleteSharing(worktree, "owned-run", "owned-token"));
        Assert.EndsWith("unexpected-third-line\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        var oversizedRunId = new string('x', 8193);
        using (var oversized = manager.AcquireWriter(repository.Path, oversizedRunId, "token"))
            Assert.Throws<WorktreeBoundaryException>(() => oversized.GetDiagnosticBytes());
        Assert.Throws<WorktreeBoundaryException>(() => manager.RecoverWriterWithDeleteSharing(repository.Path, oversizedRunId, "token"));
    }
}

