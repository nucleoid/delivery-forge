namespace DeliveryForge.Evidence.Tests;

public sealed class GitRepositoryIdentityReaderTests
{
    [Fact]
    public async Task Reads_exact_head_tree_and_clean_status_from_git()
    {
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(TestEvidence.Commit('b') + Environment.NewLine),
                Result(TestEvidence.Commit('a') + Environment.NewLine),
                Result(string.Empty),
                Result(TestEvidence.Commit('c') + Environment.NewLine),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var identity = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TestEvidence.Repository(), identity);
    }

    [Fact]
    public async Task Untracked_or_modified_paths_make_the_snapshot_unclean()
    {
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(TestEvidence.Commit('b') + Environment.NewLine),
                Result(TestEvidence.Commit('a') + Environment.NewLine),
                Result(string.Empty),
                Result(TestEvidence.Commit('c') + Environment.NewLine),
                Result("?? generated.txt" + Environment.NewLine)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var identity = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.False(identity.IsClean);
    }

    [Fact]
    public async Task Evaluation_scope_includes_every_tracked_javascript_file_even_when_ignore_files_name_it()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"{TestEvidence.Commit('1')}\t.gitignore\0{TestEvidence.Commit('2')}\t.oxlintignore\0{TestEvidence.Commit('3')}\tgenerated/out.ts\0{TestEvidence.Commit('4')}\tsrc/app.ts\0{TestEvidence.Commit('5')}\tsrc/git-ignored.ts\0{TestEvidence.Commit('6')}\tsrc/tool-ignored.ts\0{TestEvidence.Commit('7')}\tsrc/app.cs\0"),
                Result(string.Empty),
                Result(string.Empty),
                Result(string.Empty),
                Result(string.Empty),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(head, TestContext.Current.CancellationToken);

        Assert.Equal(
            [".gitignore", ".oxlintignore", "generated/out.ts", "src/app.cs", "src/app.ts", "src/git-ignored.ts", "src/tool-ignored.ts"],
            scope.TrackedFiles);
        Assert.Empty(scope.IgnoredFiles);
        Assert.StartsWith("sha256:", scope.InputSetIdentity);
    }

    [Fact]
    public async Task New_inline_suppression_in_a_changed_frozen_blob_is_reported()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result("src/app.ts\0"),
                Result("/* eslint-disable */\nexport const value = 1;\n"),
                Result("export const value = 1;\n")),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(head, TestContext.Current.CancellationToken);

        Assert.Equal(["src/app.ts"], scope.SuppressionViolations);
    }

    [Fact]
    public async Task Visual_studio_ignore_patterns_and_root_vendor_names_cannot_hide_tracked_inputs()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"{TestEvidence.Commit('1')}\t.gitignore\0{TestEvidence.Commit('2')}\tnode_modules/owned.ts\0{TestEvidence.Commit('3')}\tvendor/anti-slop/owned.ts\0"),
                Result(".gitignore\0"),
                Result("export const nodeOwned = true;\n"),
                Result("export const vendorOwned = true;\n")),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(head, TestContext.Current.CancellationToken);

        Assert.Empty(scope.IgnoredFiles);
        Assert.Contains("node_modules/owned.ts", scope.TrackedFiles);
        Assert.Contains("vendor/anti-slop/owned.ts", scope.TrackedFiles);
    }

    private static CommandResult Result(string stdout, int exitCode = 0) =>
        new(exitCode, stdout, string.Empty, false, false, TestEvidence.Time(0), TestEvidence.Time(1));
}
