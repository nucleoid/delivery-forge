namespace DeliveryForge.Evidence;

/// <summary>Point-in-time DATA only. No process handle, membership, control or settlement authority.</summary>
public enum CommandStartupState { NotObserved, NotApplicable, NotAttempted, AttemptedUnknown, Established }

/// <summary>Original creator sequencing observations; absence is not known closure.
/// Stage strings are bounded to128 characters and reason to2048 by the producing owner.</summary>
public sealed record CommandStartupObservation(
    CommandStartupState Creation, CommandStartupState RootHandle,
    CommandStartupState JobAssignment, CommandStartupState Resume,
    CommandStartupState ReaderEndpoints, CommandStartupState StandardOutputDrain,
    CommandStartupState StandardErrorDrain, string LastStage, string? FailureStage,
    double OriginalElapsedSeconds, double OriginalTotalSeconds, double OriginalWorkSeconds,
    string? Reason);
