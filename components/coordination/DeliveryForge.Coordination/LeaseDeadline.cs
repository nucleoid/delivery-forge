namespace DeliveryForge.Coordination;

public static class LeaseDeadline
{
    public static TimeSpan ComputeSafeDeadline(
        DateTimeOffset serverTime,
        DateTimeOffset expiresAt,
        TimeSpan roundTrip,
        TimeSpan monotonicReceived,
        TimeSpan maximumClockUncertainty)
    {
        if (expiresAt <= serverTime) throw new ArgumentOutOfRangeException(nameof(expiresAt));
        if (roundTrip < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(roundTrip));
        if (monotonicReceived < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(monotonicReceived));
        if (maximumClockUncertainty < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumClockUncertainty));

        var conservativeRemaining = expiresAt - serverTime - roundTrip - maximumClockUncertainty;
        if (conservativeRemaining <= TimeSpan.Zero)
            throw new CoordinationUncertainException("No conservative lease lifetime remains after latency and clock uncertainty.");
        try { return monotonicReceived + conservativeRemaining; }
        catch (OverflowException exception)
        {
            throw new CoordinationUncertainException("Safe monotonic deadline overflowed.", exception);
        }
    }
}
