using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Evidence;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

[Collection(QualityInterruptionCollection.Name)]
public sealed class QualityInterruptionScenarioTests(QualityInterruptionFixture fixture)
{
    [Fact(Timeout = 780_000)]
    public async Task Actual_cold_barrier_control_compiles_and_passes_but_owner_remains_advisory()
    {
        var phase = await Phase(QualityInterruptionPhase.ColdControl);
        Assert.Equal(0, phase.Gate.Execution.ExitCode);
        Assert.False(phase.Gate.Execution.TimedOut);
        Assert.False(phase.Gate.Execution.Cancelled);
        Assert.False(phase.Gate.SourceChanged);
        Assert.True(phase.Gate.Before.IsClean);
        Assert.Equal(phase.Gate.Before, phase.Gate.After);
        Assert.Equal(GateOutcome.Incomplete, phase.Gate.Outcome);
        Assert.Contains("advisory", phase.Gate.Reason, StringComparison.OrdinalIgnoreCase);
        AssertPassingTrx(phase.InvocationTrxPath);
        Assert.DoesNotContain("--no-build", phase.Gate.Invocation.Arguments);
        using var provenance = Read(phase.ProvenancePath);
        var root = provenance.RootElement;
        Assert.True(root.GetProperty("ownedBinAndObjRemoved").GetBoolean());
        Assert.Empty(root.GetProperty("generatedDllsBefore").EnumerateArray());
        AssertFileHash(root.GetProperty("producedDllPath").GetString()!, root.GetProperty("dllHashAfter").GetString()!);
        Assert.Contains("Subject ->", phase.Gate.Execution.StandardOutput, StringComparison.Ordinal);
        Assert.True(root.GetProperty("releasedAfterEntered").GetBoolean());
    }

    [Fact(Timeout = 780_000)]
    public async Task Actual_tracked_source_drift_after_compiled_entry_is_ERROR_despite_passing_TRX()
    {
        var phase = await Phase(QualityInterruptionPhase.SourceDrift);
        Assert.Equal(0, phase.Gate.Execution.ExitCode);
        Assert.False(phase.Gate.Execution.TimedOut);
        Assert.False(phase.Gate.Execution.Cancelled);
        AssertPassingTrx(phase.InvocationTrxPath);
        Assert.True(phase.Gate.SourceChanged);
        Assert.Equal(GateOutcome.Error, phase.Gate.Outcome);
        Assert.Contains("source", phase.Gate.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(phase.Gate.Before.IsClean);
        Assert.False(phase.Gate.After.IsClean);
        Assert.NotEqual(phase.Gate.Before, phase.Gate.After);
        using var provenance = Read(phase.ProvenancePath);
        var root = provenance.RootElement;
        Assert.True(root.GetProperty("mutationAfterEntered").GetBoolean());
        Assert.True(root.GetProperty("sourceDirtyThroughAfterRead").GetBoolean());
        Assert.True(root.GetProperty("restoredOnlyAfterRefusalPersisted").GetBoolean());
        Assert.NotEqual(root.GetProperty("originalTrackedSourceSha256").GetString(), root.GetProperty("modifiedTrackedSourceSha256").GetString());
        AssertFileHash(root.GetProperty("modifiedSourceArchivePath").GetString()!, root.GetProperty("modifiedTrackedSourceSha256").GetString()!);
        AssertFileHash(root.GetProperty("restoredTrackedSourcePath").GetString()!, root.GetProperty("originalTrackedSourceSha256").GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(root.GetProperty("dirtyGitStatusPath").GetString()!)));
    }

    [Fact(Timeout = 780_000)]
    public async Task Actual_entered_warm_workload_times_out_with_observed_quiescence_and_sticky_INCOMPLETE()
    {
        var phase = await Phase(QualityInterruptionPhase.Timeout);
        AssertInterrupted(phase, cancelled: false);
        using var provenance = Read(phase.ProvenancePath);
        var root = provenance.RootElement;
        Assert.Equal(180, root.GetProperty("workloadSelfLimitSeconds").GetInt32());
        Assert.Equal(75, root.GetProperty("requestedCommandDeadlineSeconds").GetInt32());
        Assert.False(root.GetProperty("primaryReleaseWritten").GetBoolean());
        Assert.Contains("--no-build", phase.Gate.Invocation.Arguments);
        Assert.Contains("--no-restore", phase.Gate.Invocation.Arguments);
    }

