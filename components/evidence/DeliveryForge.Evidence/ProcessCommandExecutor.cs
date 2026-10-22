using System.Diagnostics;

namespace DeliveryForge.Evidence;

public sealed class ProcessCommandExecutor : ICommandExecutor
{
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);
    private readonly Action<Process> _killTree;
    private readonly Func<Process, bool> _hasExited;
    private readonly Func<Task, TimeSpan, Task> _waitForDrain;
    private readonly TimeSpan _waitTimeout;
    private readonly bool _creationOwnedWindows;
    private readonly LinuxScopeSelection? _selectedLinuxScope;
    private readonly IOwnedDuplexBudget? _selectedLinuxOriginal;

    /// <summary>Explicit trusted Linux profile and the actual parent owner are
    /// retained in this composition, never reconstructed from result DATA.</summary>
    public ProcessCommandExecutor(LinuxScopeSelection linuxScope, IOwnedDuplexBudget actualOriginal) : this()
    {
        ArgumentNullException.ThrowIfNull(linuxScope); ArgumentNullException.ThrowIfNull(actualOriginal);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux owner selection on another platform.");
        actualOriginal.CheckWork(); _selectedLinuxScope = linuxScope; _selectedLinuxOriginal = actualOriginal;
    }
    internal bool SupportsCreationOwnedBoundedCapture => _creationOwnedWindows;

    public ProcessCommandExecutor()
        : this(
            process => process.Kill(entireProcessTree: true),
            process => process.HasExited,
            waitForDrain: null,
            DefaultWaitTimeout)
    {
        _creationOwnedWindows = true;
    }

    internal ProcessCommandExecutor(
        Action<Process> killTree,
        Func<Process, bool> hasExited,
        Func<Task, TimeSpan, Task>? waitForDrain = null,
        TimeSpan? waitTimeout = null)
    {
        _killTree = killTree;
        _hasExited = hasExited;
        _waitForDrain = waitForDrain ?? ((task, timeout) =>
            task.WaitAsync(timeout, CancellationToken.None));
        _waitTimeout = waitTimeout ?? DefaultWaitTimeout;
    }

    /// <summary>Captures a finite byte prefix per stream within one entire owner lifetime budget.
    /// Observations are data only; injected execution delegates cannot establish this owner proof.</summary>
    public Task<BoundedCommandCaptureResult> ExecuteBoundedCaptureAsync(
        CommandInvocation invocation,
        TimeSpan entireBudget,
        int maximumBytesPerStream,
        CancellationToken cancellationToken)
    {
        return ExecuteBoundedCaptureAsync(invocation, entireBudget, maximumBytesPerStream, cancellationToken, null);
    }

    /// <summary>Optional restrictive same-origin budget; the ordinary four-argument API is unchanged.</summary>
    public Task<BoundedCommandCaptureResult> ExecuteBoundedCaptureAsync(
        CommandInvocation invocation, TimeSpan entireBudget, int maximumBytesPerStream,
        CancellationToken cancellationToken, IOwnedDuplexBudget? enclosing)
    {
        if (_selectedLinuxScope is not null)
        {
            RequireSelectedLinuxCaptureOwner(_selectedLinuxScope, _selectedLinuxOriginal, enclosing, _selectedLinuxScope);
            return ExecuteBoundedCaptureAsync(invocation, entireBudget, maximumBytesPerStream, cancellationToken, enclosing ?? _selectedLinuxOriginal, _selectedLinuxScope);
        }
        if (!_creationOwnedWindows)
            throw new InvalidOperationException("Injected executor delegates cannot establish creation-owned byte capture.");
        if (entireBudget <= TimeSpan.Zero || entireBudget > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(entireBudget));
        if (maximumBytesPerStream <= 0 || maximumBytesPerStream > 128 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytesPerStream));
        if (enclosing is not null && (enclosing.OriginalStarted <= 0 || enclosing.OriginalStarted > Stopwatch.GetTimestamp() ||
            enclosing.WholeLimit <= TimeSpan.Zero || enclosing.WholeLimit > TimeSpan.FromMinutes(20) ||
            enclosing.WorkLimit <= TimeSpan.Zero || enclosing.WorkLimit > TimeSpan.FromSeconds(1100) || enclosing.WorkLimit >= enclosing.WholeLimit))
            throw new ArgumentOutOfRangeException(nameof(enclosing), "Only a finite restrictive enclosing original is supported.");
        if (OperatingSystem.IsWindows())
            return WindowsCommandLifetime.ExecuteBoundedCaptureAsync(invocation, entireBudget, maximumBytesPerStream, cancellationToken, enclosing);
        if (OperatingSystem.IsLinux())
        {
            RequireUnscopedLinuxCaptureHasNoEnclosingOwner(enclosing);
            return BoundedProcessCapture.ExecuteAsync(invocation, entireBudget, maximumBytesPerStream, cancellationToken);
        }
        throw new PlatformNotSupportedException("Creation-owned bounded capture supports Windows and Linux only.");
    }

    /// <summary>Explicit Linux scope profile; selection DATA cannot issue ownership.
    /// The same original owner creates, binds and retires the real kernel scope.</summary>
    public Task<BoundedCommandCaptureResult> ExecuteBoundedCaptureAsync(
        CommandInvocation invocation, TimeSpan entireBudget, int maximumBytesPerStream,
        CancellationToken cancellationToken, IOwnedDuplexBudget? enclosing, LinuxScopeSelection? linuxScope)
    {
        if (linuxScope is null)
            return ExecuteBoundedCaptureAsync(invocation, entireBudget, maximumBytesPerStream, cancellationToken, enclosing);
        RequireSelectedLinuxCaptureOwner(_selectedLinuxScope, _selectedLinuxOriginal, enclosing, linuxScope);
        if (!_creationOwnedWindows || !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Explicit Linux scope requires the original production Linux owner.");
        ArgumentNullException.ThrowIfNull(enclosing);
        if (entireBudget <= TimeSpan.Zero || entireBudget > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(entireBudget));
        if (maximumBytesPerStream <= 0 || maximumBytesPerStream > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytesPerStream));
        if (enclosing.OriginalStarted <= 0 || enclosing.OriginalStarted > Stopwatch.GetTimestamp() ||
            enclosing.WholeLimit <= TimeSpan.Zero || enclosing.WholeLimit > TimeSpan.FromSeconds(1200) ||
            enclosing.WorkLimit <= TimeSpan.Zero || enclosing.WorkLimit > TimeSpan.FromSeconds(1100) || enclosing.WorkLimit >= enclosing.WholeLimit)
            throw new ArgumentOutOfRangeException(nameof(enclosing));
        enclosing.CheckWork();
        return ScopedBoundedCapture.ExecuteAsync(invocation, entireBudget, maximumBytesPerStream, cancellationToken, enclosing, linuxScope);
    }

    // These restrictive guards are shared by the actual pre-birth paths. They
    // cannot issue a scope, native owner or authority from selection DATA.
    internal static void RequireSelectedLinuxCaptureOwner(LinuxScopeSelection? selectedScope,
        IOwnedDuplexBudget? selectedOriginal, IOwnedDuplexBudget? enclosing, LinuxScopeSelection requestedScope)
    {
        if (selectedScope is null) return;
        if (!ReferenceEquals(requestedScope, selectedScope))
            throw new InvalidOperationException("Configured Linux scope cannot be replaced by a foreign selection.");
        var actualRoot = enclosing is OwnedWorkPhaseBudget phase ? phase.Parent : enclosing;
        if (actualRoot is OwnedCleanupPhaseBudget cleanup) actualRoot = cleanup.Parent;
        if (enclosing is not null && !ReferenceEquals(actualRoot, selectedOriginal))
            throw new InvalidOperationException("Configured owner cannot be replaced by a foreign enclosing budget.");
    }
    internal static void RequireUnscopedLinuxCaptureHasNoEnclosingOwner(IOwnedDuplexBudget? enclosing)
    {
        if (enclosing is not null)
            throw new PlatformNotSupportedException("An actual enclosing Linux owner requires its explicit creation-owned containing scope before process birth; legacy observation cannot adopt that owner.");
    }

    public async Task<CommandResult> ExecuteAsync(
        CommandInvocation invocation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_selectedLinuxScope is not null)
            return (await ExecuteBoundedCaptureAsync(invocation, timeout, 8 * 1024 * 1024, cancellationToken,
                _selectedLinuxOriginal, _selectedLinuxScope).ConfigureAwait(false)).ProjectOrdinaryCommand();
        if (_creationOwnedWindows && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            return (await ExecuteBoundedCaptureAsync(
                invocation, timeout, 64 * 1024 * 1024, cancellationToken).ConfigureAwait(false)).ProjectOrdinaryCommand();
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        CommandEnvironment.Complete(startInfo, invocation);
        foreach (var argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var pair in invocation.Environment ?? new Dictionary<string, string>())
            startInfo.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = startInfo };
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            if (!process.Start())
                return new CommandResult(
                    null, string.Empty, "Process did not start.", false, false, startedAt, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new CommandResult(
                null, string.Empty, $"Process start failed: {exception.Message}",
                false, false, startedAt, DateTimeOffset.UtcNow);
        }
        var originalInput = process.StandardInput;
        var inputTask = BoundedCaptureStream.Observe(WriteStandardInputAsync(originalInput, invocation.StandardInput));
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var timedOut = false;
        var cancelled = false;
        var killSucceeded = true;
        var killAttempted = false;
        string? killExceptionType = null;
        string? killExceptionDetail = null;
        var killExceptionDetailTruncated = false;
        var exitWaitCompleted = true;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = !timedOut;
            killSucceeded = TryKillTree(process, out killAttempted, out killExceptionType,
                out killExceptionDetail, out killExceptionDetailTruncated);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(_waitTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                exitWaitCompleted = false;
            }
        }

        string stdout;
        string stderr;
        var drainCompleted = true;
        Exception? drainFailure = null;
        var drains = Task.WhenAll(stdoutTask, stderrTask, inputTask);
        try
        {
            await _waitForDrain(drains, _waitTimeout).ConfigureAwait(false);
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            drainCompleted = false;
            stdout = "<stdout drain timed out>";
            stderr = "<stderr drain timed out>";
        }
        catch (Exception failure) when (IsDrainIoFailure(failure))
        {
            drainFailure = failure; drainCompleted = false;
            stdout = stdoutTask.Status == TaskStatus.RanToCompletion ? stdoutTask.Result : "<stdout drain failed>";
            stderr = stderrTask.Status == TaskStatus.RanToCompletion ? stderrTask.Result : "<stderr drain failed>";
            // This injected/unsupported-platform compatibility path returns its
            // failure diagnostics without claiming a retained creation-owned graph.
        }
        var processExited = ObserveHasExited(process);
        return new CommandResult(
            processExited ? process.ExitCode : null,
            stdout,
            stderr,
            timedOut,
            cancelled,
            startedAt,
            DateTimeOffset.UtcNow,
            drainCompleted && inputTask.Status == TaskStatus.RanToCompletion && processExited &&
            (!timedOut && !cancelled || killSucceeded && exitWaitCompleted))
        {
            LifecycleDiagnostics = new(killAttempted, killAttempted && killSucceeded, killExceptionType, killExceptionDetail,
                killExceptionDetailTruncated, exitWaitCompleted, processExited, drainCompleted,
                (int)Math.Clamp(_waitTimeout.TotalMilliseconds, 0, int.MaxValue),
                ObserveStream(stdoutTask), ObserveStream(stderrTask))
            {
                NativeFailure = drainFailure is null ? null : BoundedExceptionDetail(drainFailure).Detail
            }
        };
    }

    internal static async Task WriteStandardInputAsync(StreamWriter writer, string? input)
    {
        Exception? primary = null;
        try
        {
            if (input is not null)
                await writer.WriteAsync(input.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) { primary = failure; }
        try { writer.Close(); }
        catch (Exception firstClose)
        {
            if (primary is not null)
                throw new AggregateException("Actual stdin write/flush and first writer close both failed.", primary, firstClose);
            throw;
        }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }

    // Only actual stream I/O/first-close failures are projected. Cancellation,
    // owner expiry and unrelated failures keep their original throwing semantics.
    private static bool IsDrainIoFailure(Exception failure) =>
        failure is IOException or InvalidOperationException or System.ComponentModel.Win32Exception ||
        failure is AggregateException aggregate && aggregate.InnerExceptions.All(IsDrainIoFailure);

    private bool TryKillTree(Process process, out bool attempted, out string? exceptionType,
        out string? exceptionDetail, out bool detailTruncated)
    {
        attempted = false; exceptionType = null; exceptionDetail = null; detailTruncated = false;
        try
        {
            if (_hasExited(process))
                return false;

            attempted = true;
            _killTree(process);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            exceptionType = exception.GetType().FullName;
            (exceptionDetail, detailTruncated) = BoundedExceptionDetail(exception);
            return false;
        }
    }

    private static CommandStreamObservation ObserveStream(Task<string> task)
    {
        _ = task.ContinueWith(static late => { _ = late.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var status = task.Status;
        var complete = status == TaskStatus.RanToCompletion;
        var faulted = status == TaskStatus.Faulted;
        var cancelled = status == TaskStatus.Canceled;
        return new(complete, faulted, cancelled, complete ? task.Result : null);
    }

    private static (string Detail, bool Truncated) BoundedExceptionDetail(Exception exception)
    {
        const int limit = 2048;
        var builder = new System.Text.StringBuilder();
        var pending = new Stack<Exception>(); pending.Push(exception);
        var count = 0; var truncated = false;
        while (pending.TryPop(out var current))
        {
            if (++count > 8 || builder.Length >= limit) { truncated = true; break; }
            var type = current.GetType().FullName ?? current.GetType().Name;
            var message = current is AggregateException ? "aggregate command failure" : current.Message;
            var text = type + ": " + message +
                (current is System.ComponentModel.Win32Exception native ? " (Win32=" + native.NativeErrorCode + ")" : "") + "\n";
            var length = Math.Min(limit - builder.Length, text.Length);
            builder.Append(text.AsSpan(0, length));
            truncated |= length != text.Length;
            if (current is AggregateException aggregate)
            {
                var take = Math.Min(8, aggregate.InnerExceptions.Count);
                truncated |= take != aggregate.InnerExceptions.Count;
                for (var index = take - 1; index >= 0; index--) pending.Push(aggregate.InnerExceptions[index]);
            }
            else if (current.InnerException is not null) pending.Push(current.InnerException);
        }
        return (builder.ToString(), truncated);
    }

    private bool ObserveHasExited(Process process)
    {
        try
        {
            return _hasExited(process);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
