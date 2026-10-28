using System.Diagnostics;
using System.Text;
namespace DeliveryForge.Evidence;

// Ordinary bounded capture REUSES the same original duplex/group owner. This is
// a capture consumer, not a Process.Start, executor or native authority factory.
internal static class ScopedBoundedCapture
{
    sealed class Frame { internal required ScopedCaptureBudget Budget; internal OwnedDuplexProcess? Session; internal Task? Output; internal readonly List<Exception> Failures = []; internal DuplexRetirement? Retirement; }
    static readonly List<Frame> Retained = [];
    internal static async Task<BoundedCommandCaptureResult> ExecuteAsync(CommandInvocation invocation, TimeSpan localGrant, int quota, CancellationToken caller, IOwnedDuplexBudget actualParent, LinuxScopeSelection scope, LinuxScopeImage? exactNativeImage = null)
    {
        if (invocation.StandardInput is not null)
        {
            // This owner currently closes stdin without a byte-write operation.
            // Refuse the unsupported profile before any local/native birth;
            // original refusal/cancellation still takes precedence.
            actualParent.CheckWork();
            caller.ThrowIfCancellationRequested();
            throw new PlatformNotSupportedException("Selected owned Linux capture does not support supplied standard input; no unscoped fallback is permitted.");
        }
        var began = DateTimeOffset.UtcNow; var budget = new ScopedCaptureBudget(actualParent, localGrant, caller); var frame = new Frame { Budget = budget }; lock (Retained) Retained.Add(frame); var stdout = new BoundedCaptureStream(quota); bool timedOut = false, cancelled = false; Exception? primary = null; DuplexNativeObservation native = new(false, false, uint.MaxValue, null, "Not entered.");
        try
        {
            budget.CheckWork(); frame.Session = OwnedDuplexProcess.OpenCapture(invocation, budget, scope, quota, exactNativeImage); if (frame.Session.StartupFailure is Exception startup) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startup).Throw();
            frame.Session.CloseInputForCapture(); budget.CheckWork();
            // DrainAsync starts on these actual original streams and retains each
            // underlying operation in the SAME owner; no read-all/string parser.
            frame.Output = stdout.DrainAsync(frame.Session.Output); frame.Session.AttachOriginalOutputConsumer(frame.Output);
            while (true)
            {
                budget.CheckWork(); native = frame.Session.ObserveNativeForCapture(); if (stdout.Exceeded || frame.Session.ErrorQuotaExceeded || frame.Output.IsFaulted || frame.Output.IsCanceled) break;
                if (native.Known && native.RootExited && native.ActiveProcesses == 0 && frame.Output.IsCompleted && frame.Session.OriginalErrorDrainTask?.IsCompleted == true) break;
                // Finite source-owned kernel observation cadence; no fresh clock.
                await Task.Delay(budget.NextObservationDelay(), budget.Token).ConfigureAwait(false);
            }
            if (frame.Output.IsCompleted) await frame.Output.ConfigureAwait(false);
        }
        catch (Exception error) { primary = error; frame.Failures.Add(error); (timedOut, cancelled) = budget.ClassifyFailure(error, caller.IsCancellationRequested); }
        if (frame.Session is not null)
        {
            try { var mustStop = primary is not null || stdout.Exceeded || frame.Session.ErrorQuotaExceeded || frame.Output?.IsFaulted == true; frame.Retirement = await frame.Session.RetireAsync(mustStop).ConfigureAwait(false); } catch (Exception error) { frame.Failures.Add(error); }
            if (frame.Output is not null) { try { if (!frame.Output.IsCompleted) await frame.Output.WaitAsync(budget.CleanupRemaining).ConfigureAwait(false); else await frame.Output.ConfigureAwait(false); } catch (Exception error) { frame.Failures.Add(error); } }
        }
        var retired = frame.Retirement; var known = retired?.KnownClosed == true && frame.Output?.IsCompleted == true && budget.CanStillFinish;
        var outBytes = stdout.Snapshot(); var errBytes = frame.Session?.StandardError ?? Array.Empty<byte>(); var exceeded = stdout.Exceeded || frame.Session?.ErrorQuotaExceeded == true;
        var nativeFailed = frame.Failures.Count != 0 || retired?.Failures.Count > 0 || frame.Session?.StartupFailure is not null;
        // Export bounded actual causes before a known-closed frame is released.
        // Closure alone must not erase a failed observation or imply success.
        var failureDetails = frame.Failures.Select(error => error.GetType().FullName + ": " + CommandStartupObservation.Clip(error.Message, 512))
            .Concat(retired?.Failures ?? []).Concat(frame.Session?.StartupFailure is Exception startFailure ? [startFailure.GetType().FullName + ": " + CommandStartupObservation.Clip(startFailure.Message, 512)] : []);
        var failure = nativeFailed ? CommandStartupObservation.Clip("Actual scoped capture failed; bounded original causes: " + string.Join(" | ", failureDetails.Take(8).Select(detail => CommandStartupObservation.Clip(detail, 512))), 4096) : null;
        var outputDone = retired?.OutputEof == true && frame.Output?.IsCompletedSuccessfully == true; var errorDone = retired?.ErrorEof == true;
        var command = new CommandResult(retired?.ExitCode, Encoding.UTF8.GetString(outBytes), Encoding.UTF8.GetString(errBytes), timedOut, cancelled, began, DateTimeOffset.UtcNow, known) { LifecycleDiagnostics = new(false, false, null, failure, false, retired?.RootExited == true, retired?.RootExited == true, outputDone && errorDone, 0, new(outputDone, frame.Output?.IsFaulted == true, frame.Output?.IsCanceled == true, outputDone ? Encoding.UTF8.GetString(outBytes) : null), new(errorDone, false, false, errorDone ? Encoding.UTF8.GetString(errBytes) : null)) { NativeFailure = failure, OwnedTree = new(frame.Session?.HasCreationOwnedLinuxScope == true, known, known ? 0 : -1) } };
        var result = new BoundedCommandCaptureResult(command, outBytes, errBytes, exceeded, retired?.RootExited == true, outputDone, errorDone, new(budget.OriginalStarted, budget.LocalCleanupEntered, Stopwatch.Frequency), frame.Session?.HasCreationOwnedLinuxScope == true ? BoundedCaptureOwnershipKind.LinuxCreationOwnedScope : BoundedCaptureOwnershipKind.LegacyObservation);
        // LocalCleanupEntered is an observed local entry, not imported parent
        // cleanup authority. Real effects have already used the actual parent MIN.
        var timely = budget.CanStillFinish; if (known) { budget.Dispose(); lock (Retained) Retained.Remove(frame); } else budget.PreserveUnknown(); return timely ? result : result.RefuseLateCompletion();
    }
}
