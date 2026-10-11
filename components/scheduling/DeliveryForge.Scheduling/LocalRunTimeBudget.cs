namespace DeliveryForge.Scheduling;

// Data-only time arithmetic. This cannot allocate capacity, authorize a run or mint a ticket.
public sealed class LocalRunTimeBudget
{
    private LocalRunTimeBudget() { }

    public static LocalRunTimeBudget Adopt(TimeSpan originalLimit, DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc, DateTimeOffset adoptedAtUtc) =>
        throw new NotImplementedException("Conservative cold-adoption monotonic allowance is not yet implemented.");

    public TimeSpan Remaining(TimeSpan elapsedSinceAdoption, DateTimeOffset nowUtc) =>
        throw new NotImplementedException("Conservative remaining local lifetime is not yet implemented.");
}
