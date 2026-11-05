using System.Runtime.CompilerServices;

namespace DeliveryForge.Tool.Codex;

internal enum CodexProtocolFailureKind
{
    RetainedUnknown, IoLedgerBound, MissingOperationTask, OutgoingFrameBound,
    OutgoingJsonlBound, OutgoingBytesBound, TruncatedFrame, IncomingBytesBound,
    EmptyOrExcessFrame, IncomingLineBound, EnvelopeObject, RpcVersion,
    ResponseIdType, MethodShape, MixedMethodResponse, NotificationTimestamp,
    EnvelopeMember, MissingIdAndMethod, ResponseShape, MalformedRuntimeFrame,
    UnexpectedEof, ResponseCorrelation, ConfigurationCorrelation, TraceBound,
    TrustMethodCorrelation, MissingConfinedRoots, TerminalStatus,
    TerminalThread, DuplicateOrExcessTerminal
}

// Diagnostics only. Values retain no exception/key, payload, message or owner.
internal sealed record CodexProtocolFailureFacts(string Code, long? ReadBytes, int? Messages);

internal static class CodexProtocolFailure
{
    static readonly ConditionalWeakTable<Exception, CodexProtocolFailureFacts> facts = new();

    // The caller supplies its existing source-owned literal, never wire text.
    // Return the same newly created base IOException; do not wrap or replace it.
    internal static IOException Create(CodexProtocolFailureKind kind, string literal, long? readBytes = null, int? messages = null)
    {
        var error = new IOException(literal);
        try
        {
            if (Enum.GetName(kind) is { } code)
                facts.Add(error, new(code,
                    readBytes is >= 0 and <= 16781312 ? readBytes : null,
                    messages is >= 0 and <= 4097 ? messages : null));
        }
        catch (Exception) { /* Optional diagnostic failure cannot replace the original guard. */ }
        return error;
    }

    // Only the caught graph can name a fact. Equal facts on different exception
    // objects are ambiguous; repeated references to one object are not.
    internal static CodexProtocolFailureFacts? FindSingle(Exception actual)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { actual };
        var pending = new Queue<Exception>(); pending.Enqueue(actual);
        CodexProtocolFailureFacts? found = null;
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
