using DeliveryForge.Planning;

namespace DeliveryForge.Scheduling;

// Pure graph data does not grant dispatch authority.
public sealed class DependencyGraph
{
    private readonly Dictionary<string, IReadOnlyList<string>> _dependencies;
    private DependencyGraph(Dictionary<string, IReadOnlyList<string>> dependencies, List<string> order)
    {
        _dependencies = dependencies;
        TopologicalOrder = order.AsReadOnly();
    }

    public static DependencyGraph Build(IEnumerable<DependencyNode> nodes, int maximumNodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (maximumNodes is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        var dependencies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var edgeCount = 0;
        foreach (var node in nodes)
        {
            if (dependencies.Count >= maximumNodes) throw new SchedulingException("Graph exceeds its explicit node bound.");
            if (node is null || !ValidId(node.Id) || node.DependsOn is null || string.IsNullOrWhiteSpace(node.IntegrationCondition))
                throw new SchedulingException("Dependency nodes require explicit identities and integration conditions.");
            if (node.DependsOn.Count > maximumNodes) throw new SchedulingException("Dependency list exceeds graph bound.");
            var edges = node.DependsOn.ToArray();
            edgeCount = checked(edgeCount + edges.Length);
            if (edgeCount > 65536 || edges.Any(id => !ValidId(id)) || edges.Distinct(StringComparer.Ordinal).Count() != edges.Length)
                throw new SchedulingException("Dependency edges are invalid, duplicated or exceed the finite edge bound.");
            Array.Sort(edges, StringComparer.Ordinal);
            if (!dependencies.TryAdd(node.Id, Array.AsReadOnly(edges))) throw new SchedulingException($"Duplicate node '{node.Id}'.");
        }
        foreach (var (id, edges) in dependencies)
            foreach (var edge in edges)
                if (!dependencies.ContainsKey(edge)) throw new SchedulingException($"Node '{id}' requires missing prerequisite '{edge}'.");

        // Iterative DFS avoids making caller graph depth a call-stack limit.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var root in dependencies.Keys.Order(StringComparer.Ordinal))
        {
            if (state.ContainsKey(root)) continue;
            var stack = new List<(string Id, int Next)> { (root, 0) };
            state[root] = 1;
            while (stack.Count != 0)
            {
                var (id, next) = stack[^1];
                var edges = dependencies[id];
                if (next == edges.Count)
                {
                    state[id] = 2;
                    order.Add(id);
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                stack[^1] = (id, next + 1);
                var dependency = edges[next];
                if (!state.TryGetValue(dependency, out var observed))
                {
                    state[dependency] = 1;
                    stack.Add((dependency, 0));
                }
                else if (observed == 1)
                {
                    var start = stack.FindIndex(frame => frame.Id == dependency);
                    throw new SchedulingException("Dependency cycle: " + string.Join(" -> ", stack.Skip(start).Select(frame => frame.Id).Append(dependency)));
                }
            }
        }
        return new DependencyGraph(dependencies, order);
    }

    public IReadOnlyList<string> TopologicalOrder { get; }
    public IReadOnlyList<string> DependenciesOf(string id) => _dependencies.TryGetValue(id, out var edges)
        ? edges : throw new SchedulingException($"Unknown node '{id}'.");
    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && id == id.Trim() && !id.Any(char.IsControl);
}

public sealed class SchedulingException(string message) : Exception(message);
