namespace DeliveryForge.Evidence;

/// <summary>Point-in-time DATA only. No process handle, membership, control or settlement authority.</summary>
public enum CommandStartupState { NotObserved, NotApplicable, NotAttempted, AttemptedUnknown, Established, Refused }

/// <summary>Immutable, bounded original creator sequencing DATA; absence is not known closure.</summary>
public sealed record CommandStartupObservation
{
    public CommandStartupState Creation { get; }
    public CommandStartupState RootHandle { get; }
    public CommandStartupState JobAssignment { get; }
    public CommandStartupState Resume { get; }
    public CommandStartupState ReaderEndpoints { get; }
    public CommandStartupState StandardOutputDrain { get; }
    public CommandStartupState StandardErrorDrain { get; }
    public string LastStage { get; }
    public string? FailureStage { get; }
    public double OriginalElapsedSeconds { get; }
    public double OriginalTotalSeconds { get; }
    public double OriginalWorkSeconds { get; }
    public string? Reason { get; }

    public CommandStartupObservation(CommandStartupState creation, CommandStartupState rootHandle,
        CommandStartupState jobAssignment, CommandStartupState resume, CommandStartupState readerEndpoints,
        CommandStartupState standardOutputDrain, CommandStartupState standardErrorDrain, string lastStage,
        string? failureStage, double originalElapsedSeconds, double originalTotalSeconds, double originalWorkSeconds,
        string? reason)
    {
        foreach (var state in new[] { creation, rootHandle, jobAssignment, resume, readerEndpoints, standardOutputDrain, standardErrorDrain })
            if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(creation));
        if (lastStage is null || lastStage.Length > 128 || failureStage?.Length > 128 || reason?.Length > 2048)
            throw new ArgumentException("Startup diagnostic text exceeds its bounded DATA shape.");
        if (!double.IsFinite(originalElapsedSeconds) || !double.IsFinite(originalTotalSeconds) || !double.IsFinite(originalWorkSeconds) ||
            originalElapsedSeconds < 0 || originalTotalSeconds < 0 || originalWorkSeconds < 0 || originalWorkSeconds > originalTotalSeconds)
            throw new ArgumentOutOfRangeException(nameof(originalElapsedSeconds));
        Creation = creation; RootHandle = rootHandle; JobAssignment = jobAssignment; Resume = resume;
        ReaderEndpoints = readerEndpoints; StandardOutputDrain = standardOutputDrain; StandardErrorDrain = standardErrorDrain;
        LastStage = lastStage; FailureStage = failureStage; OriginalElapsedSeconds = originalElapsedSeconds;
        OriginalTotalSeconds = originalTotalSeconds; OriginalWorkSeconds = originalWorkSeconds; Reason = reason;
    }

    internal static string Clip(string value, int maximum) => value.Length > maximum ? value[..maximum] : value;
}
