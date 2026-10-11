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
        if (!_leases.TryGetValue(request.Lease.Resource, out var current) || current.ExpiresAt <= now ||
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
            : InspectTransportResult.HeldByOther();
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
    private readonly Dictionary<string, string> _epochs = new(StringComparer.Ordinal);
    private readonly HashSet<(string LifecycleId, string Proof)> _usedTakeovers = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool FailNextPrepare { get; set; }
    public bool FailNextProgress { get; set; }
    public bool FailAfterNextAppend { get; set; }
    public bool FailIndeterminateBeforeNextAppend { get; set; }
    public Func<CoordinationCheckpointRecord, bool>? FailAfterAppendWhen { get; set; }
    public bool ReturnNullNextClaim { get; set; }
    public FixtureReadBehavior NextReadBehavior { get; set; }
    public bool ReverseReads { get; set; }
    public Action<CoordinationCheckpointRecord>? AfterAppend { get; set; }
    public TaskCompletionSource? DelayNextAppend { get; set; }
    public TaskCompletionSource DelayedAppendCompleted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<CoordinationCheckpointRecord> Records => _records.Select(RoundTrip).ToArray();

    public Task<CoordinationWriterClaim> ClaimWriterAsync(
        string lifecycleId, string proposedWriterEpoch, string? trustedTakeoverProof,
        CancellationToken cancellationToken = default)
    {
        if (ReturnNullNextClaim)
        {
            ReturnNullNextClaim = false;
            return Task.FromResult<CoordinationWriterClaim>(null!);
        }
        lock (_records)
        {
            if (_epochs.ContainsKey(lifecycleId))
            {
                if (string.IsNullOrWhiteSpace(trustedTakeoverProof) ||
                    !_usedTakeovers.Add((lifecycleId, trustedTakeoverProof)))
                    throw new CoordinationUncertainException("fixture-writer-owned");
            }
            _epochs[lifecycleId] = proposedWriterEpoch;
            var next = Records.Where(record => LifecycleId(record) == lifecycleId)
                .Select(record => record.RecordSequence).DefaultIfEmpty().Max() + 1;
            return Task.FromResult(new CoordinationWriterClaim(lifecycleId, proposedWriterEpoch, next));
        }
    }

    public Task AppendAsync(
        CoordinationWriterClaim claim, long expectedRecordSequence, CoordinationCheckpointRecord record,
        CancellationToken cancellationToken = default)
    {
        if (DelayNextAppend is { } barrier)
        {
            DelayNextAppend = null;
            return AppendAfterBarrierAsync(barrier.Task, claim, expectedRecordSequence, record);
        }
        return AppendCore(claim, expectedRecordSequence, record);
    }

    private async Task AppendAfterBarrierAsync(
        Task barrier, CoordinationWriterClaim claim, long expectedRecordSequence, CoordinationCheckpointRecord record)
    {
        await barrier.ConfigureAwait(false);
        await AppendCore(claim, expectedRecordSequence, record).ConfigureAwait(false);
        DelayedAppendCompleted.TrySetResult();
    }

    private Task AppendCore(
        CoordinationWriterClaim claim, long expectedRecordSequence, CoordinationCheckpointRecord record)
    {
        lock (_records)
        {
            if (FailIndeterminateBeforeNextAppend)
            {
                FailIndeterminateBeforeNextAppend = false;
                throw new IOException("fixture-ambiguous-before-append");
            }
            if (record.Prepared is not null && FailNextPrepare)
            {
                FailNextPrepare = false;
                throw new CoordinationCheckpointNotWrittenException("fixture-prepare-failure");
            }
            if (record.Progress is not null && FailNextProgress)
            {
                FailNextProgress = false;
                throw new CoordinationCheckpointNotWrittenException("fixture-progress-failure");
            }
            if (!_epochs.TryGetValue(claim.LifecycleId, out var epoch) || epoch != claim.WriterEpoch ||
                record.WriterEpoch != epoch)
                throw new CoordinationUncertainException("fixture-writer-stale");
            var existing = Records.Where(item => LifecycleId(item) == claim.LifecycleId).ToArray();
            var duplicate = existing.SingleOrDefault(item => item.RecordSequence == expectedRecordSequence);
            if (duplicate is not null)
            {
                if (duplicate != record) throw new CoordinationProtocolException("fixture-conflicting-duplicate");
                return Task.CompletedTask;
            }
            var next = existing.Select(item => item.RecordSequence).DefaultIfEmpty().Max() + 1;
            if (expectedRecordSequence != next || record.RecordSequence != expectedRecordSequence)
                throw new CoordinationUncertainException("fixture-cas-conflict");
            _records.Add(JsonSerializer.SerializeToUtf8Bytes(record, Json));
            AfterAppend?.Invoke(record);
            if (FailAfterNextAppend || FailAfterAppendWhen?.Invoke(record) == true)
            {
                FailAfterNextAppend = false;
                throw new IOException("fixture-ambiguous-append");
            }
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadLifecycleAsync(
        string lifecycleId, CancellationToken cancellationToken = default)
    {
        var behavior = NextReadBehavior;
        NextReadBehavior = FixtureReadBehavior.Current;
        var records = (ReverseReads
            ? Records.Where(record => LifecycleId(record) == lifecycleId).Reverse()
            : Records.Where(record => LifecycleId(record) == lifecycleId)).ToArray();
        return behavior switch
        {
            FixtureReadBehavior.Null => Task.FromResult<IReadOnlyList<CoordinationCheckpointRecord>>(null!),
            FixtureReadBehavior.Throw => Task.FromException<IReadOnlyList<CoordinationCheckpointRecord>>(
                new IOException("fixture-read-failed")),
            FixtureReadBehavior.Lag => Task.FromResult<IReadOnlyList<CoordinationCheckpointRecord>>(records.SkipLast(1).ToArray()),
            FixtureReadBehavior.Conflict => Task.FromResult<IReadOnlyList<CoordinationCheckpointRecord>>(
                records.Length == 0 ? records : [.. records.SkipLast(1), Conflicting(records[^1])]),
            _ => Task.FromResult<IReadOnlyList<CoordinationCheckpointRecord>>(records),
        };
    }

    public void Inject(CoordinationCheckpointRecord record) =>
        _records.Add(JsonSerializer.SerializeToUtf8Bytes(record, Json));

    private static string? LifecycleId(CoordinationCheckpointRecord record) =>
        record.Prepared?.LifecycleId ?? record.Progress?.LifecycleId;

    private static CoordinationCheckpointRecord RoundTrip(byte[] bytes) =>
        JsonSerializer.Deserialize<CoordinationCheckpointRecord>(bytes, Json) ??
        throw new InvalidOperationException("fixture-checkpoint-roundtrip-failed");

    private static CoordinationCheckpointRecord Conflicting(CoordinationCheckpointRecord record) =>
        record with { WriterEpoch = Guid.NewGuid().ToString("D") };
}

public enum FixtureReadBehavior { Current, Null, Throw, Lag, Conflict }
