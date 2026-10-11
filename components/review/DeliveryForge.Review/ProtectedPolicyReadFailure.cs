using DeliveryForge.Evidence;

namespace DeliveryForge.Review;

// Immutable actual owner observations only. No process handles, control, policy or acceptance authority.
// A false settlement observation means unproved closure, not evidence that a live orphan exists.
public sealed class ProtectedPolicyCaptureDiagnosticException : Exception
{
    internal ProtectedPolicyCaptureDiagnosticException(BoundedCommandCaptureResult capture, string reason, Exception? originalFailure = null)
        : base(reason.Length > 1024 ? reason[..1024] : reason, originalFailure) => ActualCapture = capture;
    public BoundedCommandCaptureResult ActualCapture { get; }
    public bool ResidualProcessUncertain => !ActualCapture.Command.OwnedProcessQuiescent;
}

public static class ProtectedPolicyReadFailure
{
    // Failure classification/data routing only. It cannot produce a policy, opaque owner or success proof.
    public static Exception Create(BoundedCommandCaptureResult capture, CancellationToken caller, string reason)
    {
        ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(reason);
        var diagnostic = new ProtectedPolicyCaptureDiagnosticException(capture, reason);
        if (caller.IsCancellationRequested || capture.Command.Cancelled)
            return new OperationCanceledException(reason, diagnostic, caller);
        if (capture.Command.TimedOut) return new TimeoutException(reason, diagnostic);
        return new ReviewPolicyException(reason, diagnostic);
    }

    // Preserve a subsequent parse/path/deadline failure and its original cause. The captured result
    // is already immutable with cloned raw-byte accessors; no mutable invocation collections are stored.
    internal static Exception Attach(Exception failure, BoundedCommandCaptureResult capture, CancellationToken caller)
    {
        if (failure.InnerException is ProtectedPolicyCaptureDiagnosticException) return failure;
        var diagnostic = new ProtectedPolicyCaptureDiagnosticException(capture, failure.Message, failure);
        if (caller.IsCancellationRequested || failure is OperationCanceledException)
            return new OperationCanceledException(failure.Message, diagnostic, caller);
        if (failure is TimeoutException) return new TimeoutException(failure.Message, diagnostic);
        return new ReviewPolicyException(failure.Message, diagnostic);
    }
}
