using DeliveryForge.Evidence;

namespace DeliveryForge.Tool.Tests;

// An actual unstarted review host has no object-view callback or native owner.
// This only proves pre-entry refusal does not consume its first disposition.
public sealed class ReviewDispositionBoundaryTests
{
    [Fact]
    public void Closed_WORK_refuses_before_review_disposition_entry_and_same_original_cleanup_can_close_unstarted_host()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "forge-review-close-boundary-" + Guid.NewGuid().ToString("N"));
        var git = Path.Combine(root, "unlaunched-git.exe");
        var selection = new InstalledReviewSelection("review.json", "evidence.json", "sha256:" + new string('a', 64),
            new(git, 1, new string('a', 64)), "git version boundary-only", Path.Combine(root, "artifacts"));
        var configuration = new TrustedConfiguration("https://github.com/fixture/boundary", root, "refs/heads/main", git,
            InstalledReview: selection);
        var host = new InstalledReviewHost(configuration, new(Path.Combine(root, "config.json"), 1, new string('a', 64)),
            original, new ProcessCommandExecutor());
        var started = original.OriginalStarted; var whole = original.WholeLimit;
        original.BeginFailureCleanup();
        Assert.ThrowsAny<Exception>(host.DisposeDuringWork);
        Assert.False(host.DispositionEntered); Assert.False(host.DispositionReturned);
        Assert.False(Directory.Exists(root)); Assert.False(original.HasExplicitUnknown);
        host.Dispose();
        Assert.True(host.DispositionEntered); Assert.True(host.DispositionReturned);
        host.Dispose();
        Assert.Equal(started, original.OriginalStarted); Assert.Equal(whole, original.WholeLimit);
        Assert.True(original.CleanupRemaining > TimeSpan.Zero); Assert.False(original.HasExplicitUnknown);
        Assert.ThrowsAny<Exception>(original.Check);
    }
}
