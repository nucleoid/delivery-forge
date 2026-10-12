namespace DeliveryForge.Planning;

// Caller-selected DATA constraints, obtained independently of the untrusted bundle.
// None of these strings is execution, policy, review or publication authority.
public sealed record PlanImportSource(string RepositoryRoot, string MutableReference, string TrustedGitExecutable);

public sealed record PlanImportExpectations(string BundleIdentity, string ContractIdentity, string Repository,
    string WorkItem, string RepositoryRoot, string MutableReference, string RequestedCeiling);

public sealed class PlanImportResult
{
    internal PlanImportResult(FrozenPlan? plan, IReadOnlyList<string> diagnostics)
    { Plan = plan; Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); }
    public FrozenPlan? Plan { get; }
    public bool DownstreamReady => Plan?.DownstreamReady == true;
    public IReadOnlyList<string> Diagnostics { get; }
}

public static class PlanImporter
{
    public const int MaximumBundleBytes = 16 * 1024 * 1024;

    // Future implementation must create its private prepared context from the ACTUAL
    // trusted reader/root/ref and working metadata before constructing a FrozenPlan.
    // No public context/Ready setter, PlanContract-only projection or supplied Git flags.
    public static Task<PlanImportResult> ImportAndReconcileAsync(ReadOnlyMemory<byte> richBundle,
        PlanImportSource source, PlanImportExpectations expectations, TimeSpan entireBudget,
        ImportedContextEnvelope? freshImportedContext = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); // No Git/path access for pre-cancelled callers.
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(expectations);
        if (entireBudget <= TimeSpan.Zero || entireBudget > TimeSpan.FromSeconds(120))
            throw new ArgumentOutOfRangeException(nameof(entireBudget));
        if (richBundle.Length is 0 or > MaximumBundleBytes)
            throw new PlanningException("Rich planning bundle exceeds the bounded import byte allowance.");
        throw new NotImplementedException("Bounded rich-plan owner import/reconciliation is RED; stored readiness/source flags cannot recreate a FrozenPlan.");
    }
}
