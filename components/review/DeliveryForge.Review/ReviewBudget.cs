namespace DeliveryForge.Review;

public sealed record ReviewBudget(int? MaximumRounds, TimeSpan MaximumDuration, long MaximumTokens, decimal MaximumCost)
{
    public static ReviewBudget None { get; } = new(null, TimeSpan.Zero, 0, 0);
    public bool IsFinite =>
        MaximumRounds is null or > 0 &&
        MaximumDuration > TimeSpan.Zero &&
        MaximumTokens > 0 &&
        MaximumCost > 0;
}

public sealed record ReviewCost(TimeSpan Duration, long Tokens, decimal Cost);

public sealed record BudgetDecision(
    ReviewOutcome Outcome,
    string Reason,
    IReadOnlyList<ReviewFinding> Findings);

public sealed class ReviewBudgetLedger(ReviewBudget budget)
{
    private readonly ReviewBudget _budget = budget.IsFinite
        ? budget
        : throw new ArgumentException("A finite review budget is required.", nameof(budget));
    private readonly List<ReviewFinding> _findings = [];

    public int ConsumedRounds { get; private set; }
    public TimeSpan ConsumedDuration { get; private set; }
    public long ConsumedTokens { get; private set; }
    public decimal ConsumedCost { get; private set; }

    public void RecordFindings(IEnumerable<ReviewFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        _findings.AddRange(findings);
    }

    public bool TryReserve(ReviewCost cost, out BudgetDecision decision)
    {
        if (cost.Duration < TimeSpan.Zero || cost.Tokens < 0 || cost.Cost < 0)
            throw new ArgumentOutOfRangeException(nameof(cost), "Review cost cannot be negative.");

        var exhausted =
            (_budget.MaximumRounds is int rounds && ConsumedRounds + 1 > rounds) ||
            ConsumedDuration + cost.Duration > _budget.MaximumDuration ||
            ConsumedTokens + cost.Tokens > _budget.MaximumTokens ||
            ConsumedCost + cost.Cost > _budget.MaximumCost;
        if (exhausted)
        {
            decision = new BudgetDecision(
                ReviewOutcome.Incomplete,
                "The explicit review budget is exhausted; review is paused without discarding findings.",
                _findings.ToArray());
            return false;
        }

        ConsumedRounds++;
        ConsumedDuration += cost.Duration;
        ConsumedTokens += cost.Tokens;
        ConsumedCost += cost.Cost;
        decision = new BudgetDecision(ReviewOutcome.Incomplete, "Review budget reserved.", _findings.ToArray());
        return true;
    }
}
