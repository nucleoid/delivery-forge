using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

public sealed class RepositorySetupProfileTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact]
    public void Profile_key_casefolds_repository_identity_and_canonicalizes_supported_Windows_checkout_aliases()
    {
        var fixture = Fixture.Create();
        var key = AutomaticSetup.RepositoryKey("owner/example", fixture.Repository);
        Assert.Equal(key, AutomaticSetup.RepositoryKey("OWNER/EXAMPLE", fixture.Repository));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(key, AutomaticSetup.RepositoryKey("owner/example", @"\\?\" + fixture.Repository));
            Assert.Throws<ToolUsageException>(() => AutomaticSetup.RepositoryKey("owner/example", @"\\server\share\checkout"));
        }
        Assert.False(Directory.Exists(fixture.State));
    }

    [Theory]
    [InlineData("owner/example\n")]
    [InlineData("owner/example.git")]
    [InlineData("owner/example.GIT")]
    public void Profile_key_refuses_noncanonical_repository_identity_without_creating_state(string identity)
    {
        var fixture = Fixture.Create();
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.RepositoryKey(identity, fixture.Repository));
        Assert.False(Directory.Exists(fixture.State));
    }

    [Fact]
    public async Task Identity_alias_cannot_turn_a_present_pin_drifted_registration_into_absence()
    {
        var fixture = Fixture.Create(); using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var registered = await application.ExecuteAsync(CommandLine.Parse(fixture.RegisterArguments()), original.Token);
        var receipt = Assert.IsType<RepositorySetupProfileResult>(registered.Data);
        var before = File.ReadAllBytes(receipt.Registration.Path);
        File.AppendAllText(fixture.Template, " ");
        Assert.Throws<ToolUsageException>(() => RepositorySetupProfiles.Read("OWNER/EXAMPLE", fixture.Repository, fixture.State, original));
        Assert.Equal(before, File.ReadAllBytes(receipt.Registration.Path));
        Assert.Empty(application.RetainedExternalCalls);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("git")]
    [InlineData("root")]
    [InlineData("coordination-key")]
    [InlineData("coordination-checkpoints")]
    [InlineData("coordination-evidence")]
    [InlineData("review-git")]
    public void Registration_refuses_overlap_with_actual_selected_input_paths_without_overwriting_them(string input)
    {
        var fixture = Fixture.Create(); using var original = Lifetime();
        var config = TrustedConfiguration.Load(fixture.Template, original);
        var registrationPath = RepositorySetupProfiles.Location("owner/example", fixture.Repository, fixture.State);
        if (input == "policy") config = config with { PolicyFile = registrationPath };
        if (input == "git") config = config with { GitExecutable = registrationPath };
        if (input == "root")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(registrationPath)!);
            File.Copy(config.StandaloneRoot!.Path, registrationPath);
            config = config with { StandaloneRoot = config.StandaloneRoot! with { Path = registrationPath } };
        }
        if (input.StartsWith("coordination-", StringComparison.Ordinal))
            config = config with { Coordination = new(new Uri("https://fixture.invalid"), "fixture", "fixture-principal",
                input == "coordination-key" ? registrationPath : Path.Combine(fixture.Root, "unread-key"),
                input == "coordination-checkpoints" ? registrationPath : Path.Combine(fixture.Root, "unused-checkpoints"),
                new(input == "coordination-evidence" ? registrationPath : Path.Combine(fixture.Root, "unread-deployment"), 1, new string('a', 64))) };
        if (input == "review-git")
            config = config with { InstalledReview = new("review.json", "evidence.json", "sha256:" + new string('a', 64),
                new(registrationPath, 1, new string('a', 64)), "git version 2.0", Path.Combine(fixture.Root, "unused-review-artifacts")) };
        File.WriteAllBytes(fixture.Template, JsonSerializer.SerializeToUtf8Bytes(config, BoundedFiles.Json));
        var before = File.Exists(registrationPath) ? File.ReadAllBytes(registrationPath) : null;
        var failure = Assert.Throws<ToolUsageException>(() => RepositorySetupProfiles.Register(fixture.Template,
            "owner/example", fixture.Repository, fixture.State, original));
        Assert.Contains("overlap", failure.Message);
        if (before is null) Assert.False(Directory.Exists(fixture.State));
        else Assert.Equal(before, File.ReadAllBytes(registrationPath));
    }

    [Fact]
    public async Task Explicit_null_plan_selection_is_refused_before_profile_lookup_or_state_creation()
    {
        var fixture = Fixture.Create(); using var original = Lifetime();
        var selection = Path.Combine(fixture.Root, "null-selection.json"); File.WriteAllText(selection, "null");
        var application = new ToolApplication(actualOriginal: original);
        await Assert.ThrowsAsync<IOException>(() => application.ExecuteAsync(CommandLine.Parse(fixture.Arguments()
            .Concat(new[] { "--plan-selection", selection }).ToArray()), original.Token));
        Assert.False(Directory.Exists(fixture.State)); Assert.Empty(application.RetainedExternalCalls);
    }

    [Fact(Skip = "Linux selected-scope refusal; no native dispatch required.", SkipUnless = nameof(IsLinux))]
    public async Task Registered_Linux_plan_selection_cannot_use_injected_executor_to_skip_missing_scope()
    {
        var fixture = Fixture.Create(); using var original = Lifetime();
        var application = new ToolApplication(actualExecutor: new ProcessCommandExecutor(), actualOriginal: original);
        await application.ExecuteAsync(CommandLine.Parse(fixture.RegisterArguments()), original.Token);
        var before = Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories);
        var unread = Path.Combine(fixture.Root, "unread-bundle.json");
        var selection = Path.Combine(fixture.Root, "parent-selection.json");
        File.WriteAllBytes(selection, JsonSerializer.SerializeToUtf8Bytes(new ParentPlanSetupSelection(
            new(unread, 1, new string('a', 64)), "selected-bundle", "selected-contract", "#8", "plan", PlanSetupPurpose.Revise, "implement"), BoundedFiles.Json));
        var failure = await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse(fixture.Arguments()
            .Concat(new[] { "--plan-selection", selection }).ToArray()), original.Token));
        Assert.Contains("Linux ordinary scope", failure.Message);
        Assert.False(File.Exists(unread));
        Assert.Equal(before, Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories));
        Assert.Empty(application.RetainedExternalCalls);
    }

    [Fact]
    public async Task Explicit_registration_then_known_location_discovery_preserves_scope_and_allocates_fresh_paths()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var registered = await application.ExecuteAsync(CommandLine.Parse(fixture.RegisterArguments()), original.Token);
        Assert.Equal("SETUP_PROFILE_REGISTERED", registered.Code);
        Assert.False(registered.RuntimeCertified);
        var receipt = Assert.IsType<RepositorySetupProfileResult>(registered.Data);
        var registrationBytes = File.ReadAllBytes(receipt.Registration.Path);
        var templateBytes = File.ReadAllBytes(fixture.Template);
        var first = Assert.IsType<AutomaticSetupResult>((await application.ExecuteAsync(CommandLine.Parse(fixture.Arguments()), original.Token)).Data);
        var second = Assert.IsType<AutomaticSetupResult>((await application.ExecuteAsync(CommandLine.Parse(fixture.Arguments("implement")), original.Token)).Data);
        var firstConfig = TrustedConfiguration.Load(first.ConfigurationPath, original);
        var nextConfig = TrustedConfiguration.Load(second.ConfigurationPath, original);
        Assert.Equal("plan", firstConfig.MaximumCeiling); // Ordinary planning does not inherit an implementation request.
        Assert.Equal("implement", nextConfig.MaximumCeiling);
        Assert.Equal("plan", nextConfig.ExpectedPlanCeiling); // Discovery does not upgrade a frozen plan.
        Assert.Equal("selected-bundle-data", nextConfig.ExpectedBundleIdentity);
        Assert.Equal("selected-contract-data", nextConfig.ExpectedContractIdentity);
        Assert.Equal("owner/example#8", nextConfig.ExpectedWorkItem);
        Assert.Equal(new[] { "src" }, second.Selection.WorkerPaths.AllowedPaths);
        Assert.Equal(new[] { "private" }, second.Selection.WorkerPaths.Exclusions);
        Assert.NotEqual(first.RunDirectory, second.RunDirectory);
        Assert.Equal(receipt.Registration, first.ProfileRegistration);
        Assert.Equal(receipt.Registration, second.ProfileRegistration);
        Assert.False(Directory.Exists(first.Selection.StoreRoot));
        Assert.False(Directory.Exists(second.Selection.WorkerPaths.Worktree));
        Assert.False(Directory.Exists(second.Selection.WorkerPaths.ResultDirectory));
        Assert.Equal(templateBytes, File.ReadAllBytes(fixture.Template));
        Assert.Equal(registrationBytes, File.ReadAllBytes(receipt.Registration.Path));
        Assert.Empty(application.RetainedExternalCalls);
        Assert.False(first.RuntimeCertified);
        Assert.False(second.RuntimeCertified);
    }

    [Fact]
    public async Task Missing_execution_profile_reports_specific_prerequisite_without_creating_state_or_launching_git()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var result = await application.ExecuteAsync(CommandLine.Parse(fixture.Arguments("implement")), original.Token);
        Assert.Equal("INCOMPLETE", result.Outcome);
        Assert.Equal("SETUP_EXECUTION_PROFILE_REQUIRED", result.Code);
        Assert.False(Directory.Exists(fixture.State));
        Assert.Empty(application.RetainedExternalCalls);
        Assert.False(result.RuntimeCertified);
    }

    [Fact]
    public async Task Changed_template_pin_refuses_before_allocating_a_run()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        await application.ExecuteAsync(CommandLine.Parse(fixture.RegisterArguments()), original.Token);
        var before = Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories);
        File.AppendAllText(fixture.Template, " ");
        await Assert.ThrowsAsync<IOException>(() => application.ExecuteAsync(CommandLine.Parse(fixture.Arguments("implement")), original.Token));
        Assert.Equal(before, Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories));
        Assert.Empty(application.RetainedExternalCalls);
    }

    [Fact]
    public async Task A_registration_copied_to_another_repository_key_cannot_retarget_the_template()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var result = await application.ExecuteAsync(CommandLine.Parse(fixture.RegisterArguments()), original.Token);
        var selected = Assert.IsType<RepositorySetupProfileResult>(result.Data);
        var other = Directory.CreateDirectory(Path.Combine(fixture.Root, "other-checkout")).FullName;
        var otherPath = RepositorySetupProfiles.Location("owner/other", other, fixture.State);
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.Copy(selected.Registration.Path, otherPath);
        var before = Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories);
        await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse([
            "setup", "--repo", other, "--repository", "owner/other", "--ceiling", "implement", "--state-root", fixture.State]), original.Token));
        Assert.Equal(before, Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories));
        Assert.Empty(application.RetainedExternalCalls);
    }

    [Fact]
    public async Task Requested_ceiling_and_explicit_overrides_cannot_raise_or_replace_the_registered_selection()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        await application.ExecuteAsync(CommandLine.Parse(fixture.RegisterArguments()), original.Token);
        var before = Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories);
        var ceiling = await application.ExecuteAsync(CommandLine.Parse(fixture.Arguments("pr")), original.Token);
        Assert.Equal("SETUP_PROFILE_CEILING_EXCEEDED", ceiling.Code);
        var changedReference = fixture.Arguments("implement").Concat(new[] { "--reference", "refs/heads/other" }).ToArray();
        var replacement = await application.ExecuteAsync(CommandLine.Parse(changedReference), original.Token);
        Assert.Equal("SETUP_PROFILE_OVERRIDE_REFUSED", replacement.Code);
        Assert.Equal(before, Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories));
        Assert.Empty(application.RetainedExternalCalls);
    }

    [Fact]
    public async Task Registration_never_overwrites_an_existing_decision()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var first = await application.ExecuteAsync(CommandLine.Parse(fixture.RegisterArguments()), original.Token);
        var receipt = Assert.IsType<RepositorySetupProfileResult>(first.Data);
        var before = File.ReadAllBytes(receipt.Registration.Path);
        await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse(fixture.RegisterArguments()), original.Token));
        Assert.Equal(before, File.ReadAllBytes(receipt.Registration.Path));
        Assert.Empty(application.RetainedExternalCalls);
    }

    [Fact]
    public async Task Registration_of_a_template_for_a_different_repository_refuses_before_state_creation()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var application = new ToolApplication(actualOriginal: original);
        var arguments = fixture.RegisterArguments();
        arguments[Array.IndexOf(arguments, "--repository") + 1] = "owner/other";
        await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse(arguments), original.Token));
        Assert.False(Directory.Exists(fixture.State));
        Assert.Empty(application.RetainedExternalCalls);
    }

    [Fact]
    public void Parent_selected_setup_can_narrow_but_cannot_replace_other_root_fields_or_expand_scope()
    {
        var fixture = Fixture.Create();
        using var original = Lifetime();
        var config = TrustedConfiguration.Load(fixture.Template, original);
        var root = AutomaticSetup.ReadOrdinaryTemplateRoot(config, fixture.Template, original);
        var selected = AutomaticSetup.RebaseSelected(config, root with { WorkerPaths = root.WorkerPaths with { AllowedPaths = ["src/control.txt"] } }, fixture.State, original);
        Assert.Equal(new[] { "src/control.txt" }, selected.Selection.WorkerPaths.AllowedPaths);
        var before = Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories);
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.RebaseSelected(config,
            root with { WorkerPaths = root.WorkerPaths with { AllowedPaths = ["src-other/control.txt"] } }, fixture.State, original));
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.RebaseSelected(config,
            root with { WorkerPaths = root.WorkerPaths with { Exclusions = [] } }, fixture.State, original));
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.RebaseSelected(config,
            root with { StoreRoot = Path.Combine(fixture.Root, "foreign-store") }, fixture.State, original));
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.RebaseSelected(config,
            root with { WorkerPaths = root.WorkerPaths with { AllowedPaths = ["src/../private/control.txt"] } }, fixture.State, original));
        Assert.Equal(before, Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Parent_selected_setup_may_add_or_broaden_denies_but_cannot_omit_owner_protection()
    {
        var fixture = Fixture.Create(["src"], ["private/secret"]);
        using var original = Lifetime();
        var config = TrustedConfiguration.Load(fixture.Template, original);
        var root = AutomaticSetup.ReadOrdinaryTemplateRoot(config, fixture.Template, original);
        string[] planExclusions = ["publication", "private", "src/blocked", "merge"];
        var selected = root with { WorkerPaths = root.WorkerPaths with { AllowedPaths = ["src/control.txt"], Exclusions = planExclusions } };
        var prepared = AutomaticSetup.RebaseSelected(config, selected, fixture.State, original);
        Assert.Equal(planExclusions, prepared.Selection.WorkerPaths.Exclusions);
        var before = Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories);
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.RebaseSelected(config,
            selected with { WorkerPaths = selected.WorkerPaths with { Exclusions = ["publication", "merge"] } }, fixture.State, original));
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.RebaseSelected(config,
            selected with { WorkerPaths = selected.WorkerPaths with { AllowedPaths = ["src/blocked/control.txt"] } }, fixture.State, original));
        Assert.Equal(before, Directory.GetFiles(fixture.State, "*", SearchOption.AllDirectories));
        Assert.Equal(new[] { "private/secret" }, root.WorkerPaths.Exclusions);
    }

    [Fact]
    public void Parent_selected_scope_uses_platform_case_for_exclusions_and_refuses_Windows_case_collisions()
    {
        var fixture = Fixture.Create(["Library"], ["Library/Secret.cs"]);
        using var original = Lifetime();
        var config = TrustedConfiguration.Load(fixture.Template, original);
        var root = AutomaticSetup.ReadOrdinaryTemplateRoot(config, fixture.Template, original);
        var excludedCaseVariant = root with { WorkerPaths = root.WorkerPaths with { AllowedPaths = ["Library/secret.cs"] } };
        var colliding = root with { WorkerPaths = root.WorkerPaths with { AllowedPaths = ["Library/A.cs", "Library/a.cs"] } };
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<ToolUsageException>(() => AutomaticSetup.RebaseSelected(config, excludedCaseVariant, fixture.State, original));
            Assert.Throws<ToolUsageException>(() => AutomaticSetup.RebaseSelected(config, colliding, fixture.State, original));
            Assert.False(Directory.Exists(fixture.State));
        }
        else
        {
            Assert.Equal(new[] { "Library/secret.cs" }, AutomaticSetup.RebaseSelected(config, excludedCaseVariant, fixture.State, original).Selection.WorkerPaths.AllowedPaths);
            Assert.Equal(new[] { "Library/A.cs", "Library/a.cs" }, AutomaticSetup.RebaseSelected(config, colliding, fixture.State, original).Selection.WorkerPaths.AllowedPaths);
        }
        // A directory selector spanning an excluded descendant is refused on
        // either platform, even when that directory was independently allowed.
        Assert.Throws<ToolUsageException>(() => AutomaticSetup.RebaseSelected(config, root, fixture.State, original));
    }

    [Theory]
    [InlineData("src/secret./control.txt")]
    [InlineData("src/secret /control.txt")]
    [InlineData("src/SECRE~1/control.txt")]
    [InlineData("src/CON.txt")]
    [InlineData("src/LPT9.log")]
    [InlineData("src/aux/control.txt")]
    [InlineData("src/COM\u00b9.log")]
    [InlineData("src/CONIN$/control.txt")]
    public void Parent_selected_alias_scope_refuses_before_allocating_state_on_Windows(string alias)
    {
        var fixture = Fixture.Create(["src"], ["src/secret"]);
        using var original = Lifetime();
        var config = TrustedConfiguration.Load(fixture.Template, original);
        var root = AutomaticSetup.ReadOrdinaryTemplateRoot(config, fixture.Template, original);
        var before = File.ReadAllBytes(config.StandaloneRoot!.Path);
        var selected = root with { WorkerPaths = root.WorkerPaths with { AllowedPaths = [alias] } };
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<ToolUsageException>(() => AutomaticSetup.RebaseSelected(config, selected, fixture.State, original));
            Assert.False(Directory.Exists(fixture.State));
        }
        else
        {
            var prepared = AutomaticSetup.RebaseSelected(config, selected, fixture.State, original);
            Assert.Equal(new[] { alias }, prepared.Selection.WorkerPaths.AllowedPaths);
            Assert.False(Directory.Exists(prepared.Selection.WorkerPaths.Worktree));
        }
        Assert.Equal(before, File.ReadAllBytes(config.StandaloneRoot.Path));
    }

    [Theory]
    [InlineData("setup --register-config /template")]
    [InlineData("setup --register-config /template --repo /repo")]
    [InlineData("setup --register-config /template --repo /repo --repository owner/example --config /other")]
    [InlineData("setup --register-config /template --repo /repo --repository owner/example --ceiling implement")]
    [InlineData("setup --register-config /template --repo /repo --repository owner/example --reference refs/heads/main")]
    [InlineData("setup --register-config /template --repo /repo --repository owner/example --dry-run")]
    [InlineData("setup --config /template --ceiling implement")]
    [InlineData("setup --config /template --plan-selection /selected")]
    [InlineData("setup --register-config /template --repo /repo --repository owner/example --plan-selection /selected")]
    public void Parser_refuses_ambiguous_registration_and_template_overrides(string arguments)
        => Assert.Throws<ToolUsageException>(() => CommandLine.Parse(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    static ToolLifetime Lifetime() => new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    sealed record Fixture(string Root, string Repository, string State, string Template)
    {
        internal string[] RegisterArguments() => ["setup", "--repo", Repository, "--repository", "owner/example", "--register-config", Template, "--state-root", State];
        internal string[] Arguments(string? ceiling = null) => ceiling is null
            ? ["setup", "--repo", Repository, "--repository", "owner/example", "--state-root", State]
            : ["setup", "--repo", Repository, "--repository", "owner/example", "--state-root", State, "--ceiling", ceiling];
        internal static Fixture Create(string[]? allowed = null, string[]? exclusions = null)
        {
            var root = Directory.CreateTempSubdirectory("forge-repository-profile-").FullName;
            var repository = Directory.CreateDirectory(Path.Combine(root, "checkout")).FullName;
            using var original = Lifetime();
            var setup = AutomaticSetup.PreparePlan("owner/example", repository, "refs/heads/selected", typeof(ToolApplication).Assembly.Location,
                Path.Combine(root, "template-state"), original);
            var selection = setup.Selection with { WorkerPaths = setup.Selection.WorkerPaths with { AllowedPaths = allowed ?? ["src"], Exclusions = exclusions ?? ["private"] } };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(selection, BoundedFiles.Json);
            var selectedRoot = Path.Combine(root, "selected-root.json");
            BoundedFiles.WriteNew(selectedRoot, bytes, original);
            var configuration = TrustedConfiguration.Load(setup.ConfigurationPath, original) with
            {
                MaximumCeiling = "implement",
                ExpectedBundleIdentity = "selected-bundle-data",
                ExpectedContractIdentity = "selected-contract-data",
                ExpectedWorkItem = "owner/example#8",
                ExpectedPlanCeiling = "plan",
                StandaloneRoot = new(selectedRoot, bytes.LongLength, BoundedFiles.Sha(bytes))
            };
            var template = Path.Combine(root, "owner-selected-template.json");
            BoundedFiles.WriteNew(template, JsonSerializer.SerializeToUtf8Bytes(configuration, BoundedFiles.Json), original);
            return new(root, repository, Path.Combine(root, "profile-state"), template);
        }
    }
}
