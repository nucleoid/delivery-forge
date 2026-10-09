using System.Diagnostics;
using DeliveryForge.Execution;

namespace DeliveryForge.Execution.Tests.Processes;

public sealed class ProcessControlTests
{
    [Fact]
    public async Task Quiesces_only_a_positively_identified_owned_child()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        await using var control = new ProcessControl();
        var launch = OperatingSystem.IsWindows()
            ? new ProcessLaunch("ping.exe", ["127.0.0.1", "-n", "30"])
            : new ProcessLaunch("/bin/sleep", ["30"]);
        var child = control.StartOwned(launch);
        var mismatch = child.Identity with { ArgumentDigest = new string('0', 64) };

        Assert.True(System.IO.Path.IsPathFullyQualified(child.Identity.ExecutablePath));
        Assert.True(File.Exists(child.Identity.ExecutablePath));
        Assert.Equal(ProcessControlOutcome.LiveOwned, control.Observe(child.Identity).Outcome);

        var refused = await control.QuiesceAsync(mismatch, TimeSpan.FromMilliseconds(100), cancellationToken);
        Assert.Equal(ProcessControlOutcome.IdentityUnknown, refused.Outcome);
        Assert.False(child.Process.HasExited);

        var stopped = await control.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(ProcessControlOutcome.Quiesced, stopped.Outcome);
        Assert.True(child.Process.HasExited);
    }
}
