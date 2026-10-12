using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Contracts.Validation;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

[Collection(LocalSingleFixtureCollection.Name)]
public sealed class ActualParentCrashEarlyTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("plan-frozen", "frozen-plan-import-capability-unavailable", 0, 0, 0)]
    [InlineData("worker-prepared", "prepared-is-not-paused-authority", 1, 0, 0)]
    [InlineData("host-accepted", "fresh-bound-host-observation-required", 1, 1, 0)]
    [InlineData("owned-command-started", "fresh-controller-native-identity-unknown", 1, 1, 1)]
    public async Task Actual_force_terminated_parent_at_early_boundary_requires_fresh_non_authorizing_recovery(
        string stage, string requiredRefusal, int prepared, int accepted, int registrations)
    {
        var started = Stopwatch.GetTimestamp();
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wall.CancelAfter(TimeSpan.FromSeconds(120));
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(wall.Token);
        setup.CancelAfter(TimeSpan.FromSeconds(75));
        await using var artifacts = await OwnedScenarioRun.CreateAsync(setup.Token);
        artifacts.RetainArtifacts("Actual parent crash RED/setup/recovery and unknown descendants remain retained.");
        var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, setup.Token);
        // Retained runtime: this slice does not authorize recursive deletion or unknown-child adoption.
        artifacts.RetainArtifacts("Crash runtime retained without destructive cleanup: " + runtime.Root);
        var nonce = Guid.NewGuid().ToString("N");
        var sentinel = Path.Combine(artifacts.Root, "outside-sentinel.txt");
        var sentinelBytes = System.Text.Encoding.UTF8.GetBytes("outside exact crash runtime:" + nonce);
        await using (var writer = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await writer.WriteAsync(sentinelBytes, setup.Token);
        var repository = await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", setup.Token);
        await artifacts.WriteNewJsonAsync("actual-crash-early-spec.json", new
        {
            fixture = true,
            productionCertified = false,
            firstFullRealRunCertified = false,
            stage,
            nonce,
            seed = 11019,
            actualOsParentCrashRequired = true,
            integratedBaseCommit = artifacts.IntegratedSource.Commit,
            implementationHead = artifacts.IntegratedSource.HeadCommit,
            runtime.Root,
            runtime.Repository,
            frozenBaseCommit = repository.Commit,
            wallSeconds = 120,
            setupSeconds = 75,
            readySeconds = 5,
            forceExitSeconds = 5,
            recoverySeconds = 15,
            retentionSeconds = 20,
            childSelfSeconds = 15,
            originalDemandSeconds = 90,
            cpu = 2,
            memoryMiB = 4096,
            sentinelPath = sentinel,
            sentinelSha256 = Hash(sentinelBytes),
            remainingBoundaries = new[] { "command-completed", "gate-receipt-written", "review-registration", "review-receipt", "scheduler-checkpoint", "publication-intent", "publication-readback", "cleanup-receipt" },
            limitation = "External host/control replies alone are marked fixtures; actual driver kill and fresh process required. No model/GH/Continuum or imported process authority."
        }, setup.Token);
        var result = await ActualParentCrashOrchestrator.RunEarlyAsync(artifacts, runtime, stage, nonce, sentinel, repository.Commit, started, wall.Token);
        var readyBytes = await File.ReadAllBytesAsync(result.ReadyPath, wall.Token);
        Assert.Equal(readyBytes, await File.ReadAllBytesAsync(result.CapturedReadyPath, wall.Token));
        using var ready = JsonDocument.Parse(readyBytes);
        Assert.Equal(stage, ready.RootElement.GetProperty("stage").GetString());
        Assert.Equal(nonce, ready.RootElement.GetProperty("nonce").GetString());
        Assert.Equal(repository.Commit, ready.RootElement.GetProperty("frozenBaseCommit").GetString());
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, ready.RootElement.GetProperty("integratedBaseCommit").GetString());
        Assert.Equal("plan", ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(result.PlanContractPath, wall.Token)).SchemaName);
        using var parent = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ParentObservationPath, wall.Token));
        var proof = parent.RootElement;
        Assert.True(proof.GetProperty("fixture").GetBoolean());
        Assert.True(proof.GetProperty("actualOsParentCrash").GetBoolean());
        Assert.False(proof.GetProperty("firstFullRealRunCertified").GetBoolean());
        Assert.Equal("LiveOwned", proof.GetProperty("originalBeforeKillOutcome").GetString());
        Assert.Equal("AlreadyExited", proof.GetProperty("originalAfterKillOutcome").GetString());
        Assert.Equal("owned-original-handle-Kill(false)", proof.GetProperty("terminationMode").GetString());
        Assert.Equal(Hash(readyBytes), proof.GetProperty("readySha256BeforeKill").GetString());
        Assert.True(proof.GetProperty("freshRecoveryProcessStartedAfterOriginalExit").GetBoolean());
        Assert.NotEqual(proof.GetProperty("originalDriverStartUtcTicks").GetInt64(), proof.GetProperty("recoveryDriverStartUtcTicks").GetInt64());
        Assert.Equal(Hash(sentinelBytes), Hash(await File.ReadAllBytesAsync(sentinel, wall.Token)));
        using var recovered = JsonDocument.Parse(await File.ReadAllBytesAsync(result.RecoveryReportPath, wall.Token));
        var recovery = recovered.RootElement;
        Assert.Equal(nonce, recovery.GetProperty("nonce").GetString());
        Assert.Equal(stage, recovery.GetProperty("stage").GetString());
        Assert.Equal(requiredRefusal, recovery.GetProperty("specificSafeRefusal").GetString());
        Assert.True(recovery.GetProperty("fixture").GetBoolean());
        Assert.False(recovery.GetProperty("productionCertified").GetBoolean());
        Assert.Equal(0, recovery.GetProperty("newWorkerLaunches").GetInt32());
        Assert.Equal(0, recovery.GetProperty("resumeCalls").GetInt32());
        Assert.Equal(0, recovery.GetProperty("signalOldNativeCalls").GetInt32());
        Assert.Equal(0, recovery.GetProperty("publicationCalls").GetInt32());
        Assert.Equal(0, recovery.GetProperty("destructiveCleanupCalls").GetInt32());
        Assert.False(recovery.GetProperty("oldTicketOrReceiptBecameAuthority").GetBoolean());
        Assert.Equal(0, recovery.GetProperty("freshControllerOwnedNativeCount").GetInt32());
        Assert.Equal(prepared, recovery.GetProperty("observedWorkerPreparedCount").GetInt32());
        Assert.Equal(accepted, recovery.GetProperty("observedWorkerAcceptedCount").GetInt32());
        Assert.Equal(registrations, recovery.GetProperty("observedProcessRegisteredCount").GetInt32());
        Assert.Equal(0, recovery.GetProperty("observedWorkerCompletedCount").GetInt32());
        Assert.NotEmpty(recovery.GetProperty("independentlyReopenedDurableFiles").EnumerateArray());
        foreach (var file in recovery.GetProperty("independentlyReopenedDurableFiles").EnumerateArray())
        {
            Assert.Equal(file.GetProperty("beforeKillSha256").GetString(), file.GetProperty("afterCrashSha256").GetString());
            Assert.Equal(file.GetProperty("afterCrashSha256").GetString(), Hash(await File.ReadAllBytesAsync(file.GetProperty("path").GetString()!, wall.Token)));
        }
        if (registrations != 0)
        {
            Assert.Equal("IdentityUnknown", recovery.GetProperty("recordedChildFreshControllerOutcome").GetString());
            Assert.True(recovery.GetProperty("unknownChildRetainsResources").GetBoolean());
        }
        if (accepted != 0) Assert.True(recovery.GetProperty("externalHostFixtureExplicitlyMarked").GetBoolean());
        using var control = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ReleaseControlReportPath, wall.Token));
        Assert.Equal(stage, control.RootElement.GetProperty("stage").GetString());
        Assert.True(control.RootElement.GetProperty("actualReadyReached").GetBoolean());
        Assert.True(control.RootElement.GetProperty("normalReleaseInsteadOfKill").GetBoolean());
        Assert.Equal(0, control.RootElement.GetProperty("driverExitCode").GetInt32());
        Assert.True(control.RootElement.GetProperty("positiveOriginalControllerExitObserved").GetBoolean());
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(120));
    }
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
