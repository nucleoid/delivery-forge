namespace DeliveryForge.Evidence.Tests;

public sealed class CapabilityDetectorTests
{
    [Fact]
    public async Task Documented_json_version_probe_retains_exact_build_metadata()
    {
        using var temp = new TempDirectory();
        var executable = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "tool.exe" : "tool");
        await File.WriteAllTextAsync(executable, "fixture", TestContext.Current.CancellationToken);
        var report = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Crap4CSharp", "not-applicable.json"), TestContext.Current.CancellationToken);
        var detector = new ToolCapabilityDetector(new QueueCommandExecutor(
            new CommandResult(0, report, "", false, false, TestEvidence.Time(0), TestEvidence.Time(1)),
            new CommandResult(0, "--format json --syntax-only", "", false, false, TestEvidence.Time(1), TestEvidence.Time(2))));

        var result = await detector.DetectAsync(new CapabilityProbe(
            "crap4csharp", ExecutableSource.TrustedExplicit, executable, temp.Path, null,
            ["analyze", "--syntax-only", "--format", "json", "Empty.cs"], ["--help"],
            "0.1.0", "1.0", true), TestContext.Current.CancellationToken);

        Assert.True(result.Capability.Supported);
        Assert.Equal("0.1.0+1129c9d3280168b294f9fed1542f81ca716c6d53", result.Capability.Version);
        Assert.True(result.Capability.Fixture);
        Assert.StartsWith("sha256:", result.Capability.ExecutableIdentity);
    }

    [Fact]
    public async Task Trusted_explicit_probe_binds_version_help_and_executable_hash()
    {
        using var temp = new TempDirectory();
        var executable = System.IO.Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "tool.exe" : "tool");
        await File.WriteAllTextAsync(executable, "fixture", TestContext.Current.CancellationToken);
        var detector = new ToolCapabilityDetector(new QueueCommandExecutor(
            new CommandResult(0, "1.2.3", "", false, false, TestEvidence.Time(0), TestEvidence.Time(1)),
            new CommandResult(0, "--report json-v1", "", false, false, TestEvidence.Time(1), TestEvidence.Time(2))));

        var result = await detector.DetectAsync(new CapabilityProbe(
            "crap4csharp", ExecutableSource.TrustedExplicit, executable, null, null,
            ["--version"], ["--help"], "1.2", "1.2", true),
            TestContext.Current.CancellationToken);

        Assert.True(result.Capability.Supported);
        Assert.Equal("1.2.3", result.Capability.Version);
        Assert.StartsWith("sha256:", result.Capability.ExecutableIdentity);
        Assert.Equal(["--version"], result.VersionInvocation!.Arguments);
        Assert.Equal(["--help"], result.HelpInvocation!.Arguments);
    }

    [Fact]
    public async Task Missing_or_mismatched_executable_is_incomplete_capability()
    {
        var detector = new ToolCapabilityDetector(new QueueCommandExecutor());
        var missing = await detector.DetectAsync(new CapabilityProbe(
            "mutate4csharp", ExecutableSource.TrustedExplicit,
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            null, null, ["--version"], ["--help"], "1.", "certified-v1", false),
            TestContext.Current.CancellationToken);

        Assert.False(missing.Capability.Supported);
        Assert.Contains("not found", missing.Capability.Limitations![0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tool_manifest_is_nonproduction_until_entry_package_bytes_are_bound()
    {
        using var temp = new TempDirectory();
        var manifestDirectory = System.IO.Path.Combine(temp.Path, ".config");
        Directory.CreateDirectory(manifestDirectory);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(manifestDirectory, "dotnet-tools.json"),
            """
            {"version":1,"isRoot":true,"tools":{"example.tool":{"version":"1.0.0","commands":["crap4csharp"]}}}
            """,
            TestContext.Current.CancellationToken);
        var detector = new ToolCapabilityDetector(new QueueCommandExecutor());

        var result = await detector.DetectAsync(new CapabilityProbe(
            "crap4csharp", ExecutableSource.RepositoryToolManifest, null, temp.Path,
            ".config/dotnet-tools.json", ["--version"], ["--help"], "1.0", "1.0", false),
            TestContext.Current.CancellationToken);

        Assert.False(result.Capability.Supported);
        Assert.Contains(
            "package bytes",
            result.Capability.Limitations!.Single(),
            StringComparison.OrdinalIgnoreCase);
    }
}
