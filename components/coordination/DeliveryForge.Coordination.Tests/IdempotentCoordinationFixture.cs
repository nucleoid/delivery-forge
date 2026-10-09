using System.Text.Json;

namespace DeliveryForge.Coordination.Tests;

internal sealed class IdempotentCoordinationFixture(Func<DateTimeOffset> serverNow) : ICoordinationTransport
{
    private readonly object _gate = new();
    private readonly Dictionary<(CoordinationIntentKind Kind, string Id), (object Request, object Response)> _cache = [];
    private readonly Dictionary<CoordinationResource, ServerLease> _leases = [];
    private long _nextFence;

    public List<AcquireRequest> AcquireRequests { get; } = [];
    public List<RenewRequest> RenewRequests { get; } = [];
    public List<InspectRequest> InspectRequests { get; } = [];
    public List<ReleaseRequest> ReleaseRequests { get; } = [];
    public CoordinationIntentKind? LoseNextReplyAfterCommit { get; set; }

    public Task<CoordinationCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CoordinationContractFixture.Capabilities());

    public Task<AcquireTransportResult> AcquireAsync(AcquireRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            AcquireRequests.Add(request);
            var response = ReplayOrCommit(CoordinationIntentKind.Acquire, request.RequestId, request, () => Acquire(request));
            return ReplyOrLose(CoordinationIntentKind.Acquire, response);
        }
    }

    public Task<RenewTransportResult> RenewAsync(RenewRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RenewRequests.Add(request);
            var response = ReplayOrCommit(CoordinationIntentKind.Renew, request.RequestId, request, () => Renew(request));
            return ReplyOrLose(CoordinationIntentKind.Renew, response);
        }
    }

    public Task<InspectTransportResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            InspectRequests.Add(request);
            var response = ReplayOrCommit(CoordinationIntentKind.Inspect, request.RequestId, request, () => Inspect(request));
            return ReplyOrLose(CoordinationIntentKind.Inspect, response);
        }
    }

    public Task<ReleaseTransportResult> ReleaseAsync(ReleaseRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ReleaseRequests.Add(request);
            var response = ReplayOrCommit(CoordinationIntentKind.Release, request.RequestId, request, () => Release(request));
            return ReplyOrLose(CoordinationIntentKind.Release, response);
        }
    }

    public void Replace(CoordinationResource resource, LeaseOwner owner)
    {
        lock (_gate)
        {
            var now = serverNow();
            _leases[resource] = new ServerLease(owner, $"lease-{Guid.NewGuid():D}", (++_nextFence).ToString(), now, now.AddMinutes(5));
        }
    }

    private AcquireTransportResult Acquire(AcquireRequest request)
    {
        var now = serverNow();
        if (_leases.TryGetValue(request.Resource, out var current) && current.ExpiresAt > now)
            return AcquireTransportResult.Contended();
        var lease = new ServerLease(request.Owner, $"lease-{Guid.NewGuid():D}", (++_nextFence).ToString(), now, now + request.Ttl);
        _leases[request.Resource] = lease;
        return AcquireTransportResult.Acquired(lease.LeaseId, lease.Fence, lease.ServerTime, lease.ExpiresAt);
    }

    private RenewTransportResult Renew(RenewRequest request)
    {
        var now = serverNow();
        if (!_leases.TryGetValue(request.Lease.Resource, out var current) ||
            current.Owner != request.Lease.Owner || current.LeaseId != request.Lease.LeaseId || current.Fence != request.Lease.FencingToken)
            return RenewTransportResult.Uncertain("lease-lost");
        var renewed = current with { ServerTime = now, ExpiresAt = now.AddMinutes(5) };
        _leases[request.Lease.Resource] = renewed;
        return RenewTransportResult.Renewed(renewed.ServerTime, renewed.ExpiresAt);
    }

    private InspectTransportResult Inspect(InspectRequest request)
    {
        var now = serverNow();
        if (!_leases.TryGetValue(request.Resource, out var current) || current.ExpiresAt <= now)
            return InspectTransportResult.Available();
        return current.Owner == request.Owner
            ? InspectTransportResult.HeldByCaller(current.LeaseId, current.Fence, now, current.ExpiresAt)
            : InspectTransportResult.HeldByOther("masked-by-adapter", "masked-by-adapter");
    }

    private ReleaseTransportResult Release(ReleaseRequest request)
    {
        var pair = _leases.SingleOrDefault(item => item.Value.LeaseId == request.LeaseId);
        if (pair.Value is null || pair.Value.Owner != request.Owner || pair.Value.Fence != request.FencingToken)
            return ReleaseTransportResult.LeaseLost();
        _leases.Remove(pair.Key);
        return ReleaseTransportResult.Released();
    }

    private T ReplayOrCommit<T>(CoordinationIntentKind kind, string id, object request, Func<T> commit)
    {
        if (_cache.TryGetValue((kind, id), out var cached))
        {
            if (!PayloadEquals(cached.Request, request)) throw new InvalidOperationException("idempotency-key-payload-conflict");
            return (T)cached.Response;
        }
        var response = commit()!;
        _cache.Add((kind, id), (request, response));
        return response;
    }

    private static bool PayloadEquals(object left, object right) => (left, right) switch
    {
        (AcquireRequest a, AcquireRequest b) => a == b,
        (InspectRequest a, InspectRequest b) => a == b,
        (ReleaseRequest a, ReleaseRequest b) => a == b,
        (RenewRequest a, RenewRequest b) => a.RequestId == b.RequestId &&
            a.Lease.Resource == b.Lease.Resource && a.Lease.Owner == b.Lease.Owner &&
            a.Lease.LeaseId == b.Lease.LeaseId && a.Lease.FencingToken == b.Lease.FencingToken &&
            a.Lease.ServerTime == b.Lease.ServerTime && a.Lease.ExpiresAt == b.Lease.ExpiresAt &&
            a.Lease.SafeDeadline == b.Lease.SafeDeadline && a.Lease.RenewAt == b.Lease.RenewAt &&
            a.Lease.Generation == b.Lease.Generation,
        _ => false,
    };

    private Task<T> ReplyOrLose<T>(CoordinationIntentKind kind, T response)
    {
        if (LoseNextReplyAfterCommit == kind)
        {
            LoseNextReplyAfterCommit = null;
            return Task.FromException<T>(new TimeoutException("committed-response-lost"));
        }
        return Task.FromResult(response);
    }

    private sealed record ServerLease(
        LeaseOwner Owner, string LeaseId, string Fence, DateTimeOffset ServerTime, DateTimeOffset ExpiresAt);
}

