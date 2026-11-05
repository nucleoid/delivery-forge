namespace DeliveryForge.Evidence.Tests;

// Direct metadata guards only; these controls issue no source or runtime authority.
public sealed class InstalledToolPathTests
{
    public static bool Windows => OperatingSystem.IsWindows();

    [Fact]
    public void Plain_accepts_ordinary_entries_and_missing_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-installed-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "ordinary.txt");
        File.WriteAllText(file, "ordinary fixture");
        TestContext.Current.TestOutputHelper!.WriteLine("Retained metadata fixture: " + root);
        InstalledToolFiles.PlainPath(root);
        InstalledToolFiles.PlainPath(file);
        foreach (var missing in new[] { Path.Combine(root, "missing"), Path.Combine(root, "missing-directory", "child") })
        {
            Assert.False(File.Exists(missing));
            Assert.False(Directory.Exists(missing));
            if (Windows) Assert.Equal((FileAttributes)(-1), new FileInfo(missing).Attributes);
            InstalledToolFiles.PlainPath(missing);
        }
    }

    [Fact(Timeout = 30000, SkipUnless = nameof(Windows), Skip = "Actual Windows junction metadata requires Windows.")]
    public async Task Plain_refuses_live_junction_leaf_ancestor_and_dangling_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-installed-plain-junction-" + Guid.NewGuid().ToString("N"));
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
            Assert.Equal("Linked evidence/payload/input paths are unsupported.", Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.PlainPath(redirected)).Message);

        // Nonrecursive deletion of the exact empty owned target leaves a real dangling junction.
        Directory.Delete(target);
        var dangling = new FileInfo(link);
        Assert.False(Directory.Exists(target));
        Assert.False(dangling.Exists);
        Assert.NotEqual((FileAttributes)(-1), dangling.Attributes);
        Assert.True((dangling.Attributes & FileAttributes.ReparsePoint) != 0);
        foreach (var redirected in new[] { link, Path.Combine(link, "child") })
            Assert.Equal("Linked evidence/payload/input paths are unsupported.", Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.PlainPath(redirected)).Message);

        Directory.Delete(link); // Nonrecursive unlink; never traverse the junction target.
        Assert.False(Directory.Exists(link));
        InstalledToolFiles.PlainPath(link);
    }
}
