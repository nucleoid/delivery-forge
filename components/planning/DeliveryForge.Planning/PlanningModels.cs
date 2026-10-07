namespace DeliveryForge.Planning;

public sealed class PlanningException(string message) : Exception(message);

public enum IntakeDepth { Minimal, Deep }
public enum EvidenceRequirement { Optional, Required }
public enum EvidenceSourceKind { User, Repository, Policy, Memory, CodeIntelligence, Imported }

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
    bool Heuristic = false);

public sealed record IntakeAssessment(
    bool Ready,
    IntakeDepth Depth,
    string? RecommendedQuestion,
    IReadOnlyList<string> Limitations);

public sealed record RepositoryFile(
    string Path,
    string ObjectId,
    string Mode,
    byte[] Bytes,
    bool IsSymlink,
    bool EscapesWorktree,
    string GenerationClassification);

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
    IReadOnlyList<PlanUnknown> Unknowns);

public sealed record FrozenPlan(
    string Identity,
    string ContractIdentity,
    string Revision,
    string BaseCommit,
    string BaseTree,
    byte[] CanonicalBytes,
    bool DownstreamReady,
    IReadOnlyList<string> Limitations)
{
    public FrozenPlan ReconcileBase(string currentCommit, string currentTree)
    {
        if (string.Equals(BaseCommit, currentCommit, StringComparison.Ordinal) &&
            string.Equals(BaseTree, currentTree, StringComparison.Ordinal))
        {
            return this;
        }

        return this with
        {
            DownstreamReady = false,
            Limitations = Limitations
                .Append($"Base drift detected: frozen {BaseCommit}/{BaseTree}, current {currentCommit}/{currentTree}; reconcile before downstream work.")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
        };
    }
}
