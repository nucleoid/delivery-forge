using System.Text.Json;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence.Tests;

// Genuine future process tests, never satisfied by the report-format fixtures. Root separately
// prepares clean exact-head consumers and reviewed installs. These tests are UNEXECUTED.
public sealed class RealInstalledQualityToolTests
{
    [Theory]
    [InlineData("crap-pass")]
    [InlineData("crap-policy-fail")]
    [InlineData("crap-tests-fail")]
    [InlineData("mutation-killed")]
    [InlineData("mutation-survivor")]
    [InlineData("mutation-zero")]
    [Trait("Category", "InstalledToolAcceptance")]
    public async Task Real_installed_process_report_and_current_source_are_joined(string scenario)
    {
        var path = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST");
        var identity = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST_SHA256");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(identity))
            Assert.Skip("Real installed-provider acceptance requires separately reviewed Root-prepared fixtures; DATA fixtures cannot substitute.");
        var io = new InstalledToolFiles(TimeSpan.FromSeconds(30));
        var bytes = io.Read(path!, 1024 * 1024); Assert.Equal(identity, InstalledToolFiles.Identity(bytes));
        using var manifest = ProducerJson.Read(bytes);
        var item = manifest.RootElement.GetProperty("scenarios").GetProperty(scenario);
        var profile = InstalledToolProfile.LoadPinned(item.GetProperty("profilePath").GetString()!, item.GetProperty("profileSha256").GetString()!);
        Assert.True(profile.Fixture); // Test consumers never issue real Forge gate authority.
        var root = item.GetProperty("root").GetString()!; var frozenBase = item.GetProperty("base").GetString()!;
        var expected = new RepositoryIdentity(frozenBase, item.GetProperty("head").GetString()!, item.GetProperty("tree").GetString()!, true);
        var policy = await new ProtectedGitPolicyResolver(new ProcessCommandExecutor(), root, frozenBase)
            .ResolveAsync(item.GetProperty("policyRelativePath").GetString()!, TestContext.Current.CancellationToken);
        var request = new InstalledToolGateRequest(item.GetProperty("gateId").GetString()!, policy, expected,
            item.GetProperty("scope").GetString()!, item.GetProperty("project").ValueKind == JsonValueKind.Null ? null : item.GetProperty("project").GetString(),
            TimeSpan.FromSeconds(item.GetProperty("timeoutSeconds").GetInt32()), item.GetProperty("output").GetString()!);
        var result = await new InstalledToolGateProvider(root, frozenBase).RunAsync(profile, request, TestContext.Current.CancellationToken);
        Assert.False(result.SourceChanged); Assert.True(result.Execution.OwnedProcessQuiescent);
        Assert.Equal(item.GetProperty("exit").GetInt32(), result.Execution.ExitCode);
        Assert.True(result.Fixture); Assert.False(result.ProductionCapable);
        var gateDirectory = Path.GetDirectoryName(Path.GetDirectoryName(result.ReceiptPath))!;
        Assert.All(result.ArtifactHashes, row =>
        {
            var retained = Path.Combine(gateDirectory, row.Key); Assert.True(File.Exists(retained));
            Assert.Equal(row.Value, InstalledToolFiles.Identity(io.Read(retained, 128 * 1024 * 1024)));
        });
        var report = File.ReadAllBytes(Path.Combine(gateDirectory, "producer-report.json"));
        var capability = new ToolCapability(profile.Tool, profile.Version, profile.Data.Format, true, true);
        var normalized = profile.Tool == "mutate4csharp" ? Mutate4CSharpAdapter.NormalizeCurrent(report, capability, result.Execution.ExitCode!.Value) :
            Crap4CSharpAdapter.NormalizeCurrent(report, capability, result.Execution.ExitCode!.Value);
        Assert.Equal(Enum.Parse<GateOutcome>(item.GetProperty("outcome").GetString()!, true), normalized.Outcome);
        // Genuine fixture PASS is still not a verified final gate or publication authority.
        if (normalized.Outcome == GateOutcome.Pass) Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("producer-current-binding.json", result.ArtifactHashes.Keys);
    }
}