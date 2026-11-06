using System.Text.Json;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

public sealed class AutomaticSetupTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsWindows => OperatingSystem.IsWindows();
    [Theory(Skip = "Actual Windows final-path identity required.", SkipUnless = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_fresh_run_pins_use_the_actual_created_anchor_for_new_and_existing_state(bool existingState)
    {
        var fixture = Fixture.Create();
        var preserved = Path.Combine(fixture.State, "retained-before-setup.DATA");
        if (existingState)
        {
            Directory.CreateDirectory(fixture.State);
            File.WriteAllText(preserved, "preserved state evidence");
        }
        var full = Path.GetFullPath(fixture.State);
        var alias = full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        using var original = Lifetime();
        var result = AutomaticSetup.PreparePlan("owner/example", fixture.Repository, "refs/heads/main", fixture.Git, alias, original);
        var physical = DeliveryForge.Execution.Git.WorktreeManager.WindowsFinalPath(result.RunDirectory);
        // A real alternate Windows namespace spelling requires no junction,
        // short-name setting or privilege. Before this fix the emitted run/pins
        // retain that spelling even after Git reports the physical directory.
        Assert.NotEqual(Path.Combine(alias, "repos", result.RepositoryKey, "runs", result.RunId), physical);
        Assert.Equal(physical, result.RunDirectory);
        Assert.Equal(Path.Combine(physical, "control", "configuration.json"), result.ConfigurationPath);
        Assert.Equal(Path.Combine(physical, "control", "root.json"), result.Root.Path);
        Assert.Equal(Path.Combine(physical, "worktree"), result.Selection.WorkerPaths.Worktree);
        Assert.Equal(Path.Combine(physical, "results"), result.Selection.WorkerPaths.ResultDirectory);
        Assert.Equal(Path.Combine(physical, "store"), result.Selection.StoreRoot);
        Assert.Equal(Path.Combine(physical, "control", "host-unadmitted.json"), result.Selection.InstalledHostAdmission.Path);
        Assert.Equal(result.Configuration.Sha256, BoundedFiles.Sha(File.ReadAllBytes(result.ConfigurationPath)));
        Assert.Equal(result.Root.Sha256, BoundedFiles.Sha(File.ReadAllBytes(result.Root.Path)));
        var configuration = TrustedConfiguration.Load(result.ConfigurationPath, original);
        Assert.Equal(result.Root, configuration.StandaloneRoot);
        Assert.False(Directory.Exists(result.Selection.WorkerPaths.Worktree));
        Assert.False(Directory.Exists(result.Selection.WorkerPaths.ResultDirectory));
        Assert.False(Directory.Exists(result.Selection.StoreRoot));
        Assert.False(Directory.Exists(Path.Combine(physical, "checkpoints")));
        if (existingState) Assert.Equal("preserved state evidence", File.ReadAllText(preserved));
    }

    [Fact]
    public void First_use_materializes_plan_only_configuration_without_creating_worker_paths()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var result = AutomaticSetup.PreparePlan("owner/example", fixture.Repository, "refs/heads/main", fixture.Git, fixture.State, original);
        var config = TrustedConfiguration.Load(result.ConfigurationPath, original);
        Assert.Equal("plan", config.MaximumCeiling);
        Assert.Equal("unverified", config.AdapterVersion);
        Assert.Null(config.Coordination);
        Assert.False(Directory.Exists(Path.Combine(result.RunDirectory, "checkpoints")));
        Assert.Null(config.LocalSingle);
        Assert.False(result.RuntimeCertified);
        Assert.Equal(result.Root, config.StandaloneRoot);
        Assert.Equal(result.Configuration.Sha256, BoundedFiles.Sha(File.ReadAllBytes(result.ConfigurationPath)));
        Assert.Equal(result.Root.Sha256, BoundedFiles.Sha(File.ReadAllBytes(result.Root.Path)));
        using var admission = JsonDocument.Parse(File.ReadAllBytes(result.Selection.InstalledHostAdmission.Path));
        Assert.Equal("DeliveryForgeHostNotAdmitted-v1", admission.RootElement.GetProperty("Kind").GetString());
        Assert.False(admission.RootElement.GetProperty("RuntimeCertified").GetBoolean());
        Assert.False(Directory.Exists(result.Selection.StoreRoot));
        Assert.False(Directory.Exists(result.Selection.WorkerPaths.Worktree));
        Assert.False(Directory.Exists(result.Selection.WorkerPaths.ResultDirectory));
        Assert.Empty(result.Selection.WorkerPaths.AllowedPaths);
    }

    [Fact]
    public void Repeated_attempts_preserve_the_old_run_and_allocate_distinct_destinations()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var first = AutomaticSetup.PreparePlan("owner/example", fixture.Repository, "refs/heads/main", fixture.Git, fixture.State, original);
        Directory.CreateDirectory(first.Selection.WorkerPaths.Worktree);
        var retained = Path.Combine(first.Selection.WorkerPaths.Worktree, "unpublished.txt");
        File.WriteAllText(retained, "preserved unpublished work");
        var firstConfig = File.ReadAllBytes(first.ConfigurationPath);
        var second = AutomaticSetup.PreparePlan("owner/example", fixture.Repository, "refs/heads/main", fixture.Git, fixture.State, original);
        Assert.Equal(first.RepositoryKey, second.RepositoryKey);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.NotEqual(first.Selection.StoreRoot, second.Selection.StoreRoot);
        BoundedFiles.Separate(first.RunDirectory, second.RunDirectory);
        Assert.Equal("preserved unpublished work", File.ReadAllText(retained));
        Assert.Equal(firstConfig, File.ReadAllBytes(first.ConfigurationPath));
        Assert.False(Directory.Exists(second.Selection.WorkerPaths.Worktree));
    }

    [Fact]
    public void Same_basename_at_different_checkout_roots_has_distinct_repository_keys()
    {
        var first = Fixture.Create();
        var second = Fixture.Create();
        using var original = Lifetime();
        var a = AutomaticSetup.PreparePlan("owner/example", first.Repository, "refs/heads/main", first.Git, first.State, original);
        var b = AutomaticSetup.PreparePlan("owner/example", second.Repository, "refs/heads/main", second.Git, first.State, original);
        Assert.Equal(Path.GetFileName(first.Repository), Path.GetFileName(second.Repository));
        Assert.NotEqual(a.RepositoryKey, b.RepositoryKey);
        BoundedFiles.Separate(a.RunDirectory, b.RunDirectory);
    }

    [Fact]
    public void Rebase_preserves_policies_admission_plan_expectations_and_ceiling_but_freshens_run_storage()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var first = AutomaticSetup.PreparePlan("owner/example", fixture.Repository, "refs/heads/main", fixture.Git, fixture.State, original);
        var scope = new AdmissionFile(Path.Combine(fixture.Root, "unexecuted-scope-data"), 1, new string('a', 64));
        var originalRoot = first.Selection with
        {
            WorkerPaths = first.Selection.WorkerPaths with { AllowedPaths = ["src/example.cs"], Exclusions = ["secrets/**"] },
            LinuxScopeProfile = scope
        };
        var rootBytes = JsonSerializer.SerializeToUtf8Bytes(originalRoot, BoundedFiles.Json);
        var rootPath = Path.Combine(fixture.Root, "selected-template-root.json");
        BoundedFiles.WriteNew(rootPath, rootBytes, original);
        var oldCheckpoints = Directory.CreateDirectory(Path.Combine(fixture.Root, "old-checkpoints")).FullName;
        var oldEvidence = Path.Combine(oldCheckpoints, "retained-checkpoint.DATA");
        File.WriteAllText(oldEvidence, "preserve prior checkpoint evidence");
        var coordination = new ParentCoordinationConfiguration(new Uri("https://example.invalid/coordination"), "project-data", "principal-data",
            Path.Combine(fixture.Root, "unread-private-key-data"), oldCheckpoints, scope);
        var template = TrustedConfiguration.Load(first.ConfigurationPath, original) with
        {
            MaximumCeiling = "implement",
            OperationSeconds = 600,
            AdapterVersion = "selected-version-data",
            ExpectedBundleIdentity = "bundle-data",
            ExpectedContractIdentity = "contract-data",
            ExpectedWorkItem = "owner/example#8",
            ExpectedPlanCeiling = "plan",
            PolicyFile = Path.Combine(fixture.Root, "policy-data"),
            Coordination = coordination,
            StandaloneRoot = new(rootPath, rootBytes.LongLength, BoundedFiles.Sha(rootBytes))
        };
        var result = AutomaticSetup.Rebase(template, fixture.State, original);
        var rebound = TrustedConfiguration.Load(result.ConfigurationPath, original);
        Assert.Equal(template with
        {
            StandaloneRoot = rebound.StandaloneRoot,
            Coordination = coordination with { CheckpointRoot = Path.Combine(result.RunDirectory, "checkpoints") }
        }, rebound);
        Assert.Equal("plan", rebound.ExpectedPlanCeiling); // A later implement request cannot silently raise the frozen plan.
        Assert.Equal(originalRoot.InstalledHostAdmission, result.Selection.InstalledHostAdmission);
        Assert.Equal(scope, result.Selection.LinuxScopeProfile);
        Assert.Equal(originalRoot.WorkerPaths.AllowedPaths, result.Selection.WorkerPaths.AllowedPaths);
        Assert.Equal(originalRoot.WorkerPaths.Exclusions, result.Selection.WorkerPaths.Exclusions);
        Assert.NotEqual(originalRoot.StoreRoot, result.Selection.StoreRoot);
        var checkpoints = rebound.Coordination!.CheckpointRoot;
        Assert.True(Directory.Exists(checkpoints));
        Assert.Equal(checkpoints, BoundedFiles.Plain(checkpoints, true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(checkpoints));
        Assert.NotEqual(oldCheckpoints, checkpoints);
        Assert.Equal("preserve prior checkpoint evidence", File.ReadAllText(oldEvidence));
        Assert.Equal(new[] { oldEvidence }, Directory.GetFiles(oldCheckpoints));
        Assert.False(Directory.Exists(result.Selection.StoreRoot));
        Assert.False(Directory.Exists(result.Selection.WorkerPaths.Worktree));
        Assert.False(Directory.Exists(result.Selection.WorkerPaths.ResultDirectory));
        Assert.False(File.Exists(Path.Combine(result.RunDirectory, "control", "host-unadmitted.json")));
        Assert.False(result.RuntimeCertified);
        Assert.Equal(rootBytes, File.ReadAllBytes(rootPath));
    }

    [Theory]
    [InlineData("inside")]
    [InlineData("same")]
    public void Repository_overlap_refuses_before_any_setup_output(string placement)
    {
        var fixture = Fixture.Create();
        var state = placement == "inside" ? Path.Combine(fixture.Repository, "state") : fixture.Repository;
        using var original = Lifetime();
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.PreparePlan("owner/example", fixture.Repository, "refs/heads/main", fixture.Git, state, original));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Repository));
    }

    [Fact]
    public void Tampered_template_root_refuses_without_allocating_a_new_run()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var first = AutomaticSetup.PreparePlan("owner/example", fixture.Repository, "refs/heads/main", fixture.Git, fixture.State, original);
        var config = TrustedConfiguration.Load(first.ConfigurationPath, original);
        File.AppendAllText(first.Root.Path, " ");
        var before = Directory.GetDirectories(Path.GetDirectoryName(first.RunDirectory)!);
        var error = Assert.Throws<IOException>(() => AutomaticSetup.Rebase(config, fixture.State, original));
        Assert.Equal("Actual independently selected admission bytes changed.", error.Message);
        Assert.Equal(before, Directory.GetDirectories(Path.GetDirectoryName(first.RunDirectory)!));
    }

    [Fact]
    public void Invalid_template_topology_is_not_quietly_repaired_by_rebasing()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var first = AutomaticSetup.PreparePlan("owner/example", fixture.Repository, "refs/heads/main", fixture.Git, fixture.State, original);
        var invalidRoot = first.Selection with { StoreRoot = fixture.Repository };
        var raw = JsonSerializer.SerializeToUtf8Bytes(invalidRoot, BoundedFiles.Json);
        var invalidPath = Path.Combine(fixture.Root, "invalid-template-root.json");
        BoundedFiles.WriteNew(invalidPath, raw, original);
        var config = TrustedConfiguration.Load(first.ConfigurationPath, original) with
        {
            StandaloneRoot = new(invalidPath, raw.LongLength, BoundedFiles.Sha(raw))
        };
        var before = Directory.GetDirectories(Path.GetDirectoryName(first.RunDirectory)!);
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.Rebase(config, fixture.State, original));
        Assert.Equal(before, Directory.GetDirectories(Path.GetDirectoryName(first.RunDirectory)!));
    }

    [Fact(Skip = "Linux permits unprivileged symbolic links for the state-root control.", SkipUnless = nameof(IsLinux))]
    public void Non_plain_selected_state_refuses_before_writing_through_a_symbolic_link()
    {
        var fixture = Fixture.Create();
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "actual-state")).FullName;
        Directory.CreateSymbolicLink(fixture.State, target);
        using var original = Lifetime();
        Assert.Throws<IOException>(() => AutomaticSetup.PreparePlan("owner/example", fixture.Repository, "refs/heads/main", fixture.Git, fixture.State, original));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Theory]
    [InlineData("owner/example", "main")]
    [InlineData("../example", "refs/heads/main")]
    [InlineData("owner/example/other", "refs/heads/main")]
    public void Unresolved_identity_or_reference_refuses_before_state_creation(string repository, string reference)
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.PreparePlan(repository, fixture.Repository, reference, fixture.Git, fixture.State, original));
        Assert.False(Directory.Exists(fixture.State));
    }

    static ToolLifetime Lifetime() => new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    sealed record Fixture(string Root, string Repository, string Git, string State)
    {
        internal static Fixture Create()
        {
            var root = Directory.CreateTempSubdirectory("forge-setup-").FullName;
            var repository = Directory.CreateDirectory(Path.Combine(root, "checkout")).FullName;
            var git = Path.Combine(root, "unexecuted-git-data");
            File.WriteAllText(git, "never executed by setup");
            return new(root, repository, git, Path.Combine(root, "state"));
        }
    }
}
