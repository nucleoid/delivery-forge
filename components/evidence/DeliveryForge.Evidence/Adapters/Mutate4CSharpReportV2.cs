namespace DeliveryForge.Evidence.Adapters;

// Parsed producer claims are data. They never issue capability, baseline or gate authority.
internal sealed record MutationReportData(
    GateOutcome ClaimedOutcome,
    int ClaimedExitCode,
    string RunId,
    int? Enumerated,
    int Selected,
    int Executed,
    int Killed,
    int Survived,
    int FreshUncovered,
    int Omitted,
    int CompileInvalid,
    int Errors,
    string? NotApplicableRationale);

internal sealed class MutationReportFormatException(string message) : Exception(message);

internal static class Mutate4CSharpReportV2
{
    // Initial named consumer profile limit, not a claim about every producer-emitted report.
    internal const int MaximumReportBytes = 8 * 1024 * 1024;

    internal static MutationReportData ReadData(ReadOnlySpan<byte> report) =>
        throw new MutationReportFormatException("Complete producer schema2 semantic reader is not implemented.");
}
