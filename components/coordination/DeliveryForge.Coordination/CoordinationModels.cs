using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeliveryForge.Coordination;

public enum CoordinationOperation { Acquire, Renew, Inspect, Release }
public enum CoordinationAvailability { Available, Unavailable, Incomplete }
public enum LeaseOwnership { Held, Contended, Uncertain, Lost, Released }
public enum TransportOutcome { Acquired, Renewed, Inspected, Released, Contended, LeaseLost, Uncertain }
public enum LeaseReleaseState { Pending, Released, NotOwned, Unknown }
public enum InspectionState { Available, HeldByCaller, HeldByOther }
public enum CoordinationLifecyclePhase { Acquiring, Held, Renewing, Cleaning, Closed }
public enum CoordinationIntentKind { Acquire, Renew, Inspect, Release }

public sealed record CoordinationCapabilities(
    string ContractVersion,
    CoordinationAvailability Availability,
    bool Fixture,
    IReadOnlySet<CoordinationOperation> Operations,
    TimeSpan MinimumTtl,
    TimeSpan MaximumTtl,
    TimeSpan IdempotencyHorizon,
    bool DecimalStringFencing,
    bool HolderIdentityMasking,
    bool AuthenticatedExactScope,
    IReadOnlyList<string> Limitations)
{
    public const string V1ContractVersion = "continuum.coordination.v1";

    public static CoordinationCapabilities Unavailable(string limitation) => new(
        V1ContractVersion, CoordinationAvailability.Unavailable, false,
        new HashSet<CoordinationOperation>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
        false, false, false, [limitation]);
}

internal static class CoordinationContractFixture
{
    internal static CoordinationCapabilities Capabilities(
        string contractVersion = CoordinationCapabilities.V1ContractVersion,
        CoordinationAvailability availability = CoordinationAvailability.Available,
        bool fixture = true,
        IReadOnlySet<CoordinationOperation>? operations = null,
        TimeSpan? minimumTtl = null,
        TimeSpan? maximumTtl = null,
        TimeSpan? idempotencyHorizon = null,
        bool decimalStringFencing = true,
        bool holderIdentityMasking = true,
        bool authenticatedExactScope = true) => new(
            contractVersion, availability, fixture,
            operations ?? new HashSet<CoordinationOperation>(Enum.GetValues<CoordinationOperation>()),
            minimumTtl ?? TimeSpan.FromSeconds(1), maximumTtl ?? TimeSpan.FromMinutes(10),
            idempotencyHorizon ?? TimeSpan.FromHours(24), decimalStringFencing,
            holderIdentityMasking, authenticatedExactScope,
            ["test-only fixture; never production authority"]);

    internal static DispatchAuthority CopyAuthority(DispatchAuthority source) =>
        new(source.AdapterId, source.ClockDomainId, source.LifecycleId, source.Nonce);

    internal static DispatchAuthority ForgeAuthority(DispatchAuthority source, string? nonce = null) =>
        new(source.AdapterId, source.ClockDomainId, source.LifecycleId, nonce ?? source.Nonce);
}

public sealed record LeaseOwner
{
    public LeaseOwner(string principalId, string runId)
    {
        PrincipalId = RequireOpaque(principalId, nameof(principalId));
        RunId = RequireOpaque(runId, nameof(runId));
    }

    public string PrincipalId { get; }
    public string RunId { get; }

    private static string RequireOpaque(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Owner identifiers must be non-blank, bounded, and control-free.", parameter);
        return value;
    }
}

