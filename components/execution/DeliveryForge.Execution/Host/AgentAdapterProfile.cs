namespace DeliveryForge.Execution.Host;

public enum AgentCapability
{
    Launch,
    CompletionEvidence,
    Pause,
    Stop,
    Resume,
    EvidenceExport
}

public enum AgentCapabilityStatus { Supported, Unsupported, Unverified }

public sealed record AgentCapabilityEvidence(
    AgentCapability Capability,
    AgentCapabilityStatus Status,
    string Interface,
    string EvidenceReference);

public sealed record AgentAdapterProfile(
    string AdapterId,
    string AdapterVersion,
    IReadOnlyList<AgentCapabilityEvidence> Capabilities);

public sealed record AgentCapabilityAssessment(
    bool Complete,
    IReadOnlyList<AgentCapability> Missing,
    string Outcome);

public static class KnownAgentProfiles
{
    public static AgentAdapterProfile CodexCli(string version) => new(
        "codex.cli", version,
        [
            Unverified(AgentCapability.Launch, "codex exec"),
            Unverified(AgentCapability.CompletionEvidence, "codex exec process exit/output"),
            Unsupported(AgentCapability.Pause, "No pause interface was discovered."),
            Unsupported(AgentCapability.Stop, "No adapter-owned stop interface was discovered."),
            Unverified(AgentCapability.Resume, "codex resume"),
            Unverified(AgentCapability.EvidenceExport, "codex exec --json")
        ]);

    public static AgentAdapterProfile ClaudeCodeCli(string version) => new(
        "claude-code.cli", version,
        [
            Unverified(AgentCapability.Launch, "claude --background"),
            Unverified(AgentCapability.CompletionEvidence, "claude logs <id>"),
            Unsupported(AgentCapability.Pause, "No pause interface was discovered."),
            Unverified(AgentCapability.Stop, "claude stop <id>"),
            Unverified(AgentCapability.Resume, "claude --resume <session-id> --background"),
            Unverified(AgentCapability.EvidenceExport, "claude --print --output-format json")
        ]);

    public static AgentAdapterProfile PiCliUnavailable() => new(
        "pi.cli", "unavailable",
        Enum.GetValues<AgentCapability>()
            .Select(capability => Unsupported(capability, "No pi executable or live interface proof was discovered on this host."))
            .ToArray());

    public static AgentAdapterProfile OpenClawOptional(string version) => new(
        "openclaw.sessions", version,
        [
            Unverified(AgentCapability.Launch, "sessions_spawn"),
            Unverified(AgentCapability.CompletionEvidence, "session completion event/history"),
            Unsupported(AgentCapability.Pause, "No generic pause operation is claimed."),
            Unsupported(AgentCapability.Stop, "No generic stop operation is claimed."),
            Unsupported(AgentCapability.Resume, "No generic resume operation is claimed."),
            Unverified(AgentCapability.EvidenceExport, "session receipt/history export")
        ]);

    private static AgentCapabilityEvidence Unverified(AgentCapability capability, string @interface) =>
        new(capability, AgentCapabilityStatus.Unverified, @interface, "Live conformance proof has not been imported; outcome is INCOMPLETE.");

    private static AgentCapabilityEvidence Unsupported(AgentCapability capability, string reason) =>
        new(capability, AgentCapabilityStatus.Unsupported, "none", reason);
}
