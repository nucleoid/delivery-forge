using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Coordination;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed partial class LocalSingleTests
{
    [Fact]
    public async Task ProductionProfile_refuses_before_any_journal_or_fixture_dispatch()
    {
        using var repository = new PlanningRepository();
        Directory.CreateDirectory(repository.Root);
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        Assert.Equal(CoordinationAvailability.Unavailable, ProductionCoordinationProfile.CurrentUnavailable().Availability);
        var refusal = await Assert.ThrowsAsync<SchedulingException>(() => Scheduler.CreateProductionAsync(store, Cancellation));
        Assert.Contains("INCOMPLETE", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await store.DiscoverRunIdsAsync(Cancellation));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunDiscovery_rejects_reparse_ancestors_before_reading_immutable_claims(bool discover)
    {
        using var repository = new PlanningRepository();
        Directory.CreateDirectory(repository.Root);
        var target = Path.Combine(repository.Root, "actual-store");
        var original = new RunStore(target);
        await original.AppendAsync("prior", "local-scheduler-checkpoint", new { phase = "stopped" }, Cancellation);
        var alias = Path.Combine(repository.Root, "alias");
        Directory.CreateSymbolicLink(alias, target);
        try
        {
            var aliased = new RunStore(alias);
            if (discover)
                await Assert.ThrowsAsync<RunStoreCorruptionException>(() => aliased.DiscoverRunIdsAsync(Cancellation));
            else
                await Assert.ThrowsAsync<RunStoreCorruptionException>(() => aliased.RecoverAsync("prior", Cancellation));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public async Task RunDiscovery_rejects_linked_immutable_records_even_when_bytes_and_name_hash_match()
    {
        using var repository = new PlanningRepository();
        Directory.CreateDirectory(repository.Root);
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var record = await store.AppendAsync("prior", "local-scheduler-checkpoint", new { phase = "stopped" }, Cancellation);
        var relocated = Path.Combine(repository.Root, "outside-record.json");
        File.Move(record.Path, relocated);
        File.CreateSymbolicLink(record.Path, relocated);
        try { await Assert.ThrowsAsync<RunStoreCorruptionException>(() => store.RecoverAsync("prior", Cancellation)); }
        finally
        {
            File.Delete(record.Path);
            File.Move(relocated, record.Path);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task ElapsedBudget_cannot_provision_and_keeps_slots_until_explicit_stop()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var worker = Worker(repository, plan, "expired");
        var store = new RunStore(Path.Combine(repository.Root, "results"));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        using var execution = new ExecutionCoordinator(store, worktrees, processes);
        var slots = new ResourceSlots(1, 1, 1024, 0, 0, 0);
        var budget = ResourceBudget.Measure(slots, new("fixture-expiry", true, slots, TimeSpan.FromMinutes(20), 10000, 0));
        // The finite preflight must have realistic Git startup room; expiry is still tested after reservation.
        var demand = Demand with { TimeLimit = TimeSpan.FromSeconds(30) };
        var scheduler = LocalSingleScheduler.Create(plan, worker, demand, budget, store, execution, worktrees, repository.Reader);
        Exception? priorFailure = null;
        try
        {
            var ticket = await scheduler.ReserveAsync(Cancellation);
            await Task.Delay(TimeSpan.FromSeconds(30.1), Cancellation);
            await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ProvisionAndPrepareAsync(ticket, Cancellation));
            Assert.False(Directory.Exists(worker.Worktree));
            Assert.Empty((await store.RecoverAsync(worker.RunId, Cancellation)).Records);
            Assert.False(budget.CanAllocate(demand));
            Assert.True(await scheduler.StopAsync(Cancellation));
            Assert.True(budget.CanAllocate(demand));
        }
        catch (Exception exception) { priorFailure = exception; throw; }
        finally { Assert.True(await scheduler.StopAsync(Cancellation), $"Explicit cleanup failed; original failure before cleanup: {priorFailure}"); }
    }
}