internal sealed class FixtureCheckpointStore : ICoordinationCheckpointStore
{
    private readonly List<byte[]> _records = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool FailNextPrepare { get; set; }
    public bool FailNextProgress { get; set; }
    public IReadOnlyList<CoordinationCheckpointRecord> Records => _records.Select(RoundTrip).ToArray();

    public Task AppendPreparedAsync(PreparedCoordinationIntent intent, CancellationToken cancellationToken = default)
    {
        if (FailNextPrepare) { FailNextPrepare = false; throw new IOException("fixture-prepare-failure"); }
        _records.Add(JsonSerializer.SerializeToUtf8Bytes(new CoordinationCheckpointRecord(intent, null), Json));
        return Task.CompletedTask;
    }

    public Task AppendProgressAsync(CoordinationIntentProgress progress, CancellationToken cancellationToken = default)
    {
        if (FailNextProgress) { FailNextProgress = false; throw new IOException("fixture-progress-failure"); }
        _records.Add(JsonSerializer.SerializeToUtf8Bytes(new CoordinationCheckpointRecord(null, progress), Json));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadLifecycleAsync(
        string lifecycleId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CoordinationCheckpointRecord>>(Records.Where(record =>
            record.Prepared?.LifecycleId == lifecycleId || record.Progress?.LifecycleId == lifecycleId).ToArray());

    private static CoordinationCheckpointRecord RoundTrip(byte[] bytes) =>
        JsonSerializer.Deserialize<CoordinationCheckpointRecord>(bytes, Json) ??
        throw new InvalidOperationException("fixture-checkpoint-roundtrip-failed");
}
