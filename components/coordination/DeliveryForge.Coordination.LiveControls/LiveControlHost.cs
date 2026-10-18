using System.Diagnostics;
using System.Text.Json;
using DeliveryForge.Coordination;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DeliveryForge.Coordination.Tests")]

namespace DeliveryForge.Coordination.LiveControls;

// Root-owned, non-publishing live control host. It has no process/model/GitHub port.
// Its only dispatchable operation is a tracked, synchronous synthetic sink action.
internal sealed class LiveControlHost(LeaseOwner owner) : IOwnedRunQuiescenceSource, IMonotonicClock
{
    private readonly object _gate = new();
    private readonly long _original = Stopwatch.GetTimestamp();
    private readonly List<Task> _actualActions = [];
    private ContinuumCoordinationAdapter? _adapter;
    internal int Dispatches { get; private set; }
    internal int Publications { get; private set; }
    public string DomainId { get; } = Guid.NewGuid().ToString("D");
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_original);
    public QuiescenceSourceBinding Binding => new("parent-live-control-host", DomainId, DomainId,
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), true);

    internal void Bind(ContinuumCoordinationAdapter adapter)
    {
        lock (_gate)
        {
            if (_adapter is not null) throw new InvalidOperationException("Live control host binds once.");
            _adapter = adapter;
        }
    }

    internal bool TryDispatch(DispatchAuthority authority) => Act(authority, publication: false);
    internal bool TryPublish(DispatchAuthority authority) => Act(authority, publication: true);

    private bool Act(DispatchAuthority authority, bool publication)
    {
        lock (_gate)
        {
            if (_adapter is null || !_adapter.MayDispatch(authority)) return false;
            if (_actualActions.Count >= 16) throw new InvalidOperationException("Finite synthetic action bound.");
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _actualActions.Add(operation.Task);
            try
            {
                if (publication) Publications++; else Dispatches++;
                operation.SetResult();
                return true;
            }
            catch (Exception error) { operation.SetException(error); throw; }
        }
    }

    public Task<QuiescenceEvidence> ObserveAsync(LeaseOwner requested, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (requested != owner) throw new CoordinationProtocolException("Foreign live-control owner.");
            var quiet = _actualActions.All(task => task.IsCompletedSuccessfully);
            return Task.FromResult(new QuiescenceEvidence(owner, Binding.IssuerId, Binding.SourceId, DomainId,
                Elapsed, Guid.NewGuid().ToString("D"), quiet));
        }
    }
}

// One genuinely exclusive local writer lifetime, immutable flushed records and real readback.
// No restart takeover, distributed-writer certification or imported authority is implemented.
internal sealed class LiveCheckpointStore : ICoordinationCheckpointStore, IAsyncDisposable
{
    private readonly string _root;
    private readonly FileStream _exclusive;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, CoordinationWriterClaim> _claims = [];
    private readonly List<IoFrame> _io = [];
    private readonly Dictionary<string, (long Sequence, byte[] Bytes)> _pending = [];
    private readonly Action<string>? _beforeReadbackForContractTests;
    private readonly Func<FileStream, ValueTask>? _firstCloseForContractTests;
    private bool _disposed, _disposeEntered;
    private Exception? _disposeFailure;

