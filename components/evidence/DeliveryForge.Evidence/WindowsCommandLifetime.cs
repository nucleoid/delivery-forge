using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeliveryForge.Evidence;

// Private birth-bound ownership only. No PID adoption, public handles or uncontained fallback.
// Synchronous kernel calls have OS limits; async polling does not certify hard host containment.
internal sealed class WindowsCommandLifetime : IDisposable
{
    private const uint Signalled = 0, WaitTimeout = 258;
    private readonly KernelHandle _job;
    private KernelHandle? _process;
    private KernelHandle? _thread;
    private SafeFileHandle? _outRead, _outWrite, _errRead, _errWrite, _inRead, _inWrite;
    private StreamReader? _stdout, _stderr;
    private bool _assigned;
    private bool _resumed;
    private string _stage = "create-job";

    private WindowsCommandLifetime()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job.IsInvalid) throw Native("create-job");
    }

    internal static async Task<CommandResult> ExecuteAsync(CommandInvocation invocation, TimeSpan timeout, CancellationToken caller)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows creation-owned lifetime unavailable.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var startedAt = DateTimeOffset.UtcNow; var started = Stopwatch.GetTimestamp();
        if (caller.IsCancellationRequested)
            return new(null, "", "Command cancelled before creation.", false, true, startedAt, DateTimeOffset.UtcNow, true);
        WindowsCommandLifetime? owner = null;
        try
        {
            owner = new();
            owner.Start(invocation, started, timeout, caller);
        }
        catch (StartupStoppedException stopped)
        {
            var settled = false; string? cleanupFailure = null;
            try { settled = owner is not null && await owner.CleanupFailedStartAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (cleanup is Win32Exception or IOException or InvalidOperationException)
            { cleanupFailure = "; cleanup observation failed: " + Bounded(cleanup); }
            finally { owner?.Dispose(); }
            return new(null, "", "Owned Windows startup stopped at " + (owner?._stage ?? "creation") + ": " + stopped.Message + cleanupFailure,
                stopped.TimedOut, stopped.Cancelled, startedAt, DateTimeOffset.UtcNow, settled);
        }
        catch (Exception failure) when (failure is Win32Exception or IOException or ArgumentException or InvalidOperationException)
        {
            var stage = owner?._stage ?? "create-job";
            var settled = false; string? cleanupFailure = null;
            try { settled = owner is not null && await owner.CleanupFailedStartAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (cleanup is Win32Exception or IOException or InvalidOperationException)
            { cleanupFailure = "; cleanup observation failed: " + Bounded(cleanup); }
            finally { owner?.Dispose(); }
            return new(null, "", "Owned Windows launch failed at " + stage + ": " + Bounded(failure) + cleanupFailure, false,
                caller.IsCancellationRequested, startedAt, DateTimeOffset.UtcNow, settled);
        }
        using (owner)
        {
            var stdoutTask = owner._stdout!.ReadToEndAsync();
            var stderrTask = owner._stderr!.ReadToEndAsync();
            var timedOut = false; var cancelled = false; var stateKnown = true;
            var empty = false; var rootExited = false; uint active = uint.MaxValue;
            string? nativeFailure = null;
            while (true)
            {
                if (caller.IsCancellationRequested || Stopwatch.GetElapsedTime(started) >= timeout)
                {
                    cancelled = caller.IsCancellationRequested; timedOut = !cancelled; break;
                }
                if (!owner.Observe(out rootExited, out active, out nativeFailure)) { stateKnown = false; break; }
                empty = active == 0;
                if (empty && rootExited) break;
                await Task.Delay(10).ConfigureAwait(false);
            }
            var killAttempted = !empty || !rootExited || !stateKnown;
            var killSucceeded = false; string? killType = null; string? killDetail = null;
            if (killAttempted)
            {
                killSucceeded = TerminateJobObject(owner._job, unchecked((uint)-1));
                if (!killSucceeded)
                {
                    var failure = Native("terminate-owned-job"); killType = failure.GetType().FullName; killDetail = Bounded(failure);
                }
            }
            var exitWaitCompleted = empty && rootExited && stateKnown;
            if (!exitWaitCompleted)
            {
                var settlementStart = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(settlementStart) < TimeSpan.FromSeconds(5))
                {
                    if (!owner.Observe(out rootExited, out active, out var failure))
                    {
                        stateKnown = false; nativeFailure ??= failure; break;
                    }
                    if (rootExited && active == 0) { empty = true; exitWaitCompleted = true; break; }
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }
            var drained = true;
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { drained = false; }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException) { drained = false; nativeFailure ??= "pipe-drain: " + Bounded(failure); }
            var finalKnown = owner.Observe(out var finalRootExited, out var finalActive, out var finalFailure);
            stateKnown &= finalKnown; nativeFailure ??= finalFailure;
            rootExited = finalRootExited; active = finalActive; empty = finalKnown && active == 0;
            int? exit = null;
            if (rootExited)
            {
                if (GetExitCodeProcess(owner._process!, out var code)) exit = unchecked((int)code);
                else { stateKnown = false; nativeFailure ??= "get-exit-code: " + Marshal.GetLastWin32Error(); }
            }
            var outputObservation = ObserveStream(stdoutTask);
            var errorObservation = ObserveStream(stderrTask);
            var stdout = drained && outputObservation.CompletedSuccessfully ? outputObservation.CompletedText! : "<stdout drain timed out>";
            var stderr = drained && errorObservation.CompletedSuccessfully ? errorObservation.CompletedText! : "<stderr drain timed out>";
            var quiescent = stateKnown && empty && rootExited && exitWaitCompleted && drained &&
                outputObservation.CompletedSuccessfully && errorObservation.CompletedSuccessfully &&
                (!killAttempted || killSucceeded);
            return new(exit, stdout, stderr, timedOut, cancelled, startedAt, DateTimeOffset.UtcNow, quiescent)
            {
                LifecycleDiagnostics = new(killAttempted, killSucceeded, killType, killDetail, false,
                    exitWaitCompleted, rootExited, drained, 5000, outputObservation, errorObservation)
                {
                    OwnedTree = new(owner._assigned && owner._resumed, stateKnown && empty && exitWaitCompleted, active > int.MaxValue ? -1 : (int)active),
                    NativeFailure = nativeFailure
                }
            };
        }
    }

    internal static async Task<BoundedCommandCaptureResult> ExecuteBoundedCaptureAsync(
        CommandInvocation invocation, TimeSpan entireBudget, int maximumBytesPerStream, CancellationToken caller)
    {
        var began = DateTimeOffset.UtcNow; var started = Stopwatch.GetTimestamp();
        var workBudget = BoundedCaptureStream.WorkBudget(entireBudget);
        var stdout = new BoundedCaptureStream(maximumBytesPerStream); var stderr = new BoundedCaptureStream(maximumBytesPerStream);
        WindowsCommandLifetime? owner = null; Task? output = null, error = null;
        var timedOut = false; var cancelled = false; var known = true; var rootExited = false;
        uint active = uint.MaxValue;
        var killAttempted = false; var killSucceeded = false;
        string? failure = null, killType = null;
        try
        {
            owner = new();
            owner.Start(invocation, started, workBudget, caller);
            output = BoundedCaptureStream.Observe(stdout.DrainAsync(owner._stdout!.BaseStream));
            error = BoundedCaptureStream.Observe(stderr.DrainAsync(owner._stderr!.BaseStream));
            while (true)
            {
                if (caller.IsCancellationRequested) { cancelled = true; break; }
                if (stdout.Exceeded || stderr.Exceeded) break;
                if (!BoundedCaptureStream.Within(started, workBudget)) { timedOut = true; break; }
                if (!owner.Observe(out rootExited, out active, out failure)) { known = false; break; }
                if (output.IsFaulted || error.IsFaulted) { known = false; failure = "Owned byte drain failed."; break; }
                if (rootExited && active == 0 && BoundedCaptureStream.Eof(output) && BoundedCaptureStream.Eof(error)) break;
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
        catch (StartupStoppedException stopped) { cancelled = stopped.Cancelled; timedOut = stopped.TimedOut; }
        catch (Exception exception) when (exception is Win32Exception or IOException or ArgumentException or InvalidOperationException)
        { known = false; failure = Bounded(exception); }
        try
        {
            if (owner?._process is not null)
            {
                if (!rootExited || active != 0 || !known)
                {
                    killAttempted = true;
                    killSucceeded = owner._assigned ? TerminateJobObject(owner._job, unchecked((uint)-1)) : TerminateProcess(owner._process, unchecked((uint)-1));
                    if (!killSucceeded) { var native = Native("terminate-owned-byte-capture"); killType = native.GetType().FullName; failure ??= Bounded(native); }
                }
                // Cleanup consumes the original remaining allowance. No appended five-second tail.
                while (BoundedCaptureStream.Within(started, entireBudget))
                {
                    if (!owner.Observe(out rootExited, out active, out var observationFailure))
                    { known = false; failure ??= observationFailure; break; }
                    if (rootExited && active == 0 &&
                        (output is null || BoundedCaptureStream.Eof(output)) && (error is null || BoundedCaptureStream.Eof(error))) break;
                    await Task.Delay(5).ConfigureAwait(false);
                }
                var finalKnown = owner.Observe(out rootExited, out active, out var finalFailure);
                known &= finalKnown; failure ??= finalFailure;
            }
            int? exit = null;
            if (rootExited && owner?._process is not null)
            {
                if (GetExitCodeProcess(owner._process, out var code)) exit = unchecked((int)code);
                else { known = false; failure ??= "Original exit-code observation failed: " + Marshal.GetLastWin32Error(); }
            }
            var outputStatus = output?.Status; var errorStatus = error?.Status;
            var outEof = outputStatus == TaskStatus.RanToCompletion; var errEof = errorStatus == TaskStatus.RanToCompletion;
            var settled = known && rootExited && active == 0;
            var quiescent = settled && outEof && errEof && (!killAttempted || killSucceeded) &&
                BoundedCaptureStream.Within(started, entireBudget);
            var outBytes = stdout.Snapshot(); var errBytes = stderr.Snapshot();
            var command = new CommandResult(exit, Encoding.UTF8.GetString(outBytes), Encoding.UTF8.GetString(errBytes), timedOut,
                cancelled, began, DateTimeOffset.UtcNow, quiescent)
            {
                LifecycleDiagnostics = new(killAttempted, killSucceeded, killType, failure, false,
                    settled, rootExited, outEof && errEof, 0,
                    new(outEof, outputStatus == TaskStatus.Faulted, outputStatus == TaskStatus.Canceled, outEof ? Encoding.UTF8.GetString(outBytes) : null),
                    new(errEof, errorStatus == TaskStatus.Faulted, errorStatus == TaskStatus.Canceled, errEof ? Encoding.UTF8.GetString(errBytes) : null))
                {
                    OwnedTree = new(owner is { _assigned: true, _resumed: true }, settled, active > int.MaxValue ? -1 : (int)active),
                    NativeFailure = failure
                }
            };
            return new(command, outBytes, errBytes, stdout.Exceeded || stderr.Exceeded, rootExited, outEof, errEof);
        }
        finally { owner?.Dispose(); }
    }

    private void Start(CommandInvocation invocation, long originalStarted, TimeSpan originalTimeout, CancellationToken caller)
    {
        _stage = "configure-job";
        var limits = new ExtendedLimits { BasicLimitInformation = new() { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Native(_stage);
        _stage = "create-owned-pipes";
        (_outRead, _outWrite) = Pipe(parentReads: true);
        (_errRead, _errWrite) = Pipe(parentReads: true);
        (_inRead, _inWrite) = Pipe(parentReads: false);
        _stage = "prepare-command";
        var executable = ResolveExecutable(invocation.FileName);
        var commandLine = new StringBuilder(Quote(executable, force: true));
        foreach (var argument in invocation.Arguments)
        {
            commandLine.Append(' ').Append(Quote(argument));
            if (commandLine.Length >= 32767) throw new ArgumentException("Windows command line exceeds32766 characters.");
        }
        if (commandLine.Length >= 32767) throw new ArgumentException("Windows command line exceeds32766 characters.");
        var environment = EnvironmentBlock(invocation.Environment);
        var directory = Path.GetFullPath(invocation.WorkingDirectory);
        using (var attributes = new InheritedHandles(_inRead, _outWrite, _errWrite))
        {
            var startup = new StartupInfoEx
            {
                StartupInfo = new()
                {
                    Cb = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    StandardInput = _inRead.DangerousGetHandle(), StandardOutput = _outWrite.DangerousGetHandle(), StandardError = _errWrite.DangerousGetHandle()
                },
                AttributeList = attributes.Pointer
            };
            _stage = "create-suspended";
            CheckStartupBudget(originalStarted, originalTimeout, caller);
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                0x00000004 | 0x08000000 | 0x00000400 | 0x00080000, environment, directory, ref startup, out var processInfo)) throw Native(_stage);
            _process = new(processInfo.Process); _thread = new(processInfo.Thread);
            _stage = "assign-before-resume";
            if (!AssignProcessToJobObject(_job, _process)) throw Native(_stage);
            _assigned = true;
        }
        // Close every extra child-side endpoint in this parent before execution/EOF observation.
        _outWrite.Dispose(); _outWrite = null; _errWrite.Dispose(); _errWrite = null;
        _inRead.Dispose(); _inRead = null; _inWrite.Dispose(); _inWrite = null;
        _stdout = new(new FileStream(_outRead, FileAccess.Read, 4096, false), Console.OutputEncoding, true);
        _stderr = new(new FileStream(_errRead, FileAccess.Read, 4096, false), Console.OutputEncoding, true);
        _stage = "resume-owned-root";
        CheckStartupBudget(originalStarted, originalTimeout, caller);
        if (ResumeThread(_thread) == uint.MaxValue) throw Native(_stage);
        _resumed = true; _thread.Dispose(); _thread = null;
    }

    private static void CheckStartupBudget(long originalStarted, TimeSpan originalTimeout, CancellationToken caller)
    {
        var cancelled = caller.IsCancellationRequested;
        if (cancelled || Stopwatch.GetElapsedTime(originalStarted) >= originalTimeout)
            throw new StartupStoppedException(cancelled);
    }
    private sealed class StartupStoppedException(bool cancelled)
        : Exception(cancelled ? "Caller cancelled before command execution." : "Original command deadline expired before command execution.")
    {
        public bool Cancelled => cancelled;
        public bool TimedOut => !cancelled;
    }

    private bool Observe(out bool rootExited, out uint active, out string? failure)
    {
        rootExited = false; active = uint.MaxValue; failure = null;
        if (_process is null || _process.IsInvalid) { failure = "original-root-handle unavailable"; return false; }
        var wait = WaitForSingleObject(_process, 0);
        if (wait is not (Signalled or WaitTimeout)) { failure = "original-root-wait: " + Marshal.GetLastWin32Error(); return false; }
        rootExited = wait == Signalled;
        if (!QueryInformationJobObject(_job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero))
        { failure = "owned-job-accounting: " + Marshal.GetLastWin32Error(); return false; }
        active = accounting.ActiveProcesses; return true;
    }
    private async Task<bool> CleanupFailedStartAsync()
    {
        if (_process is null) return true;
        // Before resume no child code can have executed. Signal only the returned original handle/job.
        var requested = _assigned ? TerminateJobObject(_job, unchecked((uint)-1)) : TerminateProcess(_process, unchecked((uint)-1));
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(5))
        {
            if (Observe(out var exited, out var active, out _) && exited && active == 0) return requested;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return false;
    }
    private static (SafeFileHandle Read, SafeFileHandle Write) Pipe(bool parentReads)
    {
        var security = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        if (!CreatePipe(out var read, out var write, ref security, 0)) throw Native("create-pipe");
        if (!SetHandleInformation(parentReads ? read : write, 1, 0))
        { var failure = Native("noninheritable-parent-pipe"); read.Dispose(); write.Dispose(); throw failure; }
        return (read, write);
    }
    private static string ResolveExecutable(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0')) throw new ArgumentException("Invalid executable name.");
        name = name.Trim();
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"') name = name[1..^1];
        if (Path.IsPathFullyQualified(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(name);
        var buffer = new StringBuilder(32768);
        var length = SearchPathW(null, name, ".exe", (uint)buffer.Capacity, buffer, IntPtr.Zero);
        if (length == 0 || length >= buffer.Capacity) throw Native("resolve-executable");
        return buffer.ToString();
    }
    private static string Quote(string value, bool force = false)
    {
        if (value.Contains('\0')) throw new ArgumentException("NUL is not a Windows argument.");
        if (value.Length >= 32767) throw new ArgumentException("Windows argument exceeds32766 characters.");
        if (!force && value.Length != 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string>? additions)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) values[(string)entry.Key] = (string)entry.Value!;
        foreach (var entry in additions ?? new Dictionary<string, string>())
        {
            if (entry.Key.Length == 0 || entry.Key.Contains('=') || entry.Key.Contains('\0') || entry.Value.Contains('\0'))
                throw new ArgumentException("Invalid Windows environment entry.");
            values[entry.Key] = entry.Value;
        }
        var block = string.Join('\0', values.Select(entry => entry.Key + "=" + entry.Value)) + "\0\0";
        if (block.Length > 32767) throw new ArgumentException("Windows environment exceeds32767 characters.");
        return block;
    }
    private static CommandStreamObservation ObserveStream(Task<string> task)
    {
        _ = task.ContinueWith(static fault => { _ = fault.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var status = task.Status;
        return new(status == TaskStatus.RanToCompletion, status == TaskStatus.Faulted, status == TaskStatus.Canceled,
            status == TaskStatus.RanToCompletion ? task.Result : null);
    }
    private static Win32Exception Native(string stage) => new(Marshal.GetLastWin32Error(), stage);
    private static string Bounded(Exception failure)
    {
        var text = failure.GetType().Name + ": " + failure.Message + (failure is Win32Exception native ? " Win32=" + native.NativeErrorCode : "");
        return text.Length <= 2048 ? text : text[..2048] + " [truncated]";
    }
    public void Dispose()
    {
        _job.Dispose(); // kill-on-close targets this private job only; disposal is NOT settlement proof.
        _thread?.Dispose(); _process?.Dispose(); _stdout?.Dispose(); _stderr?.Dispose();
        _outRead?.Dispose(); _outWrite?.Dispose(); _errRead?.Dispose(); _errWrite?.Dispose(); _inRead?.Dispose(); _inWrite?.Dispose();
    }

    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle() : base(true) { }
        public KernelHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    private sealed class InheritedHandles : IDisposable
    {
        private readonly SafeFileHandle[] _handles;
        private readonly bool[] _pinned = new bool[3];
        private IntPtr _values;
        private bool _initialized;
        public IntPtr Pointer { get; private set; }
        public InheritedHandles(params SafeFileHandle[] handles)
        {
            _handles = handles;
            try
            {
                for (var index = 0; index < handles.Length; index++) handles[index].DangerousAddRef(ref _pinned[index]);
                nuint size = 0; InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size == 0 || size > 65536) throw Native("attribute-list-size");
                Pointer = Marshal.AllocHGlobal(checked((int)size));
                if (!InitializeProcThreadAttributeList(Pointer, 1, 0, ref size)) throw Native("attribute-list-init");
                _initialized = true; _values = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                for (var index = 0; index < handles.Length; index++) Marshal.WriteIntPtr(_values, index * IntPtr.Size, handles[index].DangerousGetHandle());
                if (!UpdateProcThreadAttribute(Pointer, 0, (nuint)0x20002, _values, (nuint)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero)) throw Native("stdio-handle-whitelist");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_initialized) { DeleteProcThreadAttributeList(Pointer); _initialized = false; }
            if (Pointer != IntPtr.Zero) { Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; }
            if (_values != IntPtr.Zero) { Marshal.FreeHGlobal(_values); _values = IntPtr.Zero; }
            for (var index = 0; index < _handles.Length; index++) if (_pinned[index]) { _handles[index].DangerousRelease(); _pinned[index] = false; }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public uint Length; public IntPtr SecurityDescriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public uint Cb; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size; public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessUserTime, PerJobUserTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits BasicLimitInformation; public IoCounters IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern KernelHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(KernelHandle job, int infoClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(KernelHandle job, int infoClass, out BasicAccounting information, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(KernelHandle job, KernelHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(KernelHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(KernelHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(KernelHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(KernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string executable, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint SearchPathW(string? path, string name, string? extension, uint length, StringBuilder result, IntPtr filePart);
}
