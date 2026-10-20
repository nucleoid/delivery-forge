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
                Result($"100644\t{TestEvidence.Commit('1')}\t.gitignore\0100644\t{TestEvidence.Commit('2')}\t.oxlintignore\0100644\t{TestEvidence.Commit('3')}\tgenerated/out.ts\0100644\t{TestEvidence.Commit('4')}\tsrc/app.ts\0100644\t{TestEvidence.Commit('5')}\tsrc/git-ignored.ts\0100644\t{TestEvidence.Commit('6')}\tsrc/tool-ignored.ts\0100644\t{TestEvidence.Commit('7')}\tsrc/app.cs\0"),
                Result(string.Empty),
                Result(string.Empty),
                Result(string.Empty),
                Result(string.Empty),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

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
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result("src/app.ts\0"),
                Result("/* eslint-disable */\nexport const value = 1;\n"),
                Result("export const value = 1;\n"),
                Result(string.Empty, 128)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(["src/app.ts"], scope.SuppressionViolations);
    }

    [Fact]
    public async Task Existing_or_repositioned_suppression_in_a_changed_file_is_never_grandfathered()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result("src/app.ts\0"),
                Result("export const unsafe = value as unknown as string;\n// eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const old = 1;\n"),
                Result("// eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const old = 1;\n"),
                Result(string.Empty, 128)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(["src/app.ts"], scope.SuppressionViolations);
    }

    [Fact]
    public async Task Ordinary_eslint_or_oxlint_prose_is_not_a_directive()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result("src/app.ts\0"),
                Result("// run oxlint before commit\nexport const eslintName = 'eslint config';\n"),
                Result("export const eslintName = 'old';\n"),
                Result(string.Empty, 128)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Empty(scope.SuppressionViolations!);
    }

    [Fact]
    public async Task Javascript_symlink_is_rejected_and_bound_into_scope_identity()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"120000\t{TestEvidence.Commit('1')}\tsrc/link.ts\0"),
                Result(string.Empty),
                Result(string.Empty, 128)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(["src/link.ts"], scope.InvalidInputs);
        Assert.StartsWith("sha256:", scope.InputSetIdentity);
    }

    [Fact]
    public async Task Visual_studio_ignore_patterns_and_root_vendor_names_cannot_hide_tracked_inputs()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\t.gitignore\0100644\t{TestEvidence.Commit('2')}\tnode_modules/owned.ts\0100644\t{TestEvidence.Commit('3')}\tvendor/anti-slop/owned.ts\0"),
                Result(".gitignore\0"),
                Result(string.Empty, 128)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Empty(scope.IgnoredFiles);
        Assert.Contains("node_modules/owned.ts", scope.TrackedFiles);
        Assert.Contains("vendor/anti-slop/owned.ts", scope.TrackedFiles);
    }

    private static CommandResult Result(string stdout, int exitCode = 0) =>
        new(exitCode, stdout, string.Empty, false, false, TestEvidence.Time(0), TestEvidence.Time(1));
}
