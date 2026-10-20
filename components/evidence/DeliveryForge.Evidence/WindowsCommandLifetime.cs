using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeliveryForge.Evidence;

// Private birth-bound ownership only. No PID adoption, public handles or uncontained fallback.
// Synchronous kernel calls have OS limits; async polling does not certify hard host containment.
internal sealed partial class WindowsCommandLifetime : IDisposable
{
    private const uint Signalled = 0, WaitTimeout = 258;
    private readonly KernelHandle _job;
    private KernelHandle? _process;
    private KernelHandle? _thread;
    private SafeFileHandle? _outRead, _outWrite, _errRead, _errWrite, _inRead, _inWrite;
    private StreamReader? _stdout, _stderr;
    private FileStream? _duplexInput;
    private bool _creationReturned, _creationSucceeded;
    private bool _assigned;
    private bool _resumed;
    private string _stage = "create-job";
    private bool _creationAttempted;
    private bool _assignmentAttempted;
    private bool _resumeAttempted;
    private bool _readerEndpointsAttempted;
    private BoundedCaptureRetention? _captureRetention;
    private BoundedCaptureStream? _captureStdout, _captureStderr;
    private Task? _captureOutputTask, _captureErrorTask;

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
        CommandInvocation invocation, TimeSpan entireBudget, int maximumBytesPerStream, CancellationToken caller, IOwnedDuplexBudget? enclosing = null)
    {
        var began = DateTimeOffset.UtcNow; var started = Stopwatch.GetTimestamp();
        var workBudget = BoundedCaptureStream.WorkBudget(entireBudget);
        long? cleanupStarted = null;
        var stdout = new BoundedCaptureStream(maximumBytesPerStream); var stderr = new BoundedCaptureStream(maximumBytesPerStream);
        WindowsCommandLifetime? owner = null; Task? output = null, error = null;
        var timedOut = false; var cancelled = false; var known = true; var rootExited = false;
        uint active = uint.MaxValue;
        var killAttempted = false; var killSucceeded = false;
        var cleanupWaitMilliseconds = 0;
        string? failure = null, killType = null;
        string? startupFailureStage = null;
        var retention = new BoundedCaptureRetention();
        retention.Add(stdout); retention.Add(stderr);
        var restrictive = new RestrictiveCaptureBudget(started, entireBudget, workBudget, enclosing, retention);
        try
        {
            if (!restrictive.WorkAvailable) throw new StartupStoppedException(caller.IsCancellationRequested || enclosing?.Token.IsCancellationRequested == true);
            owner = new(); owner._boundedBudget = restrictive;
            retention.Add(owner); // BEFORE Start and every original native resource/task.
            owner._captureRetention = retention; owner._captureStdout = stdout; owner._captureStderr = stderr;
            owner.Start(invocation, started, workBudget, caller);
            output = owner._captureOutputTask!; error = owner._captureErrorTask!;
            while (true)
            {
                if (caller.IsCancellationRequested) { cancelled = true; break; }
                if (stdout.Exceeded || stderr.Exceeded) break;
                if (!restrictive.WorkAvailable) { timedOut = true; break; }
                if (!owner.Observe(out rootExited, out active, out failure)) { known = false; break; }
                if (output.IsFaulted || error.IsFaulted) { known = false; failure = "Owned byte drain failed."; break; }
                if (rootExited && active == 0 && BoundedCaptureStream.Eof(output) && BoundedCaptureStream.Eof(error)) break;
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
        catch (StartupStoppedException stopped) { cancelled = stopped.Cancelled; timedOut = stopped.TimedOut; startupFailureStage = owner?._stage; failure = stopped.Message; retention.Add(stopped); }
        catch (Exception exception) when (exception is Win32Exception or IOException or ArgumentException or InvalidOperationException)
        { known = false; failure = Bounded(exception); startupFailureStage = owner?._stage ?? "create-job"; retention.Add(exception); }
        if (cancelled || timedOut || stdout.Exceeded || stderr.Exceeded || !known || failure is not null || !rootExited || active != 0)
            restrictive.BeginFailureCleanup(); // synchronous actual parent latch BEFORE terminate/first disposition.
        if (owner is not null) { restrictive.BeginCleanup(); cleanupStarted = restrictive.CleanupStarted; }
        output = owner?._captureOutputTask; error = owner?._captureErrorTask;
        {
            if (owner?._process is not null)
            {
                if ((!rootExited || active != 0 || !known) && restrictive.CleanupAvailable)
                {
                    killAttempted = true;
                    killSucceeded = owner._assigned ? TerminateJobObject(owner._job, unchecked((uint)-1)) : TerminateProcess(owner._process, unchecked((uint)-1));
                    if (!killSucceeded) { var native = Native("terminate-owned-byte-capture"); retention.Add(native); restrictive.BeginFailureCleanup(); killType = native.GetType().FullName; failure ??= Bounded(native); }
                }
                // Wait on the SAME birth-bound handle; no scheduler polling or appended tail.
                // This diagnostic is the actual remainder at cleanup admission, not a new grant.
                cleanupWaitMilliseconds = (int)Math.Clamp(Math.Floor(
                    restrictive.CleanupRemaining.TotalMilliseconds), 0, int.MaxValue);
                if (!rootExited && cleanupWaitMilliseconds > 0)
                {
                    var waited = WaitForSingleObject(owner._process, (uint)cleanupWaitMilliseconds);
                    if (waited is not (Signalled or WaitTimeout))
                    {
                        var native = Native("wait-original-byte-capture-root");
                        retention.Add(native); restrictive.BeginFailureCleanup(); known = false; failure ??= Bounded(native);
                    }
                }
                // Each original reader is independently observed under the SAME shrinking
                // allowance. A completed fault or late wait never establishes EOF/closure.
                foreach (var drain in new[] { output, error })
                {
                    if (drain is null) continue;
                    var remaining = restrictive.CleanupRemaining;
                    if (remaining <= TimeSpan.Zero) continue;
                    try
                    {
                        var originalWait = drain.WaitAsync(remaining, CancellationToken.None);
                        retention.Add(originalWait); _ = BoundedCaptureStream.Observe(originalWait);
                        await originalWait.ConfigureAwait(false);
                    }
                    catch (Exception drainFailure)
                    { retention.Add(drainFailure); restrictive.BeginFailureCleanup(); known = false; failure ??= Bounded(drainFailure); }
                }
                // Root signal and pipe EOF do not substitute for current Job emptiness.
                // Descendant accounting may settle later, still only inside the original total.
                while (known && restrictive.CleanupAvailable)
                {
                    if (!owner.Observe(out rootExited, out active, out var observationFailure))
                    { known = false; failure ??= observationFailure; restrictive.BeginFailureCleanup(); break; }
                    if (!rootExited || active == 0) break;
                    var remaining = restrictive.CleanupRemaining;
                    if (remaining <= TimeSpan.Zero) break;
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(5) ? remaining : TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);
                }
                string? finalFailure = null;
                var finalKnown = restrictive.CleanupAvailable && owner.Observe(out rootExited, out active, out finalFailure);
                known &= finalKnown; failure ??= finalFailure; if (!finalKnown) restrictive.BeginFailureCleanup();
            }
            int? exit = null;
            if (rootExited && owner?._process is not null && restrictive.CleanupAvailable)
            {
                if (GetExitCodeProcess(owner._process, out var code)) exit = unchecked((int)code);
                else { known = false; failure ??= "Original exit-code observation failed: " + Marshal.GetLastWin32Error(); restrictive.BeginFailureCleanup(); }
            }
            var outputStatus = output?.Status; var errorStatus = error?.Status;
            var outEof = outputStatus == TaskStatus.RanToCompletion; var errEof = errorStatus == TaskStatus.RanToCompletion;
            var settled = known && rootExited && active == 0;
            var quiescent = settled && outEof && errEof && (!killAttempted || killSucceeded) &&
                restrictive.CleanupAvailable;
            // Never-created handles can be disposed without claiming helper exit/EOF.
            var creationRefused = owner is { _creationReturned: true, _creationSucceeded: false, _process: null };
            var mayDispose = owner is not null && (((!owner._creationAttempted || creationRefused) && output is null && error is null) || quiescent);
            var dispositionKnown = owner is null;
            if (mayDispose)
            {
                dispositionKnown = owner!.DisposeBoundedKnown(started, entireBudget, retention, restrictive);
                if (!dispositionKnown) { quiescent = false; failure ??= "Original bounded resource disposition was not timely and complete."; }
            }
            if (dispositionKnown) retention.ReleaseKnown();
            else if (enclosing is IOwnedBudgetUnknownRetention actual) actual.PreserveUnknown();
            var outBytes = stdout.Snapshot(); var errBytes = stderr.Snapshot();
            var command = new CommandResult(exit, Encoding.UTF8.GetString(outBytes), Encoding.UTF8.GetString(errBytes), timedOut,
                cancelled, began, DateTimeOffset.UtcNow, quiescent)
            {
                LifecycleDiagnostics = new(killAttempted, killSucceeded, killType, failure, false,
                    settled, rootExited, outEof && errEof, cleanupWaitMilliseconds,
                    new(outEof, outputStatus == TaskStatus.Faulted, outputStatus == TaskStatus.Canceled, outEof ? Encoding.UTF8.GetString(outBytes) : null),
                    new(errEof, errorStatus == TaskStatus.Faulted, errorStatus == TaskStatus.Canceled, errEof ? Encoding.UTF8.GetString(errBytes) : null))
                {
                    OwnedTree = new(owner is { _assigned: true, _resumed: true }, settled, active > int.MaxValue ? -1 : (int)active),
                    NativeFailure = failure,
                    Startup = new(
                        creationRefused ? CommandStartupState.Refused : owner?._process is not null ? CommandStartupState.Established : owner?._creationAttempted == true ? CommandStartupState.AttemptedUnknown : CommandStartupState.NotAttempted,
                        creationRefused ? CommandStartupState.NotAttempted : owner?._process is not null ? CommandStartupState.Established : owner?._creationAttempted == true ? CommandStartupState.AttemptedUnknown : CommandStartupState.NotAttempted,
                        owner?._assigned == true ? CommandStartupState.Established : owner?._assignmentAttempted == true ? CommandStartupState.AttemptedUnknown : CommandStartupState.NotAttempted,
                        owner?._resumed == true ? CommandStartupState.Established : owner?._resumeAttempted == true ? CommandStartupState.AttemptedUnknown : CommandStartupState.NotAttempted,
                        owner is { _stdout: not null, _stderr: not null } ? CommandStartupState.Established : owner?._readerEndpointsAttempted == true ? CommandStartupState.AttemptedUnknown : CommandStartupState.NotAttempted,
                        output is not null ? CommandStartupState.Established : CommandStartupState.NotAttempted,
                        error is not null ? CommandStartupState.Established : CommandStartupState.NotAttempted,
                        owner?._stage ?? "create-job", startupFailureStage,
                        Stopwatch.GetElapsedTime(started).TotalSeconds, entireBudget.TotalSeconds, workBudget.TotalSeconds,
                        failure is null ? null : CommandStartupObservation.Clip(failure, 2048))
                }
            };
            var completed = new BoundedCommandCaptureResult(command, outBytes, errBytes, stdout.Exceeded || stderr.Exceeded, rootExited, outEof, errEof,
                new(started, cleanupStarted, Stopwatch.Frequency));
            // Includes snapshot/decoding/startup DATA and result byte copies under the SAME clock.
            return restrictive.CleanupAvailable ? completed : completed.RefuseLateCompletion();
        }
    }

    private RestrictiveCaptureBudget? _boundedBudget;

    private void Start(CommandInvocation invocation, long originalStarted, TimeSpan originalTimeout, CancellationToken caller, bool keepInput = false)
    {
        _stage = "configure-job";
        CheckRestrictiveStartup(originalStarted, originalTimeout, caller);
        var limits = new ExtendedLimits { BasicLimitInformation = new() { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Native(_stage);
        CheckRestrictiveStartup(originalStarted, originalTimeout, caller);
        if (keepInput)
        {
            if (IntPtr.Size != 8 || !GetProcessAffinityMask(GetCurrentProcess(), out var allowed, out _)) throw Native("duplex-parent-affinity");
            nuint mask = 0; for (var bit = 0; bit < 64 && System.Numerics.BitOperations.PopCount((ulong)mask) < 2; bit++) { var value = (nuint)1 << bit; if ((allowed & value) != 0) mask |= value; }
            if (System.Numerics.BitOperations.PopCount((ulong)mask) != 2) throw new IOException("Two actual available CPU bits required.");
            limits.BasicLimitInformation.LimitFlags |= 0x10 | 0x100; limits.BasicLimitInformation.Affinity = mask; var runtimeMemory = 4294967296UL; limits.ProcessMemoryLimit = checked((nuint)runtimeMemory);
            if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Native("duplex-resource-limits");
        }
        _stage = "create-owned-pipes";
        (_outRead, _outWrite) = Pipe(parentReads: true);
        CheckRestrictiveStartup(originalStarted, originalTimeout, caller);
        (_errRead, _errWrite) = Pipe(parentReads: true);
        CheckRestrictiveStartup(originalStarted, originalTimeout, caller);
        (_inRead, _inWrite) = Pipe(parentReads: false);
        CheckRestrictiveStartup(originalStarted, originalTimeout, caller);
        _stage = "prepare-command";
        var executable = ResolveExecutable(invocation.FileName);
        var commandLine = new StringBuilder(Quote(executable, force: true));
        foreach (var argument in invocation.Arguments)
        {
            commandLine.Append(' ').Append(Quote(argument));
            if (commandLine.Length >= 32767) throw new ArgumentException("Windows command line exceeds32766 characters.");
        }
        if (commandLine.Length >= 32767) throw new ArgumentException("Windows command line exceeds32766 characters.");
        var environment = EnvironmentBlock(invocation.Environment, invocation.EnvironmentIsComplete);
        var directory = Path.GetFullPath(invocation.WorkingDirectory);
        using (var attributes = new InheritedHandles(_inRead, _outWrite, _errWrite))
        {
            var startup = new StartupInfoEx
            {
                StartupInfo = new()
                {
                    Cb = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x100,
                    StandardInput = _inRead.DangerousGetHandle(),
                    StandardOutput = _outWrite.DangerousGetHandle(),
                    StandardError = _errWrite.DangerousGetHandle()
                },
                AttributeList = attributes.Pointer
            };
            _stage = "create-suspended";
            CheckStartupBudget(originalStarted, originalTimeout, caller);
            _creationAttempted = true;
            var created = CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                0x00000004 | 0x08000000 | 0x00000400 | 0x00080000, environment, directory, ref startup, out var processInfo);
            _creationReturned = true; _creationSucceeded = created;
            if (!created) throw Native(_stage);
            _process = new(processInfo.Process); _thread = new(processInfo.Thread);
            _stage = "assign-before-resume";
            _assignmentAttempted = true;
            if (!AssignProcessToJobObject(_job, _process)) throw Native(_stage);
            _assigned = true;
        }
        // Close every extra child-side endpoint in this parent before execution/EOF observation.
        _outWrite.Dispose(); _outWrite = null; _errWrite.Dispose(); _errWrite = null;
        _inRead.Dispose(); _inRead = null; if (keepInput) _duplexInput = new FileStream(_inWrite, FileAccess.Write, 4096, false); else { _inWrite.Dispose(); _inWrite = null; }
        _readerEndpointsAttempted = true;
        _stdout = new(new FileStream(_outRead, FileAccess.Read, 4096, false), Console.OutputEncoding, true);
        _stderr = new(new FileStream(_errRead, FileAccess.Read, 4096, false), Console.OutputEncoding, true);
        if (_captureRetention is not null)
        {
            _stage = "enroll-original-byte-drains";
            _captureOutputTask = BoundedCaptureStream.Observe(_captureStdout!.DrainAsync(_stdout.BaseStream));
            _captureRetention.Add(_captureOutputTask);
            _captureErrorTask = BoundedCaptureStream.Observe(_captureStderr!.DrainAsync(_stderr.BaseStream));
            _captureRetention.Add(_captureErrorTask);
        }
        _stage = "resume-owned-root";
        CheckStartupBudget(originalStarted, originalTimeout, caller);
        _resumeAttempted = true;
        if (ResumeThread(_thread) == uint.MaxValue) throw Native(_stage);
        _resumed = true; _thread.Dispose(); _thread = null;
    }

    private void CheckRestrictiveStartup(long originalStarted, TimeSpan originalTimeout, CancellationToken caller)
    {
        if (_boundedBudget is not null) CheckStartupBudget(originalStarted, originalTimeout, caller);
    }

    private void CheckStartupBudget(long originalStarted, TimeSpan originalTimeout, CancellationToken caller)
    {
        var cancelled = caller.IsCancellationRequested;
        if (cancelled || _boundedBudget?.WorkAvailable == false || Stopwatch.GetElapsedTime(originalStarted) >= originalTimeout)
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
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string>? additions, bool complete = false)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (complete)
        {
            var selected = new ProcessStartInfo();
            CommandEnvironment.Complete(selected, new("", Array.Empty<string>(), "", additions) { EnvironmentIsComplete = true });
            foreach (var row in selected.Environment) values.Add(row.Key, row.Value!);
        }
        else
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
        _thread?.Dispose(); _process?.Dispose(); _stdout?.Dispose(); _stderr?.Dispose(); _duplexInput?.Dispose();
        _outRead?.Dispose(); _outWrite?.Dispose(); _errRead?.Dispose(); _errWrite?.Dispose(); _inRead?.Dispose(); _inWrite?.Dispose();
    }

    private bool DisposeBoundedKnown(long started, TimeSpan budget, BoundedCaptureRetention retention, RestrictiveCaptureBudget? restrictive = null)
    {
        var known = true;
        // This path is entered ONLY after no Create attempt, or observed root+Job0+EACH EOF.
        foreach (var resource in new IDisposable?[] { _job, _thread, _process, _stdout, _stderr,
            _outRead, _outWrite, _errRead, _errWrite, _inRead, _inWrite })
        {
            if (resource is null) continue;
            if (!BoundedCaptureStream.Within(started, budget) || restrictive?.CleanupAvailable == false) { known = false; continue; }
            try
            {
                var wasInvalid = resource is KernelHandle original && original.IsInvalid;
                resource.Dispose();
                if (resource is KernelHandle handle && !wasInvalid && (!handle.NativeCloseEntered || !handle.NativeCloseReturned || !handle.NativeClosed)) throw new IOException("Actual original CloseHandle did not return successful first disposition.");
            }
            catch (Exception failure) { retention.Add(failure); restrictive?.BeginFailureCleanup(); known = false; }
        }
        return known && BoundedCaptureStream.Within(started, budget) && restrictive?.CleanupAvailable != false;
    }

    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle() : base(true) { }
        public KernelHandle(IntPtr value) : base(true) => SetHandle(value);
        internal bool NativeCloseEntered, NativeCloseReturned, NativeClosed;
        protected override bool ReleaseHandle() { NativeCloseEntered = true; var actual = CloseHandle(handle); NativeCloseReturned = true; NativeClosed = actual; return actual; }
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
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Cb; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size; public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTime, PerJobUserTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits BasicLimitInformation; public IoCounters IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern KernelHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(KernelHandle job, int infoClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(KernelHandle job, int infoClass, out BasicAccounting information, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(KernelHandle job, KernelHandle process);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(KernelHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(KernelHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(KernelHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(KernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string executable, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint SearchPathW(string? path, string name, string? extension, uint length, StringBuilder result, IntPtr filePart);
}
