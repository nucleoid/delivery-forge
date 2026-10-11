using DeliveryForge.Execution.Git;

namespace DeliveryForge.Execution.Tests.Repositories;

public sealed class WriterNamespaceIdentityTests
{
    [Fact(Timeout = 120_000)]
    public async Task No_follow_observation_rejects_same_held_object_symlink_missing_path_and_directory()
    {
        var repository = await GitFixture.CreateAsync(TestContext.Current.CancellationToken);
        var area = new TemporaryDirectory();
        var worktree = Path.Combine(area.Path, "assigned");
        var manager = new WorktreeManager();
        WriterLease? held = null;
        var assertionsPassed = false;
        var linkCreated = false;
        var capability = "not yet observed";
        string? retainedOriginal = null;
        try
        {
            await manager.PrepareAsync(repository.Path, worktree, repository.Head, TestContext.Current.CancellationToken);
            using (manager.AcquireWriter(worktree, "same-run", "same-token")) { }
            held = manager.RecoverWriterWithDeleteSharing(worktree, "same-run", "same-token");
            retainedOriginal = held.DiagnosticLockPath + ".owned-retained-" + Guid.NewGuid().ToString("N");
            Assert.False(File.Exists(retainedOriginal));
            File.Move(held.DiagnosticLockPath, retainedOriginal);
            try
            {
                File.CreateSymbolicLink(held.DiagnosticLockPath, retainedOriginal);
                linkCreated = true;
                capability = "actual file symlink created";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                capability = "INCOMPLETE: " + exception.GetType().Name + ": " + exception.Message;
                throw new InvalidOperationException("INCOMPLETE actual file-symlink capability; owned fixture retained: " + repository.Path, exception);
            }
            Assert.True((File.GetAttributes(held.DiagnosticLockPath) & FileAttributes.ReparsePoint) != 0);
            var redirected = held.ObserveCurrentNamespaceIdentity();
            Assert.NotEqual(WriterNamespaceIdentityOutcome.Match, redirected.Outcome);
            Assert.NotEmpty(redirected.Reason);
            // Only this exact link was created by the current fixture; never delete its target.
            File.Delete(held.DiagnosticLockPath);
            linkCreated = false;
            Assert.True(File.Exists(retainedOriginal));
            var missing = held.ObserveCurrentNamespaceIdentity();
            Assert.NotEqual(WriterNamespaceIdentityOutcome.Match, missing.Outcome);
            Assert.NotEmpty(missing.Reason);
            Directory.CreateDirectory(held.DiagnosticLockPath);
            var directory = held.ObserveCurrentNamespaceIdentity();
            Assert.NotEqual(WriterNamespaceIdentityOutcome.Match, directory.Outcome);
            Assert.NotEmpty(directory.Reason);
            Assert.True(File.Exists(retainedOriginal));
            assertionsPassed = true;
        }
        finally
        {
            held?.Dispose();
            if (assertionsPassed)
            {
                area.Dispose();
                await repository.DisposeAsync();
            }
            else
            {
                // Retain the actual failure state instead of making an unsupported link a passing control.
                await using var diagnostic = new FileStream(Path.Combine(repository.Path, "no-follow-retained.json"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await System.Text.Json.JsonSerializer.SerializeAsync(diagnostic, new
                {
                    fixture = true, productionCertified = false, repository = repository.Path, runtime = area.Path,
                    worktree, retainedOriginal, linkCreated, capability, retained = true,
                    limitation = "No native-observer behavior or filesystem atomicity certification follows from fixture data."
                }, cancellationToken: CancellationToken.None);
            }
        }
    }

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
