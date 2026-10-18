using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Coordination;

/// <summary>Reads a private service-key file only when the admitted client sends a request.
/// Keys are never placed in command arguments, diagnostic messages or public properties.</summary>
public sealed class ContinuumApiKeyFile
{
    private readonly string _path;
    private readonly List<ReadObservation> _reads = [];

    public ContinuumApiKeyFile(string absolutePath)
    {
        if (!Path.IsPathFullyQualified(absolutePath) || absolutePath.Length > 4096)
            throw new ArgumentException("A bounded absolute private credential path is required.", nameof(absolutePath));
        _path = Path.GetFullPath(absolutePath);
    }

    internal async Task<(AuthenticationHeaderValue Header, byte[] Identity)> ReadAsync(CancellationToken cancellationToken)
    {
        var cursor = _path;
        for (var depth = 0; !string.IsNullOrEmpty(cursor); depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth >= 64 || (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new CoordinationUnavailableException("Credential path is redirected or overbound.");
            cursor = Path.GetDirectoryName(cursor);
        }
        var row = new ReadObservation();
        lock (_reads)
        {
            if (_reads.Count >= 4096 || _reads.Any(item => item.AcquireEntered && !item.AcquireReturned || item.CloseEntered && !item.CloseReturned))
                throw new CoordinationUnavailableException("Private credential read ledger is full or unsettled.");
            _reads.Add(row);
        }
        var bytes = new byte[4097];
        (AuthenticationHeaderValue Header, byte[] Identity) result = default;
        try
        {
            row.AcquireEntered = true;
            var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            row.Resource = stream;
            row.AcquireReturned = true;
            var length = stream.Length;
            if (length is < 20 or > 4096)
                throw new CoordinationUnavailableException("Private credential file length is invalid.");
            var used = 0;
            while (used < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(bytes.AsMemory(used, (int)length - used), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new CoordinationUnavailableException("Private credential file changed.");
                used += read;
            }
            if (await stream.ReadAsync(bytes.AsMemory(used, 1), cancellationToken).ConfigureAwait(false) != 0 || stream.Length != length)
                throw new CoordinationUnavailableException("Private credential file changed.");
            var key = new UTF8Encoding(false, true).GetString(bytes, 0, used).TrimEnd('\r', '\n');
            if (key.Length < 20 || key.Any(character => character < '!' || character > '~'))
                throw new CoordinationUnavailableException("Private credential encoding is invalid.");
            result = (new AuthenticationHeaderValue("ApiKey", key), SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        }
        catch (Exception error) { row.Primary = error; }
        finally
        {
            if (row.AcquireReturned)
            {
                row.CloseEntered = true;
                try
                {
                    row.CloseTask = row.Resource!.DisposeAsync().AsTask();
                    await row.CloseTask.ConfigureAwait(false);
                    row.CloseReturned = true;
                }
                catch (Exception error) { row.CloseFailure = error; }
            }
            try { cancellationToken.ThrowIfCancellationRequested(); }
            catch (Exception error) { row.Post = error; }
            CryptographicOperations.ZeroMemory(bytes);
        }
        var failures = new[] { row.Primary, row.CloseFailure, row.Post }.OfType<Exception>().ToArray();
        if (failures.Length != 0) throw new AggregateException("Private credential read and independent first disposition failed.", failures);
        if (!row.AcquireReturned || !row.CloseReturned) throw new CoordinationUncertainException("Private credential acquisition or first disposition remains unsettled.");
        return result;
    }

    public override string ToString() => "Continuum private API-key file";

    private sealed class ReadObservation
    {
        internal bool AcquireEntered, AcquireReturned, CloseEntered, CloseReturned;
        internal FileStream? Resource;
        internal Task? CloseTask;
        internal Exception? Primary, CloseFailure, Post;
    }
}