    [Fact(Timeout = 780_000)]
    public async Task Later_distinct_sequential_real_passing_TRX_cannot_upgrade_or_reclaim_timeout_receipt()
    {
        var phase = await Phase(QualityInterruptionPhase.LateArtifact);
        AssertInterrupted(phase, cancelled: false);
        using var provenance = Read(phase.ProvenancePath);
        var root = provenance.RootElement;
        Assert.True(root.GetProperty("secondActualRepositoryReadBarrierReached").GetBoolean());
        Assert.True(root.GetProperty("primaryQuiescentBeforeLateStart").GetBoolean());
        Assert.Equal(0, root.GetProperty("lateExitCode").GetInt32());
        Assert.True(root.GetProperty("lateOwnedProcessQuiescent").GetBoolean());
        Assert.False(root.GetProperty("lateTimedOut").GetBoolean());
        Assert.False(root.GetProperty("lateCancelled").GetBoolean());
        Assert.True(root.GetProperty("lateStartedAt").GetDateTimeOffset() >= phase.Gate.Execution.CompletedAt);
        Assert.True(root.GetProperty("lateCompletedAt").GetDateTimeOffset() >= root.GetProperty("lateStartedAt").GetDateTimeOffset());
        AssertPassingTrx(root.GetProperty("lateTrxArchivePath").GetString()!);
        AssertFileHash(root.GetProperty("lateTrxArchivePath").GetString()!, root.GetProperty("lateTrxSha256").GetString()!);
        Assert.Equal(phase.InvocationTrxPath, root.GetProperty("lateLoggerRequestedPath").GetString());
        Assert.Equal(root.GetProperty("capturedArtifactHashesBeforeLate").GetRawText(), root.GetProperty("capturedArtifactHashesAfterLate").GetRawText());
        Assert.Equal(root.GetProperty("receiptSha256BeforeLateActivity").GetString(), root.GetProperty("receiptSha256AfterLateActivity").GetString());
        AssertFileHash(phase.Gate.ReceiptPath, root.GetProperty("receiptSha256AfterLateActivity").GetString()!);
        Assert.True(root.GetProperty("outputReclaimDenied").GetBoolean());
        Assert.Equal(0, root.GetProperty("outputReclaimSdkCommandCount").GetInt32());
        Assert.NotEqual("Pass", root.GetProperty("outputReclaimOutcome").GetString());
        Assert.False(root.GetProperty("lateReportAttributedToPrimary").GetBoolean());
    }

    [Fact(Timeout = 780_000)]
    public async Task Actual_caller_cancel_after_entered_marker_is_cancelled_not_timeout_with_sticky_receipt()
    {
        var phase = await Phase(QualityInterruptionPhase.CallerCancellation);
        AssertInterrupted(phase, cancelled: true);
        using var provenance = Read(phase.ProvenancePath);
        Assert.True(provenance.RootElement.GetProperty("callerCancelledAfterEntered").GetBoolean());
        Assert.True(provenance.RootElement.GetProperty("resultAwaitedWithUncancelledOuterToken").GetBoolean());
        Assert.False(provenance.RootElement.GetProperty("primaryReleaseWritten").GetBoolean());
    }