public sealed record CoordinationResource
{
    private static readonly Regex IssueKey = new(
        "^github:[a-z0-9](?:[a-z0-9-]{0,38})/[a-z0-9_.-]{1,100}:issue:[1-9][0-9]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ScopeKey = new(
        "^[a-z0-9](?:[a-z0-9._-]{0,127})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public CoordinationResource(string scope, string resourceKey)
    {
        if (!ScopeKey.IsMatch(scope))
            throw new ArgumentException("Scope must use the canonical bounded ASCII coordination-key grammar.", nameof(scope));
        Scope = scope;
        ResourceKey = ValidateResource(resourceKey);
    }

    public string Scope { get; }
    public string ResourceKey { get; }

    public static CoordinationResource GitHubIssue(string scope, string owner, string repository, long issueNumber)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repository);
        if (issueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(issueNumber));
        return new CoordinationResource(scope,
            $"github:{owner.ToLowerInvariant()}/{repository.ToLowerInvariant()}:issue:{issueNumber.ToString(CultureInfo.InvariantCulture)}");
    }

    public static CoordinationResource SchemaMigrations(string scope) => new(scope, "schema-migrations");

    private static string ValidateResource(string value)
    {
        var slash = value?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        var issueMarker = value?.IndexOf(":issue:", StringComparison.Ordinal) ?? -1;
        var prohibitedDotRepository = slash >= 0 && issueMarker > slash &&
            value![(slash + 1)..issueMarker] is "." or "..";
        if (string.IsNullOrEmpty(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > 512 ||
            (value != "schema-migrations" && (!IssueKey.IsMatch(value) || prohibitedDotRepository)))
            throw new ArgumentException("Resource key must be a canonical GitHub issue key or schema-migrations.", nameof(value));
        return value;
    }
}

public sealed class LeaseEvidence
{
    internal LeaseEvidence(
        CoordinationResource resource, LeaseOwner owner, string leaseId, string fencingToken,
        DateTimeOffset serverTime, DateTimeOffset expiresAt, TimeSpan safeDeadline,
        TimeSpan renewAt, string generation)
    {
        Resource = resource;
        Owner = owner;
        LeaseId = leaseId;
        FencingToken = fencingToken;
        ServerTime = serverTime;
        ExpiresAt = expiresAt;
        SafeDeadline = safeDeadline;
        RenewAt = renewAt;
        Generation = generation;
        Validate();
    }

    public CoordinationResource Resource { get; }
    public LeaseOwner Owner { get; }
    public string LeaseId { get; }
    public string FencingToken { get; }
    public DateTimeOffset ServerTime { get; }
    public DateTimeOffset ExpiresAt { get; }
    public TimeSpan SafeDeadline { get; }
    public TimeSpan RenewAt { get; }
    public string Generation { get; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(LeaseId) || LeaseId.Length > 512 || LeaseId.Any(char.IsControl))
            throw new CoordinationProtocolException("Lease ID is missing or malformed.");
        if (!long.TryParse(FencingToken, NumberStyles.None, CultureInfo.InvariantCulture, out var token) || token < 0 ||
            (FencingToken.Length > 1 && FencingToken[0] == '0'))
            throw new CoordinationProtocolException("Fencing token must be a canonical non-negative PostgreSQL BIGINT decimal string.");
        if (ExpiresAt <= ServerTime) throw new CoordinationProtocolException("Lease expiry must follow authoritative server time.");
        if (RenewAt >= SafeDeadline) throw new CoordinationProtocolException("Renewal target must precede the conservative safe deadline.");
        if (!Guid.TryParseExact(Generation, "D", out _)) throw new CoordinationProtocolException("Lease generation is malformed.");
    }
}

public sealed class LeaseSnapshot
{
    internal LeaseSnapshot(
        LeaseOwner owner,
        IEnumerable<LeaseEvidence> leases,
        LeaseOwnership ownership,
        CoordinationLifecyclePhase phase,
        string reason)
    {
        Owner = owner;
        Leases = new ReadOnlyCollection<LeaseEvidence>(leases.ToArray());
        Ownership = ownership;
        Phase = phase;
        Reason = reason;
    }

    public LeaseOwner Owner { get; }
    public IReadOnlyList<LeaseEvidence> Leases { get; }
    public LeaseOwnership Ownership { get; }
    public CoordinationLifecyclePhase Phase { get; }
    public string Reason { get; }
}

public sealed class DispatchAuthority
{
    internal DispatchAuthority(string adapterId, string clockDomainId, string lifecycleId, string nonce)
    {
        AdapterId = adapterId;
        ClockDomainId = clockDomainId;
        LifecycleId = lifecycleId;
        Nonce = nonce;
    }

    internal string AdapterId { get; }
    internal string ClockDomainId { get; }
    internal string LifecycleId { get; }
    internal string Nonce { get; }
}

public sealed class AcquireContinuation { internal AcquireContinuation() { } }
public sealed class ReleaseContinuation { internal ReleaseContinuation() { } }

