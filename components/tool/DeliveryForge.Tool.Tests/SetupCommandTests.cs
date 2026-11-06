using System.Text.Json;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

public sealed class SetupCommandTests
{
    [Fact]
    public async Task Explicit_setup_uses_selected_reference_without_launching_git_or_creating_a_host()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var result = await application.ExecuteAsync(CommandLine.Parse(fixture.Arguments("refs/heads/bug/eight")), original.Token);
        Assert.Equal("COMPLETE", result.Outcome);
        Assert.Equal("SETUP_PREPARED", result.Code);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.RuntimeCertified);
        var setup = Assert.IsType<AutomaticSetupResult>(result.Data);
        var configuration = TrustedConfiguration.Load(setup.ConfigurationPath, original);
        Assert.Equal("refs/heads/bug/eight", configuration.MutableReference);
        Assert.Equal(fixture.Git, configuration.GitExecutable);
        Assert.Equal(fixture.Repository, configuration.RepositoryRoot);
        Assert.Equal("owner/example", configuration.Repository);
        Assert.Equal("plan", configuration.MaximumCeiling);
        Assert.Null(configuration.Coordination);
        Assert.Null(configuration.LocalSingle);
        Assert.Null(configuration.InstalledQuality);
        Assert.Null(configuration.InstalledReview);
        Assert.Empty(application.RetainedExternalCalls);
        Assert.False(Directory.Exists(setup.Selection.StoreRoot));
        Assert.False(Directory.Exists(setup.Selection.WorkerPaths.Worktree));
        Assert.False(Directory.Exists(setup.Selection.WorkerPaths.ResultDirectory));
        Assert.False(setup.RuntimeCertified);
        using var admission = JsonDocument.Parse(File.ReadAllBytes(setup.Selection.InstalledHostAdmission.Path));
        Assert.Equal("DeliveryForgeHostNotAdmitted-v1", admission.RootElement.GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task Template_setup_allocates_new_paths_without_raising_a_plan_only_ceiling()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var first = Assert.IsType<AutomaticSetupResult>((await application.ExecuteAsync(CommandLine.Parse(fixture.Arguments("refs/heads/bug/eight")), original.Token)).Data);
        var firstConfig = File.ReadAllBytes(first.ConfigurationPath);
        Directory.CreateDirectory(first.Selection.StoreRoot);
        var evidence = Path.Combine(first.Selection.StoreRoot, "failed-run-evidence.txt");
        File.WriteAllText(evidence, "preserved original evidence");
        var result = await application.ExecuteAsync(CommandLine.Parse(["setup", "--config", first.ConfigurationPath, "--state-root", fixture.State]), original.Token);
        var next = Assert.IsType<AutomaticSetupResult>(result.Data);
        var configuration = TrustedConfiguration.Load(next.ConfigurationPath, original);
        Assert.Equal("SETUP_PREPARED", result.Code);
        Assert.Equal("plan", configuration.MaximumCeiling);
        Assert.Equal("refs/heads/bug/eight", configuration.MutableReference);
        Assert.Equal(first.RepositoryKey, next.RepositoryKey);
        Assert.NotEqual(first.RunDirectory, next.RunDirectory);
        Assert.Equal(first.Selection.InstalledHostAdmission, next.Selection.InstalledHostAdmission);
        Assert.False(Directory.Exists(next.Selection.StoreRoot));
        Assert.Equal("preserved original evidence", File.ReadAllText(evidence));
        Assert.Equal(firstConfig, File.ReadAllBytes(first.ConfigurationPath));
        Assert.Empty(application.RetainedExternalCalls);
        Assert.False(result.RuntimeCertified);
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("setup --repo /repo")]
    [InlineData("setup --repository owner/example")]
    [InlineData("setup --repo /repo --repository owner/example --dry-run")]
    [InlineData("setup --config /template --dry-run")]
    [InlineData("setup --config /template --repo /repo")]
    [InlineData("setup --config /template --repository owner/other")]
    [InlineData("setup --config /template --git /git")]
    [InlineData("setup --config /template --reference refs/heads/other")]
    public void Parser_refuses_ambiguous_or_ignored_setup_options(string arguments)
        => Assert.Throws<ToolUsageException>(() => CommandLine.Parse(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public async Task Invalid_explicit_reference_fails_before_setup_output_or_external_calls()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse(fixture.Arguments("main")), original.Token));
        Assert.False(Directory.Exists(fixture.State));
        Assert.Empty(application.RetainedExternalCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Repository));
    }

    [Fact]
    public async Task Missing_selected_git_fails_before_setup_output_or_external_calls()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var arguments = fixture.Arguments("refs/heads/main");
        arguments[Array.IndexOf(arguments, "--git") + 1] = Path.Combine(fixture.Root, "missing-git-data");
        var error = await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse(arguments), original.Token));
        Assert.Equal("Selected Git executable is absent.", error.Message);
        Assert.False(Directory.Exists(fixture.State));
        Assert.Empty(application.RetainedExternalCalls);
    }

    [Fact]
    public async Task Setup_overlap_failure_preserves_the_repository_and_creates_no_control_files()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var arguments = fixture.Arguments("refs/heads/main");
        arguments[Array.IndexOf(arguments, "--state-root") + 1] = Path.Combine(fixture.Repository, "state");
        await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse(arguments), original.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Repository));
        Assert.Empty(application.RetainedExternalCalls);
    }

    [Fact]
    public async Task Direct_application_dry_run_refuses_before_any_setup_effect()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var options = new Dictionary<string, string?> { ["repo"] = fixture.Repository, ["repository"] = "owner/example", ["reference"] = "refs/heads/main", ["git"] = fixture.Git, ["state-root"] = fixture.State, ["dry-run"] = null };
        await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(new(ToolMode.Setup, options), original.Token));
        Assert.False(Directory.Exists(fixture.State));
        Assert.Empty(application.RetainedExternalCalls);
    }

    static ToolLifetime Lifetime() => new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    sealed record Fixture(string Root, string Repository, string Git, string State)
    {
        internal string[] Arguments(string reference) => ["setup", "--repo", Repository, "--repository", "owner/example", "--reference", reference, "--git", Git, "--state-root", State];
        internal static Fixture Create()
        {
            var root = Directory.CreateTempSubdirectory("forge-setup-command-").FullName;
            var repository = Directory.CreateDirectory(Path.Combine(root, "repository")).FullName;
            // A real existing image selected as DATA only. The explicit branch
            // means any attempt to execute it instead of Git is a test failure.
            return new(root, repository, typeof(ToolApplication).Assembly.Location, Path.Combine(root, "state"));
        }
    }
}