    private async Task<QualityInterruptionSnapshot> Phase(QualityInterruptionPhase name)
    {
        var phases = await fixture.ObserveFiveAsync();
        Assert.Equal(1, fixture.SequenceStarts);
        Assert.Equal(5, phases.Count);
        var phase = phases[name];
        var gate = phase.Gate;
        Assert.True(Guid.TryParseExact(phase.Nonce, "N", out _));
        Assert.Equal("entered-" + phase.Nonce, Path.GetFileName(phase.EnteredPath));
        Assert.Equal(phase.Nonce, await File.ReadAllTextAsync(phase.EnteredPath, TestContext.Current.CancellationToken));
        Assert.True(gate.Execution.OwnedProcessQuiescent);
        Assert.True(Path.IsPathFullyQualified(gate.Invocation.FileName));
        Assert.Contains("--disable-build-servers", gate.Invocation.Arguments);
        Assert.Contains("-m:1", gate.Invocation.Arguments);
        Assert.Contains(gate.Invocation.Arguments, arg => arg.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("trx;LogFileName=" + phase.InvocationTrxPath, gate.Invocation.Arguments);
        var receiptBytes = await File.ReadAllBytesAsync(gate.ReceiptPath, TestContext.Current.CancellationToken);
        Assert.Equal("gate-receipt", ContractValidator.ParseAndValidate(receiptBytes).SchemaName);
        Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(phase.CapturedReceiptPath, TestContext.Current.CancellationToken));
        using var receipt = JsonDocument.Parse(receiptBytes);
        Assert.Equal("test", receipt.RootElement.GetProperty("gateId").GetString());
        var receiptDirectory = Path.GetDirectoryName(Path.GetFullPath(gate.ReceiptPath))!;
        Assert.Equal("receipts", Path.GetFileName(receiptDirectory));
        var artifacts = Directory.GetParent(receiptDirectory)!.FullName;
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(phase.ProvenancePath))!, "test"), artifacts);
        Assert.True(gate.ArtifactHashes.ContainsKey("stdout.txt"));
        Assert.True(gate.ArtifactHashes.ContainsKey("stderr.txt"));
        foreach (var artifact in gate.ArtifactHashes)
        {
            Assert.Equal(Path.GetFileName(artifact.Key), artifact.Key);
            Assert.Equal(artifact.Value, receipt.RootElement.GetProperty("artifactHashes").GetProperty(artifact.Key).GetString());
            AssertFileHash(Path.Combine(artifacts, artifact.Key), artifact.Value);
        }
        using var provenance = Read(phase.ProvenancePath);
        var root = provenance.RootElement;
        Assert.True(root.GetProperty("fixture").GetBoolean());
        Assert.False(root.GetProperty("productionCertified").GetBoolean());
        Assert.False(root.GetProperty("firstFullRealRunCertified").GetBoolean());
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, root.GetProperty("integratedBaseCommit").GetString());
        Assert.Equal(QualityInterruptionClock.ProfileId, root.GetProperty("budgetId").GetString());
        Assert.Equal(720, root.GetProperty("totalBudgetSeconds").GetInt32());
        Assert.Equal("10.0.401", root.GetProperty("observedSdkVersion").GetString());
        Assert.Equal("10.0.401", File.ReadAllText(root.GetProperty("versionStdoutPath").GetString()!).Trim());
        Assert.Equal(0, root.GetProperty("helpExitCode").GetInt32());
        Assert.Equal(0, root.GetProperty("initialRestoreExitCode").GetInt32());
        Assert.True(root.GetProperty("initialRestoreOwnedQuiescent").GetBoolean());
        Assert.Equal("locked", root.GetProperty("initialRestoreMode").GetString());
        Assert.True(File.Exists(root.GetProperty("initialRestoreStdoutPath").GetString()));
        Assert.True(File.Exists(root.GetProperty("initialRestoreStderrPath").GetString()));
        Assert.True(root.GetProperty("enteredAt").GetDateTimeOffset() >= gate.Execution.StartedAt);
        Assert.True(root.GetProperty("enteredAt").GetDateTimeOffset() <= gate.Execution.CompletedAt);
        foreach (var hash in new[] { "sdkExecutableSha256", "configurationSha256", "protectedPolicySha256", "assetsSha256", "lockSha256", "barrierSourceSha256" })
            Assert.Matches("^sha256:[0-9a-f]{64}$", root.GetProperty(hash).GetString()!);
        Assert.Equal(gate.Before.BaseCommit, root.GetProperty("protectedBaseCommit").GetString());
        Assert.Equal(gate.Before.HeadCommit, root.GetProperty("observedHeadCommit").GetString());
        Assert.Equal(gate.Before.TreeId, root.GetProperty("observedTreeId").GetString());
        return phase;
    }
    private static void AssertInterrupted(QualityInterruptionSnapshot phase, bool cancelled)
    {
        Assert.Equal(cancelled, phase.Gate.Execution.Cancelled);
        Assert.Equal(!cancelled, phase.Gate.Execution.TimedOut);
        Assert.True(phase.Gate.Execution.OwnedProcessQuiescent);
        Assert.False(phase.Gate.SourceChanged);
        Assert.True(phase.Gate.Before.IsClean);
        Assert.Equal(phase.Gate.Before, phase.Gate.After);
        Assert.Equal(GateOutcome.Incomplete, phase.Gate.Outcome);
        Assert.Contains(cancelled ? "cancel" : "timed out", phase.Gate.Reason, StringComparison.OrdinalIgnoreCase);
    }
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllBytes(path));
    private static void AssertFileHash(string path, string expected) =>
        Assert.Equal(expected, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    private static void AssertPassingTrx(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10 * 1024 * 1024 });
        var counters = XDocument.Load(reader).Descendants().Single(element => element.Name.LocalName == "Counters");
        foreach (var value in new[] { ("total", 1), ("executed", 1), ("passed", 1), ("failed", 0), ("notExecuted", 0) })
            Assert.Equal(value.Item2.ToString(CultureInfo.InvariantCulture), counters.Attribute(value.Item1)?.Value);
    }
}
