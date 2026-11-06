using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Planning;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;

public sealed class PlanSupersessionTests
{
    sealed class Fixture
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("forge-plan-successor-").FullName;
        internal string Repository => Path.Combine(Root, "repository");
        internal string Previous => Path.Combine(Root, "previous.json");
        internal string Output => Path.Combine(Root, "successor.json");
        internal string IntentPath => Path.Combine(Root, "intent.json");
        internal string ConfigPath => Path.Combine(Root, "config.json");
        internal TrustedConfiguration Configuration;
        internal PlanIntent Intent;
        internal Fixture()
        {
            Directory.CreateDirectory(Repository);
            Configuration = new("fixture/supersession", Repository, "refs/heads/main", Path.Combine(Root, "unread-git"),
                MaximumCeiling: "implement", OperationSeconds: 120);
            Intent = new(new("fixture/supersession", "#8", "implement", "Update the tracked file", ["tracked.txt"], [],
                    ["Only tracked.txt changes"], "implement"), ["tracked.txt"], [new("tracked.txt", "content", "update")],
                [new("#8", [], "root")], [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
                new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], "implementation-revision");
        }
        internal void Save()
        {
            File.WriteAllBytes(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(Configuration, BoundedFiles.Json));
            File.WriteAllBytes(IntentPath, JsonSerializer.SerializeToUtf8Bytes(Intent, BoundedFiles.Json));
        }
        internal ToolCommand Command(string? output = null) => CommandLine.Parse(["plan", "--repo", Repository,
            "--intent-file", IntentPath, "--previous-plan", Previous, "--config", ConfigPath, "--plan-out", output ?? Output]);
        internal async Task<string> Git(ToolLifetime original, params string[] arguments)
        {
            var executor = new ProcessCommandExecutor();
            var nullPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var argv = new[] { "--no-replace-objects", "-c", "core.hooksPath=" + nullPath, "-c", "core.fsmonitor=false",
                "-c", "user.name=SupersessionFixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false" }
                .Concat(arguments).ToArray();
            var actual = await executor.ExecuteAsync(new(Configuration.GitExecutable, argv, Repository, new Dictionary<string, string>
            {
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = nullPath,
                ["GIT_CONFIG_SYSTEM"] = nullPath,
                ["GIT_NO_REPLACE_OBJECTS"] = "1",
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_OPTIONAL_LOCKS"] = "0"
            }), original.Remaining, original.Token);
            Assert.True(actual.OwnedProcessQuiescent && actual.ExitCode == 0 && !actual.TimedOut && !actual.Cancelled, actual.StandardError);
            original.Check();
            return actual.StandardOutput;
        }

