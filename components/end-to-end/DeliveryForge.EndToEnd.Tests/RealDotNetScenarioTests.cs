using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Evidence;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

[Collection(RealDotNetFixtureCollection.Name)]
public sealed class RealDotNetScenarioTests(RealDotNetScenarioFixture fixture)
{
    [Fact(Timeout = 600_000)]
    public async Task Actual_failed_test_has_fresh_zero_of_one_TRX_and_owner_FAIL()
    {
        var phase = await Phase(RealDotNetFixturePhase.FailingTest);
        Assert.Equal(GateOutcome.Fail, phase.Gate.Outcome);
        Assert.NotEqual(0, phase.Gate.Execution.ExitCode);
        AssertCounts(phase.InvocationTrxPath, 0, 1);
    }
    [Fact(Timeout = 600_000)]
    public async Task Actual_repaired_cold_compile_has_fresh_passing_TRX_and_advisory_INCOMPLETE()
    {
        var phase = await Phase(RealDotNetFixturePhase.ColdPassing);
        Assert.Equal(0, phase.Gate.Execution.ExitCode);
        AssertCounts(phase.InvocationTrxPath, 1, 0);
        Assert.Equal(GateOutcome.Incomplete, phase.Gate.Outcome);
        Assert.Contains("advisory", phase.Gate.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--no-build", phase.Gate.Invocation.Arguments);
        Assert.DoesNotContain("--no-restore", phase.Gate.Invocation.Arguments);
        AssertColdDllProof(phase);
    }
    [Fact(Timeout = 600_000)]
    public async Task Actual_cold_passing_test_without_coverage_remains_required_coverage_INCOMPLETE()
    {
        var phase = await Phase(RealDotNetFixturePhase.MissingCoverage);
        Assert.Equal(0, phase.Gate.Execution.ExitCode);
        AssertCounts(phase.InvocationTrxPath, 1, 0);
        Assert.Equal(GateOutcome.Incomplete, phase.Gate.Outcome);
        Assert.Contains("Required coverage evidence is missing", phase.Gate.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-build", phase.Gate.Invocation.Arguments);
        using var provenance = JsonDocument.Parse(File.ReadAllBytes(phase.ProvenancePath));
        Assert.Equal("coverage", provenance.RootElement.GetProperty("gateId").GetString());
        Assert.Empty(provenance.RootElement.GetProperty("coverageFilesBefore").EnumerateArray());
        Assert.Empty(provenance.RootElement.GetProperty("coverageFilesAfter").EnumerateArray());
        AssertColdDllProof(phase);
    }
    [Fact(Timeout = 600_000)]
    public async Task Actual_warm_no_build_has_fresh_passing_TRX_but_no_trusted_compilation()
    {
        var phase = await Phase(RealDotNetFixturePhase.WarmNoBuild);
        Assert.Equal(0, phase.Gate.Execution.ExitCode);
        AssertCounts(phase.InvocationTrxPath, 1, 0);
        Assert.Equal(GateOutcome.Incomplete, phase.Gate.Outcome);
        Assert.Contains("No trusted compiler invocation", phase.Gate.Reason, StringComparison.Ordinal);
        Assert.Contains("--no-build", phase.Gate.Invocation.Arguments);
        Assert.Contains("--no-restore", phase.Gate.Invocation.Arguments);
        using var provenance = JsonDocument.Parse(File.ReadAllBytes(phase.ProvenancePath));
        var root = provenance.RootElement;
        Assert.Matches("^sha256:[0-9a-f]{64}$", root.GetProperty("dllHashBefore").GetString()!);
        Assert.Equal(root.GetProperty("dllHashBefore").GetString(), root.GetProperty("dllHashAfter").GetString());
    }
    private async Task<RealDotNetPhaseSnapshot> Phase(RealDotNetFixturePhase name)
    {
        var phases = await fixture.ObserveFourAsync();
        Assert.Equal(1, fixture.SequenceStarts);
        Assert.Equal(4, phases.Count);
        var phase = phases[name];
        var gate = phase.Gate;
        Assert.False(gate.Execution.TimedOut);
        Assert.False(gate.Execution.Cancelled);
        Assert.True(gate.Execution.OwnedProcessQuiescent);
        Assert.False(gate.SourceChanged);
        Assert.True(gate.Before.IsClean);
        Assert.Equal(gate.Before, gate.After);
        Assert.True(Path.IsPathFullyQualified(gate.Invocation.FileName));
        Assert.True(File.Exists(gate.ReceiptPath));
        Assert.Equal("gate-receipt", ContractValidator.ParseAndValidate(File.ReadAllBytes(gate.ReceiptPath)).SchemaName);
        foreach (var file in new[] { "stdout.txt", "stderr.txt", "evidence.trx" })
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(gate.ReceiptPath)!, file), TestContext.Current.CancellationToken);
            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), gate.ArtifactHashes[file]);
        }
        Assert.Contains("--disable-build-servers", gate.Invocation.Arguments);
        Assert.Contains("-m:1", gate.Invocation.Arguments);
        Assert.Contains(gate.Invocation.Arguments, argument => argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("trx;LogFileName=" + phase.InvocationTrxPath, gate.Invocation.Arguments);
        using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(phase.ProvenancePath, TestContext.Current.CancellationToken));
        var root = provenance.RootElement;
        Assert.True(root.GetProperty("fixture").GetBoolean());
        Assert.False(root.GetProperty("productionCertified").GetBoolean());
        Assert.False(root.GetProperty("firstFullRealRunCertified").GetBoolean());
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, root.GetProperty("integratedBaseCommit").GetString());
        Assert.Equal("real-dotnet-fixture-v1", root.GetProperty("budgetId").GetString());
        Assert.Equal(540, root.GetProperty("totalBudgetSeconds").GetInt32());
        Assert.Equal("10.0.401", root.GetProperty("observedSdkVersion").GetString());
        Assert.Equal("10.0.401", File.ReadAllText(root.GetProperty("versionStdoutPath").GetString()!).Trim());
        Assert.Equal(0, root.GetProperty("initialRestoreExitCode").GetInt32());
        Assert.True(root.GetProperty("initialRestoreOwnedQuiescent").GetBoolean());
        Assert.True(File.Exists(root.GetProperty("initialRestoreStdoutPath").GetString()));
        Assert.True(File.Exists(root.GetProperty("initialRestoreStderrPath").GetString()));
        Assert.True(File.Exists(root.GetProperty("helpStdoutPath").GetString()));
        Assert.Equal(0, root.GetProperty("helpExitCode").GetInt32());
        Assert.Equal("locked", root.GetProperty("initialRestoreMode").GetString());
        foreach (var hash in new[] { "sdkExecutableSha256", "configurationSha256", "protectedPolicySha256", "assetsSha256", "lockSha256" })
            Assert.Matches("^sha256:[0-9a-f]{64}$", root.GetProperty(hash).GetString()!);
        Assert.Equal(gate.Before.BaseCommit, root.GetProperty("protectedBaseCommit").GetString());
        Assert.Equal(gate.Before.HeadCommit, root.GetProperty("observedHeadCommit").GetString());
        Assert.Equal(gate.Before.TreeId, root.GetProperty("observedTreeId").GetString());
        return phase;
    }
    private static void AssertCounts(string trx, int passed, int failed)
    {
        using var reader = XmlReader.Create(trx, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10 * 1024 * 1024 });
        var counters = XDocument.Load(reader).Descendants().Single(element => element.Name.LocalName == "Counters");
        Assert.Equal("1", counters.Attribute("total")?.Value);
        Assert.Equal("1", counters.Attribute("executed")?.Value);
        Assert.Equal(passed.ToString(System.Globalization.CultureInfo.InvariantCulture), counters.Attribute("passed")?.Value);
        Assert.Equal(failed.ToString(System.Globalization.CultureInfo.InvariantCulture), counters.Attribute("failed")?.Value);
        Assert.Equal("0", counters.Attribute("notExecuted")?.Value);
    }
    private static void AssertColdDllProof(RealDotNetPhaseSnapshot phase)
    {
        using var provenance = JsonDocument.Parse(File.ReadAllBytes(phase.ProvenancePath));
        var root = provenance.RootElement;
        Assert.True(root.GetProperty("ownedBinAndObjRemoved").GetBoolean());
        Assert.Empty(root.GetProperty("generatedDllsBefore").EnumerateArray());
        var dll = root.GetProperty("producedDllPath").GetString()!;
        Assert.True(File.Exists(dll));
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dll))), root.GetProperty("dllHashAfter").GetString());
        Assert.Contains("Subject ->", phase.Gate.Execution.StandardOutput, StringComparison.Ordinal);
    }
}
