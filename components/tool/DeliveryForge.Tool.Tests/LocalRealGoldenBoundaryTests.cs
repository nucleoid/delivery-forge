using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

public sealed class LocalRealGoldenBoundaryTests
{
    [Theory]
    [InlineData("Ready")]
    [InlineData("AcceptedReview")]
    [InlineData("BaselineApproved")]
    [InlineData("Adapter")]
    public void Local_real_selection_accepts_locators_only_and_cannot_import_success(string claim)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-golden-config-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var git = Path.Combine(root, "data-not-executable"); File.WriteAllText(git, "DATA");
        var output = Path.Combine(root, "not-created");
        var config = new TrustedConfiguration("fixture/repo", root, "refs/heads/main", git, LocalRealGolden: new(output));
        var path = Path.Combine(root, "config.json"); File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config));
        using var original = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(config.LocalRealGolden, TrustedConfiguration.Load(path, original).LocalRealGolden);
        var node = JsonNode.Parse(File.ReadAllBytes(path))!; node["LocalRealGolden"]![claim] = true;
        File.WriteAllText(path, node.ToJsonString());
        Assert.ThrowsAny<Exception>(() => TrustedConfiguration.Load(path, original)); Assert.False(Directory.Exists(output));
    }
    [Fact]
    public async Task Pending_actual_local_real_phase_retains_same_original_without_ready_data_or_retry()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "forge-golden-phase-" + Guid.NewGuid().ToString("N"))), new WorktreeManager(), processes);
        var original = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("unit-golden", original, coordinator, () => false, false);
        var pending = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        frame.EnterRoute(); frame.LocalReal.Start(original, () => pending.Task); frame.LeaveRoute(); original.Dispose();
        Assert.False(frame.CloseWhenSettled()); Assert.True(original.HasRetainedUnknown); Assert.Same(pending.Task, frame.LocalReal.Actual);
        var observe = frame.LocalReal.ObserveAsync(original); var incomplete = ToolResult.Incomplete("UNIT_ONLY", "No real pipeline was invoked.");
        pending.SetResult(incomplete); Assert.Same(incomplete, await observe); Assert.True(frame.CloseWhenSettled());
        Assert.Throws<ObjectDisposedException>(original.Check);
    }
}
