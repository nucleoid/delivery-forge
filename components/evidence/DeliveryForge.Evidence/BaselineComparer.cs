namespace DeliveryForge.Evidence;

public sealed class ReviewedBaseline
{
    public ReviewedBaseline(
        string baselineIdentity,
        string policyIdentity,
        string sourceRevision,
        string contentIdentity,
        bool fixture,
        IReadOnlyDictionary<string, decimal> metrics)
        : this(baselineIdentity, policyIdentity, sourceRevision, contentIdentity, fixture, metrics, false)
    {
    }

    internal ReviewedBaseline(
        string baselineIdentity,
        string policyIdentity,
        string sourceRevision,
        string contentIdentity,
        bool fixture,
        IReadOnlyDictionary<string, decimal> metrics,
        bool authorityVerified)
    {
        BaselineIdentity = baselineIdentity;
        PolicyIdentity = policyIdentity;
        SourceRevision = sourceRevision;
        ContentIdentity = contentIdentity;
        Fixture = fixture;
        Metrics = metrics;
        AuthorityVerified = authorityVerified;
    }

    public string BaselineIdentity { get; }
    public string PolicyIdentity { get; }
    public string SourceRevision { get; }
    public string ContentIdentity { get; }
    public bool Fixture { get; }
    public IReadOnlyDictionary<string, decimal> Metrics { get; }
    internal bool AuthorityVerified { get; }
}

public static class BaselineComparer
{
    public static NormalizedEvidence Compare(
        IReadOnlyDictionary<string, decimal> current,
        ReviewedBaseline baseline,
        ResolvedEvidencePolicy policy)
    {
        if (baseline.Fixture)
            return NormalizedEvidence.Incomplete("Fixture baseline cannot grant production authority.");
        if (!string.Equals(baseline.PolicyIdentity, policy.PolicyIdentity, StringComparison.Ordinal))
            return NormalizedEvidence.Error("Baseline policy identity does not match the frozen effective policy.");
        if (!string.Equals(baseline.SourceRevision, policy.SourceRevision, StringComparison.Ordinal))
            return NormalizedEvidence.Incomplete("Baseline is stale for the frozen protected source revision.");
        if (current.Count == 0)
            return NormalizedEvidence.Incomplete("Current evidence contains no comparable changed-code metrics.");

        var omitted = baseline.Metrics.Keys.Except(current.Keys, StringComparer.Ordinal).ToArray();
        if (omitted.Length > 0)
            return NormalizedEvidence.Incomplete(
                $"Current evidence omits baseline metrics: {string.Join(", ", omitted)}.");

        foreach (var metric in current.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!baseline.Metrics.TryGetValue(metric.Key, out var previous))
                return NormalizedEvidence.Incomplete($"Baseline omits required metric '{metric.Key}'.");

            if (metric.Value - previous > policy.MaximumChangedCodeDebtIncrease)
            {
                return new NormalizedEvidence(
                    GateOutcome.Fail,
                    $"Changed-code metric '{metric.Key}' regressed from {previous} to {metric.Value}.",
                    false,
                    policy.AuthorityVerified && baseline.AuthorityVerified,
                    []);
            }
        }

        if (!policy.AuthorityVerified || !baseline.AuthorityVerified)
            return NormalizedEvidence.Incomplete(
                "No-regression result is advisory until policy and baseline have protected or parent-admin authority.");

        return new NormalizedEvidence(
            GateOutcome.Pass, "Protected baseline contains every required metric with no unapproved regression.",
            false, true, []);
    }
}
