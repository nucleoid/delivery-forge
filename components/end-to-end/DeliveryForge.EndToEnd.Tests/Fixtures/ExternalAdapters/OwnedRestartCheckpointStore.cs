using System.Text.Json;
using DeliveryForge.Coordination;

namespace DeliveryForge.EndToEnd.Tests;

// External coordination fixture port: actual files and exclusive file handles, never production authority.
// Restart means a new fixture-store lifetime over retained bytes; it is not a claimed OS parent crash.
internal sealed class OwnedRestartCheckpointStore : ICoordinationCheckpointStore, IDisposable
{
    private readonly string _root;
    private readonly string _takeoverReference;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, (CoordinationWriterClaim Claim, FileStream Lock)> _writers = new(StringComparer.Ordinal);
    private bool _disposed;

    public OwnedRestartCheckpointStore(string root, string takeoverReference)
    {
        _root = Path.GetFullPath(root);
        _takeoverReference = takeoverReference;
        RequirePlain(_root);
    }

    public async Task<CoordinationWriterClaim> ClaimWriterAsync(string lifecycleId, string proposedWriterEpoch,
        string? trustedTakeoverProof, CancellationToken cancellationToken = default)
    {
        var directory = LifecycleDirectory(lifecycleId);
        if (!Guid.TryParseExact(proposedWriterEpoch, "D", out _)) throw new CoordinationProtocolException("Fixture epoch must be UUID data.");
        await _serial.WaitAsync(cancellationToken);
        FileStream? writer = null;
        try
        {
            EnsureAvailable();
            if (_writers.ContainsKey(lifecycleId)) throw new CoordinationProtocolException("Fixture already owns this lifecycle writer.");
            Directory.CreateDirectory(directory);
            RequirePlain(directory);
            var lockPath = Path.Combine(directory, "writer.lock");
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                throw new CoordinationProtocolException("Fixture writer lock is not a plain owned file.");
            writer = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (writer.Length > 0 && trustedTakeoverProof != _takeoverReference)
                throw new CoordinationProtocolException("Fixture takeover reference is not issuer-configured; no epoch was replaced.");
            var records = await ReadRecords(directory, cancellationToken);
            var claim = new CoordinationWriterClaim(lifecycleId, proposedWriterEpoch, (records.LastOrDefault()?.RecordSequence ?? 0) + 1);
            await WriteClaim(writer, claim, cancellationToken);
            _writers.Add(lifecycleId, (claim, writer));
            writer = null;
            return claim;
        }
        catch (IOException)
        {
            throw new CoordinationProtocolException("An actual exclusive fixture writer is held or unavailable; no takeover occurred.");
        }
        finally { writer?.Dispose(); _serial.Release(); }
    }

    public async Task AppendAsync(CoordinationWriterClaim claim, long expectedRecordSequence,
        CoordinationCheckpointRecord record, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken);
        try
        {
            EnsureAvailable();
            if (!_writers.TryGetValue(claim.LifecycleId, out var writer) || writer.Claim.WriterEpoch != claim.WriterEpoch ||
                writer.Claim.NextRecordSequence != expectedRecordSequence || record.RecordSequence != expectedRecordSequence ||
                record.WriterEpoch != claim.WriterEpoch)
                throw new CoordinationCheckpointNotWrittenException("Actual fixture epoch/sequence differs; no record written.");
            var directory = LifecycleDirectory(claim.LifecycleId);
            RequirePlain(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            if (bytes.Length > 1024 * 1024 || expectedRecordSequence > 128)
                throw new CoordinationCheckpointNotWrittenException("Fixture journal exceeds its finite bound.");
            var path = Path.Combine(directory, $"{expectedRecordSequence:D12}-record.json");
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            var next = writer.Claim with { NextRecordSequence = expectedRecordSequence + 1 };
            await WriteClaim(writer.Lock, next, cancellationToken);
            _writers[claim.LifecycleId] = (next, writer.Lock);
        }
        finally { _serial.Release(); }
    }

    public async Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadLifecycleAsync(string lifecycleId, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken);
        try { EnsureAvailable(); return await ReadRecords(LifecycleDirectory(lifecycleId), cancellationToken); }
        finally { _serial.Release(); }
    }

    private static async Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadRecords(string directory, CancellationToken cancellationToken)
    {
        RequirePlain(directory);
        if (!Directory.Exists(directory)) return [];
        var files = Directory.EnumerateFiles(directory, "*-record.json").Take(129).Order(StringComparer.Ordinal).ToArray();
        if (files.Length > 128) throw new CoordinationProtocolException("Fixture journal record bound exceeded.");
        var records = new List<CoordinationCheckpointRecord>();
        long size = 0;
        foreach (var path in files)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new CoordinationProtocolException("Fixture record is not a plain file.");
            var length = new FileInfo(path).Length;
            size += length;
            if (length > 1024 * 1024 || size > 8 * 1024 * 1024) throw new CoordinationProtocolException("Fixture journal byte bound exceeded.");
            records.Add(JsonSerializer.Deserialize<CoordinationCheckpointRecord>(await File.ReadAllBytesAsync(path, cancellationToken))
                ?? throw new CoordinationProtocolException("Fixture record is missing."));
        }
        return records.AsReadOnly();
    }

    private string LifecycleDirectory(string lifecycleId)
    {
        if (!Guid.TryParseExact(lifecycleId, "D", out _)) throw new CoordinationProtocolException("Fixture lifecycle path is not a UUID.");
        return Path.Combine(_root, lifecycleId);
    }

    private static async Task WriteClaim(FileStream stream, CoordinationWriterClaim claim, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        stream.SetLength(0);
        await JsonSerializer.SerializeAsync(stream, claim, cancellationToken: cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    private static void RequirePlain(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new CoordinationProtocolException("Fixture checkpoint path crosses a reparse ancestor.");
    }

    private void EnsureAvailable()
    {
        if (_disposed) throw new CoordinationCheckpointNotWrittenException("Fixture writer lifetime ended; old epoch cannot append.");
    }

    public void Dispose()
    {
        if (!_serial.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Fixture writer disposal did not settle within its bound.");
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var writer in _writers.Values) writer.Lock.Dispose();
            _writers.Clear();
        }
        finally { _serial.Release(); }
    }
}
