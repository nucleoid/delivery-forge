using DeliveryForge.Evidence;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;

public sealed class OwnedLinuxOrdinaryCaptureTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();
    [Fact(Timeout = 90000, Skip = "Creation-owned Linux scope requires Linux.", SkipUnless = nameof(IsLinux))]
    public async Task Genuine_ordinary_capture_reuses_creation_scope_and_original_work_before_foreign_budget_refusal()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity(); using var original = new ToolLifetime(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var manifest = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DUPLEX_FIXTURE_MANIFEST") ?? throw new IOException("Actual fresh separately compiled fixture manifest required.");
        var fixture = BoundedFiles.ReadJson<OwnedDuplexProcessTests.FixtureBinding>(manifest, original, 65536); Assert.Equal(5, fixture.Files.Count);
        foreach (var pin in fixture.Files) { var bytes = BoundedFiles.Read(pin.Path, original); Assert.Equal(pin.Bytes, bytes.LongLength); Assert.Equal(pin.Sha256, BoundedFiles.Sha(bytes)); }
        var scopePath = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LINUX_SCOPE_PROFILE") ?? throw new IOException("Actual selected Linux capability profile required."); var scope = BoundedFiles.ReadJson<LinuxScopeSelection>(scopePath, original, 65536); var executor = new ProcessCommandExecutor(scope, original);
        var directory = Path.Combine(Path.GetTempPath(), "forge-ordinary-scope-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var nonce = Guid.NewGuid().ToString("N");
        var command = new CommandInvocation(fixture.Image, ["fast-exit", directory, nonce], directory, ActualOrdinaryCapture.ResourceEnvironment()); var start = original.OriginalStarted;
        // Two real native births/captures/closes under one actual parent's WORK.
        // A successful subordinate may not silently replace/restart the parent.
        for (var index = 0; index < 2; index++)
        {
            var actual = await executor.ExecuteBoundedCaptureAsync(command, TimeSpan.FromSeconds(15), 65536, TestContext.Current.CancellationToken, new OwnedWorkPhaseBudget(original));
            ActualOrdinaryCapture.Require(actual); Assert.Equal(0, actual.Command.ExitCode); Assert.Contains("READY|", System.Text.Encoding.UTF8.GetString(actual.StandardOutputBytes)); Assert.Equal(start, original.OriginalStarted); original.CheckWork();
        }
        using var foreign = new ToolLifetime(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteBoundedCaptureAsync(command, TimeSpan.FromSeconds(15), 65536, TestContext.Current.CancellationToken, foreign)); original.CheckWork();
        // Empty owned test directory only; no recursive disappearance of evidence.
        Assert.Empty(Directory.GetFileSystemEntries(directory)); Directory.Delete(directory);
    }
}
