using System.Security.Cryptography;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Scheduling;

namespace DeliveryForge.EndToEnd.Tests;

internal static class SchedulingScenarioFixture
{
    public static RunDemand Demand => new(new(1, 1, 512, 0, 0, 0), [], TimeSpan.FromSeconds(120), 100, 0m);
    public static ResourceBudget Budget()
    {
        var slots = new ResourceSlots(2, 2, 4096, 1, 1, 0);
        return ResourceBudget.Measure(slots, new("parent-approved-e2e-fixture-budget", true, slots,
            TimeSpan.FromSeconds(120), 1000, 0m));
    }

    public static async Task<FrozenPlan> FreezeAsync(OwnedScenarioRun run, CancellationToken cancellationToken, string? prerequisite = null)
    {
        var context = await run.Reader.ReadAsync(run.Checkout, "refs/heads/main", cancellationToken);
        var file = await run.Reader.ReadFileAsync(context, "subject.txt", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var request = new PlanningRequest("fixture/delivery-forge", "#11", "implement", "Exercise public local admission",
            ["subject.txt"], ["excluded"], ["Exact source and finite fixture budget"], "implement");
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, now, [])];
        DependencyNode[] dependencies = prerequisite is null ? [new("#11", [], "root")] :
            [new("prerequisite", [], prerequisite), new("#11", ["prerequisite"], "root")];
        var draft = new PlanDraft(request, context, evidence, [new("subject.txt", "fixture", "edit subject")], dependencies,
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], IntakePlanner.Assess(request, evidence));
        return PlanFreezer.Freeze(draft, "fixture-scheduling-" + Guid.NewGuid().ToString("N"), now);
    }

    public static FrozenWorkerPlan Worker(OwnedScenarioRun run, FrozenPlan plan, string suffix) => new(
        run.RunId + "-" + suffix, plan.ContractIdentity, plan.BaseCommit, plan.Repository, run.Checkout,
        Path.Combine(run.Root, "worktree-" + suffix), Path.Combine(run.Root, "worker-results", suffix),
        Guid.NewGuid().ToString("N"), ["subject.txt"], ["excluded"], "fixture-agent", "1",
        [AgentCapability.Launch, AgentCapability.CompletionEvidence]);

    public static async Task<string> CreateTopicAsync(OwnedScenarioRun run, CancellationToken cancellationToken)
    {
        await run.GitAsync(cancellationToken, "checkout", "-q", "-b", "topic");
        await File.WriteAllTextAsync(Path.Combine(run.Checkout, "subject.txt"), "topic prerequisite fixture\n", cancellationToken);
        await CommitAsync(run, "topic prerequisite", cancellationToken);
        var candidate = await run.GitAsync(cancellationToken, "rev-parse", "HEAD");
        await run.GitAsync(cancellationToken, "checkout", "-q", "main");
        return candidate;
    }

    public static async Task CommitAsync(OwnedScenarioRun run, string message, CancellationToken cancellationToken)
    {
        await run.GitAsync(cancellationToken, "add", "--", "subject.txt");
        await run.GitAsync(cancellationToken, "-c", "user.name=EndToEndFixture", "-c", "user.email=fixture@example.invalid",
            "commit", "-q", "-m", message);
    }

    public static async Task SpecAsync(OwnedScenarioRun run, string scenario, CancellationToken cancellationToken)
    {
        var source = await File.ReadAllBytesAsync(Path.Combine(run.Root, "source.json"), cancellationToken);
        await run.WriteNewJsonAsync("scheduling-spec.json", new
        {
            fixture = true,
            productionCertified = false,
            firstFullRealRunCertified = false,
            scenario,
            integratedBaseCommit = OwnedScenarioRun.InitialIntegratedBase,
            sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source)),
            seed = 11017,
            scenarioSeconds = 120,
            gitCommandSeconds = 15,
            childSeconds = 30,
            cpu = 2,
            memoryMiB = 4096,
            tokens = 1000,
            cost = 0,
            limitation = "Fixture bookkeeping and actual Git/file/owned native sleep only; no model worker, quality PASS or production ownership."
        }, cancellationToken);
    }
}
