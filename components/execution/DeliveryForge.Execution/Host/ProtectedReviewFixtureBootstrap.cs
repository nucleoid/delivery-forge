using DeliveryForge.Review;

namespace DeliveryForge.Execution.Host;

// Descriptive inputs only. Policy identity/revision, protected base, source/view/root issuer identities
// are obtained by the factory from validated contract bytes and actual owned Git, not caller overrides.
public sealed class ProtectedReviewFixtureConfiguration
{
    private readonly byte[] _plan;
    private readonly byte[] _evidencePolicy;
    public ProtectedReviewFixtureConfiguration(string runIdentity, byte[] frozenPlanContract, byte[] evidencePolicyContract,
        string sourceRoot, string workerGitDirectory, string trustedGitExecutable, string relativePolicyPath,
        long maximumEvidenceBytes, TimeSpan entireBudget)
    {
        RunIdentity = runIdentity; _plan = (byte[])frozenPlanContract.Clone(); _evidencePolicy = (byte[])evidencePolicyContract.Clone();
        SourceRoot = sourceRoot; WorkerGitDirectory = workerGitDirectory; TrustedGitExecutable = trustedGitExecutable;
        RelativePolicyPath = relativePolicyPath; MaximumEvidenceBytes = maximumEvidenceBytes; EntireBudget = entireBudget;
    }
    public string RunIdentity { get; }
    public byte[] FrozenPlanContract => (byte[])_plan.Clone();
    public byte[] EvidencePolicyContract => (byte[])_evidencePolicy.Clone();
    public string SourceRoot { get; }
    public string WorkerGitDirectory { get; }
    public string TrustedGitExecutable { get; }
    public string RelativePolicyPath { get; }
    public long MaximumEvidenceBytes { get; }
    public TimeSpan EntireBudget { get; }
}

// Constructed only by the owner factory. All constituent provenance has the same original Test issuer.
public sealed class ProtectedReviewFixtureBootstrap : IDisposable
{
    internal ProtectedReviewFixtureBootstrap(ReviewAuthorityHost host, ControlledGitEvidenceContext objectView,
        ProtectedReviewSourceContext protectedSource, ReviewPolicy policy, ReviewAuthorityRootContext rootContext)
    { Host = host; ObjectView = objectView; ProtectedSource = protectedSource; Policy = policy; RootContext = rootContext; }
    public ReviewAuthorityHost Host { get; }
    public ControlledGitEvidenceContext ObjectView { get; }
    public ProtectedReviewSourceContext ProtectedSource { get; }
    public ReviewPolicy Policy { get; }
    public ReviewAuthorityRootContext RootContext { get; }
    public void Dispose() => ObjectView.Dispose(); // Disposal is not process-settlement or production proof.
}
