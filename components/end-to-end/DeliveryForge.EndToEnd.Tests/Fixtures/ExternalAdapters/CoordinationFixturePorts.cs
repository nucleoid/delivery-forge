using System.Diagnostics;
using System.Text.Json;
using DeliveryForge.Coordination;

namespace DeliveryForge.EndToEnd.Tests;

// Allowed external coordination fake. No network/service endpoint or production capability is supplied.
internal sealed class CountingCoordinationFixtureTransport(bool marked = true, bool progressingRenewal = false, bool malformedAcquire = false) : ICoordinationTransport
{
    private readonly List<string> _calls = [];
    private LeaseOwner? _owner;
    private string? _lease;
    public bool Fixture => true;
    public int MutationCalls => _calls.Count(call => call != "detect" && call != "inspect");
    public int ReleaseCalls => _calls.Count(call => call == "release");
    public IReadOnlyList<string> Calls => _calls.AsReadOnly();

    public Task<CoordinationCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        _calls.Add("detect");
        return Task.FromResult(new CoordinationCapabilities(CoordinationCapabilities.V1ContractVersion,
            CoordinationAvailability.Available, marked, new HashSet<CoordinationOperation>(Enum.GetValues<CoordinationOperation>()),
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(300), TimeSpan.FromHours(24), true, true, true,
            ["Owned coordination transport fixture only; no deployed production service."]));
    }

    public Task<AcquireTransportResult> AcquireAsync(AcquireRequest request, CancellationToken cancellationToken = default)
    {
        _calls.Add("acquire");
        if (_lease is not null) return Task.FromResult(AcquireTransportResult.Contended());
        _owner = request.Owner;
        _lease = "fixture-lease-" + Guid.NewGuid().ToString("N");
        var server = DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        if (malformedAcquire) return Task.FromResult(AcquireTransportResult.Acquired(_lease, "01", server, server + request.Ttl));
        return Task.FromResult(AcquireTransportResult.Acquired(_lease, "1", server, server + request.Ttl));
    }

    public Task<RenewTransportResult> RenewAsync(RenewRequest request, CancellationToken cancellationToken = default)
    {
        _calls.Add("renew");
        if (request.Lease.Owner != _owner || request.Lease.LeaseId != _lease)
            return Task.FromResult(RenewTransportResult.Uncertain("Fixture owner/lease mismatch."));
        var server = request.Lease.ServerTime + (progressingRenewal ? TimeSpan.FromSeconds(100) : TimeSpan.Zero);
        return Task.FromResult(RenewTransportResult.Renewed(server, server + TimeSpan.FromSeconds(300)));
    }

    public Task<InspectTransportResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default)
    {
        _calls.Add("inspect");
        return Task.FromResult(_lease is null ? InspectTransportResult.Available() : InspectTransportResult.HeldByOther());
    }

    public Task<ReleaseTransportResult> ReleaseAsync(ReleaseRequest request, CancellationToken cancellationToken = default)
    {
        _calls.Add("release");
        if (request.Owner != _owner || request.LeaseId != _lease || request.FencingToken != "1")
            return Task.FromResult(ReleaseTransportResult.LeaseLost());
        _lease = null;
        return Task.FromResult(ReleaseTransportResult.Released());
    }
}

internal sealed class FixtureMonotonicClock(string domain) : IMonotonicClock
{
    private readonly long _origin = Stopwatch.GetTimestamp();
    public string DomainId { get; } = domain;
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_origin);
}

// Virtual time is confined to coordination deadline logic. Native process observations remain real.
internal sealed class AdvancingFixtureClock(string domain) : IMonotonicClock
{
    private readonly long _origin = Stopwatch.GetTimestamp();
    private long _offsetTicks;
    public string DomainId { get; } = domain;
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_origin) + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
    public void Advance(TimeSpan amount)
    {
        if (amount <= TimeSpan.Zero || amount > TimeSpan.FromSeconds(301)) throw new ArgumentOutOfRangeException(nameof(amount));
        Interlocked.Add(ref _offsetTicks, amount.Ticks);
    }
}

