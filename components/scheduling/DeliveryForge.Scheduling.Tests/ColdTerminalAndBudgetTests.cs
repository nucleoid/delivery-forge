using System.Diagnostics;
using System.Text.Json;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed partial class LocalSingleTests
{
    private const string ColdFixtureEnvironment = "DELIVERY_FORGE_TEST_COLD_TERMINAL_INPUT";
    private sealed record ColdFixtureInput(string Root, DateTimeOffset FrozenAt, string PlanIdentity, FrozenWorkerPlan Worker);

    // Invoked through the compiled xUnit v3 apphost, never through an SDK/build command.
    // Ordinary suite execution is a harmless control; only the subprocess receives the private input.
    [Fact(Timeout = 600_000)]
    public async Task ColdTerminal_fixture_process_entry()
    {
        var inputPath = Environment.GetEnvironmentVariable(ColdFixtureEnvironment);
        if (inputPath is null) return;
        var input = JsonSerializer.Deserialize<ColdFixtureInput>(await File.ReadAllTextAsync(inputPath, Cancellation))!;
        using var repository = new PlanningRepository(input.Root);
        var plan = await repository.FreezeAsync(input.FrozenAt);
        Assert.Equal(input.PlanIdentity, plan.Identity);
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes, new FixtureControl());
        var scheduler = LocalSingleScheduler.Create(plan, input.Worker, Demand with { TimeLimit = TimeSpan.FromMinutes(5) }, Budget(),
            store, execution, worktrees, repository.Reader);
        try
        {
            var ticket = await scheduler.ReserveAsync(Cancellation);
            var request = await scheduler.ProvisionAndPrepareAsync(ticket, Cancellation);
            await execution.AcceptAsync(Accepted(request), Cancellation);
            Assert.True(await scheduler.StopAsync(Cancellation), scheduler.CleanupLimitation);
            Assert.Equal("stopped", (await store.RecoverAsync(input.Worker.RunId + "-scheduler", Cancellation)).Latest!.Payload.GetProperty("phase").GetString());
            await File.WriteAllTextAsync(inputPath + ".closed", Environment.ProcessId.ToString(), Cancellation);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            Assert.True(await scheduler.StopAsync(cleanup.Token) || await scheduler.ReconcileCleanupAsync(cleanup.Token), scheduler.CleanupLimitation);
        }
    }

    [Theory(Timeout = 600_000)]
    [InlineData("quiescent")]
    [InlineData("live")]
    [InlineData("unknown")]
    [InlineData("foreign")]
    [InlineData("writer-held")]
    [InlineData("unowned-pid")]
    public async Task ColdTerminal_fresh_parent_requires_current_proof_before_independent_progress(string observation)
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var frozenAt = DateTimeOffset.UtcNow;
        var plan = await repository.FreezeAsync(frozenAt);
        var worker = Worker(repository, plan, "terminal-cold");
        var inputPath = Path.Combine(repository.Root, "fixture-input.json");
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(new ColdFixtureInput(repository.Root, frozenAt, plan.Identity, worker)), Cancellation);
        await RunColdFixtureProcessAsync(inputPath);
        Assert.NotEqual(Environment.ProcessId, int.Parse(await File.ReadAllTextAsync(inputPath + ".closed", Cancellation)));
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var history = await store.RecoverAsync(worker.RunId, Cancellation);
        var prepared = store.ReadPayload<PreparedWorkerRecord>(history.Records.Single(record => record.Kind == "worker-prepared"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        var control = new ColdObservationControl(observation);
        using var execution = new ExecutionCoordinator(store, worktrees, processes, control);
        var demand = Demand with { TimeLimit = TimeSpan.FromMinutes(5) };
        var budget = Budget();
        var receiver = LocalSingleScheduler.Create(plan, worker, demand, budget, store, execution, worktrees, repository.Reader);
        var nextWorker = Worker(repository, plan, "independent-after-terminal");
        var next = LocalSingleScheduler.Create(plan, nextWorker, demand, Budget(), store, execution, worktrees, repository.Reader);
        WriterLease? held = null;
        try
        {
            // Stored stopped claims alone must not authorize the next issue in this fresh process.
            await Assert.ThrowsAsync<SchedulingException>(() => next.ReserveAsync(Cancellation));
            if (observation == "writer-held") held = worktrees.RecoverWriter(worker.Worktree, worker.RunId, worker.WriterToken);
            if (observation == "unowned-pid")
            {
                using var current = Process.GetCurrentProcess();
                var executable = current.MainModule!.FileName!;
                // Genuine live PID, deliberately never registered with this ProcessControl lifetime.
                await store.AppendAsync(worker.RunId, "process-registered", new ProcessRegistration(
                    new(current.Id, current.StartTime.ToUniversalTime().Ticks, executable, ProcessControl.Digest(executable, [])), DateTimeOffset.UtcNow), Cancellation);
            }
            var closed = await receiver.ReconcileColdTerminalAsync(Cancellation);
            Assert.Equal(observation == "quiescent", closed);
            Assert.True(budget.CanAllocate(demand), "Cleanup-only reconciliation cannot consume or grant a dispatch reservation.");
            Assert.Single((await store.RecoverAsync(worker.RunId, Cancellation)).Records, record => record.Kind == "worker-prepared");
            Assert.DoesNotContain((await store.RecoverAsync(worker.RunId, Cancellation)).Records, record => record.Kind == "resumed");
            if (closed)
            {
                Assert.True(control.StopObservations > 0, "Closure requires a fresh bound host observation after actual parent process exit.");
                Assert.Equal(prepared.Request.RequestIdentity, WorkerEnvelope.PrepareWorker(worker).RequestIdentity);
                using var nextExecution = new ExecutionCoordinator(store, worktrees, processes);
                var progressing = LocalSingleScheduler.Create(plan, Worker(repository, plan, "proven-independent"),
                    demand, Budget(), store, nextExecution, worktrees, repository.Reader);
                try { Assert.Equal("proven-independent", (await progressing.ReserveAsync(Cancellation)).RunId); }
                finally
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    Assert.True(await progressing.StopAsync(cleanup.Token), progressing.CleanupLimitation);
                }
            }
            else
            {
                var denied = LocalSingleScheduler.Create(plan, nextWorker with { RunId = "still-denied" }, demand, Budget(), store, execution, worktrees, repository.Reader);
                await Assert.ThrowsAsync<SchedulingException>(() => denied.ReserveAsync(Cancellation));
                Assert.False(Directory.Exists(nextWorker.Worktree));
                if (observation == "unowned-pid") Assert.False(Process.GetCurrentProcess().HasExited);
            }
        }
        finally { held?.Dispose(); }
    }

    private static async Task RunColdFixtureProcessAsync(string inputPath)
    {
        var assembly = typeof(LocalSingleTests).Assembly.Location;
        var apphost = Path.Combine(Path.GetDirectoryName(assembly)!, Path.GetFileNameWithoutExtension(assembly) + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(apphost), "Compiled xUnit v3 apphost is required for genuine process separation: " + apphost);
        var start = new ProcessStartInfo(apphost) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add("DeliveryForge.Scheduling.Tests.LocalSingleTests.ColdTerminal_fixture_process_entry");
        start.ArgumentList.Add("-parallel");
        start.ArgumentList.Add("none");
        start.Environment[ColdFixtureEnvironment] = inputPath;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start owned fixture process.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout;
            var error = await stderr;
            Assert.True(process.ExitCode == 0, "Cold producer fixture failed: " + output + error);
            Assert.True(File.Exists(inputPath + ".closed"), "Fixture must positively stop before its process exits.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false); // Only this exact owned helper; never a recovered PID.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
    }

    private sealed class ColdObservationControl(string observation) : IAgentControlPort
    {
        public int StopObservations { get; private set; }
        public Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan deadline, CancellationToken cancellationToken = default)
        {
            if (action == AgentControlAction.Stop) StopObservations++;
            return Task.FromResult(new AgentControlResult(observation == "foreign" ? binding with { RunId = "foreign-run" } : binding,
                action, observation switch { "live" => AgentActivityState.Live, "unknown" => AgentActivityState.Unknown, _ => AgentActivityState.Quiescent },
                true, "marked-cold-terminal-fixture", "External host observation is a test fixture; native Git/writer/process separation is real."));
        }
    }
}

