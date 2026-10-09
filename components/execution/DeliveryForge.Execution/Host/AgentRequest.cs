namespace DeliveryForge.Execution.Host;

public sealed record AgentRequest(
    string SchemaVersion,
    string RunId,
    string RequestIdentity,
    string PlanIdentity,
    string BaseCommit,
    string Repository,
    string Worktree,
    string ResultDirectory,
    string WriterToken,
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<string> Exclusions,
    string AdapterId,
    string AdapterVersion,
    IReadOnlyList<AgentCapability> RequiredCapabilities,
    string AuthorizationCeiling = "implement");
