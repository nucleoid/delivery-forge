using DeliveryForge.Planning;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed class DependencyGraphTests
{
    [Fact]
    public void Order_is_deterministic_and_dependencies_are_defensively_copied()
    {
        var dependencies = new[] { "a" };
        var graph = DependencyGraph.Build([new("b", dependencies, "integrated"), new("a", [], "integrated")], 8);
        dependencies[0] = "foreign";
        Assert.Equal(["a", "b"], graph.TopologicalOrder);
        Assert.Equal(["a"], graph.DependenciesOf("b"));
    }

    [Fact]
    public void Exact_cycle_chain_is_reported()
    {
        var error = Assert.Throws<SchedulingException>(() => DependencyGraph.Build(
            [new("c", ["a"], "integrated"), new("a", ["b"], "integrated"), new("b", ["c"], "integrated")], 8));
        Assert.Contains("a -> b -> c -> a", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_prerequisite_is_rejected()
    {
        Assert.Throws<SchedulingException>(() => DependencyGraph.Build([new("a", ["absent"], "integrated")], 8));
    }

    [Fact]
    public void Duplicate_nodes_and_excessive_graphs_are_rejected()
    {
        Assert.Throws<SchedulingException>(() => DependencyGraph.Build([new("a", [], "integrated"), new("a", [], "integrated")], 8));
        Assert.Throws<SchedulingException>(() => DependencyGraph.Build([new("a", [], "integrated"), new("b", [], "integrated")], 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Explicit_graph_bound_must_be_finite_and_supported(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DependencyGraph.Build([new("a", [], "integrated")], limit));
    }
}