    internal LiveCheckpointStore(string root, Action<string>? beforeReadbackForContractTests = null,
        Func<FileStream, ValueTask>? firstCloseForContractTests = null, Action<FileStream>? exclusiveCreatedForContractTests = null)
    {
        _beforeReadbackForContractTests = beforeReadbackForContractTests;
        _firstCloseForContractTests = firstCloseForContractTests;
        _root = Path.GetFullPath(root);
        Plain(_root);
        if (!Directory.Exists(_root) || Directory.EnumerateFileSystemEntries(_root).Any())
            throw new CoordinationProtocolException("Root must supply an existing empty fresh control directory.");
        _exclusive = new(Path.Combine(_root, "parent-writer.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        exclusiveCreatedForContractTests?.Invoke(_exclusive);
    }

    public async Task<CoordinationWriterClaim> ClaimWriterAsync(string lifecycleId, string proposedWriterEpoch,
        string? trustedTakeoverProof, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(lifecycleId, "D", out _) || !Guid.TryParseExact(proposedWriterEpoch, "D", out _) || trustedTakeoverProof is not null)
            throw new CoordinationProtocolException("Live control store permits only fresh UUID lifecycles, with no takeover.");
        if (!await _serial.WaitAsync(0, cancellationToken)) throw new CoordinationUncertainException("Actual local writer is busy.");
        try
        {
            Available();
            if (_claims.Count >= 16 || _claims.ContainsKey(lifecycleId)) throw new CoordinationProtocolException("Finite fresh live-control claim required.");
            var directory = Path.Combine(_root, lifecycleId);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new CoordinationProtocolException("Fresh lifecycle path required.");
            Directory.CreateDirectory(directory);
            var claim = new CoordinationWriterClaim(lifecycleId, proposedWriterEpoch, 1);
            await WriteNew(Path.Combine(directory, "writer-claim.json"), JsonSerializer.SerializeToUtf8Bytes(claim), cancellationToken);
            _claims.Add(lifecycleId, claim);
            return claim;
        }
        finally { _serial.Release(); }
    }

    public async Task AppendAsync(CoordinationWriterClaim claim, long expectedRecordSequence,
        CoordinationCheckpointRecord record, CancellationToken cancellationToken = default)
    {
        if (!await _serial.WaitAsync(0, cancellationToken)) throw new CoordinationCheckpointNotWrittenException("Local writer busy; no append entered.");
        try
        {
            Available();
            if (!_claims.TryGetValue(claim.LifecycleId, out var active) || active.WriterEpoch != claim.WriterEpoch ||
                active.NextRecordSequence != expectedRecordSequence || record.RecordSequence != expectedRecordSequence ||
                record.WriterEpoch != claim.WriterEpoch || expectedRecordSequence is < 1 or > 128)
                throw new CoordinationCheckpointNotWrittenException("Exact local epoch/sequence required before append.");
            if (_pending.ContainsKey(claim.LifecycleId))
                throw new CoordinationUncertainException("Original append must be reconciled by physical readback before another write.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            if (bytes.Length > 65536) throw new CoordinationCheckpointNotWrittenException("Finite live-control record bytes required.");
            var path = Path.Combine(_root, claim.LifecycleId, $"{expectedRecordSequence:D12}-record.json");
            _pending.Add(claim.LifecycleId, (expectedRecordSequence, bytes));
            await WriteNew(path, bytes, cancellationToken);
            _pending.Remove(claim.LifecycleId);
            _claims[claim.LifecycleId] = active with { NextRecordSequence = expectedRecordSequence + 1 };
        }
        finally { _serial.Release(); }
    }

    public async Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadLifecycleAsync(string lifecycleId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(lifecycleId, "D", out _)) throw new CoordinationProtocolException("Exact lifecycle UUID required.");
        if (!await _serial.WaitAsync(0, cancellationToken)) throw new CoordinationUncertainException("Actual local read is busy.");
        try
        {
            Available();
            var directory = Path.Combine(_root, lifecycleId);
            Plain(directory);
            var rows = new List<CoordinationCheckpointRecord>();
            if (!Directory.Exists(directory)) return rows;
            var names = Directory.EnumerateFiles(directory, "*-record.json").Take(129).Order(StringComparer.Ordinal).ToArray();
            if (names.Length > 128) throw new CoordinationProtocolException("Finite complete checkpoint record bound.");
            var physical = new List<byte[]>();
            foreach (var path in names)
            {
                var bytes = await Read(path, cancellationToken);
                physical.Add(bytes);
                rows.Add(JsonSerializer.Deserialize<CoordinationCheckpointRecord>(bytes,
                    new JsonSerializerOptions { MaxDepth = 16, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                    ?? throw new CoordinationProtocolException("Actual checkpoint record is malformed."));
            }
            // Reconcile only this lifetime's exact pending bytes, never a foreign/imported epoch.
            if (_claims.TryGetValue(lifecycleId, out var active))
            {
                for (var i = 0; i < rows.Count; i++)
                    if (rows[i].RecordSequence != i + 1 || rows[i].WriterEpoch != active.WriterEpoch ||
                        Path.GetFileName(names[i]) != $"{i + 1:D12}-record.json")
                        throw new CoordinationProtocolException("Actual checkpoint prefix has a foreign epoch, sequence or filename.");
                if (_pending.TryGetValue(lifecycleId, out var pending))
                {
                    if (rows.Count != pending.Sequence || active.NextRecordSequence != pending.Sequence ||
                        !physical[^1].AsSpan().SequenceEqual(pending.Bytes))
                        throw new CoordinationUncertainException("Exact pending append is absent, incomplete or differs; no sequence adoption.");
                    _claims[lifecycleId] = active with { NextRecordSequence = pending.Sequence + 1 };
                    _pending.Remove(lifecycleId);
                }
                else if (rows.Count != active.NextRecordSequence - 1)
                    throw new CoordinationProtocolException("Actual checkpoint count differs from acknowledged local writes.");
            }
            return rows.AsReadOnly();
        }
        finally { _serial.Release(); }
    }

    private async Task WriteNew(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Plain(path);
        var frame = Frame();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            frame.Entered = true;
            frame.Stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            frame.Returned = true;
            await frame.Stream.WriteAsync(bytes, cancellationToken);
            await frame.Stream.FlushAsync(cancellationToken);
            frame.Stream.Flush(true);
        }
        catch (Exception error) { frame.Primary = error; }
        await Close(frame, cancellationToken);
        _beforeReadbackForContractTests?.Invoke(path);
        // Independent real complete-byte readback, not an in-memory acknowledgement.
        var actual = await Read(path, cancellationToken);
        if (!actual.AsSpan().SequenceEqual(bytes)) throw new CoordinationProtocolException("Complete flushed checkpoint readback differs.");
    }

    private async Task<byte[]> Read(string path, CancellationToken cancellationToken)
    {
        Plain(path);
        var frame = Frame();
        var bytes = Array.Empty<byte>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            frame.Entered = true;
            frame.Stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            frame.Returned = true;
            var length = frame.Stream.Length;
            if (length is < 1 or > 65536) throw new CoordinationProtocolException("Finite full checkpoint read required.");
            bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = await frame.Stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
                if (count <= 0) throw new CoordinationProtocolException("Actual checkpoint is truncated.");
                offset += count;
            }
            if (frame.Stream.ReadByte() != -1 || frame.Stream.Length != length) throw new CoordinationProtocolException("Actual checkpoint EOF/length changed.");
        }
        catch (Exception error) { frame.Primary = error; }
        await Close(frame, cancellationToken);
        return bytes;
    }

