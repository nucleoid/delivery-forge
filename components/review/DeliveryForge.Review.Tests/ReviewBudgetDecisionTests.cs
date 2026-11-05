namespace DeliveryForge.Review.Tests;

// Ledger DATA controls; no native reviewer, policy acceptance or provider token
// hard-cap capability is issued by these fixture policies.
public sealed class ReviewBudgetDecisionTests
{
    static ReviewBudgetLedger Ledger() => ReviewBudgetLedger.FromTrustedPolicy(ReviewPolicy.TrustedFixture(
        "sha256:" + new string('7', 64), [new FamilyPair("gpt", "claude")], ["source"], false,
        new ReviewBudget(1, TimeSpan.FromSeconds(10), 100, 1m)));

    [Fact]
    public void Exact_limit_settlement_is_not_exhausted_even_when_diagnostic_text_contains_the_word()
    {
        var ledger = Ledger();
        Assert.True(ledger.TryReserve(new(TimeSpan.FromSeconds(10), 100, 1m), out var reservation, out var reserved));
        Assert.False(reserved.IsExhausted);
        var settled = ledger.Settle(reservation!, new(TimeSpan.FromSeconds(10), 100, 1m));
        Assert.False(settled.IsExhausted);
        Assert.False((settled with { Reason = "The word exhausted is untrusted diagnostic DATA." }).IsExhausted);
        Assert.Equal(ReviewOutcome.Incomplete, settled.Outcome);
        Assert.Equal(1, ledger.ConsumedRounds);
        Assert.Equal(100, ledger.ConsumedTokens);
        Assert.Equal(1m, ledger.ConsumedCost);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("tokens")]
    [InlineData("cost")]
    public void Actual_overrun_sets_typed_exhaustion_without_losing_findings_or_changing_accounting(string dimension)
    {
        var ledger = Ledger();
        Assert.True(ledger.TryReserve(new(TimeSpan.FromSeconds(10), 100, 1m), out var reservation, out _));
        var finding = new ReviewFinding("R1", "material", "Preserve the actual finding.", "Source.cs:1");
        ledger.RecordFindings([finding]);
        var actual = new ReviewCost(TimeSpan.FromSeconds(10) + (dimension == "duration" ? TimeSpan.FromTicks(1) : TimeSpan.Zero),
            dimension == "tokens" ? 101 : 100, dimension == "cost" ? 1.01m : 1m);
        var settled = ledger.Settle(reservation!, actual);
        Assert.True(settled.IsExhausted);
        Assert.True((settled with { Reason = "Diagnostic wording changed completely." }).IsExhausted);
        Assert.Equal(ReviewOutcome.Incomplete, settled.Outcome);
        Assert.Equal([finding], settled.Findings);
        Assert.Equal(actual.Duration, ledger.ConsumedDuration);
        Assert.Equal(actual.Tokens, ledger.ConsumedTokens);
        Assert.Equal(actual.Cost, ledger.ConsumedCost);
        Assert.Throws<InvalidOperationException>(() => ledger.Settle(reservation!, actual));
    }

    [Fact]
    public void Rejected_next_round_has_typed_exhaustion_and_does_not_issue_another_reservation()
    {
        var ledger = Ledger();
        Assert.True(ledger.TryReserve(new(TimeSpan.FromSeconds(1), 1, .01m), out var first, out _));
        Assert.False(ledger.Settle(first!, new(TimeSpan.FromSeconds(1), 1, .01m)).IsExhausted);
        Assert.False(ledger.TryReserve(new(TimeSpan.FromSeconds(1), 1, .01m), out var rejected, out var exhausted));
        Assert.Null(rejected);
        Assert.True(exhausted.IsExhausted);
        Assert.Equal(1, ledger.ConsumedRounds);
        Assert.Equal(1, ledger.ConsumedTokens);
        Assert.Equal(.01m, ledger.ConsumedCost);
    }
}
