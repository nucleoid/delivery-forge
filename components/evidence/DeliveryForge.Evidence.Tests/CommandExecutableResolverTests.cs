using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeliveryForge.Evidence.Tests;

[Collection("Installed tool profile process environment")]
public sealed class CommandExecutableResolverTests
{
    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_legacy_Linux_bare_image_is_selected_before_child_environment_clear(bool complete)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Actual legacy Linux parent PATH and setsid invocation.");
            return;
        }
        var nonce = Guid.NewGuid().ToString("N");
        var marker = "parent-selected-image-" + nonce;
        var root = Path.Combine(Path.GetTempPath(), "forge-parent-path-capture-" + nonce);
        Directory.CreateDirectory(root);
        var name = "forge-parent-image-" + nonce;
        var executable = Path.Combine(root, name);
        var childPath = Path.Combine(root, "nonexistent-child-path-" + nonce);
        await File.WriteAllTextAsync(executable,
            "#!/bin/sh\nprintf '%s\\n%s\\n' \"$FORGE_CAPTURE_MARKER\" \"$PATH\"\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // Existing nonparallel process-environment collection. The unique
            // image exists only in the temporarily selected parent directory;
            // OLD setsid lookup uses childPath and fails before this script.
            Environment.SetEnvironmentVariable("PATH", root + Path.PathSeparator + originalPath);
            var invocation = new CommandInvocation(name, [], root,
                new Dictionary<string, string> { ["FORGE_CAPTURE_MARKER"] = marker, ["PATH"] = childPath }, InheritEnvironment: false)
            { EnvironmentIsComplete = complete };
            var capture = await new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(invocation,
                TimeSpan.FromSeconds(10), 4096, TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper!.WriteLine("Retained parent-selected executable: " + root);
            TestContext.Current.TestOutputHelper!.WriteLine(System.Text.Json.JsonSerializer.Serialize(capture.Command));
            Assert.Equal(0, capture.Command.ExitCode);
            Assert.Equal(marker + "\n" + childPath + "\n", capture.Command.StandardOutput);
            Assert.Equal("", capture.Command.StandardError);
            Assert.False(capture.CaptureBoundExceeded);
            Assert.True(capture.RootExitObserved && capture.StandardOutputDrainCompleted && capture.StandardErrorDrainCompleted);
            Assert.True(capture.Command.OwnedProcessQuiescent); // Legacy observation, not containing-scope authority.
            Assert.Same(capture.Command, capture.ProjectOrdinaryCommand());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void Parent_snapshot_lookup_skips_nonexecutable_candidate_and_resolves_relative_roles_from_parent()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Actual Linux executable access and parent PATH lookup.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "forge-parent-executable-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        var firstFile = Path.Combine(first, "selected-command"); var secondFile = Path.Combine(second, "selected-command");
        File.WriteAllText(firstFile, "#!/bin/sh\nexit 0\n"); File.WriteAllText(secondFile, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(firstFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(secondFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        TestContext.Current.TestOutputHelper!.WriteLine("Retained parent lookup files: " + root);
        Assert.Equal(secondFile, CommandExecutableResolver.ResolveLinuxParentSnapshot("selected-command", first + ":" + second, root));
        Assert.Equal(secondFile, CommandExecutableResolver.ResolveLinuxParentSnapshot("second/selected-command", "", root));
        Assert.Throws<FileNotFoundException>(() => CommandExecutableResolver.ResolveLinuxParentSnapshot("missing-command", first + ":" + second, root));
        Assert.Throws<ArgumentException>(() => CommandExecutableResolver.ResolveLinuxParentSnapshot("selected-command", new string('x', 32769), root));
        Assert.Throws<ArgumentException>(() => CommandExecutableResolver.ResolveLinuxParentSnapshot("selected-command", new string(':', 1024), root));
    }

    [Fact(Timeout = 15000)]
    public async Task Actual_absent_legacy_group_does_not_prove_the_original_root_exited()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Actual Linux ESRCH and original root observation.");
        using var original = new Process
        {
            StartInfo = new ProcessStartInfo("/bin/sh")
            { UseShellExecute = false, RedirectStandardInput = true }
        };
        original.StartInfo.ArgumentList.Add("-c"); original.StartInfo.ArgumentList.Add("read ignored; exit 0");
        Assert.True(original.Start());
        try
        {
            Assert.False(original.HasExited);
            var originalGroup = getpgid(original.Id);
            Assert.True(originalGroup > 0);
            Assert.NotEqual(original.Id, originalGroup);
            // This original inherits the test host's existing group. It never
            // enters setsid, so -original.Id is absent; no positive PID signal.
            Assert.False(BoundedProcessCapture.KillOwnedProcessGroup(original, out var failure));
            Assert.Contains("original root remains live", failure);
            Assert.False(original.HasExited);
        }
        finally
        {
            original.StandardInput.Close();
            await original.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.Equal(0, original.ExitCode);
        // Never signal a negative PID after the original exit was observed.
        // Root exit/EOF remains observational, not group ownership authority.
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int processId);
}
