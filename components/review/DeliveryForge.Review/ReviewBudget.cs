namespace DeliveryForge.Review;

public sealed record ReviewBudget(int? MaximumRounds, TimeSpan MaximumDuration, long MaximumTokens, decimal MaximumCost)
{
    public static ReviewBudget None { get; } = new(null, TimeSpan.Zero, 0, 0);
    public bool IsFinite =>
        MaximumRounds is > 0 &&
        MaximumDuration > TimeSpan.Zero &&
        MaximumTokens > 0 &&
        MaximumCost > 0;
}

public sealed record ReviewCost(TimeSpan Duration, long Tokens, decimal Cost);

public sealed record BudgetDecision(
    ReviewOutcome Outcome,
    string Reason,
    IReadOnlyList<ReviewFinding> Findings);

public sealed class ReviewBudgetReservation
{
    internal ReviewBudgetReservation(Guid identity, ReviewCost estimated)
    {
        Identity = identity;
        Estimated = estimated;
    }

    internal Guid Identity { get; }
    internal ReviewCost Estimated { get; }
}

public sealed class ReviewBudgetLedger
{
    private readonly object _sync = new();
    private readonly ReviewBudget _budget;
    private readonly List<ReviewFinding> _findings = [];
    private readonly Dictionary<Guid, ReviewCost> _outstanding = [];

    private ReviewBudgetLedger(ReviewBudget budget) => _budget = budget;

    public static ReviewBudgetLedger FromTrustedPolicy(ReviewPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Trusted || !policy.Budget.IsFinite)
            throw new ArgumentException("A trusted frozen policy with a finite review budget is required.", nameof(policy));
        return new ReviewBudgetLedger(policy.Budget);
    }

    public int ConsumedRounds { get; private set; }
    public TimeSpan ConsumedDuration { get; private set; }
    public long ConsumedTokens { get; private set; }
    public decimal ConsumedCost { get; private set; }

    public void RecordFindings(IEnumerable<ReviewFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        lock (_sync) _findings.AddRange(findings);
    }

    public bool TryReserve(
        ReviewCost estimated,
        out ReviewBudgetReservation? reservation,
        out BudgetDecision decision)
    {
        ValidateCost(estimated);
        lock (_sync)
        {
            if (WouldExceed(estimated, includeRound: true))
            {
                reservation = null;
                decision = Exhausted();
                return false;
            }

            reservation = new ReviewBudgetReservation(Guid.NewGuid(), estimated);
            _outstanding.Add(reservation.Identity, estimated);
            ConsumedRounds++;
            ConsumedDuration += estimated.Duration;
            ConsumedTokens += estimated.Tokens;
            ConsumedCost += estimated.Cost;
            decision = new BudgetDecision(ReviewOutcome.Incomplete, "Review budget reserved.", _findings.ToArray());
            return true;
        }
    }

    public BudgetDecision Settle(ReviewBudgetReservation reservation, ReviewCost actual)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ValidateCost(actual);
        lock (_sync)
        {
            if (!_outstanding.Remove(reservation.Identity, out var estimated) || estimated != reservation.Estimated)
                throw new InvalidOperationException("Review budget reservation is unknown or already settled.");

            ConsumedDuration += actual.Duration - estimated.Duration;
            ConsumedTokens += actual.Tokens - estimated.Tokens;
            ConsumedCost += actual.Cost - estimated.Cost;
            return WouldExceed(new ReviewCost(TimeSpan.Zero, 0, 0), includeRound: false)
                ? Exhausted()
                : new BudgetDecision(ReviewOutcome.Incomplete, "Review budget settled.", _findings.ToArray());
        }
    }

    private bool WouldExceed(ReviewCost cost, bool includeRound) =>
        (includeRound && _budget.MaximumRounds is int rounds && ConsumedRounds + 1 > rounds) ||
        ConsumedDuration + cost.Duration > _budget.MaximumDuration ||
        ConsumedTokens + cost.Tokens > _budget.MaximumTokens ||
        ConsumedCost + cost.Cost > _budget.MaximumCost;

    private BudgetDecision Exhausted() => new(
        ReviewOutcome.Incomplete,
        "The explicit review budget is exhausted; review is paused without discarding findings.",
        _findings.ToArray());

    private static void ValidateCost(ReviewCost cost)
    {
        ArgumentNullException.ThrowIfNull(cost);
        if (cost.Duration < TimeSpan.Zero || cost.Tokens < 0 || cost.Cost < 0)
            throw new ArgumentOutOfRangeException(nameof(cost), "Review cost cannot be negative.");
    }
}
