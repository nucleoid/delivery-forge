using DeliveryForge.Planning;

namespace DeliveryForge.Scheduling;

// Pure graph data does not grant dispatch authority.
public sealed class DependencyGraph
{
    public static DependencyGraph Build(IEnumerable<DependencyNode> nodes, int maximumNodes) =>
        throw new NotImplementedException();

    public IReadOnlyList<string> TopologicalOrder => throw new NotImplementedException();
    public IReadOnlyList<string> DependenciesOf(string id) => throw new NotImplementedException();
}

public sealed class SchedulingException(string message) : Exception(message);
