namespace DeliveryForge.Execution.Host;

public sealed record AgentAcceptedReceipt(
    string SchemaVersion,
    string RunId,
    string RequestIdentity,
    string PlanIdentity,
    string BaseCommit,
    string Worktree,
    string HostRunId,
    string ChildIdentity,
    DateTimeOffset AcceptedAt,
    string HostCapability);
