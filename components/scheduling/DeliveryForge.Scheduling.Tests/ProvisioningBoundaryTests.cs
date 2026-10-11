using DeliveryForge.Execution.Git;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed class ProvisioningBoundaryTests
{
    [Fact]
    public async Task Provisioning_does_not_execute_repository_post_checkout_hook()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var head = await repository.GitAsync(repository.Checkout, "rev-parse", "HEAD");
        var marker = Path.Combine(repository.Root, "hook-marker");
        var hook = Path.Combine(repository.Checkout, ".git", "hooks", "post-checkout");
        var portableMarker = marker.Replace('\\', '/').Replace("'", "'\\''");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nprintf triggered > '" + portableMarker + "'\n", TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await new WorktreeManager().PrepareAsync(repository.Checkout, Path.Combine(repository.Root, "worker"), head, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(marker));
    }
}