public sealed class ColdBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ColdBudget_nine_minute_adoption_then_rollback_to_five_cannot_extend_one_minute()
    {
        var budget = LocalRunTimeBudget.Adopt(TimeSpan.FromMinutes(10), Start, Start.AddMinutes(10), Start.AddMinutes(9));
        Assert.Equal(TimeSpan.FromSeconds(50), budget.Remaining(TimeSpan.FromSeconds(10), Start.AddMinutes(5)));
    }

    [Fact]
    public void ColdBudget_forward_wall_deadline_is_stricter_than_pinned_monotonic_allowance()
    {
        var budget = LocalRunTimeBudget.Adopt(TimeSpan.FromMinutes(10), Start, Start.AddMinutes(10), Start.AddMinutes(9));
        Assert.Equal(TimeSpan.FromSeconds(15), budget.Remaining(TimeSpan.FromSeconds(10), Start.AddMinutes(9).AddSeconds(45)));
    }

    [Fact]
    public void ColdBudget_elapsed_adopted_allowance_cannot_be_revived_by_wall_rollback()
    {
        var budget = LocalRunTimeBudget.Adopt(TimeSpan.FromMinutes(10), Start, Start.AddMinutes(10), Start.AddMinutes(9));
        Assert.Throws<SchedulingException>(() => budget.Remaining(TimeSpan.FromMinutes(1), Start.AddMinutes(5)));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, 10)]
    [InlineData(9, 11)]
    public void ColdBudget_backward_expired_or_changed_original_ceiling_is_refused(int adoptedMinute, int expiryMinute) =>
        Assert.Throws<SchedulingException>(() => LocalRunTimeBudget.Adopt(TimeSpan.FromMinutes(10), Start, Start.AddMinutes(expiryMinute), Start.AddMinutes(adoptedMinute)));
}
