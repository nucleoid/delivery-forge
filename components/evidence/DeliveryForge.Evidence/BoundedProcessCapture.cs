using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeliveryForge.Evidence;

// Linux ordinary process ownership: root plus successful original tree termination and pipe EOF.
// This does not claim Windows Job accounting or containment of deliberately escaped descendants.
internal static class BoundedProcessCapture
{
    internal static async Task<BoundedCommandCaptureResult> ExecuteAsync(CommandInvocation invocation, TimeSpan budget, int quota, CancellationToken caller)
    {
        var began = DateTimeOffset.UtcNow; var started = Stopwatch.GetTimestamp();
        var work = BoundedCaptureStream.WorkBudget(budget);
        var stdout = new BoundedCaptureStream(quota); var stderr = new BoundedCaptureStream(quota);
        var process = new Process();
        var retention = new BoundedCaptureRetention();
        retention.Add(process); retention.Add(stdout); retention.Add(stderr);
        Task? input = null, output = null, error = null;
        StreamWriter? originalInput = null;
        StreamReader? originalOutput = null, originalError = null;
        var created = false; var rootExited = false; var known = true;
        var timedOut = false; var cancelled = false; var killAttempted = false; var killSucceeded = false;
        string? failure = null, killType = null;
        var creationAttempted = false; var readersEstablished = false;
        var stage = "prepare-command"; string? failureStage = null;
        try
        {
            const string setsid = "/usr/bin/setsid";
            if (!File.Exists(setsid))
                throw new PlatformNotSupportedException("Linux creation-owned process groups require /usr/bin/setsid.");
            var info = new ProcessStartInfo(setsid)
            {
                WorkingDirectory = invocation.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (!invocation.InheritEnvironment) info.Environment.Clear();
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(invocation.FileName);
            foreach (var argument in invocation.Arguments) info.ArgumentList.Add(argument);
            foreach (var pair in invocation.Environment ?? new Dictionary<string, string>()) info.Environment[pair.Key] = pair.Value;
            if (caller.IsCancellationRequested || !BoundedCaptureStream.Within(started, work))
            { cancelled = caller.IsCancellationRequested; timedOut = !cancelled; }
            else
            {
                process.StartInfo = info;
                stage = "create-original-root"; creationAttempted = true;
                created = process.Start();
                if (!created) throw new InvalidOperationException("Original process did not start.");
                readersEstablished = true;
                originalInput = process.StandardInput; retention.Add(originalInput);
                originalOutput = process.StandardOutput; retention.Add(originalOutput);
                originalError = process.StandardError; retention.Add(originalError);
                stage = "enroll-original-byte-drains";
                output = BoundedCaptureStream.Observe(stdout.DrainAsync(originalOutput.BaseStream));
                retention.Add(output);
                error = BoundedCaptureStream.Observe(stderr.DrainAsync(originalError.BaseStream));
                retention.Add(error);
                stage = "write-original-stdin";
                input = BoundedCaptureStream.Observe(
                    ProcessCommandExecutor.WriteStandardInputAsync(originalInput, invocation.StandardInput));
                retention.Add(input);
                stage = "observe-original-root-and-drains";
                while (true)
                {
                    if (caller.IsCancellationRequested) { cancelled = true; break; }
                    if (stdout.Exceeded || stderr.Exceeded) break;
                    if (!BoundedCaptureStream.Within(started, work)) { timedOut = true; break; }
                    rootExited = process.HasExited;
                    if (input.IsFaulted || output.IsFaulted || error.IsFaulted)
                    { known = false; failure = "Owned stdin write or pipe drain failed."; break; }
                    if (rootExited && BoundedCaptureStream.Eof(output) && BoundedCaptureStream.Eof(error)) break;
                    await Task.Delay(5).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or ArgumentException)
        { known = false; failure = Bounded(exception); failureStage = stage; retention.Add(exception); }
        if (created)
        {
            // If the root already exited while descendants keep pipes, tree enumeration cannot prove their ownership.
            // Retain nonquiescence rather than adopting PIDs or inferring exit from disposal.
            try
            {
                rootExited = process.HasExited;
                if (!rootExited || !BoundedCaptureStream.Eof(output) || !BoundedCaptureStream.Eof(error))
                {
                    killAttempted = true;
                    killSucceeded = KillOwnedProcessGroup(process.Id, out var killFailure);
                    if (!killSucceeded)
                    {
                        known = false;
                        failure ??= killFailure;
                    }
                }
                while (BoundedCaptureStream.Within(started, budget))
                {
                    rootExited = process.HasExited;
                    if (rootExited && BoundedCaptureStream.Eof(output) && BoundedCaptureStream.Eof(error)) break;
                    await Task.Delay(5).ConfigureAwait(false);
                }
                rootExited = process.HasExited;
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
            { known = false; killType = exception.GetType().FullName; failure ??= Bounded(exception); retention.Add(exception); }
        }
        var outputStatus = output?.Status; var errorStatus = error?.Status;
        var outEof = outputStatus == TaskStatus.RanToCompletion; var errEof = errorStatus == TaskStatus.RanToCompletion;
        var inputComplete = input?.Status == TaskStatus.RanToCompletion;
        var quiescent = created && known && rootExited && inputComplete && outEof && errEof && (!killAttempted || killSucceeded) &&
            BoundedCaptureStream.Within(started, budget);
        int? exit = rootExited ? process.ExitCode : null;
        var outBytes = stdout.Snapshot(); var errBytes = stderr.Snapshot();
        if (((!creationAttempted && output is null && error is null) || quiescent) && BoundedCaptureStream.Within(started, budget))
        {
            var dispositionKnown = true;
            var dispositionErrors = new List<Exception>(); retention.Add(dispositionErrors);
            // Process.Dispose does not close caller-accessed synchronous StreamReaders.
            // Dispose EACH exact original independently only after original root/EACH EOF.
            foreach (var original in new IDisposable?[] { originalInput, originalOutput, originalError, process })
            {
                if (original is null) continue;
                if (!BoundedCaptureStream.Within(started, budget)) { dispositionKnown = false; continue; }
                try { original.Dispose(); }
                catch (Exception disposal) { dispositionErrors.Add(disposal); dispositionKnown = false; }
                if (!BoundedCaptureStream.Within(started, budget)) dispositionKnown = false;
            }
            if (dispositionKnown) retention.ReleaseKnown();
            else { quiescent = false; failure ??= "EACH original reader/process disposition was not timely and complete."; }
        }
        var command = new CommandResult(exit, Encoding.UTF8.GetString(outBytes), Encoding.UTF8.GetString(errBytes),
            timedOut, cancelled, began, DateTimeOffset.UtcNow, quiescent)
        {
            LifecycleDiagnostics = new(killAttempted, killSucceeded, killType, failure, false,
                rootExited, rootExited, outEof && errEof, 0,
                new(outEof, outputStatus == TaskStatus.Faulted, outputStatus == TaskStatus.Canceled, outEof ? Encoding.UTF8.GetString(outBytes) : null),
                new(errEof, errorStatus == TaskStatus.Faulted, errorStatus == TaskStatus.Canceled, errEof ? Encoding.UTF8.GetString(errBytes) : null))
            {
                NativeFailure = failure,
                Startup = new(created ? CommandStartupState.Established : creationAttempted ? CommandStartupState.AttemptedUnknown : CommandStartupState.NotAttempted,
                    created ? CommandStartupState.Established : creationAttempted ? CommandStartupState.AttemptedUnknown : CommandStartupState.NotAttempted,
                    CommandStartupState.NotApplicable, CommandStartupState.NotApplicable,
                    readersEstablished ? CommandStartupState.Established : CommandStartupState.NotAttempted,
                    output is not null ? CommandStartupState.Established : CommandStartupState.NotAttempted,
                    error is not null ? CommandStartupState.Established : CommandStartupState.NotAttempted,
                    stage, failureStage, Stopwatch.GetElapsedTime(started).TotalSeconds, budget.TotalSeconds, work.TotalSeconds,
                    failure is null ? null : CommandStartupObservation.Clip(failure, 2048))
            }
        };
        // Unknown original process/readers/tasks remain rooted, never disposed as exit proof.
        var completed = new BoundedCommandCaptureResult(command, outBytes, errBytes, stdout.Exceeded || stderr.Exceeded, rootExited, outEof, errEof);
        // Includes actual snapshot/UTF8/DTO construction; physical exit/EOF DATA stays honest.
        return BoundedCaptureStream.Within(started, budget) ? completed : completed.RefuseLateCompletion();
    }
    private static string Bounded(Exception exception)
    {
        var text = exception.GetType().Name + ": " + exception.Message;
        return text.Length > 1024 ? text[..1024] : text;
    }

    private static bool KillOwnedProcessGroup(int rootProcessId, out string? failure)
    {
        failure = null;
        if (kill(-rootProcessId, 9) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == 3) // ESRCH: the group became empty before the signal.
            return true;
        failure = $"kill-owned-process-group failed with errno {error}.";
        return false;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
