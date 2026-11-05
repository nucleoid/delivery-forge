using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Tool.Tests;

public sealed class FileCoordinationCheckpointPathTests
{
    public static bool Windows => OperatingSystem.IsWindows();

    [Fact(Timeout = 60000, SkipUnless = nameof(Windows), Skip = "Actual Windows checkpoint metadata requires Windows.")]
    public async Task Ordinary_and_missing_checkpoint_paths_preserve_real_io_readback_and_disposition()
    {
        var root = Directory.CreateTempSubdirectory("df-checkpoint-plain-").FullName;
        TestContext.Current.TestOutputHelper!.WriteLine("Retained checkpoint metadata fixture: " + root);
        var ordinary = Path.Combine(root, "ordinary.txt");
        File.WriteAllText(ordinary, "retained ordinary file");
        var ordinaryError = Assert.Throws<CoordinationProtocolException>(() => new FileCoordinationCheckpointStore(ordinary));
        Assert.Equal("Root must supply an existing empty fresh control directory.", ordinaryError.Message);

        foreach (var missing in new[] { Path.Combine(root, "missing"), Path.Combine(root, "missing-parent", "missing-leaf") })
        {
            Assert.Equal((FileAttributes)(-1), new FileInfo(missing).Attributes);
            var error = Assert.Throws<CoordinationProtocolException>(() => new FileCoordinationCheckpointStore(missing));
            Assert.Equal("Root must supply an existing empty fresh control directory.", error.Message);
            Assert.False(Directory.Exists(missing));
        }
        Assert.False(Directory.Exists(Path.Combine(root, "missing-parent")));

        var control = Path.Combine(root, "control");
        Directory.CreateDirectory(control);
        var writer = Path.Combine(control, "parent-writer.lock");
        await using (var store = new FileCoordinationCheckpointStore(control))
        {
            var claim = await store.ClaimWriterAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken);
            var record = new CoordinationCheckpointRecord(1, 1, 1, CoordinationIntentKind.Acquire, claim.WriterEpoch, 1, null, null);
            await store.AppendAsync(claim, 1, record, TestContext.Current.CancellationToken);
            Assert.Equal(record, Assert.Single(await store.ReadLifecycleAsync(claim.LifecycleId, TestContext.Current.CancellationToken)));
            Assert.Single(Directory.GetFiles(Path.Combine(control, claim.LifecycleId), "*-record.json"));
            Assert.Throws<IOException>(() => new FileStream(writer, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        using var reacquired = new FileStream(writer, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(reacquired.SafeFileHandle.IsClosed);
    }

    [Fact(Timeout = 60000, SkipUnless = nameof(Windows), Skip = "Actual Windows junction metadata requires Windows.")]
    public async Task Checkpoint_paths_refuse_existing_and_dangling_junction_leaf_and_ancestor()
    {
        var root = Directory.CreateTempSubdirectory("df-checkpoint-junction-").FullName;
        TestContext.Current.TestOutputHelper!.WriteLine("Retained checkpoint junction fixture: " + root);
        var target = Path.Combine(root, "target");
        var child = Path.Combine(target, "child");
        var link = Path.Combine(root, "junction");
        Directory.CreateDirectory(child);
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
        Assert.True((new FileInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);
        foreach (var path in new[] { link, Path.Combine(link, "child"), Path.Combine(link, "missing-parent", "missing-leaf") })
        {
            var error = Assert.Throws<CoordinationProtocolException>(() => new FileCoordinationCheckpointStore(path));
            Assert.Equal("Parent coordination checkpoint path is redirected.", error.Message);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(child));
        Assert.False(Directory.Exists(Path.Combine(target, "missing-parent")));

        // Remove only these exact empty owned targets. Retain the real dangling
        // junction and containing fixture for evidence; never recurse through it.
        Directory.Delete(child);
        Directory.Delete(target);
        Assert.False(Directory.Exists(target));
        var dangling = new FileInfo(link);
        Assert.False(dangling.Exists);
        Assert.NotEqual((FileAttributes)(-1), dangling.Attributes);
        Assert.True((dangling.Attributes & FileAttributes.ReparsePoint) != 0);
        foreach (var path in new[] { link, Path.Combine(link, "missing-parent", "missing-leaf") })
        {
            var error = Assert.Throws<CoordinationProtocolException>(() => new FileCoordinationCheckpointStore(path));
            Assert.Equal("Parent coordination checkpoint path is redirected.", error.Message);
        }
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(link, "parent-writer.lock")));
    }
}
