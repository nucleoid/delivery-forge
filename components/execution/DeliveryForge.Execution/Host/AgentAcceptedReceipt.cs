namespace DeliveryForge.Execution.Host;

public sealed record AgentAcceptedReceipt(
    string SchemaVersion,
    string RunId,
    string RequestIdentity,
    string PlanIdentity,
    string BaseCommit,
    string Worktree,
    string AdapterId,
    string AdapterVersion,
    string RuntimeRunIdentity,
    string RuntimeTaskIdentity,
    DateTimeOffset AcceptedAt,
    IReadOnlyList<AgentCapabilityEvidence> Capabilities);
