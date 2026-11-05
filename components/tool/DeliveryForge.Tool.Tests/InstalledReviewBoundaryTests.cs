using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Tool.Tests;

// These boundary tests do not launch a reviewer or issue production acceptance.
public sealed class InstalledReviewBoundaryTests
{
    [Fact]
    public async Task Pending_actual_review_task_retains_the_same_original_until_settlement()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(),
            "forge-review-phase-" + Guid.NewGuid().ToString("N"))), new WorktreeManager(), processes);
        var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("unit-review", life, coordinator, () => false, false);
        var pending = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        frame.EnterRoute(); frame.Review.Start(life, () => pending.Task); frame.LeaveRoute();
        life.Dispose();
        Assert.False(frame.CloseWhenSettled()); Assert.True(life.HasRetainedUnknown);
        Assert.Same(pending.Task, frame.Review.Actual);
        var observation = frame.Review.ObserveAsync(life);
        var incomplete = ToolResult.Incomplete("UNIT_ONLY", "No reviewer result or authority was issued.");
        pending.SetResult(incomplete);
        Assert.Same(incomplete, await observation);
        Assert.True(frame.CloseWhenSettled()); Assert.Throws<ObjectDisposedException>(life.Check);
    }

    [Theory]
    [InlineData("foreign-policy")]
    [InlineData("widened-bound")]
    [InlineData("claimed-acceptance")]
    public void Trusted_selection_cannot_import_acceptance_or_escape_its_protected_inputs(string corruption)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-review-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var git = Path.Combine(root, "nonexecutable-git-data"); File.WriteAllText(git, "DATA only");
        var selection = new InstalledReviewSelection("policy/review.json", "policy/evidence.json",
            "sha256:" + new string('a', 64), new(git, new FileInfo(git).Length, new string('b', 64)),
            "git version 2.50.0", Path.Combine(root, "artifacts"));
        var config = new TrustedConfiguration("fixture/repository", root, "refs/heads/main", git,
            InstalledReview: selection);
        var path = Path.Combine(root, "trusted.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config));
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(selection, TrustedConfiguration.Load(path, life).InstalledReview);
        var changed = JsonNode.Parse(File.ReadAllBytes(path))!;
        switch (corruption)
        {
            case "foreign-policy": changed["InstalledReview"]!["PolicyRelativePath"] = "../foreign.json"; break;
            case "widened-bound": changed["InstalledReview"]!["MaximumEvidenceBytes"] = 16 * 1024 * 1024 + 1; break;
            case "claimed-acceptance": changed["InstalledReview"]!["ReviewerAccepted"] = true; break;
        }
        File.WriteAllText(path, changed.ToJsonString());
        Assert.ThrowsAny<Exception>(() => TrustedConfiguration.Load(path, life));
        Assert.False(Directory.Exists(selection.ArtifactRoot));
        Assert.Equal("DATA only", File.ReadAllText(git));
        Assert.Empty(typeof(ProductionReviewSourceOwner).GetConstructors());
    }
}
