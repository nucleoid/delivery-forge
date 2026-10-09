namespace DeliveryForge.Evidence.Adapters;

public sealed record DotNetRunEvidence(
    string SdkVersion,
    int ExitCode,
    bool CompilationOccurred,
    int DiscoveredTests,
    int ExecutedTests,
    int PassedTests,
    int FailedTests,
    int SkippedTests,
    bool CoverageComplete,
    IReadOnlyList<string> ExpectedProjects,
    IReadOnlyList<string> ObservedProjects,
    bool TimedOut);

public sealed class DotNetAdapter
{
    public NormalizedEvidence Normalize(DotNetRunEvidence evidence)
    {
        if (!Version.TryParse(evidence.SdkVersion, out var sdk) || sdk.Major != 10)
            return NormalizedEvidence.Incomplete($"Unsupported or malformed .NET SDK version '{evidence.SdkVersion}'.");
        if (evidence.TimedOut)
            return NormalizedEvidence.Incomplete("The .NET command timed out.");
        if (evidence.ExitCode != 0 || evidence.FailedTests > 0)
            return new NormalizedEvidence(GateOutcome.Fail, "The .NET build or test command failed.", false, true, []);
        if (!evidence.CompilationOccurred)
            return NormalizedEvidence.Incomplete("No trusted compiler invocation or clean-build proof was observed.");
        if (evidence.DiscoveredTests == 0 || evidence.ExecutedTests == 0)
            return NormalizedEvidence.Incomplete("The .NET run discovered or executed zero tests.");
        if (evidence.ExecutedTests != evidence.DiscoveredTests ||
            evidence.PassedTests + evidence.FailedTests + evidence.SkippedTests != evidence.ExecutedTests)
            return NormalizedEvidence.Incomplete("The .NET test counts are incomplete or inconsistent.");
        if (!evidence.CoverageComplete)
            return NormalizedEvidence.Incomplete("Required coverage evidence is missing or incomplete.");

        var omitted = evidence.ExpectedProjects.Except(evidence.ObservedProjects, StringComparer.Ordinal).ToArray();
        if (omitted.Length > 0)
            return NormalizedEvidence.Incomplete($"Required test projects were omitted: {string.Join(", ", omitted)}.");

        return NormalizedEvidence.Pass("Pinned .NET SDK build/test evidence is complete.");
    }
}
