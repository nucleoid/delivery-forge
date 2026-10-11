namespace DeliveryForge.Coordination;

public sealed record AcquireRequest(CoordinationResource Resource, LeaseOwner Owner, TimeSpan Ttl, string RequestId);
public sealed record RenewRequest(LeaseEvidence Lease, string RequestId);
public sealed record InspectRequest(CoordinationResource Resource, LeaseOwner Owner, string RequestId);
public sealed record ReleaseRequest(string LeaseId, LeaseOwner Owner, string FencingToken, string RequestId);

public sealed record AcquireTransportResult(
    TransportOutcome Outcome, string? LeaseId, string? FencingToken,
    DateTimeOffset? ServerTime, DateTimeOffset? ExpiresAt, string Reason)
{
    public static AcquireTransportResult Acquired(string leaseId, string fencingToken, DateTimeOffset serverTime, DateTimeOffset expiresAt) =>
        new(TransportOutcome.Acquired, leaseId, fencingToken, serverTime, expiresAt, "acquired");
    public static AcquireTransportResult Contended(string reason = "resource is held by another owner") =>
        new(TransportOutcome.Contended, null, null, null, null, reason);
    public static AcquireTransportResult Uncertain(string reason) => new(TransportOutcome.Uncertain, null, null, null, null, reason);
}

public sealed record RenewTransportResult(TransportOutcome Outcome, DateTimeOffset? ServerTime, DateTimeOffset? ExpiresAt, string Reason)
{
    public static RenewTransportResult Renewed(DateTimeOffset serverTime, DateTimeOffset expiresAt) =>
        new(TransportOutcome.Renewed, serverTime, expiresAt, "renewed");
    public static RenewTransportResult Uncertain(string reason) => new(TransportOutcome.Uncertain, null, null, reason);
}

public sealed record InspectTransportResult(
    TransportOutcome Outcome, bool Held, bool CallerOwnsLease, string? LeaseId, string? FencingToken,
    DateTimeOffset? ServerTime, DateTimeOffset? ExpiresAt, string Reason)
{
    public static InspectTransportResult Available() => new(TransportOutcome.Inspected, false, false, null, null, null, null, "available");
    public static InspectTransportResult HeldByOther(string? prohibitedLeaseId = null, string? prohibitedFencingToken = null) =>
        new(TransportOutcome.Inspected, true, false, prohibitedLeaseId, prohibitedFencingToken, null, null, "held by another owner");
    public static InspectTransportResult HeldByCaller(string leaseId, string fencingToken, DateTimeOffset serverTime, DateTimeOffset expiresAt) =>
        new(TransportOutcome.Inspected, true, true, leaseId, fencingToken, serverTime, expiresAt, "held by caller");
}

public sealed record ReleaseTransportResult(TransportOutcome Outcome, string Reason)
{
    public static ReleaseTransportResult Released() => new(TransportOutcome.Released, "released");
    public static ReleaseTransportResult LeaseLost(string reason = "lease lost") => new(TransportOutcome.LeaseLost, reason);
    public static ReleaseTransportResult Uncertain(string reason) => new(TransportOutcome.Uncertain, reason);
}

public interface ICoordinationTransport
{
    Task<CoordinationCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<AcquireTransportResult> AcquireAsync(AcquireRequest request, CancellationToken cancellationToken = default);
    Task<RenewTransportResult> RenewAsync(RenewRequest request, CancellationToken cancellationToken = default);
    Task<InspectTransportResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default);
    Task<ReleaseTransportResult> ReleaseAsync(ReleaseRequest request, CancellationToken cancellationToken = default);
}
