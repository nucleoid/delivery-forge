using System.Runtime.CompilerServices;
using DeliveryForge.Evidence;

namespace DeliveryForge.Tool.Claude;

internal enum ClaudeReviewerObservationOperation { Version, AuthStatus }

// Code-owned diagnostic scalars only: no arguments, output, hashes, account
// values, exception Data or execution authority are retained here.
internal sealed record ClaudeReviewerObservationFacts(string Code, string Operation, int? ExitCode,
    bool TimedOut, bool Cancelled, bool RootExitObserved, bool OwnedProcessQuiescent,
    bool StandardOutputDrainCompleted, bool StandardErrorDrainCompleted, bool CaptureBoundExceeded,
    bool NativeFailurePresent, bool? OwnedTreeCreationOwned, bool? OwnedTreeSettlementObserved, int? OwnedTreeActiveProcesses,
    string? OwnershipKind, int? StandardOutputBytes, int? StandardErrorBytes);

internal static class ClaudeReviewerObservationFailure
{
    static readonly ConditionalWeakTable<Exception, ClaudeReviewerObservationFacts> facts = new();

    internal static IOException CaptureIncomplete(ClaudeReviewerObservationOperation operation, BoundedCommandCaptureResult capture) =>
        Create("CaptureIncomplete", "Actual reviewer observation is incomplete.", operation, capture);

    internal static IOException VersionMismatch(BoundedCommandCaptureResult capture) =>
        Create("VersionMismatch", "Actual reviewer version differs from its selected image profile.", ClaudeReviewerObservationOperation.Version, capture);

    static IOException Create(string code, string literal, ClaudeReviewerObservationOperation operation, BoundedCommandCaptureResult capture)
    {
        var error = new IOException(literal);
        try
        {
            if (Enum.GetName(operation) is { } name)
            {
                var output = capture.StandardOutputBytes.Length;
                var stderr = capture.StandardErrorBytes.Length;
                var lifecycle = capture.Command.LifecycleDiagnostics;
                var ownedTree = lifecycle?.OwnedTree;
                var active = ownedTree?.ActiveProcesses;
                facts.Add(error, new(code, name, capture.Command.ExitCode, capture.Command.TimedOut, capture.Command.Cancelled,
                    capture.RootExitObserved, capture.Command.OwnedProcessQuiescent,
                    capture.StandardOutputDrainCompleted, capture.StandardErrorDrainCompleted, capture.CaptureBoundExceeded,
                    lifecycle?.NativeFailure is not null, ownedTree?.CreationOwned, ownedTree?.SettlementObserved,
                    active is >= 0 and <= 4096 ? active : null, Enum.GetName(capture.OwnershipKind),
                    operation == ClaudeReviewerObservationOperation.Version && output <= 65536 ? output : null,
                    operation == ClaudeReviewerObservationOperation.Version && stderr <= 65536 ? stderr : null));
            }
        }
        catch (Exception) { /* Optional facts cannot replace the original observation refusal. */ }
        return error;
    }

    // Reuse the existing finite, reference-based caught-graph rule. Separate
    // exceptions are ambiguous even when their diagnostic values are equal.
    internal static ClaudeReviewerObservationFacts? FindSingle(Exception actual)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { actual };
        var pending = new Queue<Exception>(); pending.Enqueue(actual);
        ClaudeReviewerObservationFacts? found = null;
        while (pending.TryDequeue(out var error))
        {
            if (facts.TryGetValue(error, out var candidate))
            {
                if (found is not null) return null;
                found = candidate;
            }
            if (error is AggregateException wide && wide.InnerExceptions.Count > 16) return null;
            IEnumerable<Exception> causes = error is AggregateException aggregate ? aggregate.InnerExceptions :
                error.InnerException is { } inner ? [inner] : [];
            foreach (var cause in causes)
            {
                if (seen.Contains(cause)) continue;
                if (seen.Count >= 16) return null;
                seen.Add(cause); pending.Enqueue(cause);
            }
        }
        return found;
    }
}
