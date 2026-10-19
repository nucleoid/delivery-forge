using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Evidence;

// Cooperative whole-invocation deadline and cumulative byte admission. These checks do not
// claim hard interruption of filesystem/crypto/runtime work or hostile-filesystem atomicity.
internal sealed class InstalledToolFiles(TimeSpan budget, CancellationToken token = default) : IOwnedDuplexBudget, IOwnedFailureCleanup, IOwnedBudgetUnknownRetention
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly IOwnedDuplexBudget? _parent;
    private readonly InstalledToolHostScope.Credits? _credits;
    private readonly long _phaseStarted = Stopwatch.GetTimestamp();
    internal InstalledToolFiles(IOwnedDuplexBudget actualParent, TimeSpan maximum, InstalledToolHostScope.Credits credits)
        : this(maximum, actualParent.Token)
    {
        if (maximum <= TimeSpan.Zero || maximum > TimeSpan.FromSeconds(1200)) throw new ArgumentOutOfRangeException(nameof(maximum));
        actualParent.CheckWork(); _parent = new OwnedWorkPhaseBudget(actualParent);
        _started = actualParent.OriginalStarted; _credits = credits;
    }
    internal InstalledToolFiles(IOwnedDuplexBudget actualParent, TimeSpan maximum, InstalledToolHostScope.Credits credits,
        DeliveryForge.Coordination.ICoordinatedEffectAdmission actualAdmission, string actualRunId)
        : this(actualParent, maximum, credits)
    {
        ArgumentNullException.ThrowIfNull(actualAdmission);
        if (string.IsNullOrWhiteSpace(actualRunId)) throw new ArgumentException("Actual containing run required.");
        _admission = actualAdmission as DeliveryForge.Coordination.ICoordinatedSynchronousEffectAdmission
            ?? throw new EvidenceWriteException("Actual containing admission must retain synchronous file operations independently of lease-state locking.");
        _runId = actualRunId;
    }
    private readonly DeliveryForge.Coordination.ICoordinatedSynchronousEffectAdmission? _admission;
    private readonly string? _runId;
    // Reentrancy applies ONLY to this enrolled synchronous thread. The parent's
    // lease-state monitor is not held across I/O; this never flows across an await.
    [ThreadStatic] private static InstalledToolFiles? activeEffect;
    internal T Effect<T>(Func<T> action)
    {
        Check();
        if (ReferenceEquals(activeEffect, this) || _admission is null) return action();
        return _admission.EnterSynchronous(_runId!, () =>
        {
            Check(); var prior = activeEffect; activeEffect = this;
            try { var value = action(); Check(); return value; }
            finally { activeEffect = prior; }
        });
    }
    internal void Effect(Action action) => Effect(() => { action(); return true; });
    internal void EnsureDirectory(string path) => Effect(() =>
    {
        Check(); PlainPath(path); Directory.CreateDirectory(path); Check();
    });
    private TimeSpan PhaseOffset => Stopwatch.GetElapsedTime(_started, _phaseStarted);
    public void BeginFailureCleanup()
    {
        lock (_timing) _cleanup ??= Stopwatch.GetTimestamp();
        if (_parent is IOwnedFailureCleanup actual) actual.BeginFailureCleanup();
        _ = CleanupRemaining;
    }
    public void PreserveUnknown()
    {
        if (_credits is not null) _credits.Unknown = true;
        if (_parent is IOwnedBudgetUnknownRetention actual) actual.PreserveUnknown();
    }
    internal void AdmitCaptureCall() => _credits?.Call();
    internal void AdmitCaptureBytes(long bytes) => _credits?.Capture(bytes);
    private readonly object _timing = new();
    private long? _cleanup;
    private long _read;
    private readonly List<FileFrame> _frames = [];
    private readonly BoundedCaptureRetention _retained = new();
    internal const long MaximumReadBytes = 4L * 1024 * 1024 * 1024;
    public long OriginalStarted => _started;
    public TimeSpan WholeLimit => _parent is null ? budget : Minimum(_parent.WholeLimit, PhaseOffset + budget);
    public TimeSpan WorkLimit => _parent is null ? LocalWork : Minimum(_parent.WorkLimit, PhaseOffset + LocalWork);
    private TimeSpan LocalWork => TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1100).Ticks,
        budget.Ticks - Math.Min(budget.Ticks / 4, TimeSpan.FromSeconds(60).Ticks)));
    private static TimeSpan Minimum(TimeSpan left, TimeSpan right) => left < right ? left : right;
    public CancellationToken Token => token;
    public void CheckWork()
    {
        _admission?.CheckCurrent(_runId!);
        lock (_timing) CheckWorkCore();
    }
    private void CheckWorkCore()
    {
        _parent?.CheckWork(); token.ThrowIfCancellationRequested();
        if (_cleanup is not null || Stopwatch.GetElapsedTime(_started) >= WorkLimit)
            throw new TimeoutException("Original installed provider WORK endpoint is closed or expired.");
    }
    public TimeSpan CleanupRemaining
    {
        get
        {
            lock (_timing)
            {
                _cleanup ??= Stopwatch.GetTimestamp();
                var whole = WholeLimit - Stopwatch.GetElapsedTime(_started);
                if (_parent is not null) whole = Minimum(whole, _parent.CleanupRemaining);
                var once = TimeSpan.FromSeconds(60) - Stopwatch.GetElapsedTime(_cleanup.Value);
                var remaining = whole < once ? whole : once;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("SAME original whole/earliest once60 endpoint expired.");
                return remaining;
            }
        }
    }
    // Called only after the concrete executor has joined the actual settled Linux
    // capture. This starts ONLY this subordinate files finalization; no timestamp
    // is imported from a child that never entered cleanup, and no parent failure
    // notification is sent. Parent WORK/whole and prior cleanup still bound MIN.
    internal void FinishSettledWorkCapture(BoundedCaptureTimingObservation timing)
    {
        _admission?.CheckCurrent(_runId!);
        lock (_timing)
        {
            if (timing.StopwatchFrequency != Stopwatch.Frequency ||
                timing.OriginalStartedTimestamp != _started || timing.CleanupStartedTimestamp is not null)
                throw new EvidenceWriteException("Actual settled WORK capture must join this original once without a child cleanup stamp.");
            if (_cleanup is null) { CheckWorkCore(); _cleanup = Stopwatch.GetTimestamp(); }
        }
        _ = CleanupRemaining;
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
        get { Check(); lock (_timing) return _cleanup is null ? WorkLimit - Stopwatch.GetElapsedTime(_started) : CleanupRemaining; }
    }
    internal void Check()
    {
        // Never acquire the parent monitor while holding this timing lock: native
        // process admission uses the opposite order when it checks its budget.
        _admission?.CheckCurrent(_runId!);
        lock (_timing) { if (_cleanup is null) CheckWorkCore(); else _ = CleanupRemaining; }
    }
    internal bool IsClosing { get { lock (_timing) return _cleanup is not null; } }
    internal object TimingData
    {
        get
        {
            lock (_timing) return new
            {
                OriginalStarted = _started,
                WholeSeconds = WholeLimit.TotalSeconds,
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
        _credits?.File(); var frame = new FileFrame(path, write); _frames.Add(frame); _retained.Add(frame);
        return frame;
    }

    private void FirstClose(FileFrame frame)
    {
        if (!frame.AcquireReturned || frame.Stream is null) return;
        try
        {
            CheckDisposition(); frame.CloseEntered = true;
            try { frame.Stream.Dispose(); frame.CloseReturned = true; }
            catch (Exception error) { frame.CloseFailure = error; }
        }
        catch (Exception error) { frame.CloseFailure = error; }
        try { CheckDisposition(); } catch (Exception error) { frame.PostFailure = error; }
    }

    // Revocation forbids new effects, not closing a handle already owned by this
    // original. Keep the same whole/earliest cleanup bounds and ignore cancellation
    // only for this existing-handle disposition.
    private void CheckDisposition()
    {
        try { lock (_timing) { if (_cleanup is null) CheckWorkCore(); else _ = CleanupRemaining; } }
        catch { BeginFailureCleanup(); _ = CleanupRemaining; }
    }

    private void Require(FileFrame frame)
    {
        var errors = new[] { frame.Primary, frame.CloseFailure, frame.PostFailure }.Where(e => e is not null).Select(e => e!).ToArray();
        if (frame.AcquireEntered && !frame.AcquireReturned && frame.Primary is null || frame.Stream is not null && (!frame.CloseReturned || frame.CloseFailure is not null || frame.PostFailure is not null)) PreserveUnknown();
        if (errors.Length > 0) throw new AggregateException("Original file primary/first-close/post failures retained.", errors);
        if (!frame.AcquireReturned || !frame.CloseEntered || !frame.CloseReturned)
            throw new EvidenceWriteException("Actual file acquisition/disposition remains unknown; original stream/buffer retained.");
    }

    internal byte[] Read(string path, long maximum) => Effect(() => ReadCore(path, maximum));
private byte[] ReadCore(string path, long maximum)
    {
        var frame = NewFrame(path, false);
        try
        {
            Check(); frame.AcquireEntered = true;
            frame.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); frame.AcquireReturned = true;
            Check(); var length = frame.Stream.Length; Check();
            if (length < 0 || length > maximum || length > int.MaxValue || _read > MaximumReadBytes - length)
                throw new EvidenceWriteException("Named complete-file or cumulative read bound exceeded.");
            _credits?.Read(length); _read += length; Check(); frame.Bytes = new byte[(int)length];
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
            try { BeginFailureCleanup(); } catch (Exception post) { frame.PostFailure = post; }
        }
        finally { FirstClose(frame); }
        Require(frame);
        var complete = frame.Bytes!; frame.Bytes = null; frame.Stream = null; return complete;
    }

    internal string FileHash(string path, long maximum) => Effect(() => FileHashCore(path, maximum));
private string FileHashCore(string path, long maximum)
    {
        var bytes = Read(path, maximum); Check(); var hash = Identity(bytes); Check(); return hash;
    }

    internal void WriteNew(string path, byte[] bytes) => Effect(() => WriteNewCore(path, bytes));
private void WriteNewCore(string path, byte[] bytes)
    {
        _credits?.Write(bytes.LongLength);
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
            try { BeginFailureCleanup(); } catch (Exception post) { frame.PostFailure = post; }
        }
        finally { FirstClose(frame); }
        Require(frame); frame.Bytes = null; frame.Stream = null;
    }

    internal void ReleaseKnown()
    {
        if (_frames.Any(f => f.AcquireEntered && !f.CloseReturned)) return;
        _retained.ReleaseKnown();
    }
    internal Dictionary<string, string> Inventory(string root, int count, long bytes, bool skipGit) => Effect(() => InventoryCore(root, count, bytes, skipGit));