        internal async Task<FrozenPlan> PreparePrevious(ToolLifetime original)
        {
            var executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
            var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
                .Select(path => Path.Combine(path, executable)).First(File.Exists);
            Configuration = Configuration with { GitExecutable = git };
            File.WriteAllText(Path.Combine(Repository, "tracked.txt"), "unchanged repository evidence\n");
            await Git(original, "init", "-q", "-b", "main"); await Git(original, "add", "--", "tracked.txt"); await Git(original, "commit", "-q", "-m", "original base");
            var reader = new GitRepositoryContextReader(git);
            var context = await reader.ReadAsync(Repository, "refs/heads/main", original.Token);
            var file = await reader.ReadFileAsync(context, "tracked.txt", original.Token);
            var request = Intent.Request with { Mode = "plan", RequestedCeiling = "plan" };
            EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, DateTimeOffset.UtcNow, [])];
            var previous = PlanFreezer.Freeze(new(request, context, evidence, Intent.ChangeMap, Intent.Dependencies, Intent.Gates,
                Intent.Rollout, Intent.Unknowns, IntakePlanner.Assess(request, evidence)), "planning-revision", DateTimeOffset.UtcNow);
            File.WriteAllBytes(Previous, previous.CanonicalBytes);
            Configuration = Configuration with
            {
                ExpectedBundleIdentity = previous.Identity,
                ExpectedContractIdentity = previous.ContractIdentity,
                ExpectedWorkItem = "#8",
                ExpectedPlanCeiling = "plan"
            };
            Save(); return previous;
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("dry-run")]
    [InlineData("no-output")]
    [InlineData("wrong-identity")]
    [InlineData("wrong-original-ceiling")]
    [InlineData("dirty-worktree")]
    [InlineData("committed-branch-advance")]
    [InlineData("widened-scope")]
    [InlineData("changed-exclusions")]
    [InlineData("changed-gates")]
    [InlineData("same-revision")]
    [InlineData("different-work-item")]
    [InlineData("same-output")]
    public async Task Previous_plan_is_independently_imported_and_preserved_before_explicit_successor_is_frozen(string scenario)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        var fixture = new Fixture(); var previous = await fixture.PreparePrevious(original);
        var before = File.ReadAllBytes(fixture.Previous);
        switch (scenario)
        {
            case "wrong-identity": fixture.Configuration = fixture.Configuration with { ExpectedBundleIdentity = "sha256:" + new string('0', 64) }; break;
            case "wrong-original-ceiling": fixture.Configuration = fixture.Configuration with { ExpectedPlanCeiling = "implement" }; break;
            case "dirty-worktree": File.AppendAllText(Path.Combine(fixture.Repository, "tracked.txt"), "unreconciled change\n"); break;
            case "committed-branch-advance":
                await fixture.Git(original, "commit", "--allow-empty", "-q", "-m", "new committed base");
                Assert.NotEqual(previous.BaseCommit, (await fixture.Git(original, "rev-parse", "HEAD")).Trim());
                Assert.Empty(await fixture.Git(original, "status", "--porcelain=v1", "--untracked-files=all"));
                break;
            case "widened-scope": fixture.Intent = fixture.Intent with
            {
                Request = fixture.Intent.Request with { Included = ["tracked.txt", "other.txt"] },
                ChangeMap = [new("tracked.txt", "content", "update"), new("other.txt", "content", "update")]
            }; break;
            case "changed-exclusions": fixture.Intent = fixture.Intent with { Request = fixture.Intent.Request with { Excluded = ["private/**"] } }; break;
            case "changed-gates": fixture.Intent = fixture.Intent with
            { Gates = [new("test", new("dotnet", [PlanCommandArgument.Literal("test"), PlanCommandArgument.Literal("--no-build")]), "PASS")] }; break;
            case "same-revision": fixture.Intent = fixture.Intent with { Revision = previous.Revision }; break;
            case "different-work-item": fixture.Intent = fixture.Intent with { Request = fixture.Intent.Request with { WorkItem = "#9" } }; break;
        }
        fixture.Save(); var application = new ToolApplication(actualOriginal: original);
        ToolResult? result = null;
        var command = fixture.Command(scenario == "same-output" ? fixture.Previous : null);
        if (scenario is "dry-run" or "no-output")
        {
            var options = command.Options.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
            if (scenario == "dry-run") options.Add("dry-run", null); else options.Remove("plan-out");
            command = command with { Options = options };
        }
        var error = await Record.ExceptionAsync(async () => result = await application.ExecuteAsync(command, original.Token));
        Assert.Equal(before, File.ReadAllBytes(fixture.Previous));
        Assert.Empty(application.RetainedAssignments);
        if (scenario is "dry-run" or "no-output")
        {
            Assert.Null(error); Assert.NotNull(result); Assert.Equal("PLAN_FROZEN", result.Code);
            using var data = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, BoundedFiles.Json));
            Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("PlanPath").ValueKind);
            Assert.False(File.Exists(fixture.Output)); return;
        }
        if (scenario != "success")
        {
            Assert.True(error is not null || result?.Outcome == "INCOMPLETE");
            if (scenario is "widened-scope" or "changed-exclusions" or "changed-gates")
            {
                Assert.Null(error); Assert.NotNull(result); Assert.Equal("PREVIOUS_PLAN_SCOPE_CHANGED", result.Code);
            }
            if (scenario == "committed-branch-advance")
            {
                Assert.Null(error); Assert.NotNull(result); Assert.Equal("PREVIOUS_PLAN_RECONCILIATION_INCOMPLETE", result.Code);
            }
            Assert.False(File.Exists(fixture.Output)); return;
        }
        Assert.Null(error); Assert.NotNull(result); Assert.Equal("PLAN_FROZEN", result.Code); Assert.False(result.RuntimeCertified);
        using var resultData = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, BoundedFiles.Json));
        Assert.Equal(fixture.Output, resultData.RootElement.GetProperty("PlanPath").GetString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(fixture.Output)); var successor = json.RootElement;
        Assert.Equal(previous.ContractIdentity, successor.GetProperty("supersedes").GetString());
        Assert.NotEqual(previous.Identity, successor.GetProperty("identity").GetString());
        Assert.Equal("implementation-revision", successor.GetProperty("declaredRevision").GetString());
        Assert.Equal("implement", successor.GetProperty("plan").GetProperty("mode").GetString());
        Assert.Equal("implement", successor.GetProperty("plan").GetProperty("requestedCeiling").GetString());
        Assert.Contains(application.RetainedExternalCalls, row => row.Kind == "ActualPreviousPlanImport");
    }

    [Theory]
    [InlineData("missing-expectations")]
    [InlineData("planning-intent")]
    [InlineData("planning-maximum")]
    public async Task Supersession_without_independent_implementation_authority_refuses_before_previous_bundle_or_Git(string scenario)
    {
        var fixture = new Fixture();
        if (scenario == "planning-intent") fixture.Intent = fixture.Intent with { Request = fixture.Intent.Request with { Mode = "plan", RequestedCeiling = "plan" } };
        if (scenario == "planning-maximum") fixture.Configuration = fixture.Configuration with { MaximumCeiling = "plan" };
        fixture.Save(); var application = new ToolApplication(); ToolResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await application.ExecuteAsync(fixture.Command(), TestContext.Current.CancellationToken));
        if (scenario == "missing-expectations") { Assert.Null(error); Assert.NotNull(result); Assert.Equal("PLAN_EXPECTATIONS_REQUIRED", result.Code); }
        else Assert.IsType<ToolUsageException>(error);
        Assert.Empty(application.RetainedExternalCalls); Assert.Empty(application.RetainedAssignments);
        Assert.False(File.Exists(fixture.Output)); Assert.False(File.Exists(fixture.Previous));
    }
}
