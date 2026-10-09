namespace DeliveryForge.Execution.Host;

public sealed record AgentCompletionReceipt(
    string SchemaVersion,
    string RunId,
    string RequestIdentity,
    string AdapterId,
    string AdapterVersion,
    string RuntimeRunIdentity,
    string RuntimeTaskIdentity,
    string Outcome,
    string Worktree,
    string HeadCommit,
    string TreeId,
    DateTimeOffset CompletedAt,
    IReadOnlyList<string> ArtifactPaths,
    IReadOnlyList<string> Limitations);
