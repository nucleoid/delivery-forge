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
    public async Task Evaluation_scope_comes_from_the_exact_tree_and_effective_ignores()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result(".gitignore\0.oxlintignore\0generated/out.ts\0src/app.ts\0src/git-ignored.ts\0src/tool-ignored.ts\0src/app.cs\0"),
                Result("generated\nsrc/git-ignored.ts\n"),
                Result("src/tool-ignored.ts\n")),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(head, TestContext.Current.CancellationToken);

        Assert.Equal(
            [".gitignore", ".oxlintignore", "generated/out.ts", "src/app.cs", "src/app.ts", "src/git-ignored.ts", "src/tool-ignored.ts"],
            scope.TrackedFiles);
        Assert.Equal(
            ["generated/out.ts", "src/git-ignored.ts", "src/tool-ignored.ts"],
            scope.IgnoredFiles.Order(StringComparer.Ordinal));
    }

    private static CommandResult Result(string stdout, int exitCode = 0) =>
        new(exitCode, stdout, string.Empty, false, false, TestEvidence.Time(0), TestEvidence.Time(1));
}
