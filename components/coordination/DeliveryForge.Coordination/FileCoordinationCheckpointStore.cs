using System.Text.Json;
using System.Security.Cryptography;

namespace DeliveryForge.Coordination;

public sealed record ParentCheckpointBinding(int SchemaVersion, string ParentRunId, string LeaseRunId,
    string PlanIdentity, string ContractIdentity, string Repository, string WorkItem, CoordinationResource Resource,
    IReadOnlyList<CoordinationResource>? AdditionalResources = null, string? Branch = null, string? WorkspaceIdentity = null, string? FootprintSha256 = null);

// One genuinely exclusive local writer lifetime, immutable flushed records and real readback.
// No restart takeover, distributed-writer certification or imported authority is implemented.
public sealed class FileCoordinationCheckpointStore : ICoordinationCheckpointStore, IAsyncDisposable
{
    private readonly string _root;
    private readonly FileStream _exclusive;
    private readonly WriterAcquisition _originalWriter = new();
    private static readonly List<WriterAcquisition> OriginalWriters = [];
    private sealed class WriterAcquisition { internal bool Entered, Returned, CloseEntered, Closed; internal FileStream? Resource; internal Task? CloseTask; internal Exception? Failure; }
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, CoordinationWriterClaim> _claims = [];
    private readonly List<IoFrame> _io = [];
    private readonly Dictionary<string, (long Sequence, byte[] Bytes, IoFrame? Write)> _pending = [];
    private readonly Dictionary<string, List<byte[]>> _acknowledged = [];
    private bool _parentBindingEntered;
    private bool _manifestAcknowledged;
    private byte[]? _manifestBytes;
    private readonly Action<string>? _beforeReadbackForContractTests;
    private readonly Action<string>? _beforeDurableFlushForContractTests;
    private readonly Func<FileStream, ValueTask>? _firstCloseForContractTests;
    private Exception? _manifestFailure;
    private readonly string _instance = Guid.NewGuid().ToString("D");
    private bool _disposed, _disposeEntered;
    private Exception? _disposeFailure;