private Dictionary<string, string> InventoryCore(string root, int count, long bytes, bool skipGit)
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

    internal enum ComparisonPhase { InstallationProfile, InstallationCurrent, GitCurrent, ConsumerCurrent }
    internal sealed record MismatchSample(string Kind, string KeySha256);
    internal sealed record InventoryMismatch(string Phase, int BeforeCount, int AfterCount,
        int Missing, int Changed, int Added, bool AllowNew, MismatchSample[] Samples, bool Truncated);
    private const string MismatchKey = "DeliveryForge.InventoryMismatch";
    private const string MismatchFailureKey = "DeliveryForge.InventoryMismatchDiagnosticFailure";

    internal static InventoryMismatch? Mismatch(Exception failure) =>
        failure is EvidenceWriteException ? failure.Data[MismatchKey] as InventoryMismatch : null;
    internal static Exception? MismatchDiagnosticFailure(Exception failure) =>
        failure is EvidenceWriteException ? failure.Data[MismatchFailureKey] as Exception : null;

    // The original refusal predicate remains authoritative. This overload adds DATA only
    // from the already captured dictionaries, under their existing original owner's clock.
    internal static void Same(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after,
        bool allowNew, InstalledToolFiles owner, ComparisonPhase phase)
    {
        if (!allowNew && before.Count != after.Count || before.Any(p => !after.TryGetValue(p.Key, out var hash) || hash != p.Value))
        {
            var failure = new EvidenceWriteException("Complete original source/payload membership or bytes changed.");
            try { failure.Data[MismatchKey] = DescribeMismatch(before, after, allowNew, owner, phase); }
            catch (Exception independent) { failure.Data[MismatchFailureKey] = independent; }
            throw failure;
        }
    }

    private static InventoryMismatch DescribeMismatch(IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after, bool allowNew, InstalledToolFiles owner, ComparisonPhase phase)
    {
        owner.Check();
        var label = phase switch
        {
            ComparisonPhase.InstallationProfile => "installation-profile",
            ComparisonPhase.InstallationCurrent => "installation-current",
            ComparisonPhase.GitCurrent => "git-current",
            ComparisonPhase.ConsumerCurrent => "consumer-current",
            _ => throw new EvidenceWriteException("Unknown comparison diagnostic phase.")
        };
        if (before.Count > 100000 || after.Count > 100000)
            throw new EvidenceWriteException("Comparison diagnostic census exceeds original inventory profile.");
        var selected = new List<(string Key, string Kind)>(16);
        var missing = 0; var changed = 0; var added = 0;
        foreach (var pair in before)
        {
            owner.Check();
            if (!after.TryGetValue(pair.Key, out var value)) { missing++; Select(pair.Key, "Missing"); }
            else if (value != pair.Value) { changed++; Select(pair.Key, "Changed"); }
            owner.Check();
        }
        foreach (var pair in after)
        {
            owner.Check();
            if (!before.ContainsKey(pair.Key)) { added++; Select(pair.Key, "Added"); }
            owner.Check();
        }
        var samples = new MismatchSample[selected.Count];
        for (var i = 0; i < selected.Count; i++)
        {
            owner.Check();
            var keyBytes = new UTF8Encoding(false, true).GetBytes(selected[i].Key);
            owner.Check();
            var digest = Identity(keyBytes);
            owner.Check();
            samples[i] = new(selected[i].Kind, digest);
        }
        var result = new InventoryMismatch(label, before.Count, after.Count, missing, changed, added,
            allowNew, samples, missing + changed + added > samples.Length);
        owner.Check();
        var serialized = JsonSerializer.SerializeToUtf8Bytes(result);
        owner.Check();
        if (serialized.Length > 8192) throw new EvidenceWriteException("Comparison diagnostic representation exceeds8192 bytes.");
        return result;

        void Select(string key, string kind)
        {
            owner.Check();
            if (key.Length > 32768) throw new EvidenceWriteException("Comparison diagnostic key exceeds finite profile.");
            // Permitted additions cannot evict the identities that caused refusal.
            // All refused categories stay ordinal; only allowNew additions rank last.
            var at = selected.FindIndex(item =>
                allowNew && ((kind == "Added") != (item.Kind == "Added"))
                    ? kind != "Added"
                    : StringComparer.Ordinal.Compare(key, item.Key) < 0);
            if (at < 0) at = selected.Count;
            if (at < 16)
            {
                if (selected.Count == 16) selected.RemoveAt(15);
                selected.Insert(at, (key, kind));
            }
            owner.Check();
        }
    }
}
