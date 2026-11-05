using DeliveryForge.Planning;
using DeliveryForge.Scheduling;

namespace DeliveryForge.Tool;

// Pure scheduling data. Selecting a candidate does not allocate resources,
// prove integration, grant ownership or start a worker.
internal sealed record ProductionQueueNode(string Id, ChangeFootprint Changes, RunDemand Demand, int RiskClass, IReadOnlyList<string> Dependencies);
internal sealed class ProductionBatchQueue
{
    readonly ProductionQueueNode[] nodes;
    readonly DependencyGraph graph;
    readonly FairnessPolicy fairness;
    readonly Dictionary<string, string> phases = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> bypasses = new(StringComparer.Ordinal);
    internal ProductionBatchQueue(IReadOnlyList<ProductionQueueNode> input, FairnessPolicy policy)
    {
        if (input.Count is < 2 or > 32 || !policy.Approved || string.IsNullOrWhiteSpace(policy.Identity) || policy.MaximumBypasses is < 1 or > 1024)
            throw new ToolUsageException("Bounded queue and approved finite fairness policy required.");
        fairness = policy;
        nodes = input.Select(node => node with
        {
            Changes = node.Changes with { Paths = Array.AsReadOnly(node.Changes.Paths.ToArray()), SharedOwners = Array.AsReadOnly(node.Changes.SharedOwners.ToArray()) },
            Demand = node.Demand with { ExclusiveResources = Array.AsReadOnly(node.Demand.ExclusiveResources.ToArray()) },
            Dependencies = Array.AsReadOnly(node.Dependencies.ToArray())
        }).ToArray();
        if (nodes.Any(node => node.RiskClass is < 0 or > 100)) throw new ToolUsageException("Invalid queue risk class.");
        graph = DependencyGraph.Build(nodes.Select(node => new DependencyNode(node.Id, node.Dependencies, "frozen-plan-data")), 32);
        foreach (var node in nodes) { phases.Add(node.Id, "Pending"); bypasses.Add(node.Id, 0); }
    }
    internal ProductionQueueNode? SelectCandidate(ResourceBudget budget)
    {
        // Current measured installed profile has one intensive lane. Selection
        // does not secretly allocate a second reservation while one is pending.
        if (phases.Values.Any(phase => phase == "Selected")) return null;
        var ready = nodes.Where(node => phases[node.Id] == "Pending" && graph.DependenciesOf(node.Id).All(id => phases[id] == "Pass"))
            .OrderByDescending(node => bypasses[node.Id] >= fairness.MaximumBypasses)
            .ThenBy(node => bypasses[node.Id] >= fairness.MaximumBypasses ? 0 : node.RiskClass)
            .ThenBy(node => Array.IndexOf(nodes, node)).ToArray();
        var next = ready.FirstOrDefault(node => budget.CanAllocate(node.Demand) &&
            !nodes.Any(active => phases[active.Id] == "Selected" && OverlapDetector.Conflicts(active.Changes, node.Changes)));
        if (next is null) return null;
        phases[next.Id] = "Selected";
        foreach (var waiting in ready.Where(node => node.Id != next.Id)) bypasses[waiting.Id] = Math.Min(fairness.MaximumBypasses, bypasses[waiting.Id] + 1);
        return next;
    }
    internal void RecordData(string id, string phase)
    {
        if (!phases.TryGetValue(id, out var previous) || previous != "Selected" || phase is not ("Pass" or "Fail" or "Incomplete" or "ReplanRequired" or "LocalComplete"))
            throw new ToolUsageException("Only the selected queue candidate can record a terminal observation.");
        phases[id] = phase;
    }
    internal IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(phases, StringComparer.Ordinal);
}
