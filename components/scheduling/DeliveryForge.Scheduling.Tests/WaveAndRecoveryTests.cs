using DeliveryForge.Execution;
using DeliveryForge.Planning;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed class WaveAndRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delivery-forge-waves-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static RunDemand Demand => new(new(1, 1, 1024, 0, 0, 0), [], TimeSpan.FromSeconds(30), 10, 0);
    private static FixtureSchedulingItem Item(string id, int risk = 0) => new(id, new([id + ".cs"], []), Demand, risk);
    private RunStore Store => new(_root);

    private Task<Scheduler> Create(IReadOnlyList<DependencyNode> nodes, IReadOnlyList<FixtureSchedulingItem> items, int slots = 2, int bypasses = 2, string runId = "parent")
    {
        var capacity = new ResourceSlots(slots, slots, slots * 1024, 1, 1, 0);
        var budget = ResourceBudget.Measure(capacity, new("approved-fixture-budget", true, capacity, TimeSpan.FromMinutes(20), 1000, 0));
        return Scheduler.CreateContractFixtureAsync(runId, DependencyGraph.Build(nodes, 32), items, budget,
            new("approved-fixture-fairness", true, bypasses), Store, Cancellation);
    }

    [Fact]
    public async Task Independent_nodes_share_a_visibly_fixture_wave()
    {
        var scheduler = await Create([new("a", [], "integrated"), new("b", [], "integrated")], [Item("a"), Item("b")]);
        var wave = await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation);
        Assert.Equal(["a", "b"], wave.Select(ticket => ticket.NodeId));
        Assert.All(wave, ticket => Assert.True(ticket.Fixture));
    }

    [Fact]
    public async Task Passing_prerequisite_waits_for_integration_observation()
    {
        var scheduler = await Create([new("a", [], "integrated"), new("b", ["a"], "integrated")], [Item("a"), Item("b")]);
        var a = Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        await scheduler.StartFixtureAsync(a, Cancellation);
        await scheduler.RecordFixtureOutcomeAsync(a, ScheduledOutcome.Pass, true, Cancellation);
        Assert.Empty(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        Assert.Equal("b", Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string> { "a" }, Cancellation)).NodeId);
    }

    [Fact]
    public async Task Failure_blocks_dependents_while_independent_work_progresses()
    {
        var scheduler = await Create([new("a", [], "integrated"), new("b", ["a"], "integrated"), new("c", [], "integrated")], [Item("a"), Item("b"), Item("c")], 1);
        var a = Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        await scheduler.StartFixtureAsync(a, Cancellation);
        await scheduler.RecordFixtureOutcomeAsync(a, ScheduledOutcome.Fail, true, Cancellation);
        Assert.Equal("c", Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string> { "a" }, Cancellation)).NodeId);
    }

    [Fact]
    public async Task Finite_aging_prevents_higher_risk_class_starvation()
    {
        var scheduler = await Create([new("old", [], "integrated"), new("new1", [], "integrated"), new("new2", [], "integrated")], [Item("old", 10), Item("new1"), Item("new2")], 1, 1);
        var first = Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        Assert.Equal("new1", first.NodeId);
        await scheduler.StartFixtureAsync(first, Cancellation);
        await scheduler.RecordFixtureOutcomeAsync(first, ScheduledOutcome.Pass, true, Cancellation);
        Assert.Equal("old", Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation)).NodeId);
    }

    [Fact]
    public async Task Pause_revokes_unstarted_ticket_and_stop_prevents_resume()
    {
        var scheduler = await Create([new("a", [], "integrated")], [Item("a")]);
        var ticket = Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        await scheduler.PauseAsync(Cancellation);
        await Assert.ThrowsAsync<SchedulingException>(() => scheduler.StartFixtureAsync(ticket, Cancellation));
        Assert.Empty(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        await scheduler.ResumeFixtureAsync(new(), Cancellation);
        var replacement = Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        Assert.NotSame(ticket, replacement);
        await scheduler.StopAsync(Cancellation);
        await Assert.ThrowsAsync<SchedulingException>(() => scheduler.ResumeFixtureAsync(new(), Cancellation));
    }

    [Fact]
    public async Task Uncertain_quiescence_keeps_capacity_reserved_and_blocks_restart()
    {
        var scheduler = await Create([new("a", [], "integrated"), new("b", [], "integrated")], [Item("a"), Item("b")], 1);
        var ticket = Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        await scheduler.StartFixtureAsync(ticket, Cancellation);
        await scheduler.RecordFixtureOutcomeAsync(ticket, ScheduledOutcome.Incomplete, false, Cancellation);
        Assert.Empty(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        var queue = await Scheduler.ReconstructAsync("parent", Store, new(LiveChild: true), Cancellation);
        Assert.True(queue.Fixture);
        Assert.False(queue.MayResumeData);
        Assert.NotEmpty(queue.Limitations);
    }

    [Fact]
    public async Task Foreign_ticket_and_existing_journal_cannot_mint_dispatch()
    {
        var scheduler = await Create([new("a", [], "integrated")], [Item("a")]);
        var other = await Create([new("a", [], "integrated")], [Item("a")], runId: "other");
        var ticket = Assert.Single(await other.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        await Assert.ThrowsAsync<SchedulingException>(() => scheduler.StartFixtureAsync(ticket, Cancellation));
        await Assert.ThrowsAsync<SchedulingException>(() => Create([new("a", [], "integrated")], [Item("a")]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
