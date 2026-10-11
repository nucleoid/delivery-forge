using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

[Collection(LocalSingleFixtureCollection.Name)]
public sealed class ColdRecoveryScenarioTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("cold.paused-new-ticket", "old-ticket-denied", "new-ticket-resumes-exact-request")]
    [InlineData("cold.held-original-writer", "live-writer-refuses-adoption", "no-resume-before-writer-release")]
    [InlineData("cold.base-drift", "fresh-raw-base-drift-denied", "no-resume-after-base-drift")]
    [InlineData("cold.missing-required-artifact", "pinned-artifact-absence-denied", "required-list-not-weakened")]
    [InlineData("cold.foreign-fresh-host", "foreign-or-unknown-host-denied", "no-resume-from-foreign-quiescence")]
    [InlineData("cold.stopped-no-resume", "terminal-journal-refuses-adoption", "cleanup-only-no-ticket-or-resume")]
    [InlineData("cold.delayed-completion-retained-controller", "actual-barrier-child-exited", "fresh-coordinator-completes-with-retained-controller")]
    [InlineData("cold.delayed-completion-fresh-controller", "actual-barrier-child-exited", "fresh-controller-cannot-adopt-recorded-child")]
    public async Task Cold_lifetime_requires_fresh_public_observations_and_never_remints_recorded_ownership(
        string scenario, string firstInvariant, string secondInvariant)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var artifacts = await OwnedScenarioRun.CreateAsync(cancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, cancellationToken);
        var initial = await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", cancellationToken);
        await artifacts.WriteNewJsonAsync("cold-slice-spec.json", new
        {
            fixture = true, productionCertified = false, fullRealProfileCertified = false, scenario,
            integratedBaseCommit = artifacts.IntegratedSource.Commit, implementationHead = artifacts.IntegratedSource.HeadCommit,
            frozenBaseCommit = initial.Commit, frozenBaseTree = initial.Tree, seed = 11017,
            wallSeconds = 120, originalDemandSeconds = 90, gitSeconds = 15, hostControlSeconds = 2,
            childSeconds = 15, naturalExitBarrierSeconds = 5, cleanupReserveSeconds = 10,
            cpu = 2, memoryMiB = 4096, tokens = 1000, cost = 0,
            limitation = "Actual Git/files/registered owned child; explicit external host-control fixture only. Lifetime replacement is not an OS parent crash."
        }, cancellationToken);
        var options = new ScenarioOptions(ScenarioProfile.LocalContract, [], new(TimeSpan.FromSeconds(120), 2, 4096, 1000, 0m), 11017);
        var result = await new ScenarioRunner(artifacts, runtime).RunAsync(scenario, options, cancellationToken);
        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.True(result.Fixture);
        Assert.False(result.FullRealProfileCertified);
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, result.IntegratedBaseCommit);
        Assert.True(result.Invariants[firstInvariant]);
        Assert.True(result.Invariants[secondInvariant]);
        Assert.True(result.Invariants["fresh-store-coordinator-lifetime"]);
        Assert.True(result.Invariants["no-native-relaunch"]);
        var observation = Assert.Single((await artifacts.Store.RecoverAsync(artifacts.RunId, cancellationToken)).Records,
            record => record.Kind == "scenario-cold-observation");
        Assert.Contains(observation.Sha256, result.ReceiptIds);
        var payload = observation.Payload;
        Assert.True(payload.GetProperty("fixture").GetBoolean());
        Assert.False(payload.GetProperty("productionCertified").GetBoolean());
        Assert.False(payload.GetProperty("actualOsParentCrash").GetBoolean());
        Assert.Equal(initial.Commit, payload.GetProperty("frozenBaseCommit").GetString());
        Assert.NotEmpty(payload.GetProperty("hostCallLedger").EnumerateArray());
        var runId = payload.GetProperty("workerRunId").GetString()!;
        var history = await runtime.Store.RecoverAsync(runId, cancellationToken);
        var preparedRecord = Assert.Single(history.Records, record => record.Kind == "worker-prepared");
        var prepared = runtime.Store.ReadPayload<PreparedWorkerRecord>(preparedRecord);
        Assert.Equal(initial.Commit, prepared.Request.BaseCommit);
        Assert.Equal(prepared.Request.RequestIdentity, WorkerEnvelope.ComputeRequestIdentity(prepared.Request));
        Assert.Equal(prepared.Request.RequestIdentity, payload.GetProperty("requestIdentity").GetString());
        Assert.Equal(prepared.Request.WriterToken, payload.GetProperty("writerToken").GetString());
        Assert.Equal(prepared.Request.AuthorizationCeiling, payload.GetProperty("authorizationCeiling").GetString());
        Assert.Single(history.Records, record => record.Kind == "process-registered");
        Assert.All(history.Records, record => Assert.Contains(record.Sha256,
            payload.GetProperty("journalHashes").EnumerateArray().Select(item => item.GetString())));
        var identities = runtime.Processes.GetAllOwnedIdentities();
        var ownedIdentity = Assert.Single(identities);
        Assert.Equal(ProcessControlOutcome.AlreadyExited, runtime.Processes.Observe(ownedIdentity).Outcome);
        await using var independentController = new ProcessControl();
        Assert.Equal(ProcessControlOutcome.IdentityUnknown, independentController.Observe(ownedIdentity).Outcome);
        var delayed = scenario.StartsWith("cold.delayed-completion", StringComparison.Ordinal);
        if (!delayed)
        {
            var checkpoints = (await runtime.Store.RecoverAsync(runId + "-scheduler", cancellationToken)).Records
                .Where(record => record.Kind == "local-scheduler-checkpoint").ToArray();
            Assert.NotEmpty(checkpoints);
            var started = checkpoints[0].Payload.GetProperty("startedAtUtc").GetDateTimeOffset();
            var expires = checkpoints[0].Payload.GetProperty("expiresAtUtc").GetDateTimeOffset();
            Assert.Equal(TimeSpan.FromSeconds(90), expires - started);
            Assert.All(checkpoints, record =>
            {
                Assert.Equal(started, record.Payload.GetProperty("startedAtUtc").GetDateTimeOffset());
                Assert.Equal(expires, record.Payload.GetProperty("expiresAtUtc").GetDateTimeOffset());
            });
            Assert.Empty(history.Records.Where(record => record.Kind == "worker-completed"));
        }
        if (scenario == "cold.paused-new-ticket")
        {
            Assert.Equal(1, payload.GetProperty("resumeHostCalls").GetInt32());
            Assert.Single(history.Records, record => record.Kind == "paused-reconciled");
            Assert.Single(history.Records, record => record.Kind == "resumed");
            var schedulerHistory = await runtime.Store.RecoverAsync(runId + "-scheduler", cancellationToken);
            Assert.Single(schedulerHistory.Records, record => record.Kind == "local-scheduler-checkpoint" &&
                record.Payload.GetProperty("phase").GetString() == "resuming");
            Assert.Equal(prepared.Request.RequestIdentity, payload.GetProperty("resumedRequestIdentity").GetString());
            Assert.Equal(prepared.Request.WriterToken, payload.GetProperty("resumedWriterToken").GetString());
            Assert.Equal(prepared.Request.AuthorizationCeiling, payload.GetProperty("resumedAuthorizationCeiling").GetString());
        }
        else if (!delayed)
        {
            Assert.Equal(0, payload.GetProperty("resumeHostCalls").GetInt32());
            Assert.Empty(history.Records.Where(record => record.Kind == "resumed"));
        }
        if (scenario == "cold.base-drift")
        {
            var current = await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", cancellationToken);
            Assert.NotEqual(initial.Commit, current.Commit);
            Assert.Equal(current.Commit, payload.GetProperty("receiverObservedBaseCommit").GetString());
        }
        if (delayed)
        {
            var artifactPath = payload.GetProperty("actualChildArtifactPath").GetString()!;
            Assert.True(Path.IsPathFullyQualified(artifactPath));
            var relative = Path.GetRelativePath(prepared.Request.ResultDirectory, artifactPath);
            Assert.False(Path.IsPathFullyQualified(relative));
            Assert.False(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.Equal("owned-child-completed", await File.ReadAllTextAsync(artifactPath, cancellationToken));
            Assert.True(payload.GetProperty("completionAfterDurableRegistration").GetBoolean());
            if (scenario.EndsWith("retained-controller", StringComparison.Ordinal))
                Assert.Single(history.Records, record => record.Kind == "worker-completed");
            else
            {
                Assert.Empty(history.Records.Where(record => record.Kind == "worker-completed"));
                Assert.Equal("IdentityUnknown", payload.GetProperty("freshControllerProcessOutcome").GetString());
            }
        }
        artifacts.MarkAssertionsPassed(); // Runtime remains retained for raw negative/fixture observations.
    }
}
