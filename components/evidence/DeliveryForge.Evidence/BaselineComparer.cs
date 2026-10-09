namespace DeliveryForge.Evidence;

public sealed record ReviewedBaseline(
    string BaselineIdentity,
    string PolicyIdentity,
    string SourceRevision,
    string ContentIdentity,
    bool Fixture,
    IReadOnlyDictionary<string, decimal> Metrics);

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
                    true,
                    []);
            }
        }

        return NormalizedEvidence.Pass("Reviewed baseline contains every compared metric with no unapproved regression.");
    }
}