    public FileCoordinationCheckpointStore(string root) : this(root, null, null, null) { }
    internal FileCoordinationCheckpointStore(string root, Action<string>? beforeReadbackForContractTests,
        Func<FileStream, ValueTask>? firstCloseForContractTests, Action<FileStream>? exclusiveCreatedForContractTests, Action<string>? beforeDurableFlushForContractTests = null)
    {
        _beforeDurableFlushForContractTests = beforeDurableFlushForContractTests;
        _beforeReadbackForContractTests = beforeReadbackForContractTests;
        _firstCloseForContractTests = firstCloseForContractTests;
        _root = Path.GetFullPath(root);
        Plain(_root);
        if (!Directory.Exists(_root) || Directory.EnumerateFileSystemEntries(_root).Any())
            throw new CoordinationProtocolException("Root must supply an existing empty fresh control directory.");
        lock (OriginalWriters) { if (OriginalWriters.Count >= 64) throw new CoordinationUncertainException("Finite retained writer acquisition bound."); OriginalWriters.Add(_originalWriter); }
        try
        {
            _originalWriter.Entered = true;
            _exclusive = new(Path.Combine(_root, "parent-writer.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _originalWriter.Resource = _exclusive; _originalWriter.Returned = true;
            exclusiveCreatedForContractTests?.Invoke(_exclusive);
        }
        catch (Exception error) { _originalWriter.Failure = error; throw; }
    }

    internal static bool RetainsOriginalWriterForContractTests(FileStream original) { lock (OriginalWriters) return OriginalWriters.Any(row => ReferenceEquals(row.Resource, original) && row.Returned && !row.Closed); }

    private async Task EnsureManifest(CancellationToken cancellationToken)
    {
        if (!_manifestAcknowledged)
        {
            if (_manifestFailure is not null) throw new CoordinationUncertainException("Original versioned manifest write remains indeterminate; no replay.");
            try
            {
                _manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new StoreManifest(1, _instance, "single-original-local-writer-no-takeover"));
                await WriteNew(Path.Combine(_root, "coordination-store-v1.json"), _manifestBytes, cancellationToken);
                _manifestAcknowledged = true;
            }
            catch (Exception error) { _manifestFailure = error; throw; }
        }
        else
        {
            var actual = await Read(Path.Combine(_root, "coordination-store-v1.json"), cancellationToken);
            if (!actual.AsSpan().SequenceEqual(_manifestBytes)) throw new CoordinationProtocolException("Original versioned manifest bytes changed.");
        }
    }

    public async Task BindParentAsync(ParentCheckpointBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.SchemaVersion != 1 || !Guid.TryParseExact(binding.LeaseRunId, "D", out _) ||
            new[] { binding.ParentRunId, binding.PlanIdentity, binding.ContractIdentity, binding.Repository, binding.WorkItem }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)))
            throw new CoordinationProtocolException("Exact bounded parent-to-lease run provenance required.");
        if (!await _serial.WaitAsync(0, cancellationToken)) throw new CoordinationUncertainException("Original parent provenance writer busy.");
        try
        {
            Available(); if (_parentBindingEntered) throw new CoordinationUncertainException("Original parent provenance write already entered; no replay.");
            _parentBindingEntered = true;
            await EnsureManifest(cancellationToken);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(binding);
            if (bytes.Length > 65536) throw new CoordinationProtocolException("Finite parent binding required.");
            await WriteNew(Path.Combine(_root, "parent-run-binding-v1.json"), bytes, cancellationToken);
        }
        finally { _serial.Release(); }
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
            await EnsureManifest(cancellationToken);
            if (_claims.Count >= 16 || _claims.ContainsKey(lifecycleId)) throw new CoordinationProtocolException("Finite fresh parent coordination claim required.");
            var directory = Path.Combine(_root, lifecycleId);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new CoordinationProtocolException("Fresh lifecycle path required.");
            Directory.CreateDirectory(directory);
            var claim = new CoordinationWriterClaim(lifecycleId, proposedWriterEpoch, 1);
            await WriteNew(Path.Combine(directory, "writer-claim.json"), JsonSerializer.SerializeToUtf8Bytes(claim), cancellationToken);
            _claims.Add(lifecycleId, claim);
            _acknowledged.Add(lifecycleId, []);
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
            await EnsureManifest(cancellationToken);
            if (!_claims.TryGetValue(claim.LifecycleId, out var active) || active.WriterEpoch != claim.WriterEpoch ||
                active.NextRecordSequence != expectedRecordSequence || record.RecordSequence != expectedRecordSequence ||
                record.WriterEpoch != claim.WriterEpoch || expectedRecordSequence is < 1 or > 512)
                throw new CoordinationCheckpointNotWrittenException("Exact local epoch/sequence required before append.");
            if (_pending.ContainsKey(claim.LifecycleId))
                throw new CoordinationUncertainException("Original append must be reconciled by physical readback before another write.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            if (bytes.Length > 65536) throw new CoordinationCheckpointNotWrittenException("Finite parent coordination record bytes required.");
            var path = Path.Combine(_root, claim.LifecycleId, $"{expectedRecordSequence:D12}-sha256-{Convert.ToHexStringLower(SHA256.HashData(bytes))}-record.json");
            _pending.Add(claim.LifecycleId, (expectedRecordSequence, bytes, null));
            await WriteNew(path, bytes, cancellationToken, frame => _pending[claim.LifecycleId] = (expectedRecordSequence, bytes, frame));
            _pending.Remove(claim.LifecycleId);
            _acknowledged[claim.LifecycleId].Add(bytes);
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
            await EnsureManifest(cancellationToken);
            var directory = Path.Combine(_root, lifecycleId);
            Plain(directory);
            var rows = new List<CoordinationCheckpointRecord>();
            if (!Directory.Exists(directory)) return rows;
            var names = Directory.EnumerateFiles(directory, "*-record.json").Take(513).Order(StringComparer.Ordinal).ToArray();
            if (names.Length > 512) throw new CoordinationProtocolException("Finite complete checkpoint record bound.");
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
                        Path.GetFileName(names[i]) != $"{i + 1:D12}-sha256-{Convert.ToHexStringLower(SHA256.HashData(physical[i]))}-record.json")
                        throw new CoordinationProtocolException("Actual checkpoint prefix has a foreign epoch, sequence or filename.");
                var known = _acknowledged[lifecycleId];
                if (rows.Count < known.Count) throw new CoordinationProtocolException("Previously acknowledged checkpoint disappeared.");
                for (var i = 0; i < known.Count; i++)
                    if (!physical[i].AsSpan().SequenceEqual(known[i])) throw new CoordinationProtocolException("Whole previously acknowledged checkpoint bytes changed.");
                if (_pending.TryGetValue(lifecycleId, out var pending))
                {
                    if (pending.Write is not { DurableFlushReturned: true, Closed: true })
                        throw new CoordinationUncertainException("Original durable flush/first close did not return; visible bytes cannot certify durability.");
                    if (rows.Count != pending.Sequence || active.NextRecordSequence != pending.Sequence ||
                        !physical[^1].AsSpan().SequenceEqual(pending.Bytes))
                        throw new CoordinationUncertainException("Exact pending append is absent, incomplete or differs; no sequence adoption.");
                    _claims[lifecycleId] = active with { NextRecordSequence = pending.Sequence + 1 };
                    _pending.Remove(lifecycleId);
                    _acknowledged[lifecycleId].Add(pending.Bytes);
                }
                else if (rows.Count != active.NextRecordSequence - 1)
                    throw new CoordinationProtocolException("Actual checkpoint count differs from acknowledged local writes.");
            }
            else if (rows.Count != 0) throw new CoordinationProtocolException("Foreign lifecycle records are not authority in this original writer.");
            return rows.AsReadOnly();
        }
        finally { _serial.Release(); }
    }

    private async Task WriteNew(string path, byte[] bytes, CancellationToken cancellationToken, Action<IoFrame>? enrolled = null)
    {
        Plain(path);
        var frame = Frame(); frame.IsWrite = true; enrolled?.Invoke(frame);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            frame.Entered = true;
            frame.Stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            frame.Returned = true;
            frame.WriteTask = frame.Stream.WriteAsync(bytes, cancellationToken).AsTask();
            await frame.WriteTask; frame.WriteReturned = true;
            frame.FlushTask = frame.Stream.FlushAsync(cancellationToken);
            await frame.FlushTask; frame.FlushReturned = true;
            _beforeDurableFlushForContractTests?.Invoke(path);
            frame.Stream.Flush(true); frame.DurableFlushReturned = true;
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
        if (errors.Length != 0) throw new AggregateException("Independent parent checkpoint IO and first-close outcomes.", errors);
    }

    private static void Plain(string path)
    {
        var cursor = Path.GetFullPath(path);
        for (var depth = 0; !string.IsNullOrEmpty(cursor); depth++)
        {
            if (depth >= 64 || ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0))
                throw new CoordinationProtocolException("Parent coordination checkpoint path is redirected.");
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    private void Available()
    {
        if (_disposed || _disposeEntered || _io.Any(row => row.Entered && !row.Returned || row.Returned && !row.Closed))
            throw new CoordinationUncertainException("Parent checkpoint writer is closed or has unsettled actual IO.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!await _serial.WaitAsync(0)) throw new CoordinationUncertainException("Actual parent checkpoint IO has not returned.");
        try
        {
            if (_disposeEntered)
            {
                if (_disposeFailure is not null || !_disposed) throw new CoordinationUncertainException("First checkpoint disposition remains failed.");
                return;
            }
            _disposeEntered = true;
            if (_manifestFailure is not null || _io.Any(row => row.Entered && !row.Returned || row.Returned && !row.Closed || row.IsWrite && row.Entered && !row.DurableFlushReturned))
            {
                _disposeFailure = new CoordinationUncertainException("Checkpoint lifetime has unsettled actual IO; exclusive writer retained.");
                throw _disposeFailure;
            }
            try { _originalWriter.CloseEntered = true; _originalWriter.CloseTask = _exclusive.DisposeAsync().AsTask(); await _originalWriter.CloseTask; _originalWriter.Closed = true; _disposed = true; lock (OriginalWriters) OriginalWriters.Remove(_originalWriter); }
            catch (Exception error) { _disposeFailure = error; }
            if (_disposeFailure is not null)
                throw new CoordinationUncertainException("Checkpoint lifetime has failed or unsettled actual disposition.");
        }
        finally { _serial.Release(); }
    }

    private sealed record StoreManifest(int SchemaVersion, string WriterInstance, string OwnershipContract);

    private sealed class IoFrame
    {
        internal bool Entered, Returned, CloseEntered, Closed, IsWrite, WriteReturned, FlushReturned, DurableFlushReturned;
        internal Task? WriteTask, FlushTask;
        internal FileStream? Stream;
        internal Exception? Primary, CloseFailure, Post;
    }
}
