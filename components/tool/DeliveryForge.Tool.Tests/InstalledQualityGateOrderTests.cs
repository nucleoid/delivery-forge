using System.Text.Json;
using DeliveryForge.Evidence;

namespace DeliveryForge.Tool.Tests;

// Configuration acceptance only: these nonexecutable inputs issue no baseline,
// host capability or installed-tool result.
public sealed class InstalledQualityGateOrderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Protected_comparison_requires_callable_gate_to_finish_last(bool callableLast)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-quality-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var profile = Path.Combine(root, "profile-data.json");
        File.WriteAllText(profile, "NONEXECUTABLE configuration-test DATA");
        var pin = new AdmissionFile(profile, new FileInfo(profile).Length, new string('a', 64));
        var registry = new ProtectedBaselineRegistrySelection(new string('b', 40), new string('c', 40),
            "refs/heads/main", "quality/registry.json", "sha256:" + new string('d', 64),
            Path.Combine(root, "historical-artifacts"), new(profile, pin.Bytes, pin.Sha256));
        var crap = new InstalledQualityGateSelection("callables", "crap4csharp", pin, "all", "tests/Subject.Tests.csproj", 120);
        var mutation = new InstalledQualityGateSelection("mutation", "mutate4csharp", pin, "base", null, 600);
        var quality = new InstalledQualitySelection("quality/policy.json", "sha256:" + new string('e', 64),
            Path.Combine(root, "current-artifacts"), callableLast ? [mutation, crap] : [crap, mutation], registry);
        var config = new TrustedConfiguration("fixture/repository", root, "refs/heads/main", profile,
            InstalledQuality: quality);
        var path = Path.Combine(root, "trusted-config.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config));
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (callableLast)
            Assert.Equal("crap4csharp", TrustedConfiguration.Load(path, life).InstalledQuality!.Gates[^1].Tool);
        else
        {
            var error = Assert.Throws<ToolUsageException>(() => TrustedConfiguration.Load(path, life));
            Assert.Contains("callable gate last", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(Directory.Exists(quality.ArtifactRoot));
        Assert.False(Directory.Exists(registry.ArtifactRoot));
        Assert.Equal("NONEXECUTABLE configuration-test DATA", File.ReadAllText(profile));
    }
}
