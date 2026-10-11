using DeliveryForge.Evidence;

namespace DeliveryForge.Review;

// Immutable actual owner observations only. No process handles, control, policy or acceptance authority.
// A false settlement observation means unproved closure, not evidence that a live orphan exists.
public sealed class ProtectedPolicyCaptureDiagnosticException : Exception
{
    internal ProtectedPolicyCaptureDiagnosticException(BoundedCommandCaptureResult capture, string reason)
        : base(reason.Length > 1024 ? reason[..1024] : reason) => ActualCapture = capture;
    public BoundedCommandCaptureResult ActualCapture { get; }
    public bool ResidualProcessUncertain => !ActualCapture.Command.OwnedProcessQuiescent;
}

public static class ProtectedPolicyReadFailure
{
    // Failure classification/data routing only. It cannot produce a policy, opaque owner or success proof.
    public static Exception Create(BoundedCommandCaptureResult capture, CancellationToken caller, string reason) =>
        throw new NotImplementedException("Actual protected-policy capture diagnostics through typed refusals are RED.");
}
