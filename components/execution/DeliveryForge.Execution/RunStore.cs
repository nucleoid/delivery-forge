using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Execution;

public sealed record StoredRunRecord(string RunId, long Sequence, string Kind, DateTimeOffset CreatedAt, JsonElement Payload, string Path, string Sha256);
public sealed record RunRecovery(IReadOnlyList<StoredRunRecord> Records, StoredRunRecord? Latest, bool PointerWasRecovered);

public sealed partial class RunStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AppendLocks = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public RunStore(string root) => _root = Path.GetFullPath(root);
    [System.Text.Json.Serialization.JsonIgnore]
    public string RootPath => _root;

    public async Task<StoredRunRecord> AppendAsync<T>(string runId, string kind, T payload, CancellationToken cancellationToken = default)
    {
        CheckOriginal();
        ValidateName(runId, nameof(runId));
        ValidateName(kind, nameof(kind));
        var runDirectory = GetRunDirectory(runId);
        RejectReparseAncestors(runDirectory);
        var appendLock = AppendLocks.GetOrAdd(runDirectory, _ => new SemaphoreSlim(1, 1));
        using var originalDeadline = Deadline(cancellationToken); cancellationToken = originalDeadline.Token;
        await appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Effect(() => Directory.CreateDirectory(runDirectory));
            RejectReparseAncestors(runDirectory);
            var recovery = await RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
            var sequence = (recovery.Latest?.Sequence ?? 0) + 1;
            var document = JsonSerializer.SerializeToUtf8Bytes(
                new PersistedRecord<T>(runId, sequence, kind, DateTimeOffset.UtcNow, payload), _json);
            if (document.Length > 16 * 1024 * 1024 || recovery.Records.Count >= 4096 ||
                recovery.Records.Sum(record => new FileInfo(record.Path).Length) + document.Length > 64 * 1024 * 1024)
                throw new RunStoreCorruptionException("Appending would exceed the finite recoverable run history bounds.");
            var hash = Convert.ToHexStringLower(SHA256.HashData(document));
            var fileName = $"{sequence:D12}-sha256-{hash}.json";
            var target = Path.Combine(runDirectory, fileName);
            var temp = Path.Combine(runDirectory, $".record-{Guid.NewGuid():N}.tmp");
            try
            {
                await WriteNewDurablyAsync(temp, document, cancellationToken).ConfigureAwait(false);
                try { Effect(() => { File.Move(temp, target, overwrite: false); return true; }); }
                catch (IOException exception)
                {
                    throw new RunStoreWriterConflictException("An immutable record already occupies the next sequence; one-writer ownership was violated.", exception);
                }
            }
            finally
            {
                TryDelete(temp);
            }

            await ReplacePointerAsync(runDirectory, fileName, hash, cancellationToken).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(document);
            return new StoredRunRecord(runId, sequence, kind, parsed.RootElement.GetProperty("createdAt").GetDateTimeOffset(),
                parsed.RootElement.GetProperty("payload").Clone(), target, hash);
        }
        finally
        {
            appendLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> DiscoverRunIdsAsync(CancellationToken cancellationToken = default)
    {
        CheckOriginal();
        var runs = Path.Combine(_root, "runs");
        RejectReparseAncestors(runs);
        if (!Directory.Exists(runs)) return [];
        var directories = Effect(() => Directory.EnumerateDirectories(runs).Take(4097).ToArray());
        if (directories.Length > 4096) throw new RunStoreCorruptionException("Run registry exceeds the finite discovery bound.");
        var identities = new List<string>();
        foreach (var directory in directories)
        {
            CheckOriginal();
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new RunStoreCorruptionException("Run registry contains a reparse/symlink directory.");
            var first = Directory.EnumerateFiles(directory, "*-sha256-*.json").Order(StringComparer.Ordinal).FirstOrDefault()
                ?? throw new RunStoreCorruptionException("Run namespace has no identifiable immutable record; cleanup/reconciliation is required.");
            if ((File.GetAttributes(first) & FileAttributes.ReparsePoint) != 0 || new FileInfo(first).Length > 16 * 1024 * 1024)
                throw new RunStoreCorruptionException("Run identity record is a reparse entry or exceeds the finite discovery bound.");
            using var document = JsonDocument.Parse(await ReadOriginalAsync(first, cancellationToken).ConfigureAwait(false));
            var runId = document.RootElement.GetProperty("runId").GetString() ?? throw new RunStoreCorruptionException("Run identity is missing.");
            CheckOriginal();
        ValidateName(runId, nameof(runId));
            if (!string.Equals(Path.GetFullPath(directory), GetRunDirectory(runId), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new RunStoreCorruptionException("Run identity does not match its hashed storage namespace.");
            await RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
            identities.Add(runId);
        }
        return identities.AsReadOnly();
    }
    public async Task<RunRecovery> RecoverAsync(string runId, CancellationToken cancellationToken = default)
    {
        CheckOriginal();
        ValidateName(runId, nameof(runId));
        var runDirectory = GetRunDirectory(runId);
        RejectReparseAncestors(runDirectory);
        if (!Directory.Exists(runDirectory)) return new RunRecovery([], null, false);
        var records = new List<StoredRunRecord>();
        var paths = Effect(() => Directory.EnumerateFiles(runDirectory, "*-sha256-*.json").Take(4097).ToArray());
        if (paths.Length > 4096) throw new RunStoreCorruptionException("Immutable run history exceeds the finite record bound.");
        long totalBytes = 0;
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            CheckOriginal();
            RejectReparseFile(path);
            var length = new FileInfo(path).Length;
            totalBytes = checked(totalBytes + length);
            if (length > 16 * 1024 * 1024 || totalBytes > 64 * 1024 * 1024)
                throw new RunStoreCorruptionException("Immutable run history exceeds the finite recovery byte bound.");
            var (fileSequence, expectedHash) = ParseFileIdentity(path);
            var bytes = await ReadOriginalAsync(path, cancellationToken).ConfigureAwait(false);
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (actualHash != expectedHash)
                throw new RunStoreCorruptionException($"Immutable run record '{Path.GetFileName(path)}' is corrupt.");
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                var record = new StoredRunRecord(root.GetProperty("runId").GetString()!, root.GetProperty("sequence").GetInt64(),
                    root.GetProperty("kind").GetString()!, root.GetProperty("createdAt").GetDateTimeOffset(),
                    root.GetProperty("payload").Clone(), path, actualHash);
                if (record.RunId != runId || record.Sequence != fileSequence)
                    throw new RunStoreCorruptionException($"Immutable run record '{Path.GetFileName(path)}' does not match its run namespace or filename sequence.");
                records.Add(record);
            }
            catch (RunStoreCorruptionException) { throw; }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new RunStoreCorruptionException($"Immutable run record '{Path.GetFileName(path)}' is invalid.", exception);
            }
        }

        records.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        for (var index = 0; index < records.Count; index++)
            if (records[index].Sequence != index + 1)
                throw new RunStoreCorruptionException("Run record sequence contains a gap or duplicate.");

        var latest = records.LastOrDefault();
        var pointerPath = Path.Combine(runDirectory, "current.json");
        if (File.Exists(pointerPath)) RejectReparseFile(pointerPath);
        if (latest is null && File.Exists(pointerPath))
            throw new RunStoreCorruptionException("Run pointer references an immutable history that is missing.");
        var pointerRecovered = !await PointerMatchesAsync(runDirectory, latest, cancellationToken).ConfigureAwait(false);
        if (pointerRecovered && latest is not null)
            await ReplacePointerAsync(runDirectory, Path.GetFileName(latest.Path), latest.Sha256, cancellationToken).ConfigureAwait(false);
        return new RunRecovery(records, latest, pointerRecovered && latest is not null);
    }

    public T ReadPayload<T>(StoredRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Payload.Deserialize<T>(_json) ?? throw new RunStoreCorruptionException($"Record {record.Sequence} payload is invalid.");
    }

    private string GetRunDirectory(string runId)
        => Path.Combine(_root, "runs", GetRunNamespaceIdentity(runId));

    // Namespace identity is data, not proof of an active/closed run or permission to dispatch.
    public string GetRunNamespaceIdentity(string runId)
    {
        ValidateName(runId, nameof(runId));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runId)));
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new RunStoreCorruptionException("Run storage cannot traverse a reparse/symlink ancestor.");
    }

    private static void RejectReparseFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new RunStoreCorruptionException("Immutable run storage cannot read a reparse/symlink file.");
    }

    private async Task<bool> PointerMatchesAsync(string runDirectory, StoredRunRecord? latest, CancellationToken cancellationToken)
    {
        var pointerPath = Path.Combine(runDirectory, "current.json");
        if (latest is null) return !File.Exists(pointerPath);
        try
        {
            using var pointer = JsonDocument.Parse(await ReadOriginalAsync(pointerPath, cancellationToken).ConfigureAwait(false));
            return pointer.RootElement.GetProperty("fileName").GetString() == Path.GetFileName(latest.Path) &&
                   pointer.RootElement.GetProperty("sha256").GetString() == latest.Sha256;
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task ReplacePointerAsync(string runDirectory, string fileName, string hash, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { fileName, sha256 = hash }, _json);
        var temp = Path.Combine(runDirectory, $".current-{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteNewDurablyAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            Effect(() => { File.Move(temp, Path.Combine(runDirectory, "current.json"), overwrite: true); return true; });
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task WriteNewDurablyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        FileStream? stream = null; Exception? primary = null, close = null;
        try
        {
            Effect(() => stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough));
            if (original is null) { await stream!.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); await stream.FlushAsync(cancellationToken).ConfigureAwait(false); stream.Flush(true); }
            else { for (var offset = 0; offset < bytes.Length; offset += 65536) { cancellationToken.ThrowIfCancellationRequested(); var part = bytes.Slice(offset, Math.Min(65536, bytes.Length - offset)); Effect(() => { stream!.Write(part.Span); return true; }); } Effect(() => { stream!.Flush(true); return true; }); }
        }
        catch (Exception error) { primary = error; }
        if (stream is not null)
        {
            try
            {
                if (original is not null) { if (primary is not null) _ = original.CleanupRemaining; else CheckOriginal(); }
                stream.Dispose();
                if (original is not null) { if (primary is not null) _ = original.CleanupRemaining; else CheckOriginal(); }
            }
            catch (Exception error) { close = error; RetainFile(stream); }
        }
        if (primary is not null && close is not null) throw new AggregateException("Original store write and first disposition failed.", primary, close);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (close is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(close).Throw();
    }

    private static (long Sequence, string Hash) ParseFileIdentity(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string marker = "-sha256-";
        var offset = name.IndexOf(marker, StringComparison.Ordinal);
        if (offset != 12 || !long.TryParse(name[..offset], out var sequence) || sequence < 1)
            throw new RunStoreCorruptionException("Invalid immutable record filename sequence.");
        var hash = name[(offset + marker.Length)..];
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)) || hash != hash.ToLowerInvariant())
            throw new RunStoreCorruptionException("Invalid immutable record filename hash.");
        return (sequence, hash);
    }

    private static void ValidateName(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Run id and kind are required.", name);
    }

    private void TryDelete(string path)
    {
        // A revoked forward write retains its temporary evidence; cleanup does
        // not silently erase it or replace the original refusal.
        if (original is not null && !cleanupOnly) { try { CheckOriginal(); } catch (Exception) { return; } }
        try { Effect(() => { File.Delete(path); return true; }); } catch (IOException) { }
    }

    private sealed record PersistedRecord<T>(string RunId, long Sequence, string Kind, DateTimeOffset CreatedAt, T Payload);
}

public sealed partial class RunStoreCorruptionException : Exception
{
    public RunStoreCorruptionException(string message) : base(message) { }
    public RunStoreCorruptionException(string message, Exception inner) : base(message, inner) { }
}

public sealed partial class RunStoreWriterConflictException(string message, Exception inner) : Exception(message, inner);
