namespace DeliveryForge.Evidence;

/// <summary>Immutable observational data, without process handles or signaling authority.
/// Quotas and prefixes count raw bytes, including partial UTF8 sequences, rather than UTF16 characters.</summary>
public sealed class BoundedCommandCaptureResult
{
    private readonly byte[] _standardOutput;
    private readonly byte[] _standardError;

    internal BoundedCommandCaptureResult(CommandResult command, byte[] standardOutput, byte[] standardError,
        bool captureBoundExceeded, bool rootExitObserved, bool standardOutputDrainCompleted, bool standardErrorDrainCompleted)
    {
        Command = command;
        _standardOutput = (byte[])standardOutput.Clone();
        _standardError = (byte[])standardError.Clone();
        CaptureBoundExceeded = captureBoundExceeded;
        RootExitObserved = rootExitObserved;
        StandardOutputDrainCompleted = standardOutputDrainCompleted;
        StandardErrorDrainCompleted = standardErrorDrainCompleted;
    }

    public CommandResult Command { get; }
    public byte[] StandardOutputBytes => (byte[])_standardOutput.Clone();
    public byte[] StandardErrorBytes => (byte[])_standardError.Clone();
    public bool CaptureBoundExceeded { get; }
    public bool RootExitObserved { get; }
    public bool StandardOutputDrainCompleted { get; }
    public bool StandardErrorDrainCompleted { get; }

    private BoundedCommandCaptureResult(BoundedCommandCaptureResult actual, CommandResult command)
    {
        Command = command;
        // Internal immutable storage is never returned directly; public byte getters still clone.
        _standardOutput = actual._standardOutput; _standardError = actual._standardError;
        CaptureBoundExceeded = actual.CaptureBoundExceeded; RootExitObserved = actual.RootExitObserved;
        StandardOutputDrainCompleted = actual.StandardOutputDrainCompleted;
        StandardErrorDrainCompleted = actual.StandardErrorDrainCompleted;
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
