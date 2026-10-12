using DeliveryForge.Evidence;

namespace DeliveryForge.Execution.Host;

/// <summary>Immutable diagnostic projection only. It cannot issue a context or native ownership.</summary>
public sealed class ReviewGitPreparationCaptureData
{
    private ReviewGitPreparationCaptureData() { }
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

    // RED declaration only: null must later map NotObserved, never native closure.
    public static ReviewGitPreparationCaptureData FromCapture(BoundedCommandCaptureResult? actualReturned)
        => throw new NotImplementedException("Actual preparation startup DATA projection is not implemented.");
}
