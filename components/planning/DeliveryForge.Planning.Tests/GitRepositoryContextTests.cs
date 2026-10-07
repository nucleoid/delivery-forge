using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
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
        Assert.Equal(context.Commit, typeof(RepositoryFile).GetProperty("Commit")?.GetValue(exact));
        Assert.Equal(context.Tree, typeof(RepositoryFile).GetProperty("Tree")?.GetValue(exact));
    }

    [Fact]
    public async Task Verified_imported_context_is_bound_to_the_exact_repository_commit_and_tree()
    {
        InitializeRepository();
        var reader = new GitRepositoryContextReader();
        var first = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);
        var file = await reader.ReadFileAsync(first, "tracked.txt", TestContext.Current.CancellationToken);
        var digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(file.Bytes))}";
        var imported = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry("repository", "git:tracked.txt", "summary", "sha256:" + new string('a', 64), DateTimeOffset.UnixEpoch, CheckoutDigest: digest),
            file);
        var digestConflict = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry("repository", "git:tracked.txt", "summary", null, DateTimeOffset.UnixEpoch, CheckoutDigest: "sha256:" + new string('b', 64)),
            file);
        var pathConflict = ImportedContextVerifier.VerifyAgainst(
            new ImportedContextEntry("repository", "git:other.txt", "summary", null, DateTimeOffset.UnixEpoch, CheckoutDigest: digest),
            file);

        Assert.Equal(CheckoutVerification.Verified, imported.CheckoutVerification);
        Assert.Equal(CheckoutVerification.Conflict, digestConflict.CheckoutVerification);
        Assert.Equal(CheckoutVerification.Conflict, pathConflict.CheckoutVerification);
        Assert.Equal("sha256:" + new string('a', 64), imported.Digest);

        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "second commit bytes");
        Run("git", "add tracked.txt");
        Run("git", "commit -q -m second");
        var second = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);
        var request = new PlanningRequest("owner/repo", "#4", "implement", "Bind repository identity", ["planning"], ["execution"], ["identity is exact"], "implement");
        EvidenceItem[] evidence = [new(EvidenceSourceKind.Repository, "git:tracked.txt", digest, DateTimeOffset.UnixEpoch, [])];
        var envelope = new ImportedContextEnvelope("1.0.0", [imported], [], []);
        var assessment = IntakePlanner.Assess(request, evidence, envelope, EvidenceRequirement.Required);
        var draft = new PlanDraft(
            request,
            second,
            evidence,
            [new("tracked.txt", "content", "update")],
            [new("root", [], "repository context is exact")],
            [new("test", "dotnet test", "passes")],
            new("additive", "none", "none", "none", "none", "none", "tests", "revert", []),
            [],
            assessment);

        Assert.Contains(assessment.Limitations, limitation => limitation.Contains(first.Commit, StringComparison.Ordinal) && limitation.Contains(first.Tree, StringComparison.Ordinal));
        var error = Assert.Throws<PlanningException>(() => PlanFreezer.Freeze(draft, "revision-identity", DateTimeOffset.UnixEpoch));
        Assert.Contains("repository identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_clone_object_read_never_contacts_the_promisor_remote()
    {
        var source = Path.Combine(Path.GetTempPath(), $"delivery-forge-planning-source-{Guid.NewGuid():N}");
        var bare = Path.Combine(Path.GetTempPath(), $"delivery-forge-planning-bare-{Guid.NewGuid():N}.git");
        Directory.CreateDirectory(source);
        try
        {
            RunIn(source, "git", "init -q");
            RunIn(source, "git", "config user.email planning@example.invalid");
            RunIn(source, "git", "config user.name Planning Tests");
            File.WriteAllText(Path.Combine(source, "tracked.txt"), new string('x', 8192));
            RunIn(source, "git", "add tracked.txt");
            RunIn(source, "git", "commit -q -m initial");
            RunIn(source, "git", $"clone -q --bare . {bare}");
            RunIn(bare, "git", "config uploadpack.allowFilter true");
            Directory.CreateDirectory(_root);
            RunIn(_root, "git", $"clone -q --filter=blob:none --no-checkout file://{bare} .");

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            RunIn(_root, "git", $"remote set-url origin http://127.0.0.1:{port}/repo.git");
            var connection = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
            var reader = new GitRepositoryContextReader();

            var error = await Assert.ThrowsAsync<PlanningException>(() =>
                reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken));
            Assert.Contains("partial clone", error.Message, StringComparison.OrdinalIgnoreCase);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(connection.IsCompleted, "Exact object reads must reject before contacting a promisor remote.");
        }
        finally
        {
            DeleteTree(source);
            DeleteTree(bare);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Effective_included_and_worktree_promisor_configuration_is_rejected(bool useWorktreeConfig)
    {
        InitializeRepository();
        if (useWorktreeConfig)
        {
            Run("git", "config extensions.worktreeConfig true");
            Run("git", "config --worktree remote.origin.promisor true");
        }
        else
        {
            File.WriteAllText(Path.Combine(_root, ".git", "promisor.cfg"), "[remote \"origin\"]\n\tpromisor = true\n");
            Run("git", "config include.path promisor.cfg");
        }

        var reader = new GitRepositoryContextReader();

        var error = await Assert.ThrowsAsync<PlanningException>(() =>
            reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken));
        Assert.Contains("partial clone", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Parent_context_records_gitlinks_without_entering_a_promisor_submodule()
    {
        var source = Path.Combine(Path.GetTempPath(), $"delivery-forge-planning-submodule-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        try
        {
            RunIn(source, "git", "init -q");
            RunIn(source, "git", "config user.email planning@example.invalid");
            RunIn(source, "git", "config user.name Planning Tests");
            File.WriteAllText(Path.Combine(source, "dependency.txt"), "dependency bytes");
            RunIn(source, "git", "add dependency.txt");
            RunIn(source, "git", "commit -q -m dependency");
            InitializeRepository();
            Run("git", $"-c protocol.file.allow=always submodule add -q {source} dependency");
            Run("git", "commit -q -am submodule");

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            RunIn(Path.Combine(_root, "dependency"), "git", "config remote.origin.promisor true");
            RunIn(Path.Combine(_root, "dependency"), "git", $"remote set-url origin http://127.0.0.1:{port}/dependency.git");
            var connection = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();

            var context = await new GitRepositoryContextReader().ReadAsync(
                _root,
                "HEAD",
                TestContext.Current.CancellationToken);

            Assert.Contains(context.Submodules, item => item.EndsWith(" dependency", StringComparison.Ordinal));
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(connection.IsCompleted, "Reading parent context must not launch Git or contact a remote in submodules.");
        }
        finally
        {
            DeleteTree(source);
        }
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
    public async Task Repository_file_reader_rejects_a_context_with_a_forged_reader_binding()
    {
        InitializeRepository();
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);

        var forged = ForgeContext(context, tree: new string('f', 40));
        var error = await Assert.ThrowsAsync<PlanningException>(() =>
            reader.ReadFileAsync(forged, "tracked.txt", TestContext.Current.CancellationToken));

        Assert.Contains("issued", error.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task Parent_traversal_after_expanded_directory_symlink_is_an_escape()
    {
        InitializeRepository();
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "outside"), "still inside lexically");
        Run("git", "add outside");
        AddCommittedSymlink("sub/d", "..");
        AddCommittedSymlink("sub/x", "d/../../outside");
        Run("git", "commit -q -m symlink-parent-traversal");
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);

        var file = await reader.ReadFileAsync(context, "sub/x", TestContext.Current.CancellationToken);

        Assert.Equal(SymlinkResolution.Escapes, file.SymlinkResolution);
        Assert.True(file.EscapesWorktree);
    }

    [Fact]
    public async Task Repeated_symlink_path_cannot_hide_a_later_posix_escape_as_a_cycle()
    {
        InitializeRepository();
        File.WriteAllText(Path.Combine(_root, "outside"), "inside only before traversal");
        Run("git", "add outside");
        AddCommittedSymlink("d", ".");
        AddCommittedSymlink("x", "d/d/../outside");
        Run("git", "commit -q -m repeated-symlink-path");
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);

        var file = await reader.ReadFileAsync(context, "x", TestContext.Current.CancellationToken);

        Assert.Equal(SymlinkResolution.Escapes, file.SymlinkResolution);
        Assert.True(file.EscapesWorktree);
    }

    [Fact]
    public async Task Windows_text_collapse_escape_wins_over_an_in_tree_posix_resolution()
    {
        InitializeRepository();
        Directory.CreateDirectory(Path.Combine(_root, "sub", "inner"));
        File.WriteAllText(Path.Combine(_root, "sub", "inner", ".keep"), "kept");
        File.WriteAllText(Path.Combine(_root, "outside"), "root file");
        Run("git", "add sub/inner/.keep outside");
        AddCommittedSymlink("d", "sub/inner");
        AddCommittedSymlink("x", "d/../../outside");
        Run("git", "commit -q -m windows-text-collapse");
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);

        var file = await reader.ReadFileAsync(context, "x", TestContext.Current.CancellationToken);

        Assert.Equal(SymlinkResolution.Escapes, file.SymlinkResolution);
        Assert.True(file.EscapesWorktree);
    }

    [Fact]
    public async Task Symlink_segment_work_is_globally_bounded_and_fails_conservatively()
    {
        InitializeRepository();
        Directory.CreateDirectory(Path.Combine(_root, "dir"));
        File.WriteAllText(Path.Combine(_root, "dir", ".keep"), "kept");
        Run("git", "add dir/.keep");
        var target = string.Join('/', Enumerable.Repeat("dir/..", 5_000)) + "/tracked.txt";
        AddCommittedSymlink("bounded", target);
        Run("git", "commit -q -m bounded-symlink");
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var file = await reader.ReadFileAsync(context, "bounded", deadline.Token);

        Assert.True(file.EscapesWorktree);
        Assert.NotEqual(SymlinkResolution.InTree, file.SymlinkResolution);
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
        Assert.True(cycle.EscapesWorktree);
        Assert.True(missing.EscapesWorktree);
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

    [Fact]
    public async Task Exact_git_reads_ignore_replace_refs()
    {
        InitializeRepository();
        File.WriteAllText(Path.Combine(_root, "replacement.txt"), "replacement bytes");
        var original = RunCapture("git", "rev-parse HEAD:tracked.txt");
        var replacement = RunCapture("git", "hash-object -w replacement.txt");
        Run("git", $"replace {original} {replacement}");

        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);
        var file = await reader.ReadFileAsync(context, "tracked.txt", TestContext.Current.CancellationToken);

        Assert.Equal(original, file.ObjectId);
        Assert.Equal("committed bytes", Encoding.UTF8.GetString(file.Bytes));
    }

    [Fact]
    public async Task Exact_git_reads_ignore_inherited_object_index_common_dir_and_config_routing()
    {
        InitializeRepository();
        var names = new[]
        {
            "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_INDEX_FILE", "GIT_COMMON_DIR",
            "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0"
        };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            var invalid = Path.Combine(_root, "inherited-routing-must-not-be-used");
            Environment.SetEnvironmentVariable("GIT_OBJECT_DIRECTORY", invalid);
            Environment.SetEnvironmentVariable("GIT_ALTERNATE_OBJECT_DIRECTORIES", invalid);
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", invalid);
            Environment.SetEnvironmentVariable("GIT_COMMON_DIR", invalid);
            Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", "'core.repositoryformatversion=999'");
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "core.repositoryformatversion");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "999");

            var reader = new GitRepositoryContextReader();
            var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);
            var file = await reader.ReadFileAsync(context, "tracked.txt", TestContext.Current.CancellationToken);

            Assert.False(context.Dirty);
            Assert.Equal("committed bytes", Encoding.UTF8.GetString(file.Bytes));
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    [Fact]
    public async Task Exact_git_paths_are_always_literal_pathspecs()
    {
        if (OperatingSystem.IsWindows()) return;

        InitializeRepository();
        File.WriteAllText(Path.Combine(_root, "*.txt"), "literal wildcard");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "wildcard bait");
        Run("git", "add -- *.txt a.txt");
        Run("git", "commit -q -m literal-pathspec");
        var reader = new GitRepositoryContextReader();
        var context = await reader.ReadAsync(_root, "HEAD", TestContext.Current.CancellationToken);

        var file = await reader.ReadFileAsync(context, "*.txt", TestContext.Current.CancellationToken);

        Assert.Equal("literal wildcard", Encoding.UTF8.GetString(file.Bytes));
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

    private static void RunIn(string workingDirectory, string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
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

    private static RepositoryContext ForgeContext(RepositoryContext source, string? tree = null)
    {
        var constructor = Assert.Single(typeof(RepositoryContext).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic));
        return (RepositoryContext)constructor.Invoke(
        [
            source.RepositoryRoot,
            source.RequestedRef,
            source.Commit,
            tree ?? source.Tree,
            source.DetachedHead,
            source.Dirty,
            source.Shallow,
            source.Submodules,
            source.Limitations,
            "sha256:" + new string('0', 64)
        ]);
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
