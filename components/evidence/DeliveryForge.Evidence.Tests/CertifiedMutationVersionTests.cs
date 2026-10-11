using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Evidence.Tests;

public sealed class CertifiedMutationVersionTests
{
    [Fact]
    public async Task Complete_prerelease_and_metadata_are_retained_as_fixture_probe_data()
    {
        var manifestPath = Environment.GetEnvironmentVariable("DELIVERY_FORGE_MUTATION_CORPUS_MANIFEST");
        var stdout = "0.1.0-local.format-only+format-only";
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken));
            var entry = manifest.RootElement.GetProperty("version");
            var bytes = await File.ReadAllBytesAsync(entry.GetProperty("path").GetString()!, TestContext.Current.CancellationToken);
            Assert.Equal(entry.GetProperty("length").GetInt64(), bytes.LongLength);
            Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            stdout = Encoding.UTF8.GetString(bytes);
        }
        using var temp = new TempDirectory();
        var executable = Path.Combine(temp.Path, "format-only-probe");
        await File.WriteAllTextAsync(executable, "FORMAT ONLY: this file is never executed", TestContext.Current.CancellationToken);
        // Explicit replay fixture tests parsing of captured probe text, not installed capability.
        var detector = new ToolCapabilityDetector(new QueueCommandExecutor(
            new(0, stdout, "", false, false, TestEvidence.Time(0), TestEvidence.Time(1)),
            new(0, "FORMAT ONLY structured-report help", "", false, false, TestEvidence.Time(1), TestEvidence.Time(2))));
        var detected = await detector.DetectAsync(new("mutate4csharp", ExecutableSource.TrustedExplicit,
            executable, temp.Path, null, ["--version"], ["--help"], "0.1.0", "2", true), TestContext.Current.CancellationToken);
        Assert.True(detected.Capability.Supported);
        Assert.Equal(stdout.Trim(), detected.Capability.Version);
        Assert.True(detected.Capability.Fixture);
        Assert.Contains('+', detected.Capability.Version);
        Assert.Contains('-' , detected.Capability.Version);
    }
}
