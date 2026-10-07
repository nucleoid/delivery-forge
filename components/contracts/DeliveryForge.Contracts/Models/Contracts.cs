namespace DeliveryForge.Contracts.Models;

public abstract record ContractDocument(string SchemaVersion, string Kind, string Identity);

public sealed record PlanContract(
    string SchemaVersion, string Kind, string Identity, string Repository, string WorkItem,
    string Mode, string PlanRevision, string BaseCommit, DateTimeOffset CreatedAt,
    PlanScope Scope, IReadOnlyList<string> AcceptanceCriteria)
    : ContractDocument(SchemaVersion, Kind, Identity);

public sealed record PlanScope(string Outcome, IReadOnlyList<string> Included, IReadOnlyList<string> Excluded);

public sealed record RunManifestContract(
    string SchemaVersion, string Kind, string Identity, string RunId, string Repository,
    string PlanIdentity, string BaseCommit, string HeadCommit, string TreeId,
    string AuthorizationCeiling, string WorkflowState, IReadOnlyList<string> GateReceiptIds,
    DateTimeOffset UpdatedAt) : ContractDocument(SchemaVersion, Kind, Identity);

public sealed record CheckpointContract(
    string SchemaVersion, string Kind, string Identity, string RunId, long Sequence,
    string WorkflowState, string HeadCommit, string TreeId, string NextAction,
    IReadOnlyList<string> GateReceiptIds, DateTimeOffset CreatedAt)
    : ContractDocument(SchemaVersion, Kind, Identity);

public sealed record CapabilityReportContract(
    string SchemaVersion, string Kind, string Identity, string CapabilityId, string Tool,
    string Version, bool Supported, IReadOnlyList<string> Limitations, DateTimeOffset ObservedAt)
    : ContractDocument(SchemaVersion, Kind, Identity);

public sealed record GateReceiptContract(
    string SchemaVersion, string Kind, string Identity, string GateId, string PolicyIdentity,
    string BaseCommit, string HeadCommit, string TreeId, string Command, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt, int? ExitCode, string Outcome, string Reason,
    IReadOnlyDictionary<string, string> ArtifactHashes, bool SourceChanged,
    IReadOnlyList<string> Limitations, string? NotApplicableRationale)
    : ContractDocument(SchemaVersion, Kind, Identity);

public sealed record ReviewReceiptContract(
    string SchemaVersion, string Kind, string Identity, string BaseCommit, string HeadCommit,
    string TreeId, string PatchSha256, string ReviewerFamily, string Outcome, int FindingCount,
    string ArtifactHash, DateTimeOffset CreatedAt) : ContractDocument(SchemaVersion, Kind, Identity);

public sealed record PublicationReceiptContract(
    string SchemaVersion, string Kind, string Identity, string BaseCommit, string HeadCommit,
    string TreeId, string Action, string RemoteRef, string ResultId, DateTimeOffset CreatedAt)
    : ContractDocument(SchemaVersion, Kind, Identity);

public sealed record EvidencePolicyContract(
    string SchemaVersion, string Kind, string Identity, string PolicyId,
    IReadOnlyList<string> RequiredGates, string AuthorizedCeiling, bool RequireIndependentReview,
    IReadOnlyList<string> AllowedNotApplicable, DateTimeOffset CreatedAt)
    : ContractDocument(SchemaVersion, Kind, Identity);

public sealed record ReplayBundleContract(
    string SchemaVersion, string Kind, string Identity, string RunManifestIdentity,
    IReadOnlyList<string> ReceiptIdentities, IReadOnlyList<string> ContractIdentities,
    DateTimeOffset CreatedAt) : ContractDocument(SchemaVersion, Kind, Identity);
