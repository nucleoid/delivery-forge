namespace DeliveryForge.Scheduling;

// Data-only time arithmetic. This cannot allocate capacity, authorize a run or mint a ticket.
public sealed class LocalRunTimeBudget
{
    private readonly DateTimeOffset _startedAtUtc;
    private readonly DateTimeOffset _expiresAtUtc;
    private readonly TimeSpan _adoptedAllowance;

    private LocalRunTimeBudget(DateTimeOffset startedAtUtc, DateTimeOffset expiresAtUtc, TimeSpan adoptedAllowance)
    {
        _startedAtUtc = startedAtUtc;
        _expiresAtUtc = expiresAtUtc;
        _adoptedAllowance = adoptedAllowance;
    }

    public static LocalRunTimeBudget Adopt(TimeSpan originalLimit, DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc, DateTimeOffset adoptedAtUtc)
    {
        if (originalLimit <= TimeSpan.Zero || expiresAtUtc - startedAtUtc != originalLimit ||
            adoptedAtUtc < startedAtUtc || adoptedAtUtc >= expiresAtUtc)
            throw new SchedulingException("Original finite lifetime differs, is exhausted, or cannot be adopted with this clock observation.");
        return new(startedAtUtc, expiresAtUtc, expiresAtUtc - adoptedAtUtc);
    }

    public TimeSpan Remaining(TimeSpan elapsedSinceAdoption, DateTimeOffset nowUtc)
    {
        if (elapsedSinceAdoption < TimeSpan.Zero || nowUtc < _startedAtUtc)
            throw new SchedulingException("Local budget clock moved backwards or monotonic elapsed time is unknown.");
        var monotonic = _adoptedAllowance - elapsedSinceAdoption;
        var wall = _expiresAtUtc - nowUtc;
        var remaining = monotonic < wall ? monotonic : wall;
        if (remaining <= TimeSpan.Zero) throw new SchedulingException("The immutable finite local run time budget is exhausted.");
        return remaining;
    }
}
