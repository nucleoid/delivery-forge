using System.Security.Cryptography;
using System.Text.Json;

namespace DeliveryForge.Planning;

public sealed class PlanningException(string message) : Exception(message);

public enum IntakeDepth { Minimal, Deep }
public enum EvidenceRequirement { Optional, Required }
public enum EvidenceSourceKind { User, Repository, Policy, Memory, CodeIntelligence, Imported }
public enum CheckoutVerification { Unverified, Verified, Conflict }
public enum SymlinkResolution { NotSymlink, InTree, Escapes, Cycle, Missing, BoundExceeded }

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
    string? UserOwnedDecision = null,
    string? RecommendedOption = null);

public sealed record EvidenceItem(
    EvidenceSourceKind SourceKind,
    string Locator,
    string? Digest,
    DateTimeOffset ObservedAt,
    IReadOnlyList<string> Caveats,
    string? Supersedes = null,
    bool IsComplete = true,
    EvidenceRequirement Requirement = EvidenceRequirement.Optional);

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
    internal string? VerifiedCommit { get; init; }
    internal string? VerifiedTree { get; init; }
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
        IReadOnlyList<VerifiedRepositoryIdentity> verifiedRepositoryIdentities,
        string bindingDigest)
    {
        Ready = ready;
        Depth = depth;
        RecommendedQuestion = recommendedQuestion;
        Limitations = limitations.ToArray();
        ImportedContextRequirement = importedContextRequirement;
        ImportedContextAvailable = importedContextAvailable;
        VerifiedRepositoryIdentities = verifiedRepositoryIdentities.ToArray();
        BindingDigest = bindingDigest;
    }

    public bool Ready { get; }
    public IntakeDepth Depth { get; }
    public string? RecommendedQuestion { get; }
    public IReadOnlyList<string> Limitations { get; }
    public EvidenceRequirement ImportedContextRequirement { get; }
    public bool ImportedContextAvailable { get; }
    internal IReadOnlyList<VerifiedRepositoryIdentity> VerifiedRepositoryIdentities { get; }
    internal string BindingDigest { get; }
}

internal sealed record VerifiedRepositoryIdentity(string Locator, string Commit, string Tree);

public sealed class RepositoryFile
{
    private readonly byte[] _bytes;

    internal RepositoryFile(
        string path,
        string objectId,
        string mode,
        byte[] bytes,
        bool isSymlink,
        bool escapesWorktree,
        string generationClassification,
        SymlinkResolution symlinkResolution,
        string commit,
        string tree)
    {
        Path = path;
        ObjectId = objectId;
        Mode = mode;
        _bytes = bytes.ToArray();
        IsSymlink = isSymlink;
        EscapesWorktree = escapesWorktree;
        GenerationClassification = generationClassification;
        SymlinkResolution = symlinkResolution;
        Commit = commit;
        Tree = tree;
    }

    public string Path { get; }
    public string ObjectId { get; }
    public string Mode { get; }
    public byte[] Bytes => _bytes.ToArray();
    public bool IsSymlink { get; }
    public bool EscapesWorktree { get; }
    public string GenerationClassification { get; }
    public SymlinkResolution SymlinkResolution { get; }
    public string Commit { get; }
    public string Tree { get; }
}

public sealed class RepositoryContext
{
    private readonly string[] _submodules;
    private readonly string[] _limitations;
    private readonly string _readerBinding;

    private RepositoryContext(
        string repositoryRoot,
        string requestedRef,
        string commit,
        string tree,
        bool detachedHead,
        bool dirty,
        bool shallow,
        IReadOnlyList<string> submodules,
        IReadOnlyList<string> limitations,
        string readerBinding)
    {
        RepositoryRoot = repositoryRoot;
        RequestedRef = requestedRef;
        Commit = commit;
        Tree = tree;
        DetachedHead = detachedHead;
        Dirty = dirty;
        Shallow = shallow;
        _submodules = submodules.ToArray();
        _limitations = limitations.ToArray();
        _readerBinding = readerBinding;
    }

    public string RepositoryRoot { get; }
    public string RequestedRef { get; }
    public string Commit { get; }
    public string Tree { get; }
    public bool DetachedHead { get; }
    public bool Dirty { get; }
    public bool Shallow { get; }
    public IReadOnlyList<string> Submodules => _submodules.ToArray();
    public IReadOnlyList<string> Limitations => _limitations.ToArray();

    internal static RepositoryContext Create(
        string repositoryRoot,
        string requestedRef,
        string commit,
        string tree,
        bool detachedHead,
        bool dirty,
        bool shallow,
        IReadOnlyList<string> submodules,
        IReadOnlyList<string> limitations)
    {
        var copiedSubmodules = submodules.ToArray();
        var copiedLimitations = limitations.ToArray();
        var binding = ComputeBinding(
            repositoryRoot, requestedRef, commit, tree, detachedHead, dirty, shallow,
            copiedSubmodules, copiedLimitations);
        return new RepositoryContext(
            repositoryRoot, requestedRef, commit, tree, detachedHead, dirty, shallow,
            copiedSubmodules, copiedLimitations, binding);
    }

    internal bool IsReaderIssued() => string.Equals(
        _readerBinding,
        ComputeBinding(
            RepositoryRoot, RequestedRef, Commit, Tree, DetachedHead, Dirty, Shallow,
            _submodules, _limitations),
        StringComparison.Ordinal);

    private static string ComputeBinding(
        string repositoryRoot,
        string requestedRef,
        string commit,
        string tree,
        bool detachedHead,
        bool dirty,
        bool shallow,
        IReadOnlyList<string> submodules,
        IReadOnlyList<string> limitations)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            repositoryRoot,
            requestedRef,
            commit,
            tree,
            detachedHead,
            dirty,
            shallow,
            submodules,
            limitations
        });
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }
}

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
