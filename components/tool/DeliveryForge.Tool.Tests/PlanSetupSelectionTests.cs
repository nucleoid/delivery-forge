using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Planning;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Scheduling;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

public sealed class PlanSetupSelectionTests
{
    // Ordinary Windows owned Git is directly available to this fixture. A Linux
    // run needs its real independently selected process scope, not a test bypass.
    public static bool IsWindows => OperatingSystem.IsWindows();
    static AdmissionFile Write(string path, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, BoundedFiles.Json); File.WriteAllBytes(path, bytes);
        return new(path, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
    static LocalWorkerPaths Paths(string[] allowed, string[] excluded) => new(
        Path.Combine(Path.GetTempPath(), "unexecuted-work"), Path.Combine(Path.GetTempPath(), "unexecuted-results"), allowed, excluded);

    [Fact]
    public void File_derivation_narrows_directory_prefixes_and_does_not_mutate_exclusions()
    {
        var owner = Paths(["src", "tests"], ["src/private", "secrets"]);
        var actual = PlanSetupSelection.ConstrainPaths(["tests/Case.cs", "src/A.cs", "src/A.cs"], owner);
        Assert.Equal(new[] { "src/A.cs", "tests/Case.cs" }, actual);
        Assert.Equal(new[] { "src/private", "secrets" }, owner.Exclusions);
        Assert.Equal(new[] { "src", "tests" }, owner.AllowedPaths);
    }

    [Fact]
    public void Frozen_plan_exclusions_keep_exact_spellings_add_denies_and_preserve_owner_protection()
    {
        var owner = Paths(["src"], ["quality/private"]);
        string[] planExclusions = ["publication", "quality", "src/blocked", "merge"];
        var adopted = PlanSetupSelection.ConstrainExclusions(planExclusions, owner.Exclusions);
        Assert.Equal(planExclusions, adopted);
        Assert.Equal(new[] { "quality/private" }, owner.Exclusions);
        Assert.Equal(new[] { "src/A.cs" }, PlanSetupSelection.ConstrainPaths(["src/A.cs"], owner with { Exclusions = adopted }));
        Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainPaths(["src/blocked/A.cs"], owner with { Exclusions = adopted }));
    }