    private IoFrame Frame()
    {
        if (_io.Count >= 8192) throw new CoordinationUncertainException("Finite retained local IO ledger exhausted.");
        var frame = new IoFrame(); _io.Add(frame); return frame;
    }

    private async Task Close(IoFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Returned)
        {
            frame.CloseEntered = true;
            try
            {
                if (_firstCloseForContractTests is null) await frame.Stream!.DisposeAsync();
                else await _firstCloseForContractTests(frame.Stream!);
                frame.Closed = true;
            }
            catch (Exception error) { frame.CloseFailure = error; }
        }
        try { cancellationToken.ThrowIfCancellationRequested(); }
        catch (Exception error) { frame.Post = error; }
        var errors = new[] { frame.Primary, frame.CloseFailure, frame.Post }.OfType<Exception>().ToArray();
        if (errors.Length != 0) throw new AggregateException("Independent live checkpoint IO and first-close outcomes.", errors);
    }

    private static void Plain(string path)
    {
        var cursor = Path.GetFullPath(path);
        for (var depth = 0; !string.IsNullOrEmpty(cursor); depth++)
        {
            if (depth >= 64 || ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0))
                throw new CoordinationProtocolException("Live-control checkpoint path is redirected.");
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    private void Available()
    {
        if (_disposed || _disposeEntered || _io.Any(row => row.Entered && !row.Returned || row.Returned && !row.Closed))
            throw new CoordinationUncertainException("Live checkpoint writer is closed or has unsettled actual IO.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!await _serial.WaitAsync(0)) throw new CoordinationUncertainException("Actual live checkpoint IO has not returned.");
        try
        {
            if (_disposeEntered)
            {
                if (_disposeFailure is not null || !_disposed) throw new CoordinationUncertainException("First checkpoint disposition remains failed.");
                return;
            }
            _disposeEntered = true;
            if (_io.Any(row => row.Entered && !row.Returned || row.Returned && !row.Closed))
            {
                _disposeFailure = new CoordinationUncertainException("Checkpoint lifetime has unsettled actual IO; exclusive writer retained.");
                throw _disposeFailure;
            }
            try { await _exclusive.DisposeAsync(); _disposed = true; }
            catch (Exception error) { _disposeFailure = error; }
            if (_disposeFailure is not null)
                throw new CoordinationUncertainException("Checkpoint lifetime has failed or unsettled actual disposition.");
        }
        finally { _serial.Release(); }
    }

    private sealed class IoFrame
    {
        internal bool Entered, Returned, CloseEntered, Closed;
        internal FileStream? Stream;
        internal Exception? Primary, CloseFailure, Post;
    }
}
