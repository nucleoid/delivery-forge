namespace DeliveryForge.Scheduling;

public sealed record ResourceSlots(int Agent, int Cpu, long MemoryMiB, int Build, int Test, int Mutation);
public sealed record RunDemand(ResourceSlots Slots, IReadOnlyList<string> ExclusiveResources, TimeSpan TimeLimit, long TokenLimit, decimal CostLimit);
public sealed record BudgetPolicy(string Identity, bool Approved, ResourceSlots Caps, TimeSpan MaximumTime, long MaximumTokens, decimal MaximumCost);

// Trusted parent budget data is not distributed ownership or child-quiescence proof.
public sealed class ResourceBudget
{
    private readonly object _gate = new();
    private readonly ResourceSlots _capacity;
    private readonly BudgetPolicy _policy;
    private ResourceSlots _used = new(0, 0, 0, 0, 0, 0);
    private long _allocatedTokens;
    private decimal _allocatedCost;
    private readonly HashSet<string> _exclusive = new(StringComparer.Ordinal);

    private ResourceBudget(ResourceSlots capacity, BudgetPolicy policy) { _capacity = capacity; _policy = policy; }

    public static ResourceBudget Measure(ResourceSlots measured, BudgetPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Approved || string.IsNullOrWhiteSpace(policy.Identity) || policy.Identity.Length > 256 ||
            !ValidSlots(measured) || !ValidSlots(policy.Caps) || !ValidLimits(policy.MaximumTime, policy.MaximumTokens, policy.MaximumCost))
            throw new SchedulingException("Measured capacity and an approved identified finite parent policy are required.");
        var caps = policy.Caps;
        return new ResourceBudget(new(Math.Min(measured.Agent, caps.Agent), Math.Min(measured.Cpu, caps.Cpu),
            Math.Min(measured.MemoryMiB, caps.MemoryMiB), Math.Min(measured.Build, caps.Build), Math.Min(measured.Test, caps.Test), Math.Min(measured.Mutation, caps.Mutation)), policy);
    }

    public string PolicyIdentity => _policy.Identity;

    public bool CanAllocate(RunDemand demand)
    {
        if (!TryCapture(demand, out var captured)) return false;
        lock (_gate) return Fits(captured);
    }

    public ResourceReservation Allocate(RunDemand demand)
    {
        if (!TryCapture(demand, out var captured)) throw new SchedulingException("Run demand requires finite, policy-bounded resource/time/token/cost limits.");
        lock (_gate)
        {
            if (!Fits(captured)) throw new SchedulingException("Run demand exceeds remaining capacity or contends on an exclusive resource.");
            _used = Add(_used, captured.Slots);
            _allocatedTokens = checked(_allocatedTokens + captured.TokenLimit);
            _allocatedCost = checked(_allocatedCost + captured.CostLimit);
            foreach (var resource in captured.ExclusiveResources) _exclusive.Add(resource);
            return new ResourceReservation(() => Release(captured));
        }
    }

    private bool TryCapture(RunDemand? demand, out RunDemand captured)
    {
        captured = null!;
        if (demand is null || !ValidSlots(demand.Slots) || !ValidLimits(demand.TimeLimit, demand.TokenLimit, demand.CostLimit) ||
            demand.TimeLimit > _policy.MaximumTime || demand.TokenLimit > _policy.MaximumTokens || demand.CostLimit > _policy.MaximumCost ||
            demand.ExclusiveResources is null || demand.ExclusiveResources.Count > 1024) return false;
        var resources = demand.ExclusiveResources.ToArray();
        if (resources.Any(resource => string.IsNullOrWhiteSpace(resource) || resource.Length > 256 || resource != resource.Trim() || resource.Any(char.IsControl)) ||
            resources.Distinct(StringComparer.Ordinal).Count() != resources.Length) return false;
        captured = demand with { ExclusiveResources = Array.AsReadOnly(resources) };
        return true;
    }

    private bool Fits(RunDemand demand)
    {
        var slots = demand.Slots;
        // Subtraction avoids accepting overflow from an attacker-controlled demand.
        return demand.TokenLimit <= _policy.MaximumTokens - _allocatedTokens && demand.CostLimit <= _policy.MaximumCost - _allocatedCost &&
            slots.Agent <= _capacity.Agent - _used.Agent && slots.Cpu <= _capacity.Cpu - _used.Cpu &&
            slots.MemoryMiB <= _capacity.MemoryMiB - _used.MemoryMiB && slots.Build <= _capacity.Build - _used.Build &&
            slots.Test <= _capacity.Test - _used.Test && slots.Mutation <= _capacity.Mutation - _used.Mutation &&
            !demand.ExclusiveResources.Any(_exclusive.Contains);
    }

    // Allocated token/cost ceilings stay consumed; releasing slots does not prove unused spend.
    private void Release(RunDemand demand)
    {
        lock (_gate)
        {
            var slots = demand.Slots;
            _used = new(_used.Agent - slots.Agent, _used.Cpu - slots.Cpu, _used.MemoryMiB - slots.MemoryMiB,
                _used.Build - slots.Build, _used.Test - slots.Test, _used.Mutation - slots.Mutation);
            foreach (var resource in demand.ExclusiveResources) _exclusive.Remove(resource);
        }
    }

    private static bool ValidSlots(ResourceSlots? slots) => slots is not null && slots.Agent is > 0 and <= 4096 && slots.Cpu is > 0 and <= 4096 &&
        slots.MemoryMiB is > 0 and <= 1073741824 && slots.Build is >= 0 and <= 4096 && slots.Test is >= 0 and <= 4096 && slots.Mutation is >= 0 and <= 4096;
    private static bool ValidLimits(TimeSpan time, long tokens, decimal cost) => time > TimeSpan.Zero && time <= TimeSpan.FromDays(1) && tokens is > 0 and <= 1000000000 && cost is >= 0 and <= 1000000;
    private static ResourceSlots Add(ResourceSlots left, ResourceSlots right) => new(checked(left.Agent + right.Agent), checked(left.Cpu + right.Cpu),
        checked(left.MemoryMiB + right.MemoryMiB), checked(left.Build + right.Build), checked(left.Test + right.Test), checked(left.Mutation + right.Mutation));
}

// Only the issuing budget creates a reservation. Expiry never releases a live child implicitly.
public sealed class ResourceReservation : IDisposable
{
    private Action? _release;
    internal ResourceReservation(Action release) => _release = release;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
