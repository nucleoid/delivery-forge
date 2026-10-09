namespace DeliveryForge.Evidence.Tests;

public sealed class GitRepositoryIdentityReaderTests
{
    [Fact]
    public async Task Reads_exact_head_tree_and_clean_status_from_git()
    {
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(TestEvidence.Commit('b') + Environment.NewLine),
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
                Result(TestEvidence.Commit('c') + Environment.NewLine),
                Result("?? generated.txt" + Environment.NewLine)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var identity = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.False(identity.IsClean);
    }

    private static CommandResult Result(string stdout) =>
        new(0, stdout, string.Empty, false, false, TestEvidence.Time(0), TestEvidence.Time(1));
}
