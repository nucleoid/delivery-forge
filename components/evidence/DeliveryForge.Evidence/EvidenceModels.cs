namespace DeliveryForge.Evidence;

public enum EvidenceAdapterKind
{
    DotNet,
    Crap4CSharp,
    Mutate4CSharp
}

public enum GateOutcome
{
    Pass,
    Fail,
    Incomplete,
    Error,
    NotApplicable
}

public sealed record NormalizedEvidence(
    GateOutcome Outcome,
    string Reason,
    bool Fixture,
    bool ProductionCapable,
    IReadOnlyList<string> Limitations,
    string? NotApplicableRationale = null)
{
    public static NormalizedEvidence Pass(string reason, bool fixture = false) =>
        new(GateOutcome.Pass, reason, fixture, false, ["Tool outcome is not production authority."], null);

    public static NormalizedEvidence Incomplete(string reason, bool fixture = false, params string[] limitations) =>
        new(GateOutcome.Incomplete, reason, fixture, false, limitations, null);

    public static NormalizedEvidence Error(string reason, bool fixture = false, params string[] limitations) =>
        new(GateOutcome.Error, reason, fixture, false, limitations, null);
}

public sealed record ToolCapability(
    string Tool,
    string Version,
    string FormatVersion,
    bool Supported,
    bool Fixture,
    IReadOnlyList<string>? Limitations = null,
    string? ExecutablePath = null,
    string? ExecutableIdentity = null,
    IReadOnlyList<string>? Operations = null)
{
    internal bool DetectionVerified { get; init; }

    internal static ToolCapability FromDetection(
        string tool,
        string version,
        string formatVersion,
        bool fixture,
        IReadOnlyList<string> limitations,
        string executablePath,
        string executableIdentity,
        IReadOnlyList<string> operations) =>
        new(tool, version, formatVersion, true, fixture, limitations, executablePath, executableIdentity, operations)
        {
            DetectionVerified = true
        };

    public static ToolCapability DetectedFixture(
        string tool, string version, string formatVersion, string executablePath,
        string executableIdentity, IReadOnlyList<string> operations) =>
        FromDetection(tool, version, formatVersion, true, [], executablePath, executableIdentity, operations);

    internal static ToolCapability UnsupportedDetection(
        string tool, string formatVersion, bool fixture, string limitation) =>
        new(tool, "unavailable", formatVersion, false, fixture, [limitation])
        {
            DetectionVerified = true
        };

    public static ToolCapability Unsupported(string tool, string limitation) =>
        new(tool, "unavailable", "unavailable", false, false, [limitation]);
}

public sealed record RepositoryIdentity(string BaseCommit, string HeadCommit, string TreeId, bool IsClean);

public sealed record CommandInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record CommandResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool OwnedProcessQuiescent = true);

public sealed record GateRequest(
    string GateId,
    ResolvedEvidencePolicy Policy,
    ToolCapability Capability,
    EvidenceAdapterKind Adapter,
    string ConfigurationIdentity,
    RepositoryIdentity ExpectedRepository,
    CommandInvocation Invocation,
    string Scope,
    TimeSpan Timeout,
    string OutputDirectory);

public sealed record GateRunResult(
    GateOutcome Outcome,
    string Reason,
    bool Fixture,
    bool ProductionCapable,
    IReadOnlyList<string> Limitations,
    bool SourceChanged,
    CommandInvocation Invocation,
    CommandResult Execution,
    RepositoryIdentity Before,
    RepositoryIdentity After,
    string PolicyIdentity,
    string CapabilityIdentity,
    string ConfigurationIdentity,
    string Scope,
    IReadOnlyDictionary<string, string> ArtifactHashes,
    string ReceiptPath,
    string? NotApplicableRationale);

public interface ICommandExecutor
{
    Task<CommandResult> ExecuteAsync(
        CommandInvocation invocation,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public interface IRepositoryIdentityReader
{
    Task<RepositoryIdentity> ReadAsync(CancellationToken cancellationToken);
}

public sealed class EvidenceWriteException(string message) : Exception(message);
