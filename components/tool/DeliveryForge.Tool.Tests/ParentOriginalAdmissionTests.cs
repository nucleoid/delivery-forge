using DeliveryForge.Execution;
using DeliveryForge.Evidence;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// Real lifetime/frame and the production parent's exact original-admission guard.
// No live lease, native result, deployment or production authority is invented.
public sealed class ParentOriginalAdmissionTests
{
    [Fact]
    public async Task Active_bridge_frame_keeps_its_obligation_without_denying_parent_admission()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "forge-parent-guard-" + Guid.NewGuid().ToString("N"))),
            new DeliveryForge.Execution.Git.WorktreeManager(), processes);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("actual-frame", life, coordinator, () => false, false);
        frame.EnterRoute();
        try
        {
            Assert.True(life.HasRetainedUnknown);
            Assert.True(ParentCoordinationHost.OriginalCanAdmitEffects(life));
            Assert.False(frame.CloseWhenSettled());
        }
        finally { frame.LeaveRoute(); Assert.True(frame.CloseWhenSettled()); }
        Assert.False(life.HasRetainedUnknown);
    }

    [Fact(Timeout = 30000)]
    public async Task Actual_Windows_cancellation_at_admission_before_birth_does_not_invent_unknown_resources()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Exercises the actual Windows capture prebirth refusal; Linux scoped proof is separate.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), cancellation.Token);
        var files = new InstalledToolFiles(life, TimeSpan.FromSeconds(20), new InstalledToolHostScope.Credits());
        var admission = new ParentEffectAdmission(new object(), _ => { cancellation.Cancel(); return true; }, _ => { });
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var root = Path.Combine(Path.GetTempPath(), "forge-prebirth-refusal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var executor = new InstalledToolGateProvider.ActualExecutor(files, new Dictionary<string, string>(), shell, admission, "owned");
        await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteAsync(new(shell,
            ["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'must not start'"], root), TimeSpan.FromSeconds(10), cancellation.Token));
        var capture = Assert.Single(executor.Calls).Capture;
        Assert.Equal(CommandStartupState.NotAttempted, capture.Command.LifecycleDiagnostics!.Startup!.Creation);
        Assert.Null(capture.Command.ExitCode);
        Assert.False(capture.RootExitObserved);
        Assert.Empty(capture.StandardOutputBytes);
        Assert.False(life.HasRetainedUnknown);
        Assert.True(admission.AllKnownSettled);
    }
}
