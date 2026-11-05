namespace DeliveryForge.Evidence.Adapters;

public sealed class Mutate4CSharpAdapter
{
    public NormalizedEvidence Normalize(ReadOnlySpan<byte> report, ToolCapability capability)
    {
        var parsed = Parse(report, capability);
        if (parsed.Outcome == GateOutcome.Error) return parsed;
        return NormalizedEvidence.Incomplete(
            "A complete mutation report is DATA until joined to its current installed invocation.",
            capability.Fixture, "Use InstalledToolGateProvider; historical reports cannot issue a current gate.");
    }

    // Only the provider's retained original process/report binding calls this path. Still no
    // production, baseline, exemption, review or publication authority is issued here.
    internal static NormalizedEvidence NormalizeCurrent(ReadOnlySpan<byte> report, ToolCapability capability, int actualExit)
    {
        var result = Parse(report, capability);
        if (result.Outcome == GateOutcome.Error) return result;
        try
        {
            var data = Mutate4CSharpReportV2.ReadData(report);
            return actualExit == data.ClaimedExitCode ? result :
                NormalizedEvidence.Error("Mutation process exit disagrees with the complete current report.", capability.Fixture);
        }
        catch (MutationReportFormatException exception)
        {
            return NormalizedEvidence.Error(exception.Message, capability.Fixture);
        }
    }

    private static NormalizedEvidence Parse(ReadOnlySpan<byte> report, ToolCapability capability)
    {
        if (capability.Tool != "mutate4csharp" || !capability.Supported || capability.FormatVersion != "2")
            return NormalizedEvidence.Error("Mutation capability or report format is unsupported.", capability.Fixture);
        try
        {
            var data = Mutate4CSharpReportV2.ReadData(report);
            return new(data.ClaimedOutcome,
                $"Mutation {data.ClaimedOutcome}: {data.Selected} selected, {data.Killed} killed, " +
                $"{data.Survived} survived, {data.FreshUncovered} uncovered, {data.Omitted} omitted, {data.Errors} errors.",
                capability.Fixture, false, capability.Limitations ?? [], data.NotApplicableRationale);
        }
        catch (MutationReportFormatException exception)
        {
            return NormalizedEvidence.Error("Invalid mutation producer report: " + exception.Message, capability.Fixture);
        }
    }
}