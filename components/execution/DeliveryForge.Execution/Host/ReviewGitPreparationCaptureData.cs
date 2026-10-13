using DeliveryForge.Evidence;

namespace DeliveryForge.Execution.Host;

/// <summary>Immutable diagnostic projection only. It cannot issue a context or native ownership.</summary>
public sealed class ReviewGitPreparationCaptureData
{
    private ReviewGitPreparationCaptureData(BoundedCommandCaptureResult? capture)
    {
        CaptureReturned = capture is not null;
        if (capture is null) return; // Explicit absence; no startup/native fact inferred.
        ExitCode = capture.Command.ExitCode; TimedOut = capture.Command.TimedOut; Cancelled = capture.Command.Cancelled;
        CaptureBoundExceeded = capture.CaptureBoundExceeded; RootExitObserved = capture.RootExitObserved;
        StandardOutputDrainCompleted = capture.StandardOutputDrainCompleted;
        StandardErrorDrainCompleted = capture.StandardErrorDrainCompleted;
        OwnedProcessQuiescent = capture.Command.OwnedProcessQuiescent;
        var actual = capture.Command.LifecycleDiagnostics;
        if (actual is null) return;
        string? Clip(string? value) => value is null ? null : value.Length > 2048 ? value[..2048] : value;
        var startup = actual.Startup;
        Startup = startup is null ? null : new(startup.Creation, startup.RootHandle, startup.JobAssignment, startup.Resume,
            startup.ReaderEndpoints, startup.StandardOutputDrain, startup.StandardErrorDrain, startup.LastStage, startup.FailureStage,
            startup.OriginalElapsedSeconds, startup.OriginalTotalSeconds, startup.OriginalWorkSeconds, startup.Reason);
        Lifecycle = actual with
        {
            Startup = Startup,
            KillExceptionType = Clip(actual.KillExceptionType),
            KillExceptionDetail = Clip(actual.KillExceptionDetail),
            KillExceptionDetailTruncated = actual.KillExceptionDetailTruncated || actual.KillExceptionDetail?.Length > 2048,
            NativeFailure = Clip(actual.NativeFailure),
            StandardOutput = actual.StandardOutput with { CompletedText = Clip(actual.StandardOutput.CompletedText) },
            StandardError = actual.StandardError with { CompletedText = Clip(actual.StandardError.CompletedText) },
            OwnedTree = actual.OwnedTree is null ? null : actual.OwnedTree with { }
        };
    }
    public bool CaptureReturned { get; }
    public int? ExitCode { get; }
    public bool TimedOut { get; }
    public bool Cancelled { get; }
    public bool CaptureBoundExceeded { get; }
    public bool RootExitObserved { get; }
    public bool StandardOutputDrainCompleted { get; }
    public bool StandardErrorDrainCompleted { get; }
    public bool OwnedProcessQuiescent { get; }
    public CommandLifecycleDiagnostics? Lifecycle { get; }
    public CommandStartupObservation? Startup { get; }

    // A projection of returned DATA only. Null remains absent, never native closure.
    public static ReviewGitPreparationCaptureData FromCapture(BoundedCommandCaptureResult? actualReturned)
        => new(actualReturned);
}
