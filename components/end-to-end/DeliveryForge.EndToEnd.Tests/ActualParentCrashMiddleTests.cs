using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Validation;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

[Collection(LocalSingleFixtureCollection.Name)]
public sealed class ActualParentCrashMiddleTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("command-completed", "fresh-controller-completed-native-identity-unknown")]
    [InlineData("review-registration", "fresh-issuer-has-no-original-registration")]
    [InlineData("review-receipt", "fresh-issuer-rejects-original-review-receipt")]
    public Task Actual_middle_parent_crash_requires_fresh_owner_refusal(string stage, string refusal)
        => RunCase(stage, refusal, realTool: false);

    [Fact(Timeout = 540_000)]
    public Task Actual_test_gate_receipt_parent_crash_preserves_genuine_TRX_without_gate_authority()
        => RunCase("gate-receipt-written", "stored-gate-receipt-is-not-required-pass", realTool: true);

    private static async Task RunCase(string stage, string refusal, bool realTool)
    {
        var started = Stopwatch.GetTimestamp(); // BEFORE any template, restore, calibration or owner setup.
        using var clock = realTool ? new RealDotNetFixtureClock(started) : null;
        var total = realTool ? 540 : 120;
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken,
            clock?.WallToken ?? CancellationToken.None);
        wall.CancelAfter(TimeSpan.FromSeconds(total) - Stopwatch.GetElapsedTime(started));
        await using var artifacts = await OwnedScenarioRun.CreateAsync(wall.Token);
        artifacts.RetainArtifacts("Actual middle crash RED/genuine prerequisite evidence; all remaining crash boundaries retained.");
        var nonce = Guid.NewGuid().ToString("N");
        var sentinel = Path.Combine(artifacts.Root, "outside-middle-sentinel");
        var sentinelBytes = Encoding.UTF8.GetBytes("exact outside middle:" + nonce);
        await OwnedRealDotNetRuntime.WriteNewAsync(sentinel, sentinelBytes, wall.Token);
        var prerequisite = await ActualMiddleCrashPrerequisiteFixture.PrepareAsync(artifacts, stage, nonce, started, clock, wall.Token);
        // These source/real owner/file/native or real SDK/TRX assertions precede missing crash behavior.
        Assert.Equal("plan", ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(prerequisite.FrozenPlanContractPath, wall.Token)).SchemaName);
        using var actualPlan = JsonDocument.Parse(await File.ReadAllBytesAsync(prerequisite.FrozenPlanContractPath, wall.Token));
        Assert.Equal(prerequisite.RuntimeBaseCommit, actualPlan.RootElement.GetProperty("baseCommit").GetString());
        Assert.True(File.Exists(prerequisite.PrerequisiteObservationPath));
        if (stage == "command-completed") Assert.True(File.Exists(prerequisite.ActualNativeResultPath));
        if (stage == "gate-receipt-written")
        {
            Assert.Equal("gate-receipt", ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(prerequisite.ActualGateReceiptPath!, wall.Token)).SchemaName);
            Assert.True(File.Exists(prerequisite.ActualTrxPath));
        }
        if (stage == "review-receipt") Assert.Equal("review-receipt",
            ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(prerequisite.ActualReviewReceiptPath!, wall.Token)).SchemaName);
        var result = await ActualParentCrashMiddleOrchestrator.RunMiddleAsync(artifacts, stage, nonce, sentinel,
            prerequisite, started, clock, wall.Token);
        var readyBytes = await File.ReadAllBytesAsync(result.ReadyPath, wall.Token);
        Assert.Equal(readyBytes, await File.ReadAllBytesAsync(result.CapturedReadyPath, wall.Token));
        using var ready = JsonDocument.Parse(readyBytes);
        Assert.Equal(stage, ready.RootElement.GetProperty("stage").GetString());
        Assert.Equal(nonce, ready.RootElement.GetProperty("nonce").GetString());
        Assert.Equal(prerequisite.RuntimeBaseCommit, ready.RootElement.GetProperty("frozenBaseCommit").GetString());
        using var parent = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ParentObservationPath, wall.Token));
        Assert.True(parent.RootElement.GetProperty("actualOsParentCrash").GetBoolean());
        Assert.Equal("LiveOwned", parent.RootElement.GetProperty("originalBeforeKillOutcome").GetString());
        Assert.Equal("AlreadyExited", parent.RootElement.GetProperty("originalAfterKillOutcome").GetString());
        Assert.Equal("owned-original-handle-Kill(false)", parent.RootElement.GetProperty("terminationMode").GetString());
        Assert.True(parent.RootElement.GetProperty("freshRecoveryProcessStartedAfterOriginalExit").GetBoolean());
        Assert.Equal(Hash(readyBytes), parent.RootElement.GetProperty("readySha256BeforeKill").GetString());
        Assert.Equal(total, parent.RootElement.GetProperty("originalTotalSeconds").GetInt32());
        Assert.Equal(started, parent.RootElement.GetProperty("originalCaseStarted").GetInt64());
        Assert.Equal(Hash(sentinelBytes), Hash(await File.ReadAllBytesAsync(sentinel, wall.Token)));
        using var recovery = JsonDocument.Parse(await File.ReadAllBytesAsync(result.RecoveryReportPath, wall.Token));
        var observed = recovery.RootElement;
        Assert.Equal(refusal, observed.GetProperty("specificSafeRefusal").GetString());
        Assert.Equal(stage, observed.GetProperty("stage").GetString());
        Assert.Equal(nonce, observed.GetProperty("nonce").GetString());
        Assert.False(observed.GetProperty("oldBytesBecameAuthority").GetBoolean());
        foreach (var name in new[] { "newWorkerLaunches", "newCommandLaunches", "resumeCalls", "signalOldNativeCalls",
            "newGateExecutions", "newReviewerCalls", "publicationCalls", "destructiveCleanupCalls", "freshControllerOwnedNativeCount" })
            Assert.Equal(0, observed.GetProperty(name).GetInt32());
        Assert.Equal(0, observed.GetProperty("observedWorkerCompletedCount").GetInt32());
        Assert.NotEmpty(observed.GetProperty("independentlyReopenedDurableFiles").EnumerateArray());
        foreach (var file in observed.GetProperty("independentlyReopenedDurableFiles").EnumerateArray())
        {
            Assert.Equal(file.GetProperty("beforeKillSha256").GetString(), file.GetProperty("afterCrashSha256").GetString());
            Assert.Equal(file.GetProperty("afterCrashSha256").GetString(), Hash(await File.ReadAllBytesAsync(file.GetProperty("path").GetString()!, wall.Token)));
        }
        if (stage == "command-completed")
        {
            Assert.Equal(1, observed.GetProperty("observedProcessRegisteredCount").GetInt32());
            Assert.Equal("IdentityUnknown", observed.GetProperty("recordedChildFreshControllerOutcome").GetString());
            Assert.True(observed.GetProperty("originalDriverPositivelyObservedChildExit").GetBoolean());
        }
        if (stage == "gate-receipt-written")
        {
            Assert.Equal("Incomplete", observed.GetProperty("actualGateOutcome").GetString());
            Assert.Equal(1, observed.GetProperty("actualTrxPassed").GetInt32());
            Assert.Equal(0, observed.GetProperty("actualTrxFailed").GetInt32());
            Assert.Equal("gate-receipt", ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(result.BoundaryArtifactPath, wall.Token)).SchemaName);
        }
        if (stage == "review-registration") Assert.Equal(0, observed.GetProperty("originalReviewerCallsBeforeCrash").GetInt32());
        if (stage == "review-receipt")
        {
            Assert.Equal(1, observed.GetProperty("originalReviewerCallsBeforeCrash").GetInt32());
            Assert.True(observed.GetProperty("originalPinnedTestAcceptanceObserved").GetBoolean());
            Assert.True(observed.GetProperty("freshPinnedReceiverRejectedOriginalReceipt").GetBoolean());
            Assert.True(observed.GetProperty("productionTransitionDenied").GetBoolean());
            Assert.Equal("review-receipt", ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(result.BoundaryArtifactPath, wall.Token)).SchemaName);
        }
        using var control = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ReleaseControlReportPath, wall.Token));
        Assert.True(control.RootElement.GetProperty("actualReadyReached").GetBoolean());
        Assert.True(control.RootElement.GetProperty("normalReleaseInsteadOfKill").GetBoolean());
        Assert.Equal(0, control.RootElement.GetProperty("driverExitCode").GetInt32());
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(total));
    }
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
