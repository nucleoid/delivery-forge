using DeliveryForge.Execution.Git;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed class ProvisioningBoundaryTests
{
    [Fact]
    public async Task Provisioning_and_observation_do_not_execute_configured_filters()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var marker = Path.Combine(repository.Root, "filter-marker");
        var filter = Path.Combine(repository.Root, "filter.sh");
        var portableMarker = marker.Replace('\\', '/').Replace("'", "'\\''");
        await File.WriteAllTextAsync(filter, "#!/bin/sh\nprintf triggered > '" + portableMarker + "'\ncat\n", TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(filter, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await repository.GitAsync(repository.Checkout, "config", "filter.probe.smudge", "\"" + filter.Replace('\\', '/') + "\"");
        await File.WriteAllTextAsync(Path.Combine(repository.Checkout, ".gitattributes"), "*.md filter=probe\n", TestContext.Current.CancellationToken);
        await repository.GitAsync(repository.Checkout, "add", ".gitattributes");
        await repository.GitAsync(repository.Checkout, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "attributes");
        var head = await repository.GitAsync(repository.Checkout, "rev-parse", "HEAD");
        var worker = Path.Combine(repository.Root, "worker");
        var manager = new WorktreeManager(repository.GitExecutable);
        await manager.PrepareAsync(repository.Checkout, worker, head, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(marker));
        await repository.GitAsync(repository.Checkout, "config", "filter.probe.clean", "\"" + filter.Replace('\\', '/') + "\"");
        Assert.True((await manager.ObserveAsync(worker, TestContext.Current.CancellationToken)).IsClean);
        Assert.False(File.Exists(marker));
    }
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