// Quiescence is derived from the real public process controller, never from a caller success flag.
internal sealed class OwnedFixtureQuiescence(OwnedScenarioRun run, LeaseOwner receiver, IMonotonicClock clock, bool mismatchedClock = false) : IOwnedRunQuiescenceSource
{
    public QuiescenceSourceBinding Binding { get; } = new("fixture-owned-issuer", "fixture-owned-source:" + receiver.RunId,
        mismatchedClock ? "foreign-clock" : clock.DomainId, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), true);

    public Task<QuiescenceEvidence> ObserveAsync(LeaseOwner owner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (owner != receiver) throw new CoordinationProtocolException("Fixture quiescence receiver mismatch.");
        var quiescent = run.Processes.GetOwnedIdentities(receiver.RunId)
            .All(identity => run.Processes.Observe(identity).Outcome == DeliveryForge.Execution.ProcessControlOutcome.AlreadyExited);
        return Task.FromResult(new QuiescenceEvidence(owner, Binding.IssuerId, Binding.SourceId, Binding.ClockDomainId,
            clock.Elapsed, "fixture-observation:" + Guid.NewGuid().ToString("N"), quiescent));
    }
}

// External checkpoint port uses actual owned immutable files. Its writer claim is fixture state, not production authority.
internal sealed class OwnedFixtureCheckpointStore(string root) : ICoordinationCheckpointStore
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, (string Epoch, long Next)> _claims = new(StringComparer.Ordinal);

    public async Task<CoordinationWriterClaim> ClaimWriterAsync(string lifecycleId, string proposedWriterEpoch, string? trustedTakeoverProof, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(lifecycleId, "D", out _)) throw new CoordinationProtocolException("Fixture lifecycle path is not a UUID.");
        await _serial.WaitAsync(cancellationToken);
        try
        {
            if (_claims.ContainsKey(lifecycleId)) throw new CoordinationProtocolException("This initial fixture does not implement takeover.");
            Directory.CreateDirectory(Path.Combine(root, lifecycleId));
            _claims.Add(lifecycleId, (proposedWriterEpoch, 1));
            return new(lifecycleId, proposedWriterEpoch, 1);
        }
        finally { _serial.Release(); }
    }

    public async Task AppendAsync(CoordinationWriterClaim claim, long expectedRecordSequence, CoordinationCheckpointRecord record, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken);
        try
        {
            if (!_claims.TryGetValue(claim.LifecycleId, out var active) || active.Epoch != claim.WriterEpoch ||
                active.Next != expectedRecordSequence || record.RecordSequence != expectedRecordSequence || record.WriterEpoch != active.Epoch)
                throw new CoordinationCheckpointNotWrittenException("Fixture writer/sequence did not match; no write occurred.");
            var directory = Path.Combine(root, claim.LifecycleId);
            var target = Path.Combine(directory, $"{expectedRecordSequence:D12}-record.json");
            var temporary = Path.Combine(directory, "pending-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, record, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, target, overwrite: false);
            _claims[claim.LifecycleId] = (active.Epoch, expectedRecordSequence + 1);
        }
        finally { _serial.Release(); }
    }

    public async Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadLifecycleAsync(string lifecycleId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(lifecycleId, "D", out _)) throw new CoordinationProtocolException("Fixture lifecycle path is not a UUID.");
        await _serial.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(root, lifecycleId);
            if (!Directory.Exists(directory)) return [];
            var records = new List<CoordinationCheckpointRecord>();
            foreach (var path in Directory.EnumerateFiles(directory, "*-record.json").Order(StringComparer.Ordinal))
                records.Add(JsonSerializer.Deserialize<CoordinationCheckpointRecord>(await File.ReadAllBytesAsync(path, cancellationToken))
                    ?? throw new CoordinationProtocolException("Fixture record is malformed."));
            return records.AsReadOnly();
        }
        finally { _serial.Release(); }
    }
}
