namespace DeliveryForge.Planning;

public sealed class PlanningException(string message) : Exception(message);

public enum IntakeDepth { Minimal, Deep }
public enum EvidenceRequirement { Optional, Required }
public enum EvidenceSourceKind { User, Repository, Policy, Memory, CodeIntelligence, Imported }
public enum CheckoutVerification { Unverified, Verified, Conflict }
public enum SymlinkResolution { NotSymlink, InTree, Escapes, Cycle, Missing }

public sealed record PlanningRequest(
    string Repository,
    string WorkItem,
    string Mode,
    string Outcome,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<string> AcceptanceCriteria,
    string RequestedCeiling,
    IntakeDepth Depth = IntakeDepth.Minimal,
    string? UserOwnedDecision = null);

public sealed record EvidenceItem(
    EvidenceSourceKind SourceKind,
    string Locator,
    string? Digest,
    DateTimeOffset ObservedAt,
    IReadOnlyList<string> Caveats,
    string? Supersedes = null,
    bool IsComplete = true);

public sealed record ImportedContextEntry(
    string Kind,
    string Locator,
    string Summary,
    string? Digest,
    DateTimeOffset ObservedAt,
    bool Stale = false,
    bool Truncated = false,
    bool Heuristic = false,
    CheckoutVerification CheckoutVerification = CheckoutVerification.Unverified,
    string? CheckoutDigest = null)
{
    internal string? VerificationBinding { get; init; }
}

public sealed class IntakeAssessment
{
    internal IntakeAssessment(
        bool ready,
        IntakeDepth depth,
        string? recommendedQuestion,
        IReadOnlyList<string> limitations,
        EvidenceRequirement importedContextRequirement,
        bool importedContextAvailable,
        string bindingDigest)
    {
        Ready = ready;
        Depth = depth;
        RecommendedQuestion = recommendedQuestion;
        Limitations = limitations.ToArray();
        ImportedContextRequirement = importedContextRequirement;
        ImportedContextAvailable = importedContextAvailable;
        BindingDigest = bindingDigest;
    }

    public bool Ready { get; }
    public IntakeDepth Depth { get; }
    public string? RecommendedQuestion { get; }
    public IReadOnlyList<string> Limitations { get; }
    public EvidenceRequirement ImportedContextRequirement { get; }
    public bool ImportedContextAvailable { get; }
    internal string BindingDigest { get; }
}

public sealed record RepositoryFile(
    string Path,
    string ObjectId,
    string Mode,
    byte[] Bytes,
    bool IsSymlink,
    bool EscapesWorktree,
    string GenerationClassification,
    SymlinkResolution SymlinkResolution = SymlinkResolution.NotSymlink);

public sealed record RepositoryContext(
    string RepositoryRoot,
    string RequestedRef,
    string Commit,
    string Tree,
    bool DetachedHead,
    bool Dirty,
    bool Shallow,
    IReadOnlyList<string> Submodules,
    IReadOnlyList<string> Limitations);

public sealed record ChangeTarget(string Path, string Symbol, string Effect);
public sealed record DependencyNode(string Id, IReadOnlyList<string> DependsOn, string IntegrationCondition);
public sealed record PlanGate(string Id, string Command, string ExpectedOutcome);
public sealed record RolloutPlan(
    string Compatibility,
    string Configuration,
    string Secrets,
    string Migration,
    string Reauthentication,
    string Backfill,
    string Observability,
    string Rollback,
    IReadOnlyList<string> OperatorActions);
public sealed record PlanUnknown(string Description, string Owner, bool BlocksReadiness);

public sealed record PlanDraft(
    PlanningRequest Request,
    RepositoryContext Repository,
    IReadOnlyList<EvidenceItem> Provenance,
    IReadOnlyList<ChangeTarget> ChangeMap,
    IReadOnlyList<DependencyNode> Dependencies,
    IReadOnlyList<PlanGate> Gates,
    RolloutPlan Rollout,
    IReadOnlyList<PlanUnknown> Unknowns,
    IntakeAssessment Intake);

public sealed class FrozenPlan
{
    private readonly byte[] _canonicalBytes;
    private readonly byte[] _planContractBytes;

    internal FrozenPlan(
        string identity,
        string contractIdentity,
        string revision,
        string planRevision,
        string contentDigest,
        string? supersedes,
        string baseCommit,
        string baseTree,
        byte[] canonicalBytes,
        byte[] planContractBytes,
        bool downstreamReady,
        IReadOnlyList<string> limitations)
    {
        Identity = identity;
        ContractIdentity = contractIdentity;
        Revision = revision;
        PlanRevision = planRevision;
        ContentDigest = contentDigest;
        Supersedes = supersedes;
        BaseCommit = baseCommit;
        BaseTree = baseTree;
        _canonicalBytes = canonicalBytes.ToArray();
        _planContractBytes = planContractBytes.ToArray();
        DownstreamReady = downstreamReady;
        Limitations = limitations.ToArray();
    }

    public string Identity { get; }
    public string ContractIdentity { get; }
    public string Revision { get; }
    public string PlanRevision { get; }
    public string ContentDigest { get; }
    public string? Supersedes { get; }
    public string BaseCommit { get; }
    public string BaseTree { get; }
    public byte[] CanonicalBytes => _canonicalBytes.ToArray();
    public byte[] PlanContractBytes => _planContractBytes.ToArray();
    public bool DownstreamReady { get; }
    public IReadOnlyList<string> Limitations { get; }

    public FrozenPlan ReconcileBase(string currentCommit, string currentTree)
    {
        if (string.Equals(BaseCommit, currentCommit, StringComparison.Ordinal) &&
            string.Equals(BaseTree, currentTree, StringComparison.Ordinal))
        {
            return this;
        }

        return new FrozenPlan(
            Identity,
            ContractIdentity,
            Revision,
            PlanRevision,
            ContentDigest,
            Supersedes,
            BaseCommit,
            BaseTree,
            _canonicalBytes,
            _planContractBytes,
            downstreamReady: false,
            Limitations
                .Append($"Base drift detected: frozen {BaseCommit}/{BaseTree}, current {currentCommit}/{currentTree}; reconcile before downstream work.")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }
}
