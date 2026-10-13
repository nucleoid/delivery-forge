using DeliveryForge.Review;

namespace DeliveryForge.Execution.Host;

public enum ReviewGitPreparationOutcome { Prepared, TimedOut, Cancelled, CaptureBoundExceeded, Error }
public sealed record ReviewGitPreparationObservation(ReviewGitPreparationOutcome Outcome, bool OwnedProcessQuiescent,
    bool RootExitObserved, bool StandardOutputDrainCompleted, bool StandardErrorDrainCompleted,
    string CapturedStandardOutput, string CapturedStandardError, string Reason)
{
    // Optional DATA only. Null does not imply no dispatch or known native closure.
    public ReviewGitPreparationCaptureData? LastCapture { get; init; }
}
// Context retains its existing private creator/provenance. This wrapper conveys no signaling/production authority.
public sealed record ReviewGitPreparationResult(ControlledGitEvidenceContext? Context, ReviewGitPreparationObservation Observation);
