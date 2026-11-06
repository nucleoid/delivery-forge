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
        internal async Task<FrozenPlan> PreparePrevious(ToolLifetime original)
        {
            var executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
            var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
                .Select(path => Path.Combine(path, executable)).First(File.Exists);
            Configuration = Configuration with { GitExecutable = git };
            var executor = new ProcessCommandExecutor();
            async Task Git(params string[] arguments)
            {
                var nullPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
                var argv = new[] { "--no-replace-objects", "-c", "core.hooksPath=" + nullPath, "-c", "core.fsmonitor=false",
                    "-c", "user.name=SupersessionFixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false" }
                    .Concat(arguments).ToArray();
                var actual = await executor.ExecuteAsync(new(git, argv, Repository, new Dictionary<string, string>
                {
                    ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = nullPath, ["GIT_CONFIG_SYSTEM"] = nullPath,
                    ["GIT_NO_REPLACE_OBJECTS"] = "1", ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_OPTIONAL_LOCKS"] = "0"
                }), original.Remaining, original.Token);
                Assert.True(actual.OwnedProcessQuiescent && actual.ExitCode == 0 && !actual.TimedOut && !actual.Cancelled, actual.StandardError);
                original.Check();
            }
            File.WriteAllText(Path.Combine(Repository, "tracked.txt"), "unchanged repository evidence\n");
            await Git("init", "-q", "-b", "main"); await Git("add", "--", "tracked.txt"); await Git("commit", "-q", "-m", "original base");
            var reader = new GitRepositoryContextReader(git);
            var context = await reader.ReadAsync(Repository, "refs/heads/main", original.Token);
            var file = await reader.ReadFileAsync(context, "tracked.txt", original.Token);
            var request = Intent.Request with { Mode = "plan", RequestedCeiling = "plan" };
            EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, DateTimeOffset.UtcNow, [])];
            var previous = PlanFreezer.Freeze(new(request, context, evidence, Intent.ChangeMap, Intent.Dependencies, Intent.Gates,
                Intent.Rollout, Intent.Unknowns, IntakePlanner.Assess(request, evidence)), "planning-revision", DateTimeOffset.UtcNow);
            File.WriteAllBytes(Previous, previous.CanonicalBytes);
            Configuration = Configuration with { ExpectedBundleIdentity = previous.Identity, ExpectedContractIdentity = previous.ContractIdentity,
                ExpectedWorkItem = "#8", ExpectedPlanCeiling = "plan" };
            Save(); return previous;
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("wrong-identity")]
    [InlineData("wrong-original-ceiling")]
    [InlineData("changed-base")]
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
            case "changed-base": File.AppendAllText(Path.Combine(fixture.Repository, "tracked.txt"), "unreconciled change\n"); break;
            case "same-revision": fixture.Intent = fixture.Intent with { Revision = previous.Revision }; break;
            case "different-work-item": fixture.Intent = fixture.Intent with { Request = fixture.Intent.Request with { WorkItem = "#9" } }; break;
        }
        fixture.Save(); var application = new ToolApplication(actualOriginal: original);
        ToolResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await application.ExecuteAsync(
            fixture.Command(scenario == "same-output" ? fixture.Previous : null), original.Token));
        Assert.Equal(before, File.ReadAllBytes(fixture.Previous));
        Assert.Empty(application.RetainedAssignments);
        if (scenario != "success")
        {
            Assert.True(error is not null || result?.Outcome == "INCOMPLETE");
            Assert.False(File.Exists(fixture.Output)); return;
        }
        Assert.Null(error); Assert.NotNull(result); Assert.Equal("PLAN_FROZEN", result.Code); Assert.False(result.RuntimeCertified);
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
