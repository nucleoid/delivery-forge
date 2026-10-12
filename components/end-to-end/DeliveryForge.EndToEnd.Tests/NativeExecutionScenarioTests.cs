using System.Security.Cryptography;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

[Collection(LocalSingleFixtureCollection.Name)]
public sealed class NativeExecutionScenarioTests
{
    private static ScenarioOptions Options => new(ScenarioProfile.LocalContract, [],
        new(TimeSpan.FromSeconds(120), 2, 4096, 1000, 0m), 11017);

    [Fact(Timeout = 120_000)]
    public async Task Public_provision_uses_real_owned_Temp_worktree_and_exact_prepared_request()
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        await Spec(artifacts, "execution.real-provision");
        var result = await new ScenarioRunner(artifacts, runtime).RunAsync("execution.real-provision", Options, TestContext.Current.CancellationToken);
        var observation = await Observe(artifacts, result, "real-owned-worktree-provisioned", "exact-prepared-request-bound", "writer-exclusive-observed");
        Assert.Equal(observation.Payload.GetProperty("frozenBaseCommit").GetString(), observation.Payload.GetProperty("preparedHeadCommit").GetString());
        Assert.True(observation.Payload.GetProperty("worktreeRegistered").GetBoolean());
        Assert.True(observation.Payload.GetProperty("fixtureNativeTaskOnly").GetBoolean());
        artifacts.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Pause_actual_registered_native_child_prevents_late_dispatch_until_observed_quiescence()
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        await Spec(artifacts, "execution.pause-live-child");
        var result = await new ScenarioRunner(artifacts, runtime).RunAsync("execution.pause-live-child", Options, TestContext.Current.CancellationToken);
        var observation = await Observe(artifacts, result, "actual-native-child-paused", "paused-blocks-new-dispatch", "fresh-bound-fixture-host-observed");
        Assert.True(observation.Payload.GetProperty("childInitiallyLive").GetBoolean());
        Assert.True(observation.Payload.GetProperty("childFinallyQuiescent").GetBoolean());
        Assert.True(observation.Payload.GetProperty("pauseRecorded").GetBoolean());
        artifacts.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Stop_provisioned_native_run_refuses_Resume_and_any_late_task_Start()
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        await Spec(artifacts, "execution.stop-terminal");
        var result = await new ScenarioRunner(artifacts, runtime).RunAsync("execution.stop-terminal", Options, TestContext.Current.CancellationToken);
        var observation = await Observe(artifacts, result, "terminal-stop-recorded", "stopped-run-cannot-resume", "stopped-run-cannot-start-child");
        Assert.Equal(1, observation.Payload.GetProperty("processRegistrationsBefore").GetInt32());
        Assert.Equal(1, observation.Payload.GetProperty("processRegistrationsAfter").GetInt32());
        Assert.True(observation.Payload.GetProperty("childFinallyQuiescent").GetBoolean());
        artifacts.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Unknown_host_control_retains_capacity_even_after_local_native_child_exit()
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        await Spec(artifacts, "execution.unknown-host");
        var result = await new ScenarioRunner(artifacts, runtime).RunAsync("execution.unknown-host", Options, TestContext.Current.CancellationToken);
        var observation = await Observe(artifacts, result, "unknown-host-retains-capacity", "local-exit-not-host-proof", "fresh-bound-cleanup-required");
        Assert.True(observation.Payload.GetProperty("localChildQuiescentWhileHostUnknown").GetBoolean());
        Assert.False(observation.Payload.GetProperty("closedBeforeFreshHostObservation").GetBoolean());
        Assert.True(observation.Payload.GetProperty("closedAfterFreshBoundHostObservation").GetBoolean());
        artifacts.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Changed_runtime_owner_marker_refuses_cleanup_and_preserves_raw_bad_bytes()
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        await Spec(artifacts, "cleanup.changed-owner-marker");
        var result = await new ScenarioRunner(artifacts, runtime).RunAsync("cleanup.changed-owner-marker", Options, TestContext.Current.CancellationToken);
        var observation = await Observe(artifacts, result, "changed-marker-cleanup-refused", "bad-marker-retained", "outside-sentinel-preserved");
        Assert.True(Directory.Exists(runtime.Root));
        var actual = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(runtime.MarkerPath, TestContext.Current.CancellationToken)));
        Assert.NotEqual(runtime.ExpectedMarkerSha256, actual);
        Assert.Equal(actual, observation.Payload.GetProperty("actualMarkerSha256").GetString());
        Assert.True(File.Exists(observation.Payload.GetProperty("outsideSentinel").GetString()));
        artifacts.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Foreign_or_reparse_entry_refuses_cleanup_without_deleting_outside_sentinel()
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        await Spec(artifacts, "cleanup.foreign-reparse-entry");
        var result = await new ScenarioRunner(artifacts, runtime).RunAsync("cleanup.foreign-reparse-entry", Options, TestContext.Current.CancellationToken);
        var observation = await Observe(artifacts, result, "foreign-entry-cleanup-refused", "outside-sentinel-preserved", "reparse-capability-honest");
        Assert.True(Directory.Exists(runtime.Root));
        Assert.True(File.Exists(observation.Payload.GetProperty("outsideSentinel").GetString()));
        if (!observation.Payload.GetProperty("reparseCreated").GetBoolean())
        {
            Assert.Equal("INCOMPLETE", observation.Payload.GetProperty("reparseCapabilityOutcome").GetString());
            Assert.NotEmpty(observation.Payload.GetProperty("reparseCapabilityReason").GetString()!);
        }
        artifacts.MarkAssertionsPassed();
    }

    [Fact(Timeout = 120_000)]
    public async Task Actual_Temp_control_has_CreateNew_marker_real_worktree_registration_and_exclusive_writer()
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        await Spec(artifacts, "native-owned-worktree-control");
        Assert.Equal(runtime.RuntimeId, Path.GetRelativePath(runtime.Prefix, runtime.Root));
        Assert.Equal(runtime.ExpectedMarkerSha256, Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(runtime.MarkerPath, TestContext.Current.CancellationToken))));
        Assert.Throws<IOException>(() => { using var duplicate = new FileStream(runtime.MarkerPath, FileMode.CreateNew, FileAccess.Write); });
        var context = await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", TestContext.Current.CancellationToken);
        await runtime.Worktrees.PrepareAsync(runtime.Repository, runtime.Worktree, context.Commit, TestContext.Current.CancellationToken);
        var actual = await runtime.Worktrees.ObserveAsync(runtime.Worktree, TestContext.Current.CancellationToken);
        Assert.Equal(context.Commit, actual.HeadCommit);
        Assert.Equal(context.Tree, actual.TreeId);
        Assert.True(actual.IsClean);
        Assert.Contains(runtime.Worktree.Replace('\\', '/'),
            (await runtime.GitAsync(TestContext.Current.CancellationToken, "worktree", "list", "--porcelain")).Replace('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        using var writer = runtime.Worktrees.AcquireWriter(runtime.Worktree, runtime.RuntimeId, Guid.NewGuid().ToString("N"));
        runtime.RecordWriterLease(writer);
        Assert.Throws<DeliveryForge.Execution.Git.WorktreeBoundaryException>(() => runtime.Worktrees.AcquireWriter(runtime.Worktree, "foreign-fixture", "foreign-token"));
        artifacts.MarkAssertionsPassed(); // This direct control retains its live runtime; cleanup orchestration is validated separately.
    }

    [Fact(Timeout = 120_000)]
    public async Task Actual_native_control_is_LiveOwned_then_positively_quiescent_while_runtime_is_retained()
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        await Spec(artifacts, "native-quiescence-control");
        var child = runtime.StartBoundedChild();
        var before = runtime.Processes.Observe(child.Identity);
        await artifacts.WriteNewJsonAsync("native-control-observation.json", new { fixture = true, child.Identity, before }, TestContext.Current.CancellationToken);
        Assert.True(before.Outcome == ProcessControlOutcome.LiveOwned, before.Reason);
        Assert.True(Directory.Exists(runtime.Root));
        var stopped = await runtime.Processes.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(stopped.Outcome is ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited, stopped.Reason);
        Assert.Equal(ProcessControlOutcome.AlreadyExited, runtime.Processes.Observe(child.Identity).Outcome);
        Assert.True(Directory.Exists(runtime.Root));
        artifacts.MarkAssertionsPassed();
    }

    private static async Task<StoredRunRecord> Observe(OwnedScenarioRun artifacts, ScenarioResult result, params string[] invariants)
    {
        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.True(result.Fixture);
        Assert.False(result.FullRealProfileCertified);
        Assert.Equal(OwnedScenarioRun.InitialIntegratedBase, result.IntegratedBaseCommit);
        foreach (var key in invariants) Assert.True(result.Invariants[key]);
        var observation = Assert.Single((await artifacts.Store.RecoverAsync(artifacts.RunId, TestContext.Current.CancellationToken)).Records,
            record => record.Kind == "scenario-execution-observation");
        Assert.Contains(observation.Sha256, result.ReceiptIds);
        Assert.True(observation.Payload.GetProperty("fixture").GetBoolean());
        Assert.False(observation.Payload.GetProperty("productionCertified").GetBoolean());
        return observation;
    }
    private static Task Spec(OwnedScenarioRun artifacts, string name) => artifacts.WriteNewJsonAsync("native-slice-spec.json", new
    {
        fixture = true,
        productionCertified = false,
        fullRealProfileCertified = false,
        name,
        integratedBaseCommit = artifacts.IntegratedSource.Commit,
        implementationHead = artifacts.IntegratedSource.HeadCommit,
        seed = Options.Seed,
        budget = Options.Budget,
        runtimeCleanup = "RED stub; initial live runtime archived and retained",
        limitation = "Actual Git/native boundaries plus explicitly fixture host metadata only; no model worker or quality certificate."
    }, TestContext.Current.CancellationToken);
}
