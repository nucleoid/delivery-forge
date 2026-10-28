using System.Collections.Concurrent;
using System.Diagnostics;
namespace DeliveryForge.Evidence;

public sealed record DuplexNativeObservation(bool Known, bool RootExited, uint ActiveProcesses, int? ExitCode, string Detail);
public sealed class DuplexIoObservation
{
    internal DuplexIoObservation(string kind, byte[]? buffer) { Kind = kind; OriginalBuffer = buffer; }
    public string Kind { get; }
    public bool Entered { get; internal set; }
    public bool InvocationReturned { get; internal set; }
    public Task? ActualTask { get; internal set; }
    internal byte[]? OriginalBuffer { get; }
    public object? ActualResult { get; internal set; }
    public Exception? Primary { get; internal set; }
    public Exception? Post { get; internal set; }
    public bool Unknown => Entered && (!InvocationReturned || ActualTask is null || !ActualTask.IsCompleted);
}
public sealed record DuplexRetirement(bool KnownClosed, bool RootExited, uint ActiveProcesses, bool OutputEof, bool ErrorEof, bool InputCloseReturned, bool DispositionReturned, int? ExitCode, IReadOnlyList<string> Failures);

// One extension of the existing creation owners. No PID/DTO adoption, detached
// process, shell/model fallback, auth mutation or automatic restart is exposed.
public sealed class OwnedDuplexProcess
{
    // DATA profile bound only; this constant grants no scope or native authority.
    public const long MaximumExactLinuxImageBytes = 296885960L;
    static readonly ConcurrentDictionary<OwnedDuplexProcess, byte> Retained = new();
    readonly IOwnedDuplexBudget budget; readonly bool commandCapture; readonly List<DuplexIoObservation> io = []; readonly object gate = new();
    Stream? input, output, error; Action<bool>? closeInput; readonly int errorLimit; bool errorQuotaExceeded; Func<bool, DuplexNativeObservation>? observe; Action<bool>? terminate; Func<bool, bool>? dispose;
    DuplexNativeObservation lastObservation = new(false, false, uint.MaxValue, null, "No actual native observation returned.");
    Func<bool>? creationOwnedScope, creationOwnedExactImage;
    // Read-only observation of this retained owner; no DTO, adoption or issuer.
    public bool HasCreationOwnedExactLinuxImage => creationOwnedExactImage?.Invoke() == true;
    internal bool HasCreationOwnedLinuxScope => creationOwnedScope?.Invoke() == true;
    internal void AttachScopeProof(Func<bool> actualProof, Func<bool>? actualExactImage = null) { if (creationOwnedScope is not null) throw new InvalidOperationException("Original scope proof already attached."); creationOwnedScope = actualProof; creationOwnedExactImage = actualExactImage; }
    Func<IReadOnlyDictionary<string, ulong?>>? leafCounters;
    internal void AttachLeafCounters(Func<IReadOnlyDictionary<string, ulong?>> capture) { if (leafCounters is not null) throw new IOException("Original leaf observer already attached."); leafCounters = capture; }
    internal IReadOnlyDictionary<string, ulong?> CaptureLeafCounters()
    {
        lock (gate)
        {
            Check(false); if (retirement is not null || !HasCreationOwnedLinuxScope || leafCounters is null) throw new IOException("Live actual Linux owner required for leaf diagnostic.");
            try { var result = leafCounters(); Check(false); return result; }
            catch (Exception failure) { RecordOwnerFailure(failure); throw; }
        }
    }
    internal static IReadOnlyList<string> LeafCounterNames { get; } = Array.AsReadOnly(new[] { "pids.current", "pids.max", "pids.events", "memory.current", "memory.max", "memory.events" });
    internal static IReadOnlyDictionary<string, ulong?> ParseLeafCounters(IReadOnlyDictionary<string, byte[]> raw)
    {
        if (raw.Count != 6 || LeafCounterNames.Any(name => !raw.ContainsKey(name))) throw new IOException("Complete six-file leaf diagnostic required.");
        var result = new Dictionary<string, ulong?>(StringComparer.Ordinal);
        foreach (var name in LeafCounterNames)
        {
            var bytes = raw[name]; var events = name.EndsWith(".events", StringComparison.Ordinal);
            if (bytes is null || bytes.Length is 0 || bytes.Length > (events ? 1024 : 256) || bytes.Any(x => x > 127)) throw new IOException("Malformed bounded leaf counter bytes.");
            var text = System.Text.Encoding.ASCII.GetString(bytes); if (!text.EndsWith('\n')) throw new IOException("Complete newline-terminated leaf counter required.");
            var lines = text[..^1].Split('\n');
            if (!events)
            {
                if (lines.Length != 1) throw new IOException("One leaf scalar required.");
                // null here means the explicit kernel limit "max"; unavailable capture is separate.
                result.Add(name, name.EndsWith(".max", StringComparison.Ordinal) && lines[0] == "max" ? null : Number(lines[0])); continue;
            }
            var required = name == "pids.events" ? new[] { "max" } : new[] { "low", "high", "max", "oom", "oom_kill" };
            var allowed = name == "pids.events" ? required : [.. required, "oom_group_kill"];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var cells = line.Split(' ');
                if (cells.Length != 2 || cells[0].Length == 0 || cells[0].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or '-')) || !seen.Add(cells[0])) throw new IOException("Malformed or duplicate leaf event.");
                var value = Number(cells[1]);
                if (allowed.Contains(cells[0], StringComparer.Ordinal)) result.Add(name + "." + cells[0], value);
                // Future well-formed keys are validated, not projected or treated as zero.
            }
            if (required.Any(key => !result.ContainsKey(name + "." + key))) throw new IOException("Required leaf event unavailable; no zero substitution.");
        }
        return result;
        static ulong Number(string text) => ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : throw new IOException("Unsigned finite leaf counter required.");
    }
    object? actualOwner; bool inputCloseEntered, inputCloseReturned, outputEof, errorEof, disposeEntered, disposeReturned;
    bool opening = true, creationUnknown; long bytesRead, bytesWritten; int readerBusy, writerBusy; Task? errorDrain, outputConsumer; Task<DuplexRetirement>? retirement;
    readonly List<Exception> failures = []; readonly MemoryStream errorBytes = new();
    public bool HasRetainedOriginalOwner => actualOwner is not null;
    public Exception? StartupFailure { get; private set; }
    public Task<DuplexRetirement>? OriginalRetirementTask { get { lock (gate) return retirement; } }
    public Task? OriginalErrorDrainTask => errorDrain; // actual task reference; no completion/control authority.
    public IReadOnlyList<DuplexIoObservation> ActualIo { get { lock (gate) return io.ToArray(); } }
    public bool HasUnknownIo => ActualIo.Any(x => x.Unknown) || creationUnknown;
    public Stream Input { get; }
    public Stream Output { get; }
    public byte[] StandardError { get { lock (errorBytes) return errorBytes.ToArray(); } }
    OwnedDuplexProcess(IOwnedDuplexBudget budget, int errorLimit, bool commandCapture) { this.budget = budget; this.errorLimit = errorLimit; this.commandCapture = commandCapture; Input = new Facade(this, false); Output = new Facade(this, true); Retained.TryAdd(this, 0); }
    public static OwnedDuplexProcess Open(CommandInvocation invocation, IOwnedDuplexBudget original, LinuxScopeSelection? linuxScope = null) => Open(invocation, original, linuxScope, null);
    public static OwnedDuplexProcess Open(CommandInvocation invocation, IOwnedDuplexBudget original, LinuxScopeSelection? linuxScope, LinuxScopeImage? exactNativeImage) => OpenCore(invocation, original, linuxScope, 1048576, commandCapture: false, exactNativeImage);
    // Only the ordinary capture consumer treats a fully quiescent child exit as
    // command outcome DATA. Public duplex workers/reviewers retain failure semantics.
    internal static OwnedDuplexProcess OpenCapture(CommandInvocation invocation, IOwnedDuplexBudget original, LinuxScopeSelection? linuxScope, int quota, LinuxScopeImage? exactNativeImage = null) => OpenCore(invocation, original, linuxScope, quota, commandCapture: true, exactNativeImage);
    static OwnedDuplexProcess OpenCore(CommandInvocation invocation, IOwnedDuplexBudget original, LinuxScopeSelection? linuxScope, int errorLimit, bool commandCapture, LinuxScopeImage? exactNativeImage)
    {
        ArgumentNullException.ThrowIfNull(original); original.CheckWork();
        if (exactNativeImage is not null && (!OperatingSystem.IsLinux() || linuxScope is null)) throw new PlatformNotSupportedException("Exact native ELF execution requires the actual selected Linux scope; no pathname fallback.");
        // Every public exact-mode entrant shares the kernel owner's EXISTING
        // 16MiB observation quota, with cleanup reserved inside that ceiling.
        // Refuse a known impossible extent before birth;
        // the actual kernel owner still debits its remaining quota before open.
        if (exactNativeImage is not null && (exactNativeImage.Bytes < 64 || exactNativeImage.Bytes > MaximumExactLinuxImageBytes)) throw new IOException("Exact native image cannot fit the existing Linux observation byte quota.");
        if (original.WholeLimit <= TimeSpan.Zero || original.WholeLimit > TimeSpan.FromSeconds(1200) || original.WorkLimit <= TimeSpan.Zero || original.WorkLimit > TimeSpan.FromSeconds(1100) || original.WorkLimit >= original.WholeLimit || original.OriginalStarted <= 0 || Stopwatch.GetTimestamp() < original.OriginalStarted) throw new ArgumentOutOfRangeException(nameof(original));
        if (!Path.IsPathFullyQualified(invocation.FileName) || invocation.Arguments.Count > 64 || invocation.Arguments.Any(x => x.Length > 8192 || x.Contains('\0'))) throw new ArgumentException("Exact finite explicit duplex image/argv required.");
        if (errorLimit is <= 0 or > 16777216) throw new ArgumentOutOfRangeException(nameof(errorLimit)); var session = new OwnedDuplexProcess(original, errorLimit, commandCapture);
        try
        {
            if (OperatingSystem.IsWindows()) WindowsCommandLifetime.OpenDuplex(invocation, original, session);
            else if (OperatingSystem.IsLinux()) BoundedProcessCapture.OpenDuplex(invocation, original, session, linuxScope, exactNativeImage);
            else throw new PlatformNotSupportedException("Duplex owner supports Linux/Windows only.");
            session.opening = false; original.CheckWork(); session.errorDrain = session.DrainErrorAsync();
        }
        catch (Exception failure)
        {
            session.StartupFailure = failure; session.failures.Add(failure); session.opening = false;
            // Linux may have real attached stderr even when its birth barrier
            // refused. Retain the existing bounded reader under SAME failure
            // cleanup; do not replace the creator failure or invent closure.
            if (OperatingSystem.IsLinux() && session.error is not null && session.errorDrain is null)
            {
                try
                {
                    if (original is IOwnedFailureCleanup cleanup) cleanup.BeginFailureCleanup();
                    session.errorDrain = session.DrainErrorAsync(cleanup: true);
                }
                catch (Exception observationFailure) { session.failures.Add(observationFailure); }
            }
        }
        return session; // Includes failed creator frame; callers must retire it, never retry.
    }
    internal void AttachOwner(object owner, Func<bool, DuplexNativeObservation> observation, Action<bool> stop, Func<bool, bool> disposition) { actualOwner = owner; observe = observation; terminate = stop; dispose = disposition; }
    internal void AttachStreams(Stream stdin, Stream stdout, Stream stderr, Action<bool>? closeOriginalInput = null) { input = stdin; output = stdout; error = stderr; closeInput = closeOriginalInput; }
    internal void RecordOwnerFailure(Exception failure) { lock (failures) if (!failures.Contains(failure)) failures.Add(failure); }
    // Outgoing observation from THIS retained owner only. It does not adopt a
    // PID, accept a DTO or grant stop/disposition authority.
    public DuplexNativeObservation ObserveActualNative() => ObserveOriginal(false);
    internal DuplexNativeObservation ObserveNativeForCapture() => ObserveOriginal(false);
    DuplexNativeObservation ObserveOriginal(bool cleanup) { Check(cleanup); var actual = observe?.Invoke(cleanup) ?? new(false, false, uint.MaxValue, null, "No original native owner."); lastObservation = actual; Check(cleanup); return actual; }
    internal bool ErrorQuotaExceeded => errorQuotaExceeded;
    // An actual task reference is borrowed, never serialized signaling authority.
    public void AttachOriginalOutputConsumer(Task actual) { ArgumentNullException.ThrowIfNull(actual); lock (gate) { if (retirement is not null || outputConsumer is not null) throw new IOException("Original output consumer already bound or retirement entered."); outputConsumer = actual; } }
    internal void CloseInputForCapture(bool cleanup = false) { Check(cleanup); if (input is null || inputCloseEntered) return; if (Volatile.Read(ref writerBusy) != 0 || ActualIo.Any(x => x.Kind.StartsWith("stdin", StringComparison.Ordinal) && x.Unknown)) throw new IOException("Original stdin operation unresolved."); inputCloseEntered = true; try { if (closeInput is null) input.Dispose(); else closeInput(cleanup); inputCloseReturned = true; } catch (Exception failure) { RecordOwnerFailure(failure); throw; } Check(cleanup); }
    internal void MarkCreationUnknown() => creationUnknown = true;
    void Check(bool cleanup) { if (cleanup) _ = budget.CleanupRemaining; else budget.CheckWork(); }
    DuplexIoObservation Admit(string kind, byte[]? buffer) { lock (gate) { if (io.Count >= 16384) throw new IOException("Original duplex IO ledger exhausted."); var row = new DuplexIoObservation(kind, buffer) { Entered = true }; io.Add(row); return row; } }
    async Task<T> Invoke<T>(string kind, byte[]? buffer, Func<Task<T>> call, bool cleanup)
    {
        Check(cleanup); var row = Admit(kind, buffer); T result = default!; Exception? primary = null; Exception? post = null;
        try { var actual = call(); row.ActualTask = actual; row.InvocationReturned = true; if (actual is null) throw new IOException("Duplex IO returned no Task."); var left = cleanup ? budget.CleanupRemaining : budget.WorkLimit - Stopwatch.GetElapsedTime(budget.OriginalStarted); if (left <= TimeSpan.Zero) throw new TimeoutException("Original duplex endpoint expired."); result = await actual.WaitAsync(left).ConfigureAwait(false); row.ActualResult = result; if (result is int count && count == 0) { if (kind == "stdout-read") outputEof = true; else if (kind == "stderr-read") errorEof = true; } }
        catch (Exception failure) { primary = failure; row.Primary = failure; lock (failures) failures.Add(failure); }
        try { Check(cleanup); } catch (Exception failure) { post = failure; row.Post = failure; lock (failures) failures.Add(failure); }
        if (primary is not null && post is not null) throw new AggregateException("Actual duplex IO and independent endpoint post.", primary, post);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw(); return result;
    }
    async Task InvokeTask(string kind, byte[]? buffer, Func<Task> call, bool cleanup)
    {
        Check(cleanup); var row = Admit(kind, buffer); Exception? primary = null; Exception? post = null;
        try { var actual = call(); row.ActualTask = actual; row.InvocationReturned = true; if (actual is null) throw new IOException("Duplex IO returned no Task."); var left = cleanup ? budget.CleanupRemaining : budget.WorkLimit - Stopwatch.GetElapsedTime(budget.OriginalStarted); if (left <= TimeSpan.Zero) throw new TimeoutException("Original duplex endpoint expired."); await actual.WaitAsync(left).ConfigureAwait(false); }
        catch (Exception e) { primary = e; row.Primary = e; lock (failures) failures.Add(e); }
        try { Check(cleanup); } catch (Exception e) { post = e; row.Post = e; lock (failures) failures.Add(e); }
        if (primary is not null && post is not null) throw new AggregateException("Actual duplex IO and independent post.", primary, post); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
    }
    async Task<int> Read(Stream stream, byte[] target, int offset, int count, bool stderr, bool cleanup, CancellationToken token)
    {
        Check(cleanup); if (count < 0 || count > 65536 || offset < 0 || offset > target.Length - count) throw new ArgumentOutOfRangeException(nameof(count)); if (count == 0) return 0; var original = new byte[count];
        var actual = await Invoke(stderr ? "stderr-read" : "stdout-read", original, () => stream.ReadAsync(original, 0, count, token), cleanup).ConfigureAwait(false);
        lock (gate) { if (bytesRead > 32L * 1024 * 1024 - actual) throw new IOException("Complete duplex read budget exceeded."); bytesRead += actual; }
        if (actual == 0) { if (stderr) errorEof = true; else outputEof = true; } else Buffer.BlockCopy(original, 0, target, offset, actual); return actual;
    }
    async Task<int> ReadOutput(byte[] target, int offset, int count, CancellationToken token, bool cleanup = false)
    {
        if (Interlocked.CompareExchange(ref readerBusy, 1, 0) != 0) throw new IOException("One original stdout reader only.");
        try { return await Read(output ?? throw new IOException("Original stdout unavailable."), target, offset, count, false, cleanup, token).ConfigureAwait(false); } finally { Volatile.Write(ref readerBusy, 0); }
    }
    async Task WriteInput(byte[] source, int offset, int count, CancellationToken token)
    {
        budget.CheckWork(); if (count < 0 || count > 65536 || offset < 0 || offset > source.Length - count) throw new ArgumentOutOfRangeException(nameof(count)); if (Interlocked.CompareExchange(ref writerBusy, 1, 0) != 0) throw new IOException("One original stdin writer only.");
        try { if (inputCloseEntered) throw new IOException("Original stdin first close already entered."); lock (gate) { if (bytesWritten > 8L * 1024 * 1024 - count) throw new IOException("Duplex write budget exceeded."); bytesWritten += count; } var original = source.AsSpan(offset, count).ToArray(); await InvokeTask("stdin-write", original, () => (input ?? throw new IOException("Original stdin unavailable.")).WriteAsync(original, 0, original.Length, token), false).ConfigureAwait(false); }
        finally { Volatile.Write(ref writerBusy, 0); }
    }
    async Task FlushInput(CancellationToken token) { await InvokeTask("stdin-flush", null, () => (input ?? throw new IOException("Original stdin unavailable.")).FlushAsync(token), false).ConfigureAwait(false); }
    async Task DrainErrorAsync(bool cleanup = false) { try { var buffer = new byte[4096]; while (true) { var n = await Read(error ?? throw new IOException("Original stderr unavailable."), buffer, 0, buffer.Length, true, cleanup, cleanup ? CancellationToken.None : budget.Token).ConfigureAwait(false); if (n == 0) return; lock (errorBytes) { if (errorBytes.Length > errorLimit - n) { errorBytes.Write(buffer, 0, errorLimit - (int)errorBytes.Length); errorQuotaExceeded = true; throw new IOException("Complete stderr profile exceeded; original retained, no truncation acceptance."); } errorBytes.Write(buffer, 0, n); } } } catch (Exception failure) { lock (failures) if (!failures.Contains(failure)) failures.Add(failure); throw; } }
    public Task<DuplexRetirement> RetireAsync(bool failureCleanup = true) { lock (gate) return retirement ??= RetireCoreAsync(failureCleanup); }
    async Task<DuplexRetirement> RetireCoreAsync(bool cleanup)
    {
        void FailureAdmission() { if (budget is IOwnedFailureCleanup actual) actual.BeginFailureCleanup(); }
        void Admission() { if (!cleanup) { lock (failures) cleanup = failures.Count != 0; } if (cleanup) FailureAdmission(); try { Check(cleanup); } catch (Exception failure) when (!cleanup) { lock (failures) failures.Add(failure); cleanup = true; FailureAdmission(); Check(true); } }
        try
        {
            Admission(); // successful retirement remains WORK; actual failure latches parent synchronously.
            if (opening) throw new IOException("Original creator still entered.");
            if (!inputCloseEntered && input is not null && Volatile.Read(ref writerBusy) == 0 && !ActualIo.Any(x => x.Kind.StartsWith("stdin", StringComparison.Ordinal) && x.Unknown)) { Admission(); try { CloseInputForCapture(cleanup); } catch (Exception failure) { lock (failures) if (!failures.Contains(failure)) failures.Add(failure); cleanup = true; } Admission(); }
            var native = ObserveOriginal(cleanup);
            var graceful = Stopwatch.GetTimestamp();
            while (native.Known && (!native.RootExited || native.ActiveProcesses != 0) && inputCloseReturned && Stopwatch.GetElapsedTime(graceful) < TimeSpan.FromSeconds(2)) { Admission(); await Task.Delay(OperatingSystem.IsLinux() ? 100 : 5).ConfigureAwait(false); native = ObserveOriginal(cleanup); }
            // An ordinary command may report a failing gate through a nonzero
            // exit. Only its known already-quiescent root is outcome DATA; IO,
            // deadline, unknown, active-descendant and first-close failures still
            // enter the same original failure cleanup below.
            var settledCommandExit = commandCapture && native.Known && native.RootExited && native.ActiveProcesses == 0 && native.ExitCode is not null;
            if (native.Known && native.RootExited && native.ExitCode != 0 && !settledCommandExit) { cleanup = true; Admission(); }
            if (!native.Known || !native.RootExited || native.ActiveProcesses != 0)
            {
                // Retiring known owned descendants after an exit-zero root is
                // normal enclosing WORK. It cannot renew time or bypass any
                // actual IO/disposition failure, unknown state or explicit Stop.
                if (cleanup || !native.Known || !native.RootExited || native.ExitCode != 0) cleanup = true;
                Admission(); terminate?.Invoke(cleanup); Admission();
            }
            while (true) { Admission(); native = ObserveOriginal(cleanup); if (!native.Known && native.RootExited) { lock (failures) failures.Add(new IOException("Root exited but original group quiescence remains unproved; retain original owner/streams, no dependent disposition.")); Admission(); PreserveOriginalUnknown(); return new(false, true, native.ActiveProcesses, outputEof, errorEof, inputCloseReturned, false, native.ExitCode, FailureText()); } if (native.Known && native.RootExited && native.ActiveProcesses == 0 && !ActualIo.Any(x => x.Unknown) && Volatile.Read(ref readerBusy) == 0 && Volatile.Read(ref writerBusy) == 0 && (errorDrain is null || errorDrain.IsCompleted) && (outputConsumer is null || outputConsumer.IsCompleted)) break; await Task.Delay(TimeSpan.FromMilliseconds(OperatingSystem.IsLinux() ? 100 : 5)).ConfigureAwait(false); }
            if (outputConsumer is not null) { try { await outputConsumer.ConfigureAwait(false); } catch (Exception failure) { RecordOwnerFailure(failure); cleanup = true; Admission(); } }
            // A failed/finished protocol pump may leave unread bytes. Drain only
            // after its ORIGINAL read invocation is terminal and reader lease free.
            if (Volatile.Read(ref readerBusy) == 0 && !outputEof && output is not null) { var buffer = new byte[4096]; while (await ReadOutput(buffer, 0, buffer.Length, CancellationToken.None, cleanup).ConfigureAwait(false) != 0) { } }
            if (errorDrain is not null) { try { await errorDrain.WaitAsync(cleanup ? budget.CleanupRemaining : budget.WorkLimit - Stopwatch.GetElapsedTime(budget.OriginalStarted)).ConfigureAwait(false); } catch (Exception failure) { lock (failures) if (!failures.Contains(failure)) failures.Add(failure); cleanup = true; Admission(); if (!errorDrain.IsCompleted) { PreserveOriginalUnknown(); return new(false, native.RootExited, native.ActiveProcesses, outputEof, errorEof, inputCloseReturned, false, native.ExitCode, FailureText()); } } }
            if (!errorEof && error is not null) { var buffer = new byte[4096]; while (await Read(error, buffer, 0, buffer.Length, true, cleanup, CancellationToken.None).ConfigureAwait(false) != 0) { } }
            var ready = (input is null || inputCloseReturned) && native.Known && native.RootExited && native.ActiveProcesses == 0 && !HasUnknownIo && Volatile.Read(ref readerBusy) == 0 && Volatile.Read(ref writerBusy) == 0 && (output is null || outputEof) && (error is null || errorEof);
            if (ready && !disposeEntered) { Admission(); disposeEntered = true; disposeReturned = dispose?.Invoke(cleanup) == true; Admission(); }
            var known = ready && disposeReturned; if (known) Retained.TryRemove(this, out _); else { cleanup = true; Admission(); PreserveOriginalUnknown(); }
            return new(known, native.RootExited, native.ActiveProcesses, outputEof, errorEof, inputCloseReturned, disposeReturned, native.ExitCode, FailureText());
        }
        catch (Exception failure)
        {
            lock (failures) failures.Add(failure);
            try { FailureAdmission(); } catch (Exception admission) { lock (failures) failures.Add(admission); }
            PreserveOriginalUnknown(); var n = lastObservation; // No fresh native effect after endpoint/primary failure.
            return new(false, n.RootExited, n.ActiveProcesses, outputEof, errorEof, inputCloseReturned, disposeReturned, n.ExitCode, FailureText());
        }
    }
    void PreserveOriginalUnknown() { if (budget is IOwnedBudgetUnknownRetention actual) actual.PreserveUnknown(); }
    string[] FailureText() { lock (failures) return failures.Select(x => x.GetType().FullName + ": " + x.Message).ToArray(); }
    sealed class Facade(OwnedDuplexProcess owner, bool reading) : Stream
    {
        public override bool CanRead => reading; public override bool CanWrite => !reading; public override bool CanSeek => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => reading ? owner.ReadOutput(buffer, offset, count, token) : throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => !reading ? owner.WriteInput(buffer, offset, count, token) : throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken token) => !reading ? owner.FlushInput(token) : Task.CompletedTask;
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
        // Disposing a public facade cannot close private live originals.
        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}