public sealed record AcquireUncertainty(CoordinationResource Resource, string RequestId, LeaseOwner Owner, string Reason);
public sealed record LeaseReleaseProgress(
    CoordinationResource Resource, string LeaseId, string Generation, string RequestId, LeaseReleaseState State, string Reason);

public sealed record AcquisitionResult(
    LeaseSnapshot Snapshot,
    DispatchAuthority? Authority,
    AcquireContinuation? AcquireContinuation,
    ReleaseContinuation? ReleaseContinuation,
    AcquireUncertainty? Uncertainty,
    IReadOnlyList<LeaseReleaseProgress> ReleaseProgress);

public sealed record RenewalResult(LeaseSnapshot Snapshot, DispatchAuthority? Authority, ReleaseContinuation? ReleaseContinuation);

public sealed record ReleaseResult(
    LeaseSnapshot Snapshot,
    IReadOnlyList<LeaseReleaseProgress> Leases,
    ReleaseContinuation? Continuation,
    IReadOnlyList<QuiescenceEvidence> QuiescenceEvidence);

public sealed record CoordinationInspection(InspectionState State, LeaseEvidence? OwnedLease, string Reason);

public sealed record CheckpointLeaseIdentity(
    CoordinationResource Resource,
    LeaseOwner Owner,
    string LeaseId,
    string FencingToken,
    string Generation,
    DateTimeOffset ServerTime,
    DateTimeOffset ExpiresAt,
    TimeSpan SafeDeadline,
    TimeSpan RenewAt);

public sealed record PreparedCoordinationIntent(
    string LifecycleId,
    long OperationSequence,
    CoordinationIntentKind Kind,
    CoordinationResource Resource,
    LeaseOwner Owner,
    string RequestId,
    TimeSpan FirstSendMonotonic,
    string ClockDomainId,
    TimeSpan Ttl,
    CheckpointLeaseIdentity? PreviousLease,
    QuiescenceEvidence? Quiescence = null);

public sealed record CoordinationIntentProgress(
    string LifecycleId,
    long OperationSequence,
    string AdapterCode,
    CheckpointLeaseIdentity? Lease,
    long Revision = 1,
    QuiescenceEvidence? Quiescence = null);

public sealed record CoordinationCheckpointRecord(
    int SchemaVersion,
    long RecordSequence,
    long OperationSequence,
    CoordinationIntentKind Kind,
    string WriterEpoch,
    long ProgressRevision,
    PreparedCoordinationIntent? Prepared,
    CoordinationIntentProgress? Progress)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record CoordinationWriterClaim(
    string LifecycleId,
    string WriterEpoch,
    long NextRecordSequence);

public interface ICoordinationCheckpointStore
{
    Task<CoordinationWriterClaim> ClaimWriterAsync(
        string lifecycleId, string proposedWriterEpoch, string? trustedTakeoverProof,
        CancellationToken cancellationToken = default);
    Task AppendAsync(
        CoordinationWriterClaim claim, long expectedRecordSequence, CoordinationCheckpointRecord record,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadLifecycleAsync(
        string lifecycleId, CancellationToken cancellationToken = default);
}

public sealed record QuiescenceSourceBinding(
    string IssuerId,
    string SourceId,
    string ClockDomainId,
    TimeSpan MaximumObservationLatency,
    TimeSpan FreshnessLimit,
    bool BoundsKnown);

public sealed record QuiescenceEvidence(
    LeaseOwner Owner,
    string IssuerId,
    string SourceId,
    string ClockDomainId,
    TimeSpan ObservedAt,
    string EvidenceId,
    bool Quiescent);

public interface IOwnedRunQuiescenceSource
{
    QuiescenceSourceBinding Binding { get; }
    Task<QuiescenceEvidence> ObserveAsync(LeaseOwner owner, CancellationToken cancellationToken = default);
}

public class CoordinationException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class CoordinationUnavailableException(string message, Exception? inner = null) : CoordinationException(message, inner);
public sealed class CoordinationUncertainException(string message, Exception? inner = null) : CoordinationException(message, inner);
public sealed class CoordinationProtocolException(string message, Exception? inner = null) : CoordinationException(message, inner);
public sealed class CoordinationCheckpointNotWrittenException(string message) : CoordinationException(message);
