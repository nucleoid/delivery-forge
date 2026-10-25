using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Execution.Git;
namespace DeliveryForge.Tool;

public sealed class ToolLifetime : IDisposable, DeliveryForge.Evidence.IOwnedDuplexBudget, DeliveryForge.Evidence.IOwnedBudgetUnknownRetention, DeliveryForge.Evidence.IOwnedFailureCleanup
{
    readonly long started = Stopwatch.GetTimestamp(); TimeSpan total, work; readonly CancellationTokenSource linked;
    readonly object gate = new(); long cleanup; bool disposed, retainedUnknown, disposeRequested; ToolFailureAttention? attention;
    readonly HashSet<HostBridgeFrame> retainedBridgeFrames = new(ReferenceEqualityComparer.Instance);
    public ToolLifetime(TimeSpan total, CancellationToken token = default)
    {
        if (total <= TimeSpan.Zero || total > TimeSpan.FromSeconds(1200)) throw new ArgumentOutOfRangeException(nameof(total)); this.total = total;
        work = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1100).Ticks, total.Ticks - Math.Min(total.Ticks / 4, TimeSpan.FromSeconds(60).Ticks)));
        linked = CancellationTokenSource.CreateLinkedTokenSource(token); linked.CancelAfter(work);
    }
    public void Restrict(TimeSpan maximum) { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); if (maximum <= TimeSpan.Zero || maximum > total) throw new ArgumentOutOfRangeException(nameof(maximum)); if (maximum == total) return; total = maximum; var candidate = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1100).Ticks, maximum.Ticks - Math.Min(maximum.Ticks / 4, TimeSpan.FromSeconds(60).Ticks))); if (candidate < work) work = candidate; var left = work - Stopwatch.GetElapsedTime(started); if (left <= TimeSpan.Zero) throw new TimeoutException("Restricted original endpoint already expired."); linked.CancelAfter(left); } }
    public long OriginalStarted => started;
    public TimeSpan WholeLimit { get { lock (gate) return total; } }
    public TimeSpan WorkLimit { get { lock (gate) return work; } }
    public void CheckWork() => Check();
    public CancellationToken Token => linked.Token;
    public TimeSpan Remaining { get { Check(); return work - Stopwatch.GetElapsedTime(started); } }
    public void Check() { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); linked.Token.ThrowIfCancellationRequested(); if (cleanup != 0 || Stopwatch.GetElapsedTime(started) >= work) throw new TimeoutException("Original tool WORK endpoint expired."); } }
    public TimeSpan CleanupRemaining { get { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); if (cleanup == 0) cleanup = Stopwatch.GetTimestamp(); var whole = total - Stopwatch.GetElapsedTime(started); var once = TimeSpan.FromSeconds(60) - Stopwatch.GetElapsedTime(cleanup); var left = whole < once ? whole : once; if (left <= TimeSpan.Zero) throw new TimeoutException("SAME original whole/earliest once60 endpoint expired."); return left; } } }
    internal bool HasRetainedUnknown { get { lock (gate) return retainedUnknown || retainedBridgeFrames.Count != 0; } }
    internal bool HasExplicitUnknown { get { lock (gate) return retainedUnknown; } }
    internal void BindAttention(ToolFailureAttention actual) { lock (gate) { if (attention is not null) throw new IOException("Original failure attention already bound."); attention = actual; } }
    internal void RecordFirstCaught(string stage, Exception actual)
    {
        bool cleanupBegun; lock (gate) cleanupBegun = cleanup != 0;
        attention?.CaptureFirstCaught(stage, actual, cleanupBegun);
    }
    internal void BeginFailureCleanup() { _ = CleanupRemaining; attention?.EmitOnce(); }
    void DeliveryForge.Evidence.IOwnedFailureCleanup.BeginFailureCleanup() => BeginFailureCleanup();
    void DeliveryForge.Evidence.IOwnedBudgetUnknownRetention.PreserveUnknown() => RetainUnknown();
    internal void RetainUnknown() { lock (gate) retainedUnknown = true; }
    internal void RetainBridgeUnknown(HostBridgeFrame frame)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ReferenceEquals(frame.Lifetime, this)) throw new ArgumentException("Bridge obligation belongs to a different original.", nameof(frame));
            if (!retainedBridgeFrames.Contains(frame) && retainedBridgeFrames.Count >= 128) throw new IOException("Finite original bridge obligations exhausted before operation entry.");
            retainedBridgeFrames.Add(frame);
        }
    }
    internal void ReleaseSettledBridge(HostBridgeFrame frame)
    {
        lock (gate)
        {
            if (!ReferenceEquals(frame.Lifetime, this)) throw new ArgumentException("Bridge settlement belongs to a different original.", nameof(frame));
            retainedBridgeFrames.Remove(frame);
            DisposeWhenKnown();
        }
    }
    void DisposeWhenKnown()
    {
        if (disposeRequested && !disposed && !retainedUnknown && retainedBridgeFrames.Count == 0) { linked.Dispose(); disposed = true; }
    }
    public void Dispose() { lock (gate) { disposeRequested = true; DisposeWhenKnown(); } }
}
public static class BoundedFiles
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 48, WriteIndented = true, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    public static string Plain(string path, bool directory = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ToolUsageException("An explicit absolute path is required.");
        var full = Path.GetFullPath(path); var cursor = directory ? full : Path.GetDirectoryName(full)!;
        while (cursor is not null) { if (File.Exists(cursor) || Directory.Exists(cursor)) { var a = File.GetAttributes(cursor); if ((a & FileAttributes.ReparsePoint) != 0 || (a & FileAttributes.Directory) == 0) throw new IOException("Non-plain path ancestor."); } cursor = Path.GetDirectoryName(cursor); }
        if (File.Exists(full) || Directory.Exists(full)) { var a = File.GetAttributes(full); if ((a & FileAttributes.ReparsePoint) != 0 || ((a & FileAttributes.Directory) != 0) != directory) throw new IOException("Unexpected path type/reparse entry."); }
        return full;
    }
    internal sealed class FileFrame
    {
        internal required string Path; internal required ToolLifetime Original; internal FileStream? Stream;
        internal bool AcquisitionEntered, AcquisitionReturned, CloseEntered, CloseReturned, CleanupRequested;
        internal Exception? Primary, CloseFailure, Post; internal byte[]? Bytes;
        internal bool Unknown => AcquisitionEntered && !AcquisitionReturned || Stream is not null && !CloseReturned;
    }
    sealed class FileCredits { internal readonly object Gate = new(); internal int Operations; internal long Reads, Writes; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolLifetime, FileCredits> Credits = new();
    static readonly List<FileFrame> UnknownFiles = [];
    // Approved platform byte policy; ALL Linux originals use two GiB. Other
    // platforms keep one GiB. The existing per-original counters never reset.
    static long MaximumOriginalReadBytes => OperatingSystem.IsLinux() ? 2147483648L : 1073741824L;
    static void Credit(ToolLifetime life, long bytes, bool write)
    {
        life.Check(); var credit = Credits.GetValue(life, _ => new());
        lock (credit.Gate)
        {
            if (credit.Operations >= 4096 || bytes < 0)
            {
                var error = new IOException("Original finite file-operation credit exhausted before effect.");
                life.RecordFirstCaught("original-file-operation-credit", error); throw error;
            }
            var maximum = write ? 268435456L : MaximumOriginalReadBytes; var used = write ? credit.Writes : credit.Reads;
            if (bytes > maximum - used)
            {
                var error = new IOException("Original cumulative complete file-byte credit exhausted before allocation/effect.");
                life.RecordFirstCaught(write ? "original-file-write-credit" : "original-file-read-credit", error); throw error;
            }
            credit.Operations++; if (write) credit.Writes += bytes; else credit.Reads += bytes;
        }
        life.Check();
    }
    // Admission only: inspect this SAME existing credit, without reserving,
    // refunding, increasing or creating a second read allowance.
    internal static void RequireReadCapacity(ToolLifetime life, int operations, long bytes)
    {
        life.Check();
        if (operations < 0 || bytes < 0) throw new ToolUsageException("Finite declared read requirement required.");
        var credit = Credits.GetValue(life, _ => new());
        lock (credit.Gate)
            if (operations > 4096 - credit.Operations || bytes > MaximumOriginalReadBytes - credit.Reads)
                throw new ToolUsageException("Selected runtime-trust reads cannot fit the remaining SAME original file credit; no host is dispatched.");
        life.Check();
    }
    static void CloseFile(FileFrame frame)
    {
        var life = frame.Original; var cleanup = frame.Primary is not null || frame.CleanupRequested;
        if (frame.Stream is not null)
        {
            try
            {
                if (!cleanup) { try { life.Check(); } catch (Exception admission) { frame.CloseFailure = admission; cleanup = true; } }
                if (cleanup) { life.BeginFailureCleanup(); _ = life.CleanupRemaining; }
                frame.CloseEntered = true; frame.Stream.Dispose(); frame.CloseReturned = true;
            }
            catch (Exception error) { frame.CloseFailure = frame.CloseFailure is null ? error : new AggregateException(frame.CloseFailure, error); }
        }
        try { if (cleanup || frame.CloseFailure is not null) { life.BeginFailureCleanup(); _ = life.CleanupRemaining; } else life.Check(); }
        catch (Exception error) { frame.Post = error; }
        if (frame.Unknown) { life.RetainUnknown(); lock (UnknownFiles) UnknownFiles.Add(frame); }
        var failures = new List<Exception>(); if (frame.Primary is not null) failures.Add(frame.Primary); if (frame.CloseFailure is not null) failures.Add(frame.CloseFailure); if (frame.Post is not null) failures.Add(frame.Post);
        if (failures.Count > 1) throw new AggregateException("Actual complete file operation, first disposition and independent SAME original post.", failures);
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
    }
    public static byte[] Read(string path, ToolLifetime life, int maximum = 16 * 1024 * 1024)
    {
        life.Check(); if (maximum <= 0 || maximum > 268435456) throw new ArgumentOutOfRangeException(nameof(maximum)); path = Plain(path); life.Check();
        // Missing input is refused before resource construction. A racing/failed
        // entered constructor still remains explicitly UNKNOWN; no hidden retry.
        var exists = File.Exists(path); life.Check(); if (!exists) throw new IOException("Required plain input file is absent.");
        var frame = new FileFrame { Path = path, Original = life };
        try
        {
            Credit(life, 0, false); frame.AcquisitionEntered = true; frame.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); frame.AcquisitionReturned = true; life.Check();
            var length = frame.Stream.Length; life.Check(); if (length < 0 || length > maximum) throw new IOException("Input exceeds bounded file profile.");
            Credit(life, length + 1, false); frame.Bytes = new byte[(int)length]; life.Check(); frame.Stream.ReadExactly(frame.Bytes); life.Check(); var eof = frame.Stream.ReadByte(); life.Check(); var after = frame.Stream.Length; life.Check();
            if (eof != -1 || after != length) throw new IOException("Input changed complete length/EOF.");
        }
        catch (Exception error) { frame.Primary = error; }
        CloseFile(frame); return frame.Bytes ?? throw new IOException("Complete actual file result missing.");
    }
    internal const long MaximumNativeImageBytes = 512L * 1024 * 1024;
    // Native images have a separate finite extent profile. Retain the existing
    // original read credit and first-close protocol; never allocate the image.
    internal static void VerifyNativeImage(AdmissionFile pin, ToolLifetime life, CancellationToken token = default)
        => _ = VerifyNativeImageCore(pin, life, token, false);

    // Reserve BOTH the gate's complete copy and the parent's complete sealed-fd
    // verification against the SAME original cumulative file credit before birth.
    // This does not grant image/scope/reviewer authority.
    internal static void ReserveExactLinuxImageReads(AdmissionFile pin, ToolLifetime life)
    {
        life.Check(); if (pin.Bytes < 64 || pin.Bytes > DeliveryForge.Evidence.OwnedDuplexProcess.MaximumExactLinuxImageBytes) throw new ToolUsageException("Exact Linux ELF image must fit the selected finite exact-image profile and original kernel cleanup reserve.");
        Credit(life, checked(2 * (pin.Bytes + 1)), false);
    }
    // Windows sharing denies writes and deletion for the entire retained native
    // phase. The hash and every fresh volume/full-file-ID check use this SAME
    // original held file; public pin DATA cannot manufacture such an owner.
    internal sealed class RetainedNativeImage
    {
        readonly FileFrame frame; readonly AdmissionFile pin;
        internal RetainedNativeImage(FileFrame actual, AdmissionFile selected) { frame = actual; pin = selected; }
        internal bool Unknown => frame.AcquisitionEntered && !frame.AcquisitionReturned || frame.CloseEntered && !frame.CloseReturned;
        internal bool IsClosed => frame.CloseReturned;
        internal void Check(CancellationToken token = default)
        {
            frame.Original.Check(); token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows() || frame.Stream is null || frame.CloseEntered || frame.Stream.SafeFileHandle.IsClosed)
                throw new IOException("Original retained Windows image handle is unavailable.");
            _ = Plain(pin.Path);
            var identity = DeliveryForge.Execution.Git.WriterNamespaceIdentityReader.Observe(frame.Stream.SafeFileHandle, pin.Path);
            frame.Original.Check(); token.ThrowIfCancellationRequested();
            if (identity.Outcome != DeliveryForge.Execution.Git.WriterNamespaceIdentityOutcome.Match || frame.Stream.Length != pin.Bytes)
                throw new IOException("Original held native image no longer matches its exact plain namespace and extent.");
            frame.Original.Check();
        }
        internal void Close(bool cleanup)
        {
            if (frame.CloseReturned) return;
            frame.CleanupRequested |= cleanup; CloseFile(frame);
        }
        internal void RetainUnknown()
        {
            frame.Original.RetainUnknown();
            lock (UnknownFiles) if (!UnknownFiles.Contains(frame)) UnknownFiles.Add(frame);
        }
    }
    internal static RetainedNativeImage RetainVerifiedNativeImage(AdmissionFile pin, ToolLifetime life, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Retained deny-write/delete image proof is Windows-only.");
        var frame = VerifyNativeImageCore(pin, life, token, true);
        var held = new RetainedNativeImage(frame, pin);
        try { held.Check(token); return held; }
        catch (Exception failure) { frame.Primary = failure; CloseFile(frame); throw; }
    }
    static FileFrame VerifyNativeImageCore(AdmissionFile pin, ToolLifetime life, CancellationToken token, bool retain)
    {
        life.Check(); token.ThrowIfCancellationRequested();
        if (pin is null || pin.Bytes is < 1 or > MaximumNativeImageBytes || pin.Sha256 is not { Length: 64 } ||
            pin.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ToolUsageException("Finite exact lowercase native image pin required.");
        var path = Plain(pin.Path); life.Check();
        if (!File.Exists(path)) throw new IOException("Required plain native image is absent.");
        var frame = new FileFrame { Path = path, Original = life };
        try
        {
            Credit(life, 0, false); frame.AcquisitionEntered = true;
            frame.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            frame.AcquisitionReturned = true; life.Check();
            var length = frame.Stream.Length; life.Check();
            var written = File.GetLastWriteTimeUtc(path); life.Check();
            if (length != pin.Bytes || length > MaximumNativeImageBytes)
                throw new IOException("Native image extent differs from its finite selected pin.");
            Credit(life, length + 1, false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long read = 0;
            while (read < length)
            {
                life.Check(); token.ThrowIfCancellationRequested();
                var count = frame.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - read));
                life.Check();
                if (count == 0) throw new IOException("Native image ended before its complete selected extent.");
                hash.AppendData(buffer, 0, count); read += count; life.Check();
            }
            var eof = frame.Stream.ReadByte(); life.Check();
            var after = frame.Stream.Length; life.Check(); _ = Plain(path); life.Check();
            var currentLength = new FileInfo(path).Length; life.Check();
            var currentWritten = File.GetLastWriteTimeUtc(path); life.Check();
            if (eof != -1 || after != length || currentLength != length || currentWritten != written)
                throw new IOException("Native image changed complete extent, EOF or namespace metadata.");
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset()); life.Check(); token.ThrowIfCancellationRequested();
            if (actual != pin.Sha256) throw new IOException("Complete selected native image SHA256 differs.");
        }
        catch (Exception error) { frame.Primary = error; }
        if (!retain || frame.Primary is not null) CloseFile(frame);
        return frame;
    }
    public static T ReadJson<T>(string path, ToolLifetime life, int maximum = 16 * 1024 * 1024)
    {
        var bytes = Read(path, life, maximum); life.Check(); _ = CanonicalJson.Canonicalize(bytes); life.Check(); var value = JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new IOException("JSON value is null."); life.Check(); return value;
    }
    public static void WriteNew(string path, ReadOnlySpan<byte> bytes, ToolLifetime life)
    {
        life.Check(); path = Plain(path); life.Check(); if (bytes.Length > 16 * 1024 * 1024) throw new IOException("Output exceeds bounded file profile.");
        var exists = File.Exists(path); life.Check(); if (exists) throw new IOException("CreateNew target already exists; no construction entered.");
        Credit(life, bytes.Length, true); var frame = new FileFrame { Path = path, Original = life };
        try
        {
            life.Check(); frame.AcquisitionEntered = true; frame.Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); frame.AcquisitionReturned = true; life.Check();
            frame.Stream.Write(bytes); life.Check(); frame.Stream.Flush(true); life.Check();
        }
        catch (Exception error) { frame.Primary = error; }
        // Partial CreateNew outputs stay as failed evidence; never rollback,
        // reopen, truncate or retry an entered actual output construction.
        CloseFile(frame);
    }
    public static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static void Separate(string first, string second)
    {
        var a = SeparationIdentity(first).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var b = SeparationIdentity(second).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (a.StartsWith(b, comparison) || b.StartsWith(a, comparison)) throw new ToolUsageException("Result/replay/worktree destinations overlap.");
    }
    static string SeparationIdentity(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows()) return full;
        if (full.Length > 32767) throw new ToolUsageException("Separation path exceeds the Windows path bound.");
        var suffix = new Stack<string>(); var current = full;
        // Resolve the actual existing entry, including a file alias. Missing
        // output components stay absent; only their bounded suffix is appended.
        for (var depth = 0; depth < 128; depth++)
        {
            FileAttributes? attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { attributes = null; }
            catch (DirectoryNotFoundException) { attributes = null; }
            if (attributes is { } actual)
            {
                var directory = (actual & FileAttributes.Directory) != 0;
                if (suffix.Count != 0 && !directory) throw new IOException("Separation ancestor is not a directory.");
                _ = Plain(current, directory);
                // Reuse the existing production handle resolver and its exact
                // disposal; no process, retry owner or file-byte credit is added.
                var resolved = WorktreeManager.WindowsFinalPath(current);
                _ = Plain(resolved, directory);
                while (suffix.TryPop(out var component)) resolved = Path.Combine(resolved, component);
                return resolved;
            }
            var leaf = Path.GetFileName(current);
            if (leaf.Length == 0 || leaf.EndsWith('.') || leaf.EndsWith(' '))
                throw new ToolUsageException("Unresolved Windows output component is ambiguous.");
            suffix.Push(leaf);
            current = Path.GetDirectoryName(current) ?? throw new ToolUsageException("Existing separation ancestor required.");
        }
        throw new ToolUsageException("Separation ancestry exceeds the finite path profile.");
    }
}
