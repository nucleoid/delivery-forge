using DeliveryForge.Tool;
using DeliveryForge.Tool.Packaging;
namespace DeliveryForge.Tool.Tests;

public sealed class ActualPackageContentTests
{
    [Fact(Timeout = 90000)]
    public void Actual_CI_package_passes_installer_content_validation_and_owned_removal()
    {
        // Public CI supplies its real fresh archive. Exercise the real installer
        // without native worker admission or an executable child process.
        var archive = Environment.GetEnvironmentVariable("DELIVERY_FORGE_TOOL_PACKAGE") ?? throw new InvalidOperationException("Actual fresh Tool package is required.");
        var expectedSha = Environment.GetEnvironmentVariable("DELIVERY_FORGE_TOOL_PACKAGE_SHA256") ?? throw new InvalidOperationException("Actual package hash is required.");
        var host = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DOTNET_HOST") ?? throw new InvalidOperationException("Actual explicit runtime host is required.");
        using var life = new ToolLifetime(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.Equal(expectedSha, BoundedFiles.Sha(BoundedFiles.Read(archive, life, 64 * 1024 * 1024)));
        var hostBytes = BoundedFiles.Read(host, life);
        var hostPin = new FilePin(host, hostBytes.Length, BoundedFiles.Sha(hostBytes));
        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-public-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "user.txt");
        File.WriteAllText(outside, "preserve");
        var prefix = Path.Combine(root, "prefix with spaces");
        var receipt = PackageLifecycle.Install(archive, prefix, hostPin, life);
        Assert.NotEmpty(PackageLifecycle.Verify(receipt, life).Files);
        PackageLifecycle.Uninstall(receipt, life);
        Assert.False(Directory.Exists(prefix));
        Assert.Equal("preserve", File.ReadAllText(outside));
        // Keep the original owned removal journal and all failure evidence.
    }
}
