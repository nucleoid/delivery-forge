using DeliveryForge.Execution;
using DeliveryForge.Evidence;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// Real lifetime/frame and the production parent's exact original-admission guard.
// No live lease, native result, deployment or production authority is invented.
public sealed class ParentOriginalAdmissionTests
{
    [Fact]
    public void Revoked_admission_blocks_forward_work_but_not_existing_native_disposition_within_work()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var current = true;
        var admission = new ParentEffectAdmission(new object(), _ => current, _ => current = false);
        var files = new InstalledToolFiles(life, TimeSpan.FromSeconds(20), new InstalledToolHostScope.Credits(), admission, "owned");
        current = false;
        Assert.Throws<DeliveryForge.Coordination.CoordinationUncertainException>(() => files.CheckWork());
        Assert.True(files.NativeDispositionRemaining > TimeSpan.Zero);
        Assert.False(files.IsClosing);
        Assert.False(life.HasExplicitUnknown);
    }

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
    public async Task Actual_native_capture_revocation_stops_and_closes_the_original_without_false_unknown()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows original scope; Linux scoped ownership remains separate.");
        using var life = new ToolLifetime(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var current = true;
        var admission = new ParentEffectAdmission(new object(), _ => Volatile.Read(ref current), _ => Volatile.Write(ref current, false));
        var files = new InstalledToolFiles(life, TimeSpan.FromSeconds(30), new InstalledToolHostScope.Credits(), admission, "owned");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var root = Path.Combine(Path.GetTempPath(), "forge-capture-revocation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "actual-child-entered");
        var script = "[IO.File]::WriteAllText('" + marker.Replace("'", "''", StringComparison.Ordinal) + "','entered'); Start-Sleep -Seconds 30";
        var executor = new InstalledToolGateProvider.ActualExecutor(files, new Dictionary<string, string>(), shell, admission, "owned");
        var actual = executor.ExecuteAsync(new(shell, ["-NoProfile", "-NonInteractive", "-Command", script], root), TimeSpan.FromSeconds(20), life.Token);
        try
        {
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!File.Exists(marker) && !actual.IsCompleted && System.Diagnostics.Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(8))
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(marker));
            Volatile.Write(ref current, false);
            await Assert.ThrowsAnyAsync<Exception>(() => actual.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally
        {
            Volatile.Write(ref current, false);
            try { await actual.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); } catch (Exception) { }
        }
        var capture = Assert.Single(executor.Calls).Capture;
        Assert.True(capture.Command.OwnedProcessQuiescent);
        Assert.True(capture.RootExitObserved && capture.StandardOutputDrainCompleted && capture.StandardErrorDrainCompleted);
        Assert.False(life.HasExplicitUnknown);
        Assert.True(admission.AllKnownSettled);
    }

    [Fact(Timeout = 30000)]
    public async Task Actual_unsettled_Windows_creator_frame_propagates_explicit_unknown_to_its_containing_budget()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows attempted creation and retained native frame.");
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "forge-retained-creator-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var capture = await new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(new(Path.Combine(root, "missing.exe"), [], root),
            TimeSpan.FromSeconds(10), 4096, life.Token, new OwnedWorkPhaseBudget(life));
        Assert.Equal(CommandStartupState.AttemptedUnknown, capture.Command.LifecycleDiagnostics!.Startup!.Creation);
        Assert.False(capture.Command.OwnedProcessQuiescent);
        Assert.True(life.HasExplicitUnknown);
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
