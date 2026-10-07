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
    EvidenceRequirement Requirement = EvidenceRequirement.Optional)
{
    internal string? RepositoryCommit { get; init; }
    internal string? RepositoryTree { get; init; }
    internal string? RepositoryBinding { get; init; }
    internal bool? RepositoryIsSymlink { get; init; }
    internal SymlinkResolution? RepositorySymlinkResolution { get; init; }
    internal string? RepositoryGenerationClassification { get; init; }

    public static EvidenceItem FromRepositoryFile(
        RepositoryFile exactFile,
        DateTimeOffset observedAt,
        IReadOnlyList<string> caveats,
        EvidenceRequirement requirement = EvidenceRequirement.Optional)
    {
        ArgumentNullException.ThrowIfNull(exactFile);
        ArgumentNullException.ThrowIfNull(caveats);
        var unsafeSymlink = exactFile.SymlinkResolution is not (SymlinkResolution.NotSymlink or SymlinkResolution.InTree);
        var generated = !exactFile.GenerationClassification.StartsWith("not-detected", StringComparison.Ordinal);
        var safetyCaveats = caveats
            .Concat(unsafeSymlink
                ? [$"Repository symlink safety is {exactFile.SymlinkResolution}; it cannot establish readiness."]
                : [])
            .Concat(generated
                ? [$"Repository file generation classification is {exactFile.GenerationClassification}; it cannot establish readiness without authoritative generator provenance."]
                : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var item = new EvidenceItem(
            EvidenceSourceKind.Repository,
            $"git:{exactFile.Path}",
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(exactFile.Bytes))}",
            observedAt,
            safetyCaveats,
            IsComplete: !unsafeSymlink && !generated,
            Requirement: requirement)
        {
            RepositoryCommit = exactFile.Commit,
            RepositoryTree = exactFile.Tree,
            RepositoryIsSymlink = exactFile.IsSymlink,
            RepositorySymlinkResolution = exactFile.SymlinkResolution,
            RepositoryGenerationClassification = exactFile.GenerationClassification
        };
        return item with { RepositoryBinding = ComputeRepositoryBinding(item) };
    }

    internal bool IsReaderBoundRepositoryEvidence() =>
        SourceKind == EvidenceSourceKind.Repository &&
        RepositoryCommit is not null &&
        RepositoryTree is not null &&
        string.Equals(RepositoryBinding, ComputeRepositoryBinding(this), StringComparison.Ordinal);

    internal bool HasConsistentSourceLocator()
    {
        if (Locator.StartsWith("git:", StringComparison.Ordinal))
            return SourceKind == EvidenceSourceKind.Repository && !Locator.Contains('\\');
        if (SourceKind == EvidenceSourceKind.Repository)
            return false;
        if (Locator.StartsWith("policy:", StringComparison.Ordinal))
            return SourceKind == EvidenceSourceKind.Policy;
        return true;
    }

    private static string ComputeRepositoryBinding(EvidenceItem item)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            item.SourceKind,
            item.Locator,
            item.Digest,
            item.ObservedAt,
            caveats = item.Caveats.ToArray(),
            item.Supersedes,
            item.IsComplete,
            item.Requirement,
            item.RepositoryCommit,
            item.RepositoryTree,
            item.RepositoryIsSymlink,
            item.RepositorySymlinkResolution,
            item.RepositoryGenerationClassification
        });
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }
}

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
    private const string HeadIdentityPrefix = "Mutable checkout HEAD identity: ";

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
        var headIdentity = _limitations
            .FirstOrDefault(item => item.StartsWith(HeadIdentityPrefix, StringComparison.Ordinal));
        var headParts = headIdentity?[HeadIdentityPrefix.Length..].Split('/', 2);
        HeadCommit = headParts is { Length: 2 } ? headParts[0] : commit;
        HeadTree = headParts is { Length: 2 } ? headParts[1].Split(';', 2)[0] : tree;
    }

    public string RepositoryRoot { get; }
    public string RequestedRef { get; }
    public string Commit { get; }
    public string Tree { get; }
    public string HeadCommit { get; }
    public string HeadTree { get; }
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
        IReadOnlyList<string> limitations,
        string? headCommit = null,
        string? headTree = null)
    {
        var copiedSubmodules = submodules.ToArray();
        if ((headCommit is null) != (headTree is null))
        {
            throw new PlanningException("Checkout HEAD commit and tree must be supplied together.");
        }
        var copiedLimitations = limitations.ToList();
        if (headCommit is not null && headTree is not null &&
            (!string.Equals(headCommit, commit, StringComparison.Ordinal) ||
             !string.Equals(headTree, tree, StringComparison.Ordinal)))
        {
            copiedLimitations.Add(
                $"{HeadIdentityPrefix}{headCommit}/{headTree}; mutable dirty/detached observations apply to checkout HEAD, not requested {commit}/{tree}.");
        }
        var normalizedLimitations = copiedLimitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var binding = ComputeBinding(
            repositoryRoot, requestedRef, commit, tree, detachedHead, dirty, shallow,
            copiedSubmodules, normalizedLimitations);
        return new RepositoryContext(
            repositoryRoot, requestedRef, commit, tree, detachedHead, dirty, shallow,
            copiedSubmodules, normalizedLimitations, binding);
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
        string repository,
        string workItem,
        string baseReference,
        bool baseReferenceWasDetached,
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
        Repository = repository;
        WorkItem = workItem;
        BaseReference = baseReference;
        BaseReferenceWasDetached = baseReferenceWasDetached;
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
    public string Repository { get; }
    public string WorkItem { get; }
    public string BaseReference { get; }
    public bool BaseReferenceWasDetached { get; }
    public string BaseCommit { get; }
    public string BaseTree { get; }
    public byte[] CanonicalBytes => _canonicalBytes.ToArray();
    public byte[] PlanContractBytes => _planContractBytes.ToArray();
    public bool DownstreamReady { get; }
    public IReadOnlyList<string> Limitations { get; }

    public FrozenPlan ReconcileBase(RepositoryContext current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.IsReaderIssued())
        {
            throw new PlanningException("Base reconciliation requires a reader-issued repository context.");
        }
        if (!string.Equals(BaseReference, current.RequestedRef, StringComparison.Ordinal) ||
            BaseReferenceWasDetached ||
            current.DetachedHead ||
            !IsFreshnessBearingReference(current.RequestedRef))
        {
            return WithBaseDrift(
                $"Base ref freshness could not be established: frozen ref '{BaseReference}' must be reconciled through the same mutable ref; current ref is '{current.RequestedRef}' and detached/pinned snapshots are not freshness evidence.");
        }
        if (string.Equals(BaseCommit, current.Commit, StringComparison.Ordinal) &&
            string.Equals(BaseTree, current.Tree, StringComparison.Ordinal))
        {
            return this;
        }

        return WithBaseDrift(
            $"Base drift detected for ref '{BaseReference}': frozen {BaseCommit}/{BaseTree}, current {current.Commit}/{current.Tree}; reconcile before downstream work.");
    }

    private FrozenPlan WithBaseDrift(string limitation) =>
        new FrozenPlan(
            Identity,
            ContractIdentity,
            Revision,
            PlanRevision,
            ContentDigest,
            Supersedes,
            Repository,
            WorkItem,
            BaseReference,
            BaseReferenceWasDetached,
            BaseCommit,
            BaseTree,
            _canonicalBytes,
            _planContractBytes,
            downstreamReady: false,
            Limitations
                .Append(limitation)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());

    private static bool IsFreshnessBearingReference(string reference) =>
        string.Equals(reference, "HEAD", StringComparison.Ordinal) ||
        reference.StartsWith("refs/heads/", StringComparison.Ordinal);
}
