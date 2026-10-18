using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Evidence;

// Cooperative whole-invocation deadline and cumulative byte admission. These checks do not
// claim hard interruption of filesystem/crypto/runtime work or hostile-filesystem atomicity.
internal sealed class InstalledToolFiles(TimeSpan budget, CancellationToken token = default) : IOwnedDuplexBudget
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly object _timing = new();
    private long? _cleanup;
    private long _read;
    private readonly List<FileFrame> _frames = [];
    private readonly BoundedCaptureRetention _retained = new();
    internal const long MaximumReadBytes = 4L * 1024 * 1024 * 1024;
    public long OriginalStarted => _started;
    public TimeSpan WholeLimit => budget;
    public TimeSpan WorkLimit => TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1100).Ticks,
        budget.Ticks - Math.Min(budget.Ticks / 4, TimeSpan.FromSeconds(60).Ticks)));
    public CancellationToken Token => token;
    public void CheckWork()
    {
        lock (_timing)
        {
            token.ThrowIfCancellationRequested();
            if (_cleanup is not null || Stopwatch.GetElapsedTime(_started) >= WorkLimit)
                throw new TimeoutException("Original installed provider WORK endpoint is closed or expired.");
        }
    }
    public TimeSpan CleanupRemaining
    {
        get
        {
            lock (_timing)
            {
                _cleanup ??= Stopwatch.GetTimestamp();
                var whole = budget - Stopwatch.GetElapsedTime(_started);
                var once = TimeSpan.FromSeconds(60) - Stopwatch.GetElapsedTime(_cleanup.Value);
                var remaining = whole < once ? whole : once;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("SAME original whole/earliest once60 endpoint expired.");
                return remaining;
            }
        }
    }
    internal void ImportActualCleanup(BoundedCaptureTimingObservation timing)
    {
        lock (_timing)
        {
            if (timing.StopwatchFrequency != Stopwatch.Frequency || timing.OriginalStartedTimestamp < _started ||
                timing.CleanupStartedTimestamp is not { } cleanup || cleanup < timing.OriginalStartedTimestamp || cleanup > Stopwatch.GetTimestamp())
                throw new EvidenceWriteException("Actual same-origin timing observation is absent or inconsistent.");
            if (_cleanup is null || cleanup < _cleanup) _cleanup = cleanup;
        }
        _ = CleanupRemaining;
    }
    internal TimeSpan Remaining
    {
        get { lock (_timing) { Check(); return _cleanup is null ? WorkLimit - Stopwatch.GetElapsedTime(_started) : CleanupRemaining; } }
    }
    internal void Check()
    {
        lock (_timing) { if (_cleanup is null) CheckWork(); else _ = CleanupRemaining; }
    }
    internal bool IsClosing { get { lock (_timing) return _cleanup is not null; } }
    internal object TimingData
    {
        get
        {
            lock (_timing) return new
            {
                OriginalStarted = _started,
                WholeSeconds = budget.TotalSeconds,
                WorkSeconds = WorkLimit.TotalSeconds,
                CleanupStarted = _cleanup,
                Frequency = Stopwatch.Frequency
            };
        }
    }
    internal static string Identity(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static bool Hash(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
    internal static bool ObjectId(string value) => value.Length is 40 or 64 && value.All(c => char.IsAsciiHexDigitLower(c));
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static void PlainPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new EvidenceWriteException("An absolute physical path is required.");
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new EvidenceWriteException("Linked evidence/payload/input paths are unsupported.");
    }

    // Each actual stream is rooted before constructor entry. Ordinary managed exceptions are
    // known returns; entered/unreturned construction or an inadmissible first close stays held.
    private sealed class FileFrame(string path, bool write)
    {
        internal readonly string Path = path;
        internal readonly bool Write = write;
        internal FileStream? Stream;
        internal byte[]? Bytes;
        internal bool AcquireEntered, AcquireReturned, CloseEntered, CloseReturned;
        internal Exception? Primary, CloseFailure, PostFailure;
    }

    private FileFrame NewFrame(string path, bool write)
    {
        Check(); PlainPath(path); Check();
        if (_frames.Count >= 400000) throw new EvidenceWriteException("Finite actual file-operation ledger exceeds400000.");
        var frame = new FileFrame(path, write); _frames.Add(frame); _retained.Add(frame);
        return frame;
    }

    private void FirstClose(FileFrame frame)
    {
        if (!frame.AcquireReturned || frame.Stream is null) return;
        try
        {
            Check(); frame.CloseEntered = true;
            try { frame.Stream.Dispose(); frame.CloseReturned = true; }
            catch (Exception error) { frame.CloseFailure = error; }
        }
        catch (Exception error) { frame.CloseFailure = error; }
        try { Check(); } catch (Exception error) { frame.PostFailure = error; }
    }

    private static void Require(FileFrame frame)
    {
        var errors = new[] { frame.Primary, frame.CloseFailure, frame.PostFailure }.Where(e => e is not null).Select(e => e!).ToArray();
        if (errors.Length > 0) throw new AggregateException("Original file primary/first-close/post failures retained.", errors);
        if (!frame.AcquireReturned || !frame.CloseEntered || !frame.CloseReturned)
            throw new EvidenceWriteException("Actual file acquisition/disposition remains unknown; original stream/buffer retained.");
    }

    internal byte[] Read(string path, long maximum)
    {
        var frame = NewFrame(path, false);
        try
        {
            Check(); frame.AcquireEntered = true;
            frame.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); frame.AcquireReturned = true;
            Check(); var length = frame.Stream.Length; Check();
            if (length < 0 || length > maximum || length > int.MaxValue || _read > MaximumReadBytes - length)
                throw new EvidenceWriteException("Named complete-file or cumulative read bound exceeded.");
            _read += length; Check(); frame.Bytes = new byte[(int)length];
            var offset = 0;
            while (offset < frame.Bytes.Length)
            {
                Check(); var read = frame.Stream.Read(frame.Bytes, offset, Math.Min(65536, frame.Bytes.Length - offset)); Check();
                if (read == 0) throw new EvidenceWriteException("Complete evidence file ended early.");
                offset += read;
            }
            Check(); var eof = frame.Stream.ReadByte(); Check();
            var finalLength = frame.Stream.Length; Check();
            if (eof != -1 || finalLength != length) throw new EvidenceWriteException("Evidence extent changed while read.");
        }
        catch (Exception error)
        {
            frame.Primary = error;
            try { _ = CleanupRemaining; } catch (Exception post) { frame.PostFailure = post; }
        }
        finally { FirstClose(frame); }
        Require(frame);
        var complete = frame.Bytes!; frame.Bytes = null; frame.Stream = null; return complete;
    }

    internal string FileHash(string path, long maximum)
    {
        var bytes = Read(path, maximum); Check(); var hash = Identity(bytes); Check(); return hash;
    }

    internal void WriteNew(string path, byte[] bytes)
    {
        var frame = NewFrame(path, true); frame.Bytes = bytes;
        try
        {
            Check(); frame.AcquireEntered = true;
            frame.Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); frame.AcquireReturned = true;
            Check(); frame.Stream.Write(bytes); Check(); frame.Stream.Flush(true); Check();
        }
        catch (Exception error)
        {
            frame.Primary = error;
            try { _ = CleanupRemaining; } catch (Exception post) { frame.PostFailure = post; }
        }
        finally { FirstClose(frame); }
        Require(frame); frame.Bytes = null; frame.Stream = null;
    }

    internal void ReleaseKnown()
    {
        if (_frames.Any(f => f.AcquireEntered && !f.CloseReturned)) return;
        _retained.ReleaseKnown();
    }
    internal Dictionary<string, string> Inventory(string root, int count, long bytes, bool skipGit)
    {
        Check(); PlainPath(root);
        var rows = new Dictionary<string, string>(PathComparer);
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        var entries = 0; long total = 0;
        while (pending.TryPop(out var item))
        {
            Check();
            if (item.Depth > 32) throw new EvidenceWriteException("Input/payload directory depth exceeds32.");
            foreach (var path in Directory.EnumerateFileSystemEntries(item.Path))
            {
                Check();
                if (++entries > count) throw new EvidenceWriteException("Input/payload entry census exceeds the finite profile.");
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (skipGit && relative == ".git") continue;
                PlainPath(path); Check();
                var attributes = File.GetAttributes(path); Check();
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((path, item.Depth + 1));
                else
                {
                    var raw = Read(path, Math.Min(bytes - total, 128L * 1024 * 1024));
                    total += raw.LongLength;
                    rows.Add(relative, raw.Length + ":" + Identity(raw));
                }
            }
        }
        Check();
        return rows;
    }

    internal static string InventoryIdentity(IReadOnlyDictionary<string, string> rows) =>
        Identity(Encoding.UTF8.GetBytes(string.Join("\n", rows.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key.Length + ":" + p.Key + ":" + p.Value))));

    internal static void Same(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, bool allowNew)
    {
        if (!allowNew && before.Count != after.Count || before.Any(p => !after.TryGetValue(p.Key, out var hash) || hash != p.Value))
            throw new EvidenceWriteException("Complete original source/payload membership or bytes changed.");
    }
}