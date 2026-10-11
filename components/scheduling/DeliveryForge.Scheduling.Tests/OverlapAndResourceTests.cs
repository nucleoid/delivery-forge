using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed class OverlapTests
{
    private static ChangeFootprint Map(params string[] paths) => new(paths, []);

    [Fact]
    public void Disjoint_exact_paths_can_run_together() => Assert.False(OverlapDetector.Conflicts(Map("src/a.cs"), Map("src/b.cs")));

    [Theory]
    [InlineData("src/a.cs", "SRC/A.cs")]
    [InlineData("src", "src/a.cs")]
    [InlineData("src\\a.cs", "src/a.cs")]
    public void Portable_case_and_directory_overlap_serialize(string left, string right) => Assert.True(OverlapDetector.Conflicts(Map(left), Map(right)));

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("src/*.cs")]
    [InlineData("")]
    public void Unsafe_or_unresolved_maps_serialize(string path) => Assert.True(OverlapDetector.Conflicts(Map(path), Map("other.cs")));

    [Theory]
    [InlineData("migrations/001.cs", "migrations/002.cs")]
    [InlineData("schemas/one.json", "schemas/two.json")]
    [InlineData("quality/coverage-baseline.json", "quality/mutation-baseline.json")]
    [InlineData("src/a.g.cs", "src/b.generated.cs")]
    public void Shared_sensitive_classes_without_explicit_owners_serialize(string left, string right) =>
        Assert.True(OverlapDetector.Conflicts(Map(left), Map(right)));

    [Fact]
    public void Unknown_and_shared_owners_serialize()
    {
        Assert.True(OverlapDetector.Conflicts(new([], [], true), Map("other.cs")));
        Assert.True(OverlapDetector.Conflicts(new(["a.cs"], ["baseline:coverage"]), new(["b.cs"], ["baseline:coverage"])));
        Assert.False(OverlapDetector.Conflicts(new(["a.cs"], ["generated:a"]), new(["b.cs"], ["generated:b"])));
    }
}

public sealed class ResourceTests
{
    private static ResourceSlots Slots => new(4, 2, 4096, 1, 1, 0);
    private static BudgetPolicy Policy => new("approved-parent-policy", true, Slots, TimeSpan.FromMinutes(20), 10000, 1m);
    private static RunDemand Demand => new(new(1, 1, 2048, 1, 0, 0), ["schema:v1"], TimeSpan.FromMinutes(10), 1000, 0m);

    [Fact]
    public void Slots_and_exclusives_are_independent_and_release_is_idempotent()
    {
        var budget = ResourceBudget.Measure(Slots, Policy);
        var reservation = budget.Allocate(Demand);
        Assert.False(budget.CanAllocate(Demand));
        Assert.False(budget.CanAllocate(Demand with { Slots = new(1, 1, 2048, 0, 0, 0) }));
        Assert.True(budget.CanAllocate(Demand with { Slots = new(1, 1, 2048, 0, 0, 0), ExclusiveResources = ["other"] }));
        reservation.Dispose();
        reservation.Dispose();
        Assert.True(budget.CanAllocate(Demand));
    }

    [Fact]
    public void Measured_capacity_caps_agent_count_and_tool_slots()
    {
        var budget = ResourceBudget.Measure(Slots, Policy);
        Assert.False(budget.CanAllocate(Demand with { Slots = new(1, 3, 1, 0, 0, 0) }));
        Assert.False(budget.CanAllocate(Demand with { Slots = new(1, 1, long.MaxValue, 0, 0, 0) }));
        Assert.False(budget.CanAllocate(Demand with { Slots = new(1, 1, 1, 0, 0, 1) }));
        Assert.Throws<SchedulingException>(() => budget.Allocate(Demand with { Slots = new(1, 3, 1, 0, 0, 0) }));
    }

    [Fact]
    public void Missing_policy_or_measurement_and_negative_capacity_fail_closed()
    {
        Assert.Throws<SchedulingException>(() => ResourceBudget.Measure(Slots, Policy with { Approved = false }));
        Assert.Throws<SchedulingException>(() => ResourceBudget.Measure(Slots with { Cpu = 0 }, Policy));
        Assert.Throws<SchedulingException>(() => ResourceBudget.Measure(Slots with { MemoryMiB = -1 }, Policy));
        Assert.Throws<SchedulingException>(() => ResourceBudget.Measure(Slots, Policy with { Identity = "" }));
    }

    [Fact]
    public async Task Concurrent_parent_allocations_cannot_overcommit_one_slot()
    {
        var budget = ResourceBudget.Measure(Slots, Policy);
        var held = new System.Collections.Concurrent.ConcurrentBag<ResourceReservation>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            try { held.Add(budget.Allocate(Demand)); }
            catch (SchedulingException) { }
        }))).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(held);
        foreach (var reservation in held) reservation.Dispose();
        Assert.True(budget.CanAllocate(Demand));
    }

    [Fact]
    public void Approved_caps_can_lower_measured_capacity_and_inputs_are_copied()
    {
        var budget = ResourceBudget.Measure(Slots, Policy with { Caps = Slots with { Cpu = 1 } });
        Assert.False(budget.CanAllocate(Demand with { Slots = new(1, 2, 1, 0, 0, 0) }));
        var names = new[] { "schema:v1" };
        using var reservation = budget.Allocate(Demand with { ExclusiveResources = names });
        names[0] = "other";
        Assert.False(budget.CanAllocate(Demand with { Slots = new(1, 1, 1, 0, 0, 0) }));
    }

    [Fact]
    public void Cumulative_token_and_cost_budget_is_not_refunded_by_resource_release()
    {
        var budget = ResourceBudget.Measure(Slots, Policy);
        var demand = Demand with { TokenLimit = 6000, CostLimit = 0.75m };
        using (budget.Allocate(demand)) { }
        Assert.False(budget.CanAllocate(demand));
        Assert.False(budget.CanAllocate(Demand with { TokenLimit = 1, CostLimit = 0.5m }));
        Assert.True(budget.CanAllocate(Demand with { TokenLimit = 4000, CostLimit = 0.25m }));
    }

    [Fact]
    public void Finite_time_token_cost_and_positive_run_requirements_are_enforced()
    {
        var budget = ResourceBudget.Measure(Slots, Policy);
        Assert.False(budget.CanAllocate(Demand with { TimeLimit = TimeSpan.MaxValue }));
        Assert.False(budget.CanAllocate(Demand with { TokenLimit = long.MaxValue }));
        Assert.False(budget.CanAllocate(Demand with { CostLimit = 2m }));
        Assert.False(budget.CanAllocate(Demand with { Slots = new(0, 0, 0, 0, 0, 0) }));
    }
}
