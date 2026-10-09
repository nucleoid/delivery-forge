using System.Security.Cryptography;
using System.Xml.Linq;

namespace DeliveryForge.Evidence.Tests;

public sealed class RealDotNetFixtureTests
{
    [Fact(Timeout = 180_000)]
    [Trait("Category", "RealFixture")]
    public async Task Earlier_red_commit_then_clean_green_head_produces_real_test_counts()
    {
        using var temp = new TempDirectory();
        var fixture = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "RealDotNet");
        CopyTree(fixture, temp.Path);
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
        var capability = new ToolCapability(
            "dotnet", "10.0.401", "trx-v1", true, true, [], dotnet,
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dotnet)))}", ["test"]);
        var project = System.IO.Path.Combine(temp.Path, "Subject.Tests", "Subject.Tests.csproj");
        var redTrx = System.IO.Path.Combine(temp.Path, "red.trx");
        var redResult = await RunGateAsync(executor, capability, temp.Path, "red-gate",
            ["test", project, "--configuration", "Release", "--logger", $"trx;LogFileName={redTrx}"]);
        Assert.NotEqual(0, redResult.Execution.ExitCode);
        Assert.Equal(GateOutcome.Fail, redResult.Outcome);
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
        var greenResult = await RunGateAsync(executor, capability, temp.Path, "green-gate",
            ["test", project, "--configuration", "Release", "--logger", $"trx;LogFileName={greenTrx}"]);
        Assert.Equal(0, greenResult.Execution.ExitCode);
        Assert.True(File.Exists(assembly));
        Assert.Equal((1, 1, 1, 0), ReadCounts(greenTrx));
        Assert.Equal(GateOutcome.Incomplete, greenResult.Outcome);
        Assert.Contains("advisory", greenResult.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(greenResult.ReceiptPath));
        Assert.Contains("evidence.trx", greenResult.ArtifactHashes.Keys);
        Assert.StartsWith("sha256:", greenResult.ArtifactHashes["evidence.trx"]);

        var warmTrx = System.IO.Path.Combine(temp.Path, "warm.trx");
        var warmResult = await RunGateAsync(executor, capability, temp.Path, "warm-gate",
            ["test", project, "--configuration", "Release", "--no-restore", "--no-build",
                "--logger", $"trx;LogFileName={warmTrx}"]);
        Assert.Equal(0, warmResult.Execution.ExitCode);
        Assert.Equal((1, 1, 1, 0), ReadCounts(warmTrx));
        Assert.Equal(GateOutcome.Incomplete, warmResult.Outcome);
        Assert.Contains("compiler", warmResult.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<GateRunResult> RunGateAsync(
        ProcessCommandExecutor executor,
        ToolCapability capability,
        string repository,
        string outputName,
        IReadOnlyList<string> arguments)
    {
        var identity = TestEvidence.Repository();
        return new GateRunner(executor, new SequenceRepositoryIdentityReader(identity, identity), repository).RunAsync(
            new GateRequest(
                "test", TestEvidence.ResolvedPolicy(), capability, EvidenceAdapterKind.DotNet,
                TestEvidence.Identity('6'), identity,
                new CommandInvocation(capability.ExecutablePath!, arguments, repository),
                "fixture", TimeSpan.FromSeconds(90), System.IO.Path.Combine(repository, outputName)),
            TestContext.Current.CancellationToken);
    }

    private static async Task<CommandResult> GitAsync(
        ProcessCommandExecutor executor, string directory, IReadOnlyList<string> arguments)
    {
        var result = await RunAsync(executor, "git", directory, arguments);
        Assert.Equal(0, result.ExitCode);
        return result;
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
