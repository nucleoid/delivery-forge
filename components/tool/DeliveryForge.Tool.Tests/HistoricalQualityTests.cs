using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// Lifecycle and refusal controls only. No gate, native worker, Continuum lease,
// production profile or baseline approval is supplied by these DATA tests.
public sealed class HistoricalQualityTests
{
    static TrustedConfiguration Configuration()
    {
        var root = Path.Combine(Path.GetTempPath(), "historical-quality-data-" + Guid.NewGuid().ToString("N"));
        var profile = new AdmissionFile(Path.Combine(root, "external-profile.json"), 1, new string('a', 64));
        var quality = new InstalledQualitySelection("quality/policy.json", "sha256:" + new string('b', 64), Path.Combine(root, "artifacts"),
            [new("mutation", "mutate4csharp", profile, "base", null, 10), new("callables", "crap4csharp", profile, "all", "tests/Execution.Tests.csproj", 10)]);
        return new("nucleoid/delivery-forge", Path.Combine(root, "parent"), "refs/heads/main", Environment.ProcessPath!,
            MaximumCeiling: "implement", HistoricalQuality: new(new string('1', 40), new string('2', 40), quality));
    }

    [Theory]
    [InlineData("head")]
    [InlineData("tree")]
    [InlineData("registry")]
    [InlineData("mixed-current-quality")]
    [InlineData("gate-count")]
    public void Historical_selection_refuses_ambiguous_or_authoritative_inputs(string changed)
    {
        var config = Configuration(); var historical = config.HistoricalQuality!;
        HistoricalQualityHost.ValidateSelection(config, historical);
        if (changed == "head") historical = historical with { HeadCommit = "HEAD" };
        if (changed == "tree") historical = historical with { TreeId = new string('G', 40) };
        if (changed == "registry") historical = historical with { Quality = historical.Quality with {
            Registry = new(new string('3', 40), new string('4', 40), "refs/heads/main", "registry.json", "sha256:" + new string('c', 64),
                Path.Combine(Path.GetTempPath(), "registry-artifacts"), new("unused", 1, new string('d', 64))) } };
        if (changed == "mixed-current-quality") config = config with { InstalledQuality = historical.Quality };
        if (changed == "gate-count") historical = historical with { Quality = historical.Quality with { Gates = historical.Quality.Gates.Take(1).ToArray() } };
        Assert.Throws<ToolUsageException>(() => HistoricalQualityHost.ValidateSelection(config, historical));
    }

    [Fact]
    public async Task Explicit_mode_and_independent_plan_expectations_refuse_before_any_external_effect()
    {
        var path = Path.Combine(Path.GetTempPath(), "historical-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(Configuration(), BoundedFiles.Json));
        try
        {
            var application = new ToolApplication();
            await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse(["implement", "--plan", "/must-not-read", "--config", path]), TestContext.Current.CancellationToken));
            var result = await application.ExecuteAsync(CommandLine.Parse(["quality-baseline", "--plan", "/must-not-read", "--config", path]), TestContext.Current.CancellationToken);
            Assert.Equal("PLAN_EXPECTATIONS_REQUIRED", result.Code); Assert.False(result.RuntimeCertified);
            Assert.Empty(application.RetainedExternalCalls); Assert.Empty(application.RetainedAssignments);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Actual_quality_task_retains_original_until_settled_without_worker_completion()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "historical-store-" + Guid.NewGuid().ToString("N"))), new WorktreeManager(), processes);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("historical-task-control", original, coordinator, () => false, false);
        var pending = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        frame.EnterRoute();
        frame.Quality.Start(original, () => frame.Enter(() => pending.Task));
        frame.LeaveRoute();
        HistoricalQualityHost.RequireNoWorker(frame);
        Assert.False(frame.Worker.Started); Assert.True(frame.Quality.Unknown); Assert.False(frame.CloseWhenSettled());
        Assert.Same(pending.Task, Assert.Single(frame.EnteredTasks)); Assert.True(original.HasRetainedUnknown);
        pending.SetResult(ToolResult.Incomplete("DATA_ONLY", "No real gates executed by this control."));
        var result = await frame.Quality.ObserveAsync(original);
        Assert.Equal("INCOMPLETE", result.Outcome); Assert.False(result.RuntimeCertified);
        HistoricalQualityHost.RequireNoWorker(frame);
        Assert.True(frame.CloseWhenSettled()); Assert.False(original.HasRetainedUnknown);
        Assert.Null(frame.Request); Assert.Null(frame.Accepted); Assert.Null(frame.Completion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_phase_or_owner_completion_cannot_be_relabelled_historical(bool completed)
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "historical-refusal-" + Guid.NewGuid().ToString("N"))), new WorktreeManager(), processes);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("historical-refusal", original, coordinator, () => false, false);
        if (completed) frame.OwnerCompleted = true;
        else frame.Worker.Start(original, () => Task.FromResult(ToolResult.Incomplete("DATA_ONLY", "No native worker.")));
        Assert.Throws<DeliveryForge.Execution.WorkerEnvelopeException>(() => HistoricalQualityHost.RequireNoWorker(frame));
    }
}
