namespace DeliveryForge.Scheduling;

public sealed record ResourceSlots(int Agent, int Cpu, long MemoryMiB, int Build, int Test, int Mutation);
public sealed record RunDemand(ResourceSlots Slots, IReadOnlyList<string> ExclusiveResources, TimeSpan TimeLimit, long TokenLimit, decimal CostLimit);
public sealed record BudgetPolicy(string Identity, bool Approved, ResourceSlots Caps, TimeSpan MaximumTime, long MaximumTokens, decimal MaximumCost);

public sealed class ResourceBudget
{
    public static ResourceBudget Measure(ResourceSlots measured, BudgetPolicy policy) => throw new NotImplementedException();
    public bool CanAllocate(RunDemand demand) => throw new NotImplementedException();
    public ResourceReservation Allocate(RunDemand demand) => throw new NotImplementedException();
}

public sealed class ResourceReservation : IDisposable
{
    public void Dispose() => throw new NotImplementedException();
}
