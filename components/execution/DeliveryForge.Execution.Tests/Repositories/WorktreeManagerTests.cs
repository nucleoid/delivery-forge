using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Execution.Tests.Repositories;

public sealed class WorktreeManagerTests
{
    [Fact]
    public void Rejects_parent_checkout_nested_repository_and_symlink_escape()
    {
        using var directory = new TemporaryDirectory();
        var parent = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "parent")).FullName;
        Directory.CreateDirectory(System.IO.Path.Combine(parent, ".git"));
        var manager = new WorktreeManager();

        Assert.Throws<WorktreeBoundaryException>(() => manager.ValidateDestination(parent, parent));

        var nested = Directory.CreateDirectory(System.IO.Path.Combine(parent, "nested")).FullName;
        Directory.CreateDirectory(System.IO.Path.Combine(nested, ".git"));
        Assert.Throws<WorktreeBoundaryException>(() => manager.ValidateDestination(parent, nested));

        var otherRepository = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "other-repository"));
        Directory.CreateDirectory(System.IO.Path.Combine(otherRepository.FullName, ".git"));
        Assert.Throws<WorktreeBoundaryException>(() => manager.ValidateDestination(
            parent, System.IO.Path.Combine(otherRepository.FullName, "empty-worker")));

        var outside = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "outside")).FullName;
        var link = System.IO.Path.Combine(directory.Path, "link");
        Directory.CreateSymbolicLink(link, outside);
        Assert.Throws<WorktreeBoundaryException>(() => manager.ValidateDestination(parent, System.IO.Path.Combine(link, "worker")));
    }

    [Fact]
    public async Task Captures_only_a_clean_committed_head_and_tree()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        var manager = new WorktreeManager();
        var clean = await manager.CaptureCommittedRevisionAsync(repository.Path, cancellationToken);
        Assert.Equal(repository.Head, clean.HeadCommit);
        Assert.False(string.IsNullOrWhiteSpace(clean.TreeId));

        await File.WriteAllTextAsync(System.IO.Path.Combine(repository.Path, "untracked.txt"), "mine", cancellationToken);
        await Assert.ThrowsAsync<WorktreeBoundaryException>(() => manager.CaptureCommittedRevisionAsync(repository.Path, cancellationToken));
    }

    [Fact]
    public void Rejects_nested_repositories_and_out_of_scope_inventory_changes()
    {
        using var directory = new TemporaryDirectory();
        var manager = new WorktreeManager();
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "allowed.txt"), "before");
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "outside.txt"), "before");
        var before = manager.CaptureSnapshot(directory.Path);

        File.WriteAllText(System.IO.Path.Combine(directory.Path, "allowed.txt"), "after");
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "outside.txt"), "after");
        var after = manager.CaptureSnapshot(directory.Path);
        Assert.Throws<WorktreeBoundaryException>(() => manager.EnsureAllowedChanges(before, after, ["allowed.txt"], []));

        var nested = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "vendor"));
        File.WriteAllText(System.IO.Path.Combine(nested.FullName, ".git"), "gitdir: elsewhere");
        Assert.Throws<WorktreeBoundaryException>(() => manager.CaptureSnapshot(directory.Path));
    }

    [Fact]
    public async Task Enforces_one_writer_per_worktree()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        var manager = new WorktreeManager();
        using var writer = manager.AcquireWriter(repository.Path, "run", "writer-one");
        Assert.Throws<WorktreeBoundaryException>(() => manager.AcquireWriter(repository.Path, "run", "writer-two"));
    }

    [Fact]
    public async Task Recovers_only_the_exact_durable_writer_identity_after_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        var manager = new WorktreeManager();

        using (manager.AcquireWriter(repository.Path, "run", "token")) { }
        using (manager.RecoverWriter(repository.Path, "run", "token")) { }
        Assert.Throws<WorktreeBoundaryException>(() => manager.RecoverWriter(repository.Path, "run", "other"));
    }

    [Fact]
    public async Task Windows_short_path_alias_matches_the_git_reported_worktree_root()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await GitFixture.CreateAsync(cancellationToken);
        var shortPath = WindowsPathAlias.TryGetShortPath(repository.Path);
        if (shortPath is null || string.Equals(shortPath, repository.Path, StringComparison.OrdinalIgnoreCase)) return;

        var revision = await new WorktreeManager().CaptureCommittedRevisionAsync(shortPath, cancellationToken);

        Assert.Equal(repository.Head, revision.HeadCommit);
    }
}
