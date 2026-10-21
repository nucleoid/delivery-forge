using System.Xml.Linq;

namespace DeliveryForge.Evidence.Tests;

public sealed class RealDotNetFixtureTests
{
    [Fact(Timeout = 180_000)]
    [Trait("Category", "RealFixture")]
    public async Task Earlier_red_commit_then_clean_green_head_produces_real_test_counts()
    {
        using var temp = new TempDirectory();
        using var evidence = new TempDirectory();
        var fixture = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "RealDotNet");
        CopyTree(fixture, temp.Path);
        File.Copy(FindRepositoryFile("global.json"),
            System.IO.Path.Combine(temp.Path, "global.json"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, ".gitignore"),
            "**/bin/\n**/obj/\n*.trx\n", TestContext.Current.CancellationToken);
        var subject = System.IO.Path.Combine(temp.Path, "Subject", "Calculator.cs");
        var red = await File.ReadAllTextAsync(
            System.IO.Path.Combine(temp.Path, "Subject", "Calculator.red.cs.txt"),
            TestContext.Current.CancellationToken);
        var green = await File.ReadAllTextAsync(
            System.IO.Path.Combine(temp.Path, "Subject", "Calculator.green.cs.txt"),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(subject, red, TestContext.Current.CancellationToken);

        var executor = new ProcessCommandExecutor();
        await GitAsync(executor, temp.Path, ["init"]);
        await GitAsync(executor, temp.Path, ["config", "user.name", "Delivery Forge Fixture"]);
        await GitAsync(executor, temp.Path, ["config", "user.email", "fixture@delivery-forge.invalid"]);
        await GitAsync(executor, temp.Path, ["add", "."]);
        await GitAsync(executor, temp.Path, ["commit", "-m", "red fixture"]);
        var redHead = await GitAsync(executor, temp.Path, ["rev-parse", "HEAD"]);

        var dotnet = ResolveExecutable("DELIVERY_FORGE_DOTNET_HOST", "dotnet");
        var detection = await new ToolCapabilityDetector(executor).DetectAsync(
            new CapabilityProbe(
                "dotnet", ExecutableSource.TrustedExplicit, dotnet, temp.Path, null,
                ["--version"], ["--help"], "10.0.401", "trx-v1", false),
            TestContext.Current.CancellationToken);
        var capability = detection.Capability;
        Assert.True(capability.Supported,
            $"Dotnet capability: executable={dotnet}; observed={capability.Version}; " +
            $"version exit={detection.VersionResult?.ExitCode}; " +
            $"version output={detection.VersionResult?.StandardOutput} {detection.VersionResult?.StandardError}; " +
            $"help exit={detection.HelpResult?.ExitCode}; " +
            $"help output={detection.HelpResult?.StandardOutput} {detection.HelpResult?.StandardError}");
        Assert.Equal("10.0.401", capability.Version);
        var project = System.IO.Path.Combine(temp.Path, "Subject.Tests", "Subject.Tests.csproj");
        var redTrx = System.IO.Path.Combine(temp.Path, "red.trx");
        var protectedBase = redHead.StandardOutput.Trim();
        var redResult = await RunGateAsync(executor, capability, temp.Path, evidence.Path, protectedBase, "red-gate",
            ["test", project, "--configuration", "Release", "--logger", $"trx;LogFileName={redTrx}"]);
        ArchiveFixtureEvidence(evidence.Path, redResult);
        Assert.NotNull(redResult.Execution.ExitCode);
        Assert.NotEqual(0, redResult.Execution.ExitCode);
        Assert.True(redResult.Outcome == GateOutcome.Fail,
            $"RED gate: {redResult.Outcome}; reason: {redResult.Reason}; " +
            $"exit: {redResult.Execution.ExitCode}; timeout: {redResult.Execution.TimedOut}; " +
            $"cancelled: {redResult.Execution.Cancelled}; quiescent: {redResult.Execution.OwnedProcessQuiescent}\n" +
            redResult.Execution.StandardOutput + "\n" + redResult.Execution.StandardError);
        Assert.True(File.Exists(redResult.ReceiptPath));
        Assert.True(
            Directory.EnumerateFiles(temp.Path, "*.trx", SearchOption.AllDirectories).Any(),
            redResult.Execution.StandardOutput + Environment.NewLine + redResult.Execution.StandardError);
        Assert.Equal((1, 1, 0, 1), ReadCounts(redTrx));

        await File.WriteAllTextAsync(subject, green, TestContext.Current.CancellationToken);
        await GitAsync(executor, temp.Path, ["add", "Subject/Calculator.cs"]);
        await GitAsync(executor, temp.Path, ["commit", "-m", "green fixture"]);
        var greenHead = await GitAsync(executor, temp.Path, ["rev-parse", "HEAD"]);
        Assert.NotEqual(redHead.StandardOutput.Trim(), greenHead.StandardOutput.Trim());

        DeleteBuildOutputs(temp.Path);
        var greenTrx = System.IO.Path.Combine(temp.Path, "green.trx");
        var assembly = System.IO.Path.Combine(temp.Path, "Subject", "bin", "Release", "net10.0", "Subject.dll");
        Assert.False(File.Exists(assembly));
        var greenResult = await RunGateAsync(executor, capability, temp.Path, evidence.Path, protectedBase, "green-gate",
            ["test", project, "--configuration", "Release", "--logger", $"trx;LogFileName={greenTrx}"]);
        ArchiveFixtureEvidence(evidence.Path, greenResult);
        Assert.True(greenResult.Execution.ExitCode == 0,
            $"GREEN process did not complete successfully: exit={greenResult.Execution.ExitCode}; " +
            $"timeout={greenResult.Execution.TimedOut}; cancelled={greenResult.Execution.Cancelled}; " +
            $"quiescent={greenResult.Execution.OwnedProcessQuiescent}; reason={greenResult.Reason}; " +
            $"native={greenResult.Execution.LifecycleDiagnostics?.NativeFailure}\n" +
            greenResult.Execution.StandardOutput + "\n" + greenResult.Execution.StandardError);
        Assert.True(File.Exists(assembly));
        Assert.Equal((1, 1, 1, 0), ReadCounts(greenTrx));
        Assert.Equal(GateOutcome.Pass, greenResult.Outcome);
        Assert.True(greenResult.ProductionCapable);
        Assert.True(File.Exists(greenResult.ReceiptPath));
        Assert.Contains("evidence.trx", greenResult.ArtifactHashes.Keys);
        Assert.StartsWith("sha256:", greenResult.ArtifactHashes["evidence.trx"]);

        var warmTrx = System.IO.Path.Combine(temp.Path, "warm.trx");
        var warmResult = await RunGateAsync(executor, capability, temp.Path, evidence.Path, protectedBase, "warm-gate",
            ["test", project, "--configuration", "Release", "--no-restore", "--no-build",
                "--logger", $"trx;LogFileName={warmTrx}"]);
        ArchiveFixtureEvidence(evidence.Path, warmResult);
        Assert.NotNull(warmResult.Execution.ExitCode);
        Assert.Equal(0, warmResult.Execution.ExitCode);
        Assert.Equal((1, 1, 1, 0), ReadCounts(warmTrx));
        Assert.Equal(GateOutcome.Incomplete, warmResult.Outcome);
        Assert.Contains("compiler", warmResult.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<GateRunResult> RunGateAsync(
        ProcessCommandExecutor executor,
        ToolCapability capability,
        string repository,
        string outputRoot,
        string protectedBase,
        string outputName,
        IReadOnlyList<string> arguments)
    {
        var reader = new GitRepositoryIdentityReader(executor, repository, protectedBase);
        var identity = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var policy = new ResolvedEvidencePolicy(
            TestEvidence.Identity('1'), TestEvidence.Identity('2'), protectedBase,
            PolicyAuthorityKind.ProtectedGitBase, 0m, ["test"], authorityVerified: true,
            sourceRepositoryRoot: repository,
            requiredGatePluginBindings:
            [new RequiredGatePluginBinding("test", "delivery-forge.dotnet", "1.0.0", "dotnet")]);
        var boundedArguments = arguments.Concat(
            ["--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "/nodeReuse:false"])
            .ToArray();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["UseSharedCompilation"] = "false"
        };
        return await new GateRunner(
            executor, reader, repository, protectedBase,
            plugins: null, authorityRegistry: null,
            hostOwnedClaimRoot: System.IO.Path.Combine(outputRoot, ".claims", outputName)).RunAsync(
            new GateRequest(
                "test", policy, capability, EvidenceAdapterKind.DotNet,
                TestEvidence.Identity('6'), identity,
                new CommandInvocation(capability.ExecutablePath!, boundedArguments, repository, environment),
                "fixture", TimeSpan.FromSeconds(90), System.IO.Path.Combine(outputRoot, outputName)),
            TestContext.Current.CancellationToken);
    }

    private static void ArchiveFixtureEvidence(string evidenceRoot, GateRunResult result)
    {
        var destination = Environment.GetEnvironmentVariable("DELIVERY_FORGE_TEST_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(destination)) return;
        if (!Path.IsPathFullyQualified(destination))
            throw new InvalidOperationException("Fixture artifact destination must be explicit and absolute.");
        var ownedLeaf = Path.Combine(destination, "real-dotnet", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ownedLeaf);
        File.WriteAllText(Path.Combine(ownedLeaf, "observed-result.json"),
            System.Text.Json.JsonSerializer.Serialize(new { result.Outcome, result.Reason, result.Execution }));
        var files = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var source in Directory.EnumerateFiles(evidenceRoot, "*", files))
        {
            var target = Path.Combine(ownedLeaf, Path.GetRelativePath(evidenceRoot, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
        }
    }

    private static async Task<CommandResult> GitAsync(
        ProcessCommandExecutor executor, string directory, IReadOnlyList<string> arguments)
    {
        var result = await RunAsync(executor, "git", directory, arguments);
        Assert.Equal(0, result.ExitCode);
        return result;
    }

    private static string FindRepositoryFile(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, name);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"Repository file '{name}' was not found above the test output directory.");
    }

    private static Task<CommandResult> RunAsync(
        ProcessCommandExecutor executor,
        string executable,
        string directory,
        IReadOnlyList<string> arguments) =>
        executor.ExecuteAsync(
            new CommandInvocation(executable, arguments, directory),
            TimeSpan.FromSeconds(90),
            TestContext.Current.CancellationToken);

    private static (int Total, int Executed, int Passed, int Failed) ReadCounts(string trx)
    {
        if (!File.Exists(trx))
            trx = Directory.GetFiles(
                System.IO.Path.GetDirectoryName(trx)!, System.IO.Path.GetFileName(trx), SearchOption.AllDirectories)
                .Single();
        var counters = XDocument.Load(trx).Descendants().First(element => element.Name.LocalName == "Counters");
        return (
            int.Parse(counters.Attribute("total")!.Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(counters.Attribute("executed")!.Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(counters.Attribute("passed")!.Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(counters.Attribute("failed")!.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string ResolveExecutable(string environmentVariable, string name)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException($"Required executable '{name}' is unavailable.");
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void DeleteBuildOutputs(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .Where(path => System.IO.Path.GetFileName(path) is "bin" or "obj")
                     .OrderByDescending(path => path.Length).ToArray())
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
    }
}
