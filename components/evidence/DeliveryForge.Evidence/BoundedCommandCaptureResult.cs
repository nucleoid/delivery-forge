namespace DeliveryForge.Evidence;

// Observational scalars only; they cannot authorize an adopted process or a new lifetime.
public enum BoundedCaptureOwnershipKind { LegacyObservation, LinuxCreationOwnedScope }

public sealed record BoundedCaptureTimingObservation(long OriginalStartedTimestamp, long? CleanupStartedTimestamp, long StopwatchFrequency);

/// <summary>Immutable observational data, without process handles or signaling authority.
/// Quotas and prefixes count raw bytes, including partial UTF8 sequences, rather than UTF16 characters.</summary>
public sealed class BoundedCommandCaptureResult
{
    private readonly byte[] _standardOutput;
    private readonly byte[] _standardError;

    internal BoundedCommandCaptureResult(CommandResult command, byte[] standardOutput, byte[] standardError,
        bool captureBoundExceeded, bool rootExitObserved, bool standardOutputDrainCompleted, bool standardErrorDrainCompleted,
        BoundedCaptureTimingObservation? timing = null, BoundedCaptureOwnershipKind ownershipKind = BoundedCaptureOwnershipKind.LegacyObservation)
    {
        Command = command; Timing = timing; OwnershipKind = ownershipKind;
        _standardOutput = (byte[])standardOutput.Clone();
        _standardError = (byte[])standardError.Clone();
        CaptureBoundExceeded = captureBoundExceeded;
        RootExitObserved = rootExitObserved;
        StandardOutputDrainCompleted = standardOutputDrainCompleted;
        StandardErrorDrainCompleted = standardErrorDrainCompleted;
    }

    public CommandResult Command { get; }
    public BoundedCaptureOwnershipKind OwnershipKind { get; }
    public BoundedCaptureTimingObservation? Timing { get; }
    public byte[] StandardOutputBytes => (byte[])_standardOutput.Clone();
    public byte[] StandardErrorBytes => (byte[])_standardError.Clone();
    public bool CaptureBoundExceeded { get; }
    public bool RootExitObserved { get; }
    public bool StandardOutputDrainCompleted { get; }
    public bool StandardErrorDrainCompleted { get; }

    private BoundedCommandCaptureResult(BoundedCommandCaptureResult actual, CommandResult command)
    {
        Command = command; Timing = actual.Timing; OwnershipKind = actual.OwnershipKind;
        // Internal immutable storage is never returned directly; public byte getters still clone.
        _standardOutput = actual._standardOutput; _standardError = actual._standardError;
        CaptureBoundExceeded = actual.CaptureBoundExceeded; RootExitObserved = actual.RootExitObserved;
        StandardOutputDrainCompleted = actual.StandardOutputDrainCompleted;
        StandardErrorDrainCompleted = actual.StandardErrorDrainCompleted;
    }

    // Ordinary ICommandExecutor consumers cannot see the byte-quota flag.
    // Refuse their success projection while retaining the actual bounded capture
    // and its physical exit/EOF/owner observations unchanged.
    internal CommandResult ProjectOrdinaryCommand()
    {
        if (!CaptureBoundExceeded) return Command;
        const string reason = "Bounded command output exceeded the byte capture limit; retained output is a prefix.";
        var actual = Command.LifecycleDiagnostics ?? new CommandLifecycleDiagnostics(
            false, false, null, null, false, RootExitObserved, RootExitObserved,
            StandardOutputDrainCompleted && StandardErrorDrainCompleted, 0,
            new(StandardOutputDrainCompleted, false, false, StandardOutputDrainCompleted ? Command.StandardOutput : null),
            new(StandardErrorDrainCompleted, false, false, StandardErrorDrainCompleted ? Command.StandardError : null));
        return Command with
        {
            OwnedProcessQuiescent = false,
            LifecycleDiagnostics = actual with
            {
                NativeFailure = CommandStartupObservation.Clip(reason +
                    (actual.NativeFailure is null ? "" : " " + actual.NativeFailure), 2048)
            }
        };
    }

    internal BoundedCommandCaptureResult RefuseLateCompletion() => new(this, Command with
    {
        OwnedProcessQuiescent = false,
        LifecycleDiagnostics = Command.LifecycleDiagnostics is null ? null : Command.LifecycleDiagnostics with
        {
            NativeFailure = Command.LifecycleDiagnostics.NativeFailure ?? "Complete capture DATA exceeded the original lifetime allowance."
        }
    });
}
