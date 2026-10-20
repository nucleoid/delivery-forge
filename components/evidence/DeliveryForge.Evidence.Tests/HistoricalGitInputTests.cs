using System.Text.Json;

namespace DeliveryForge.Evidence.Tests;

// Real ordinary-file boundary controls only. These minimal metadata fixtures
// are not accepted Git repositories, native executions or baseline approvals.
public sealed class HistoricalGitInputTests
{
    sealed class Input
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "forge-historical-input-" + Guid.NewGuid().ToString("N"));
        internal readonly RepositoryIdentity Expected = new(new('1', 40), new('2', 40), new('3', 40), true);
        internal string Git => Path.Combine(Root, ".git");
        internal Input(string extraConfig = "")
        {
            Directory.CreateDirectory(Path.Combine(Git, "objects"));
            File.WriteAllText(Path.Combine(Git, "config"), "[core]\nrepositoryformatversion = 0\nbare = false\n" + extraConfig);
            File.WriteAllText(Path.Combine(Git, "HEAD"), new string('2', 40) + "\n");
            File.WriteAllBytes(Path.Combine(Git, "index"), [1, 2, 3]);
        }
    }
    static InstalledToolFiles Files() => new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    [Fact]
    public void Actual_ordinary_input_selects_closed_roles_and_cannot_claim_worker_or_baseline_authority()
    {
        var input = new Input(); var binding = new HistoricalGitInput(Files(), input.Root, input.Expected);
        var configured = binding.Configure(new("selected-absolute-git", ["status", "--porcelain=v1", "--untracked-files=all"], input.Root,
            new Dictionary<string, string> { ["git_dir"] = "/foreign", ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_PARAMETERS"] = "unsafe" }));
        Assert.Equal(Path.GetFullPath(input.Git), configured.Environment!["GIT_DIR"]);
        Assert.Contains("GIT_DIR", configured.Environment.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("git_dir", configured.Environment.Keys, StringComparer.Ordinal);
        Assert.Equal(Path.Combine(input.Git, "objects"), configured.Environment["GIT_OBJECT_DIRECTORY"]);
        Assert.Equal(Path.Combine(input.Git, "index"), configured.Environment["GIT_INDEX_FILE"]);
        Assert.Equal(Path.GetFullPath(input.Root), configured.Environment["GIT_WORK_TREE"]);
        Assert.Equal("0", configured.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("", configured.Environment["GIT_CONFIG_PARAMETERS"]);
        Assert.Contains("core.fsmonitor=false", configured.Arguments);
        Assert.Contains("--no-optional-locks", configured.Arguments);
        Assert.Contains("--untracked-files=all", configured.Arguments);
        using var data = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(binding.Data));
        Assert.False(data.RootElement.GetProperty("ProvisionedWorker").GetBoolean());
        Assert.False(data.RootElement.GetProperty("WriterAuthority").GetBoolean());
        Assert.False(data.RootElement.GetProperty("BaselineAuthority").GetBoolean());
        binding.Require(input.Root, input.Expected);
    }

    [Theory]
    [InlineData("[filter \"unsafe\"]\nclean = command\n")]
    [InlineData("[include]\npath = /foreign\n")]
    [InlineData("[core]\nfsmonitor = command\n")]
    public void Executable_or_included_configuration_refuses_during_input_capture_before_any_native_call(string configuration)
    {
        var input = new Input(configuration);
        Assert.Throws<EvidenceWriteException>(() => new HistoricalGitInput(Files(), input.Root, input.Expected));
    }

    [Fact]
    public void Actual_post_capture_metadata_drift_and_foreign_revision_cannot_reuse_binding()
    {
        var input = new Input(); var binding = new HistoricalGitInput(Files(), input.Root, input.Expected);
        Assert.Throws<EvidenceWriteException>(() => binding.Require(input.Root, input.Expected with { HeadCommit = new('4', 40) }));
        Assert.Throws<EvidenceWriteException>(() => binding.Configure(new("git", ["rev-parse", "HEAD"], Path.GetDirectoryName(input.Root)!)));
        File.WriteAllBytes(Path.Combine(input.Git, "index"), [1, 2, 4]);
        Assert.Throws<EvidenceWriteException>(binding.Check);
    }

    [Theory]
    [InlineData("commondir")]
    [InlineData("config.worktree")]
    [InlineData("shallow")]
    [InlineData("objects/info/alternates")]
    public void External_or_ambiguous_metadata_layout_refuses_before_any_native_call(string role)
    {
        var input = new Input(); var path = Path.Combine(input.Git, role.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "/foreign\n");
        Assert.Throws<EvidenceWriteException>(() => new HistoricalGitInput(Files(), input.Root, input.Expected));
    }

    [Fact]
    public void Linked_marker_is_not_adopted_as_historical_or_parent_provisioning_authority()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-historical-linked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".git"), "gitdir: /foreign\n");
        Assert.Throws<EvidenceWriteException>(() => new HistoricalGitInput(Files(), root,
            new(new('1', 40), new('2', 40), new('3', 40), true)));
    }
}
