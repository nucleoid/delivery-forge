using DeliveryForge.Planning;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed partial class WaveAndRecoveryTests
{
    [Fact(Timeout = 30_000)]
    public async Task FixtureExpiry_unstarted_ticket_is_revoked_and_independent_work_can_progress()
    {
        var expired = Item("a") with { Demand = Demand with { TimeLimit = TimeSpan.FromSeconds(2) } };
        var scheduler = await Create([new("a", [], "integrated"), new("b", [], "integrated")], [expired, Item("b")], 1);
        var ticket = Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        await Task.Delay(TimeSpan.FromSeconds(2.1), Cancellation);
        await Assert.ThrowsAsync<SchedulingException>(() => scheduler.StartFixtureAsync(ticket, Cancellation));
        Assert.Equal("b", Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation)).NodeId);
        await Assert.ThrowsAsync<SchedulingException>(() => scheduler.StartFixtureAsync(ticket, Cancellation));
    }

    [Fact(Timeout = 30_000)]
    public async Task FixtureExpiry_started_work_cannot_claim_success_or_release_capacity_without_quiescence()
    {
        var expired = Item("a") with { Demand = Demand with { TimeLimit = TimeSpan.FromSeconds(2) } };
        var scheduler = await Create([new("a", [], "integrated"), new("b", [], "integrated")], [expired, Item("b")], 1);
        var ticket = Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        await scheduler.StartFixtureAsync(ticket, Cancellation);
        await Task.Delay(TimeSpan.FromSeconds(2.1), Cancellation);
        await Assert.ThrowsAsync<SchedulingException>(() => scheduler.RecordFixtureOutcomeAsync(ticket, ScheduledOutcome.Pass, true, Cancellation));
        Assert.Empty(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        await scheduler.RecordFixtureOutcomeAsync(ticket, ScheduledOutcome.Incomplete, false, Cancellation);
        Assert.Empty(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation));
        await scheduler.RecordFixtureOutcomeAsync(ticket, ScheduledOutcome.Incomplete, true, Cancellation);
        Assert.Equal("b", Assert.Single(await scheduler.AllocateFixtureWaveAsync(new HashSet<string>(), Cancellation)).NodeId);
    }
}
