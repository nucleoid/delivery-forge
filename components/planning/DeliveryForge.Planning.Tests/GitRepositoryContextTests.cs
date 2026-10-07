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
        AddCommittedSymlink("escape", "../outside");
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

    [Fact]
    public async Task Oversized_exact_blob_fails_within_the_operation_deadline()
    {
        InitializeRepository();
        await File.WriteAllBytesAsync(Path.Combine(_root, "large.bin"), new byte[17 * 1024 * 1024], TestContext.Current.CancellationToken);
        Run("git", "add large.bin");
        Run("git", "commit -q -m large");
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<PlanningException>(() => reader.ReadFileAsync(context, "large.bin", deadline.Token));
        Assert.Contains("bound", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chained_committed_symlinks_detect_escape_with_portable_slash_semantics()
    {
        InitializeRepository();
        AddCommittedSymlink("d", "..");
        AddCommittedSymlink("x", "d/outside");
        Run("git", "commit -q -m chained-symlinks");
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);

        var file = await reader.ReadFileAsync(context, "x", TestContext.Current.CancellationToken);

        Assert.True(file.IsSymlink);
        Assert.True(file.EscapesWorktree);
    }

    [Fact]
    public async Task Exact_symlink_resolution_reports_cycles_missing_targets_and_windows_separators()
    {
        InitializeRepository();
        AddCommittedSymlink("a", "b");
        AddCommittedSymlink("b", "a");
        AddCommittedSymlink("missing", "not-present");
        AddCommittedSymlink("windows-escape", "..\\outside");
        Run("git", "commit -q -m symlink-dispositions");
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);

        var cycle = await reader.ReadFileAsync(context, "a", TestContext.Current.CancellationToken);
        var missing = await reader.ReadFileAsync(context, "missing", TestContext.Current.CancellationToken);
        var windowsEscape = await reader.ReadFileAsync(context, "windows-escape", TestContext.Current.CancellationToken);

        Assert.Equal(SymlinkResolution.Cycle, cycle.SymlinkResolution);
        Assert.Equal(SymlinkResolution.Missing, missing.SymlinkResolution);
        Assert.Equal(SymlinkResolution.Escapes, windowsEscape.SymlinkResolution);
        Assert.True(windowsEscape.EscapesWorktree);
    }

    [Fact]
    public async Task Exact_git_reads_ignore_inherited_repository_routing_variables()
    {
        InitializeRepository();
        var previousDirectory = Environment.GetEnvironmentVariable("GIT_DIR");
        var previousWorktree = Environment.GetEnvironmentVariable("GIT_WORK_TREE");
        try
        {
            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(_root, "not-the-repository"));
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", Path.Combine(_root, "not-the-worktree"));

            var reader = new GitRepositoryContextReader();
            var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);
            var file = await reader.ReadFileAsync(context, "tracked.txt", TestContext.Current.CancellationToken);

            Assert.Equal("committed bytes", Encoding.UTF8.GetString(file.Bytes));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_DIR", previousDirectory);
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", previousWorktree);
        }
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
        using var process = Process.Start(CreateStartInfo(fileName, arguments))!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private string RunCapture(string fileName, string arguments)
    {
        using var process = Process.Start(CreateStartInfo(fileName, arguments))!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    private ProcessStartInfo CreateStartInfo(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = _root,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        return startInfo;
    }

    private void AddCommittedSymlink(string path, string target)
    {
        var input = Path.Combine(_root, $"symlink-target-{Guid.NewGuid():N}");
        File.WriteAllText(input, target);
        var objectId = RunCapture("git", $"hash-object -w {Path.GetFileName(input)}");
        File.Delete(input);
        Run("git", $"update-index --add --cacheinfo 120000,{objectId},{path}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(directory, FileAttributes.Directory);
            }
            Directory.Delete(_root, recursive: true);
        }
    }
}
