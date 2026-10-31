using DeliveryForge.Evidence;
using DeliveryForge.Execution.Host;
using DeliveryForge.Review;

namespace DeliveryForge.Execution.Tests;

// Direct metadata guards only; these controls issue no source or runtime authority.
public sealed class ReviewSourcePathTests
{
    public static bool Windows => OperatingSystem.IsWindows();

    [Fact]
    public void Plain_accepts_ordinary_entries_and_missing_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-review-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "ordinary.txt");
        File.WriteAllText(file, "ordinary fixture");
        TestContext.Current.TestOutputHelper!.WriteLine("Retained metadata fixture: " + root);
        ProductionReviewSourceOwner.Plain(root);
        ProductionReviewSourceOwner.Plain(file);
        foreach (var missing in new[] { Path.Combine(root, "missing"), Path.Combine(root, "missing-directory", "child") })
        {
            Assert.False(File.Exists(missing));
            Assert.False(Directory.Exists(missing));
            if (Windows) Assert.Equal((FileAttributes)(-1), new FileInfo(missing).Attributes);
            ProductionReviewSourceOwner.Plain(missing);
        }
    }

    [Fact(Timeout = 30000, SkipUnless = nameof(Windows), Skip = "Actual Windows junction metadata requires Windows.")]
    public async Task Plain_refuses_live_junction_leaf_ancestor_and_dangling_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-review-plain-junction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        TestContext.Current.TestOutputHelper!.WriteLine("Retained junction fixture: " + root);
        var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var capture = await new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(
            new(command, ["/d", "/c", "mklink", "/J", link, target], root),
            TimeSpan.FromSeconds(10), 4096, TestContext.Current.CancellationToken);
        Assert.Equal(0, capture.Command.ExitCode);
        Assert.True(capture.Command.OwnedProcessQuiescent);
        Assert.True(capture.RootExitObserved);
        Assert.True(capture.StandardOutputDrainCompleted);
        Assert.True(capture.StandardErrorDrainCompleted);
        Assert.True(capture.Command.LifecycleDiagnostics?.OwnedTree is { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 });
        Assert.True(Directory.Exists(link));
        Assert.True((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);
        foreach (var redirected in new[] { link, Path.Combine(link, "child") })
            Assert.Equal("Owned source path is redirected.", Assert.Throws<ReviewPreflightException>(() => ProductionReviewSourceOwner.Plain(redirected)).Message);

        // Nonrecursive deletion of the exact empty owned target leaves a real dangling junction.
        Directory.Delete(target);
        var dangling = new FileInfo(link);
        Assert.False(Directory.Exists(target));
        Assert.False(dangling.Exists);
        Assert.NotEqual((FileAttributes)(-1), dangling.Attributes);
        Assert.True((dangling.Attributes & FileAttributes.ReparsePoint) != 0);
        foreach (var redirected in new[] { link, Path.Combine(link, "child") })
            Assert.Equal("Owned source path is redirected.", Assert.Throws<ReviewPreflightException>(() => ProductionReviewSourceOwner.Plain(redirected)).Message);

        Directory.Delete(link); // Nonrecursive unlink; never traverse the junction target.
        Assert.False(Directory.Exists(link));
        ProductionReviewSourceOwner.Plain(link);
    }
}
