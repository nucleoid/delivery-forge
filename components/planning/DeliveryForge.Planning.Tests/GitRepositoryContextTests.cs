using System.Diagnostics;
using System.Text;

namespace DeliveryForge.Planning.Tests;

public sealed class GitRepositoryContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"delivery-forge-planning-{Guid.NewGuid():N}");

    [Fact]
    public async Task Reads_exact_objects_separately_from_mutable_worktree_observations()
    {
        InitializeRepository();
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "dirty bytes");

        var exact = await reader.ReadFileAsync(context, "tracked.txt", TestContext.Current.CancellationToken);
        var observed = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);

        Assert.Equal("committed bytes", Encoding.UTF8.GetString(exact.Bytes));
        Assert.False(context.Dirty);
        Assert.True(observed.Dirty);
        Assert.Matches("^[0-9a-f]{40}$", context.Commit);
        Assert.Matches("^[0-9a-f]{40}$", context.Tree);
    }

    [Fact]
    public async Task Detached_head_and_symlink_escape_are_reported_honestly()
    {
        InitializeRepository();
        Run("git", "checkout --detach HEAD");
        File.CreateSymbolicLink(Path.Combine(_root, "escape"), "../outside");
        Run("git", "add escape");
        Run("git", "commit -m symlink");

        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);
        var file = await reader.ReadFileAsync(context, "escape", TestContext.Current.CancellationToken);

        Assert.True(context.DetachedHead);
        Assert.True(file.IsSymlink);
        Assert.True(file.EscapesWorktree);
    }

    [Fact]
    public async Task Missing_ref_or_object_is_an_actionable_failure()
    {
        InitializeRepository();
        var reader = new GitRepositoryContextReader();
        var error = await Assert.ThrowsAsync<PlanningException>(() =>
            reader.ReadAsync(_root, "refs/heads/missing", TestContext.Current.CancellationToken));
        Assert.Contains("ref", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Generated_conventions_and_path_traversal_are_reported_conservatively()
    {
        InitializeRepository();
        File.WriteAllText(Path.Combine(_root, "Generated.g.cs"), "generated");
        Run("git", "add Generated.g.cs");
        Run("git", "commit -q -m generated");
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);

        var generated = await reader.ReadFileAsync(context, "Generated.g.cs", TestContext.Current.CancellationToken);

        Assert.Equal("generated-by-convention", generated.GenerationClassification);
        await Assert.ThrowsAsync<PlanningException>(() =>
            reader.ReadFileAsync(context, "../outside", TestContext.Current.CancellationToken));
    }

    private void InitializeRepository()
    {
        Directory.CreateDirectory(_root);
        Run("git", "init -q");
        Run("git", "config user.email planning@example.invalid");
        Run("git", "config user.name Planning Tests");
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "committed bytes");
        Run("git", "add tracked.txt");
        Run("git", "commit -q -m initial");
    }

    private void Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = _root,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