    [Theory]
    [InlineData("quality", "quality/private")]
    [InlineData("quality", "quality-other")]
    public void Frozen_plan_cannot_replace_owner_deny_with_a_narrower_or_unrelated_prefix(string owner, string selected)
        => Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainExclusions([selected], [owner]));

    [Fact]
    public void Frozen_plan_cannot_omit_owner_deny_or_exceed_existing_list_bounds()
    {
        Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainExclusions([], ["quality"]));
        Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainExclusions(Enumerable.Range(0, 129).Select(index => "deny" + index).ToArray(), []));
    }

    [Fact]
    public void Owner_deny_coverage_uses_actual_platform_case_semantics()
    {
        if (OperatingSystem.IsWindows())
            Assert.Equal(new[] { "library" }, PlanSetupSelection.ConstrainExclusions(["library"], ["Library/Secret.cs"]));
        else
            Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainExclusions(["library"], ["Library/Secret.cs"]));
    }

    [Theory]
    [InlineData("src-other/A.cs", "src", "excluded")]
    [InlineData("src/private/A.cs", "src", "src/private")]
    [InlineData("src", "src", "src/private")]
    [InlineData("../outside.cs", "src", "excluded")]
    [InlineData("src/../A.cs", "src", "excluded")]
    [InlineData("src/A.cs", "src/**", "excluded")]
    [InlineData("src/A.cs", "src", "**/secret")]
    [InlineData("C:/outside.cs", "src", "excluded")]
    public void Unsupported_or_widened_file_scope_refuses(string file, string allowed, string excluded) =>
        Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainPaths([file], Paths([allowed], [excluded])));

    [Fact]
    public void Empty_profile_scope_is_not_repository_wide_authorization() =>
        Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainPaths(["src/A.cs"], Paths([], [])));

    [Fact]
    public void Exclusions_follow_actual_platform_path_case_semantics()
    {
        var owner = Paths(["Library"], ["Library/Secret.cs"]);
        if (OperatingSystem.IsWindows())
            Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainPaths(["Library/secret.cs"], owner));
        else
            Assert.Equal(new[] { "Library/secret.cs" }, PlanSetupSelection.ConstrainPaths(["Library/secret.cs"], owner));
    }

    [Fact]
    public void Distinct_plan_spellings_that_case_collide_are_not_silently_collapsed_on_Windows()
    {
        var owner = Paths(["Library"], []);
        if (OperatingSystem.IsWindows())
            Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainPaths(["Library/A.cs", "Library/a.cs"], owner));
        else
            Assert.Equal(new[] { "Library/A.cs", "Library/a.cs" }, PlanSetupSelection.ConstrainPaths(["Library/A.cs", "Library/a.cs"], owner));
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
    public void Windows_aliases_cannot_bypass_owner_denies_or_enter_either_scope_list(string alias)
    {
        var owner = Paths(["src"], ["src/secret"]);
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainPaths([alias], owner));
            Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainPaths([alias + "/child.txt"], Paths([alias], [])));
            Assert.Throws<ToolUsageException>(() => PlanSetupSelection.ConstrainExclusions([alias], [alias]));
        }
        else
        {
            Assert.Equal(new[] { alias }, PlanSetupSelection.ConstrainPaths([alias], owner));
            Assert.Equal(new[] { alias }, PlanSetupSelection.ConstrainExclusions([alias], [alias]));
        }
    }

    [Fact]
    public async Task Requested_ceiling_and_template_pin_refuse_before_bundle_or_Git_access()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = Directory.CreateTempSubdirectory("forge-selection-early-").FullName;
        var template = new TrustedConfiguration("fixture/selection", Path.Combine(root, "repo"), "refs/heads/main",
            Path.Combine(root, "unread-git"), MaximumCeiling: "plan");
        var pin = Write(Path.Combine(root, "template.json"), template);
        var bundle = new AdmissionFile(Path.Combine(root, "unread-bundle"), 1, new string('a', 64));
        var selected = new ParentPlanSetupSelection(bundle, "independent-bundle", "independent-contract", "#8", "plan", PlanSetupPurpose.Revise, "implement");
        await Assert.ThrowsAsync<ToolUsageException>(() => PlanSetupSelection.SelectAsync(pin, selected, new(), original));
        File.AppendAllText(pin.Path, " ");
        await Assert.ThrowsAsync<IOException>(() => PlanSetupSelection.SelectAsync(pin, selected, new(), original));
        Assert.False(File.Exists(bundle.Path)); Assert.False(File.Exists(template.GitExecutable));
    }

    [Theory(Skip = "Owned Windows Git fixture; Linux needs its actual selected ordinary scope.", SkipUnless = nameof(IsWindows))]
    [InlineData("revise-plan")]
    [InlineData("execute-implement")]
    [InlineData("execute-plan")]
    [InlineData("execute-above-plan")]
    [InlineData("wrong-independent-identity")]
    [InlineData("tampered-bundle")]
    [InlineData("prefix-collision")]
    [InlineData("excluded-file")]
    [InlineData("dirty-source")]
    [InlineData("committed-base-advance")]
    [InlineData("missing-owner-exclusion")]
    public async Task Actual_owned_import_binds_independent_selection_before_returning_non_authoritative_DATA(string scenario)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        var root = Directory.CreateTempSubdirectory("forge-selection-import-").FullName;
        var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, "git.exe")).First(File.Exists);
        var executor = new ProcessCommandExecutor();
        async Task Git(params string[] args)
        {
            var argv = new[] { "--no-replace-objects", "-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false",
                "-c", "user.name=SelectionFixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false" }.Concat(args).ToArray();
            var capture = await executor.ExecuteAsync(new(git, argv, repo, new Dictionary<string, string>
            {
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = "NUL",
                ["GIT_CONFIG_SYSTEM"] = "NUL",
                ["GIT_NO_REPLACE_OBJECTS"] = "1",
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_OPTIONAL_LOCKS"] = "0"
            }), original.Remaining, original.Token);
            Assert.True(capture.OwnedProcessQuiescent && capture.ExitCode == 0 && !capture.TimedOut && !capture.Cancelled, capture.StandardError);
        }
        var path = scenario == "prefix-collision" ? "src-other/A.cs" : scenario == "excluded-file" ? "src/private/A.cs" : "src/A.cs";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, path))!); File.WriteAllText(Path.Combine(repo, path), "original content\n");
        await Git("init", "-q", "-b", "main"); await Git("add", "--", path); await Git("commit", "-q", "-m", "base");
        var reader = new GitRepositoryContextReader(git); var context = await reader.ReadAsync(repo, "refs/heads/main", original.Token);
        var file = await reader.ReadFileAsync(context, path, original.Token);
        var execute = scenario.StartsWith("execute", StringComparison.Ordinal);
        var implementationPlan = scenario is "execute-implement" or "execute-above-plan";
        var oldCeiling = implementationPlan ? "implement" : "plan";
        var planExclusions = scenario == "missing-owner-exclusion" ? Array.Empty<string>() : new[] { "src/private", "publication", "merge" };
        var request = new PlanningRequest("fixture/selection", "#8", implementationPlan ? "implement" : "plan", "Update one file", [path], planExclusions, ["Only selected file changes"], oldCeiling);
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, DateTimeOffset.UtcNow, [])];
        var draft = new PlanDraft(request, context, evidence, [new(path, "content", "update")], [new("#8", [], "root")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], IntakePlanner.Assess(request, evidence));
        var plan = PlanFreezer.Freeze(draft, "independent-original", DateTimeOffset.UtcNow);
        using var frozenMaterial = JsonDocument.Parse(plan.CanonicalBytes);
        var frozenExclusions = frozenMaterial.RootElement.GetProperty("plan").GetProperty("scope").GetProperty("excluded")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        var bundlePath = Path.Combine(root, "bundle.json"); File.WriteAllBytes(bundlePath, plan.CanonicalBytes);
        var bundle = new AdmissionFile(bundlePath, plan.CanonicalBytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(plan.CanonicalBytes)));
        var admission = Write(Path.Combine(root, "non-admitted.json"), new { Kind = "DeliveryForgeHostNotAdmitted-v1" });
        var selectionRoot = new StandaloneRootSelection("DeliveryForgeStandaloneRoot-v1", Path.Combine(root, "old-store"),
            new(Path.Combine(root, "old-work"), Path.Combine(root, "old-results"), ["src"], ["src/private"]), admission);
        var rootPin = Write(Path.Combine(root, "root.json"), selectionRoot);
        var maximum = scenario == "execute-above-plan" ? "pr" : "implement";
        var template = new TrustedConfiguration(request.Repository, repo, "refs/heads/main", git, MaximumCeiling: maximum,
            StandaloneRoot: rootPin);
        var templatePin = Write(Path.Combine(root, "template.json"), template);
        var selected = new ParentPlanSetupSelection(bundle, scenario == "wrong-independent-identity" ? "sha256:" + new string('0', 64) : plan.Identity,
            plan.ContractIdentity, "#8", oldCeiling, execute ? PlanSetupPurpose.Execute : PlanSetupPurpose.Revise,
            scenario == "execute-above-plan" ? "pr" : "implement");
        if (scenario == "tampered-bundle") File.AppendAllText(bundlePath, " ");
        if (scenario == "dirty-source") File.AppendAllText(Path.Combine(repo, path), "dirty\n");
        if (scenario == "committed-base-advance") await Git("commit", "--allow-empty", "-q", "-m", "advanced base");
        var beforeTemplate = File.ReadAllBytes(templatePin.Path); var beforeRoot = File.ReadAllBytes(rootPin.Path);
        SelectedPlanSetupData? result = null;
        var error = await Record.ExceptionAsync(async () => result = await PlanSetupSelection.SelectAsync(templatePin, selected, executor, original));
        Assert.Equal(beforeTemplate, File.ReadAllBytes(templatePin.Path)); Assert.Equal(beforeRoot, File.ReadAllBytes(rootPin.Path));
        Assert.False(Directory.Exists(selectionRoot.StoreRoot)); Assert.False(Directory.Exists(selectionRoot.WorkerPaths.Worktree));
        if (scenario is not ("revise-plan" or "execute-implement")) { Assert.NotNull(error); Assert.Null(result); return; }
        Assert.Null(error); Assert.NotNull(result);
        Assert.Equal(plan.Identity, result.Configuration.ExpectedBundleIdentity); Assert.Equal(plan.ContractIdentity, result.Configuration.ExpectedContractIdentity);
        Assert.Equal(oldCeiling, result.Configuration.ExpectedPlanCeiling); Assert.Equal("implement", result.Configuration.MaximumCeiling);
        Assert.Equal(new[] { path }, result.Root.WorkerPaths.AllowedPaths); Assert.Equal(frozenExclusions, result.Root.WorkerPaths.Exclusions);
        Assert.Equal(admission, result.Root.InstalledHostAdmission); Assert.False(result.RuntimeCertified); Assert.False(result.ExecutionAuthorityIssued);
        if (scenario is "execute-implement" or "revise-plan")
        {
            // Exercise the real scheduler's exact frozen exclusion gate. The
            // old owner-only list fails; the adopted complete plan list passes.
            var store = new RunStore(Path.Combine(root, "scope-only-scheduler-store"));
            var worktrees = new WorktreeManager(git, executor, original);
            await using var processes = new ProcessControl();
            using var coordinator = new ExecutionCoordinator(store, worktrees, processes);
            var slots = new ResourceSlots(1, 2, 4096, 1, 1, 0);
            var budget = ResourceBudget.Measure(slots, new("scope-constructor-only", true, slots, TimeSpan.FromSeconds(30), 1000, 0));
            var demand = new RunDemand(slots, [], TimeSpan.FromSeconds(30), 1000, 0);
            var worker = new FrozenWorkerPlan("scope-only", plan.ContractIdentity, plan.BaseCommit, plan.Repository, repo,
                selectionRoot.WorkerPaths.Worktree, selectionRoot.WorkerPaths.ResultDirectory, "scope-only-token",
                result.Root.WorkerPaths.AllowedPaths, result.Root.WorkerPaths.Exclusions, "codex.cli", "unexecuted-scope-data",
                [AgentCapability.Launch, AgentCapability.CompletionEvidence], "implement");
            if (scenario == "revise-plan")
            {
                // Successful Revise DATA keeps the old frozen plan-only mode
                // and ceiling. It cannot authorize scheduling that old plan.
                Assert.Throws<SchedulingException>(() => LocalSingleScheduler.Create(plan,
                    worker with { AuthorizationCeiling = oldCeiling }, demand, budget, store, coordinator, worktrees, reader));
            }
            else
            {
                Assert.Throws<SchedulingException>(() => LocalSingleScheduler.Create(plan,
                    worker with { Exclusions = selectionRoot.WorkerPaths.Exclusions }, demand, budget, store, coordinator, worktrees, reader));
                Assert.NotNull(LocalSingleScheduler.Create(plan, worker, demand, budget, store, coordinator, worktrees, reader));
            }
            Assert.False(Directory.Exists(store.RootPath));
            Assert.False(Directory.Exists(worker.Worktree));
            Assert.False(Directory.Exists(worker.ResultDirectory));
        }
        // Exercise public setup grammar/discovery and the owning task boundary,
        // rather than treating successful helper parsing as end-to-end setup.
        var state = Path.Combine(root, "state");
        var application = new ToolApplication(actualExecutor: executor, actualOriginal: original);
        var registration = await application.ExecuteAsync(CommandLine.Parse(["setup", "--register-config", templatePin.Path,
            "--repo", repo, "--repository", request.Repository, "--state-root", state]), original.Token);
        Assert.Equal("SETUP_PROFILE_REGISTERED", registration.Code);
        var selectionPin = Write(Path.Combine(root, "parent-selection.json"), selected);
        var prepared = await application.ExecuteAsync(CommandLine.Parse(["setup", "--repo", repo, "--repository", request.Repository,
            "--plan-selection", selectionPin.Path, "--state-root", state]), original.Token);
        Assert.Equal("SETUP_PREPARED", prepared.Code);
        var automatic = Assert.IsType<AutomaticSetupResult>(prepared.Data);
        var actualConfig = TrustedConfiguration.Load(automatic.ConfigurationPath, original);
        Assert.Equal(plan.Identity, actualConfig.ExpectedBundleIdentity);
        Assert.Equal(oldCeiling, actualConfig.ExpectedPlanCeiling);
        Assert.Equal("implement", actualConfig.MaximumCeiling);
        Assert.Equal(new[] { path }, automatic.Selection.WorkerPaths.AllowedPaths);
        Assert.Equal(frozenExclusions, automatic.Selection.WorkerPaths.Exclusions);
        Assert.NotNull(automatic.ProfileRegistration);
        Assert.False(Directory.Exists(automatic.Selection.WorkerPaths.Worktree));
        Assert.Contains(application.RetainedExternalCalls, call => call.Kind == "ActualSetupPlanSelection" && call.ActualTask is { IsCompletedSuccessfully: true });
        Assert.Equal(beforeTemplate, File.ReadAllBytes(templatePin.Path));
        Assert.Equal(beforeRoot, File.ReadAllBytes(rootPin.Path));
    }
}
