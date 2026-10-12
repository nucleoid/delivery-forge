using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Scheduling;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

// Real public boundaries. These regressions do not provide worker or production evidence.
[Collection(LocalSingleFixtureCollection.Name)]
public sealed class NativeCleanupRegressionTests
{
    [Theory(Timeout = 120_000)]
    [InlineData(null)]
    [InlineData("different-fixture-tag")]
    public async Task Every_controller_owned_live_child_blocks_cleanup_regardless_of_tag(string? tag)
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        var child = runtime.Processes.StartOwned(Launch(), tag);
        try
        {
            var live = runtime.Processes.Observe(child.Identity);
            await artifacts.WriteNewJsonAsync("all-owned-child-red.json", new { fixture = true, tag, child.Identity, live }, TestContext.Current.CancellationToken);
            Assert.True(live.Outcome == ProcessControlOutcome.LiveOwned, live.Reason);
            Assert.False(await runtime.TryCleanupAsync(TestContext.Current.CancellationToken));
            Assert.True(Directory.Exists(runtime.Root));
            var settled = await runtime.Processes.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.True(settled.Outcome is ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited, settled.Reason);
            Assert.Equal(ProcessControlOutcome.AlreadyExited, runtime.Processes.Observe(child.Identity).Outcome);
            Assert.True(await runtime.TryCleanupAsync(TestContext.Current.CancellationToken));
            artifacts.MarkAssertionsPassed();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (runtime.Processes.Observe(child.Identity).Outcome == ProcessControlOutcome.LiveOwned)
                await runtime.Processes.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(1), cleanup.Token);
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Archive_interval_retains_an_exclusive_writer_fence(bool contend)
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        var context = await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", TestContext.Current.CancellationToken);
        await runtime.Worktrees.PrepareAsync(runtime.Repository, runtime.Worktree, context.Commit, TestContext.Current.CancellationToken);
        var token = Guid.NewGuid().ToString("N");
        using (var writer = runtime.Worktrees.AcquireWriter(runtime.Worktree, runtime.RuntimeId, token)) runtime.RecordWriterLease(writer);
        WriterLease? competing = null;
        var reachedArchive = false;
        var rejectedByFence = false;
        runtime.FixtureArchiveEntryBarrier = async cancellationToken =>
        {
            reachedArchive = true;
            if (!contend) return;
            try { competing = runtime.Worktrees.RecoverWriter(runtime.Worktree, runtime.RuntimeId, token); }
            catch (WorktreeBoundaryException) { rejectedByFence = true; }
            await artifacts.WriteNewJsonAsync("archive-fence-observation.json", new
            {
                fixture = true,
                rejectedByFence,
                acquiredCompetingWriter = competing is not null,
                limitation = "Actual public writer claim at archive entry; no simulated lock proof."
            }, cancellationToken);
        };
        try
        {
            var removed = await runtime.TryCleanupAsync(TestContext.Current.CancellationToken);
            Assert.True(reachedArchive);
            if (contend)
            {
                if (competing is not null)
                {
                    Assert.False(removed);
                    Assert.True(Directory.Exists(runtime.Root));
                    Assert.True(Directory.Exists(runtime.Worktree));
                }
                Assert.True(rejectedByFence, "A new public writer acquired the worktree during the destructive cleanup interval.");
            }
            else Assert.True(removed);
            artifacts.MarkAssertionsPassed();
        }
        finally { runtime.FixtureArchiveEntryBarrier = null; competing?.Dispose(); }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replaced_root_or_traversal_ancestor_is_neither_deleted_nor_archived(bool ancestor)
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        var outside = Path.Combine(runtime.Prefix, Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(outside));
        Directory.CreateDirectory(outside);
        await using (var marker = new FileStream(Path.Combine(outside, "owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await System.Text.Json.JsonSerializer.SerializeAsync(marker, new { fixture = true, outside, artifactRunId = artifacts.RunId, nonce = Guid.NewGuid().ToString("N"), retained = true }, cancellationToken: TestContext.Current.CancellationToken);
        var sentinelName = "outside-sentinel-" + Guid.NewGuid().ToString("N") + ".txt";
        var sentinel = Path.Combine(outside, sentinelName);
        await using (var stream = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("must never copy or delete"), TestContext.Current.CancellationToken);
        await artifacts.WriteNewJsonAsync("reparse-regression-owned-target.json", new { fixture = true, outside, sentinel, retained = true }, TestContext.Current.CancellationToken);
        var replaced = ancestor ? runtime.Repository : runtime.Root;
        var retainedOriginal = Path.Combine(runtime.Prefix, Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(retainedOriginal));
        Directory.Move(replaced, retainedOriginal);
        try
        {
            try { Directory.CreateSymbolicLink(replaced, outside); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // No replacement was created: restore only this exact live-owned moved directory.
                if (!Directory.Exists(replaced) && !File.Exists(replaced)) Directory.Move(retainedOriginal, replaced);
                await artifacts.WriteNewJsonAsync("reparse-capability.json", new { fixture = true, outcome = "INCOMPLETE", reason = exception.Message, actualLinkCreated = false }, TestContext.Current.CancellationToken);
                artifacts.RetainArtifacts("Reparse capability unsupported; original runtime and sentinel retained.");
                return;
            }
            await artifacts.WriteNewJsonAsync("reparse-capability.json", new { fixture = true, outcome = "OBSERVED", actualLinkCreated = true }, TestContext.Current.CancellationToken);
            Assert.False(await runtime.TryCleanupAsync(TestContext.Current.CancellationToken));
        }
        finally { await runtime.DisposeAsync(); }
        Assert.Equal("must never copy or delete", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(artifacts.Root, sentinelName, SearchOption.AllDirectories));
        Assert.True(Directory.Exists(retainedOriginal));
        artifacts.MarkAssertionsPassed();
    }

    [Theory(Timeout = 120_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Post_reservation_interruption_closes_before_later_independent_progress(bool cancel)
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        LocalSingleScheduler? reserved = null;
        var runner = new ScenarioRunner(artifacts, runtime)
        {
            FixtureAfterReserve = async (scheduler, cancellationToken) =>
            {
                reserved = scheduler; // Keep the real owning reference; stored data cannot reconstruct it.
                await artifacts.WriteNewJsonAsync("post-reserve-barrier.json", new { fixture = true, successfullyReserved = true, cancel }, cancellationToken);
                if (cancel)
                {
                    interruption.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw new IOException("Injected fixture setup failure after actual Reserve.");
            }
        };
        try
        {
            var options = new ScenarioOptions(ScenarioProfile.LocalContract, [], new(TimeSpan.FromSeconds(120), 2, 4096, 1000, 0m), 11017);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("execution.real-provision", options, interruption.Token));
            else await Assert.ThrowsAsync<IOException>(() => runner.RunAsync("execution.real-provision", options, interruption.Token));
            Assert.NotNull(reserved);
            var ids = await runtime.Store.DiscoverRunIdsAsync(TestContext.Current.CancellationToken);
            var terminal = false;
            foreach (var id in ids)
                terminal |= (await runtime.Store.RecoverAsync(id, TestContext.Current.CancellationToken)).Records.Any(record => record.Kind == "local-scheduler-checkpoint" && record.Payload.GetProperty("phase").GetString() == "stopped");
            Assert.True(terminal, "Post-Reserve interruption left the actual owning scheduler without a stopped checkpoint.");
            await using var independent = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
            var plan = await SchedulingScenarioFixture.FreezeAsync(independent, TestContext.Current.CancellationToken);
            var worker = SchedulingScenarioFixture.Worker(independent, plan, "independent");
            var worktrees = new WorktreeManager();
            using var execution = new ExecutionCoordinator(independent.Store, worktrees, independent.Processes);
            var next = LocalSingleScheduler.Create(plan, worker, SchedulingScenarioFixture.Demand, SchedulingScenarioFixture.Budget(), independent.Store, execution, worktrees, independent.Reader);
            try { _ = await next.ReserveAsync(TestContext.Current.CancellationToken); }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Assert.True(await next.StopAsync(cleanup.Token) || await next.ReconcileCleanupAsync(cleanup.Token), next.CleanupLimitation);
            }
            artifacts.MarkAssertionsPassed();
        }
        finally
        {
            if (reserved is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var closed = await reserved.StopAsync(cleanup.Token) || await reserved.ReconcileCleanupAsync(cleanup.Token);
                await artifacts.WriteNewJsonAsync("post-reserve-finally-observation.json", new { fixture = true, closed, reserved.CleanupLimitation }, CancellationToken.None);
                Assert.True(closed, "Retained owning scheduler could not be positively closed: " + reserved.CleanupLimitation);
            }
        }
    }

    private static ProcessLaunch Launch()
    {
        if (!OperatingSystem.IsWindows()) return new(Find("sleep"), ["30"]);
        var executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, "pwsh.exe")).FirstOrDefault(File.Exists)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return new(executable, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]);
    }
    private static string Find(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, name)).First(File.Exists);
}
