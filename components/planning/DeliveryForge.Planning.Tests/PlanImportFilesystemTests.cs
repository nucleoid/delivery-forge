using DeliveryForge.Evidence;

namespace DeliveryForge.Planning.Tests;

public sealed class PlanImportFilesystemTests
{
    public static bool Windows => OperatingSystem.IsWindows();

    [Fact]
    public void Plain_accepts_ordinary_entries_and_missing_leaf()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-import-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "ordinary.txt");
        File.WriteAllText(file, "ordinary fixture");
        TestContext.Current.TestOutputHelper!.WriteLine("Retained metadata fixture: " + root);
        PlanImportFilesystem.Plain(root);
        PlanImportFilesystem.Plain(file);
        var missing = Path.Combine(root, "missing");
        Assert.False(File.Exists(missing));
        Assert.Equal((FileAttributes)(-1), new FileInfo(missing).Attributes);
        PlanImportFilesystem.Plain(missing);
    }

    [Fact(Timeout = 30000, SkipUnless = nameof(Windows), Skip = "Actual Windows junction metadata requires Windows.")]
    public async Task Plain_refuses_junction_leaf_ancestor_and_dangling_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-import-junction-" + Guid.NewGuid().ToString("N"));
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
        Assert.Throws<PlanningException>(() => PlanImportFilesystem.Plain(link));
        Assert.Throws<PlanningException>(() => PlanImportFilesystem.Plain(Path.Combine(link, "child")));

        // Remove only this exact empty owned target, never recurse through the link.
        Directory.Delete(target);
        var dangling = new FileInfo(link);
        Assert.False(Directory.Exists(link));
        Assert.False(dangling.Exists);
        Assert.NotEqual((FileAttributes)(-1), dangling.Attributes);
        Assert.True((dangling.Attributes & FileAttributes.ReparsePoint) != 0);
        Assert.Throws<PlanningException>(() => PlanImportFilesystem.Plain(link));
        Assert.Throws<PlanningException>(() => PlanImportFilesystem.Plain(Path.Combine(link, "child")));
        Directory.Delete(link); // Nonrecursive unlink of this exact owned junction.
        Assert.False(Directory.Exists(link));
        PlanImportFilesystem.Plain(link);
    }
}
