using System.Text.Json;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence;

public enum InstalledToolPurpose { ChangedCode = 0, InitialBaselineCapture = 1 }

// Measurement DATA only. Public values cannot be supplied to the actual scope as
// a producer result, reviewed baseline, registry approval or execution capability.
public sealed record InitialBaselineSource(string ContextId, string RepositoryPath, string Sha256, bool Generated)
{
    public string LogicalPath { get; init; } = RepositoryPath;
}
public sealed record InitialBaselineCallable(string ContextId, string Path, string MethodIdentity,
    string? SourceSha256, decimal? Coverage, decimal? Crap)
{
    public string? RepositoryPath { get; init; }
}
public sealed record InitialBaselineLineCoverage(int ExecutableLines, int CoveredLines, decimal? Percent,
    int UnmeasuredLines, bool CompilerCensusComplete, string SourceSetIdentity, string Reason);
public sealed record InitialBaselineMutation(int? Enumerated, int Selected, int Executed, int Killed,
    int Survived, int Uncovered, int Omitted, int CompileInvalid, int Errors, string Baseline, bool FullProject);

public sealed class InitialBaselineCaptureData
{
    internal InitialBaselineCaptureData(string tool, GateOutcome rawOutcome, bool complete, bool mayCollectNextGate,
        string reportIdentity, IEnumerable<InitialBaselineSource> sources, IEnumerable<InitialBaselineCallable> callables,
        InitialBaselineLineCoverage? lines, InitialBaselineMutation? mutation, IEnumerable<string> diagnostics)
    {
        Tool = tool; RawOutcome = rawOutcome; Complete = complete; MayCollectNextGate = mayCollectNextGate;
        ReportIdentity = reportIdentity; Sources = Array.AsReadOnly(sources.ToArray());
        Callables = Array.AsReadOnly(callables.ToArray()); Lines = lines; Mutation = mutation;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }
    public string Tool { get; }
    public GateOutcome RawOutcome { get; }
    public bool Complete { get; }
    // Observation of a known completed capture, not an admission or new budget.
    // The actual scope/original/parent must still admit every subsequent gate.
    public bool MayCollectNextGate { get; }
    public string ReportIdentity { get; }
    public IReadOnlyList<InitialBaselineSource> Sources { get; }
    public IReadOnlyList<InitialBaselineCallable> Callables { get; }
    public InitialBaselineLineCoverage? Lines { get; }
    public InitialBaselineMutation? Mutation { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool ProductionAuthority => false;
}

internal static class InitialBaselineCapture
{
    internal static void RequirePurpose(InstalledToolPurpose purpose)
    {
        if (purpose is not (InstalledToolPurpose.ChangedCode or InstalledToolPurpose.InitialBaselineCapture))
            throw new EvidenceWriteException("Unknown installed-tool purpose.");
    }
    internal static InitialBaselineCaptureData Mutation(byte[] report, JsonElement root, JsonElement configuration, GateOutcome outcome)
    {
        var data = Mutate4CSharpReportV2.ReadData(report); // Complete official reducer/ledger validation remains mandatory.
        var projects = configuration.GetProperty("projects").EnumerateArray().ToArray();
        var scope = root.GetProperty("scopePlan");
        var units = scope.GetProperty("projectUnits").EnumerateArray().ToArray();
        var configured = projects.Select(p => p.GetProperty("project").GetString()!).Order(StringComparer.Ordinal).ToArray();
        var observed = units.Select(p => p.GetProperty("project").GetString()!).Order(StringComparer.Ordinal).ToArray();
        var suites = configuration.GetProperty("testSuites").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("path").GetString()!, StringComparer.Ordinal);
        var full = configuration.GetProperty("version").GetInt32() == 2 &&
            configuration.GetProperty("semanticProfile").GetString() == "resolved-project-graph-v1" &&
            configuration.GetProperty("exclusions").GetArrayLength() == 0 && configured.Length > 0 &&
            configured.Distinct(StringComparer.Ordinal).Count() == configured.Length && configured.SequenceEqual(observed, StringComparer.Ordinal) &&
            scope.GetProperty("isComplete").GetBoolean() && units.All(p => p.GetProperty("expansion").GetString() == "FULL_PROJECT");
        foreach (var project in projects)
        {
            var name = project.GetProperty("project").GetString();
            var matches = units.Where(u => u.GetProperty("project").GetString() == name).Take(2).ToArray();
            if (matches.Length != 1) { full = false; continue; }
            var expectedTests = project.GetProperty("testSuites").EnumerateArray().Select(s => suites[s.GetString()!]).Order(StringComparer.Ordinal);
            var actualTests = matches[0].GetProperty("tests").EnumerateArray().Select(s => s.GetString()!).Order(StringComparer.Ordinal);
            if (!expectedTests.SequenceEqual(actualTests, StringComparer.Ordinal)) full = false;
        }
        var baseline = root.GetProperty("baseline").GetString()!;
        var conditions = root.GetProperty("incompleteConditions").EnumerateArray().Select(c => c.GetProperty("code").GetString()!).ToArray();
        var diagnostic = root.TryGetProperty("diagnosticPartial", out var partial) && partial.GetBoolean();
        var complete = full && !diagnostic && baseline == "GREEN" && conditions.Length == 0 && data.Enumerated is > 0 &&
            data.Enumerated == data.Selected && data.Omitted == 0 && data.Errors == 0 && data.Executed + data.FreshUncovered == data.Selected &&
            outcome is GateOutcome.Pass or GateOutcome.Fail or GateOutcome.NotApplicable;
        // Official CreateFull leaves top-level conditions empty for a cap but
        // gives EACH omitted ledger unit one BUDGET_OMITTED evidence entry.
        // Generic omissions cannot authorize continuing the SAME parent.
        var mutationCap = root.GetProperty("policy").GetProperty("mutationCap").GetInt32();
        var omittedUnits = root.GetProperty("units").EnumerateArray().Where(unit => unit.GetProperty("disposition").GetString() == "OMITTED").ToArray();
        var budgetOnly = full && !diagnostic && baseline == "GREEN" && data.Enumerated is int enumerated && enumerated > mutationCap &&
            data.Omitted > 0 && data.Errors == 0 && data.Selected - data.Omitted == mutationCap &&
            omittedUnits.All(unit => unit.GetProperty("evidence").GetArrayLength() == 1 &&
                unit.GetProperty("evidence")[0].GetProperty("kind").GetString() == "BUDGET_OMITTED") &&
            conditions.All(c => c == "BUDGET_OMITTED") && outcome == GateOutcome.Incomplete;
        var measurement = new InitialBaselineMutation(data.Enumerated, data.Selected, data.Executed, data.Killed,
            data.Survived, data.FreshUncovered, data.Omitted, data.CompileInvalid, data.Errors, baseline, full);
        return new("mutate4csharp", outcome, complete, complete || budgetOnly, InstalledToolFiles.Identity(report), [], [], null, measurement,
            new[] { "Raw producer outcome retained; capture never approves mutation debt or omitted scope.",
                full ? "Official complete configured FULL_PROJECT scope bound." : "Complete configured FULL_PROJECT scope was not established." }.Concat(conditions));
    }
    internal static void RequireCompleteCallableScope(JsonElement approvedPolicy, JsonElement manifest)
    {
        if (approvedPolicy.GetProperty("scope").GetString() != "all" || approvedPolicy.GetProperty("exclusions").GetArrayLength() != 0)
            throw new EvidenceWriteException("Initial capture requires the complete approved callable scope without exclusions.");
        var expected = approvedPolicy.GetProperty("productionProjects").EnumerateArray().Select(p => p.GetString()!).Order(StringComparer.Ordinal).ToArray();
        var observed = manifest.GetProperty("contexts").EnumerateArray().Select(c => c.GetProperty("project").GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0 || expected.Distinct(StringComparer.Ordinal).Count() != expected.Length || expected.Any(project => !observed.Contains(project, StringComparer.Ordinal)))
            throw new EvidenceWriteException("Initial capture omits an approved production root from the full compiler dependency closure.");
    }
    internal static IReadOnlyList<InitialBaselineSource> Sources(JsonElement manifest)
    {
        var rows = new List<InitialBaselineSource>();
        foreach (var context in manifest.GetProperty("contexts").EnumerateArray())
        {
            var id = context.GetProperty("id").GetString()!;
            foreach (var input in context.GetProperty("inputs").EnumerateArray())
            {
                if (input.GetProperty("role").GetString() is not ("source" or "compiler-source")) continue;
                // Nonrepository compiler inputs stay explicit; no invented authored locator.
                var path = input.TryGetProperty("repositoryPath", out var relative) && relative.ValueKind == JsonValueKind.String
                    ? relative.GetString()! : "";
                if (path.Length != 0) InstalledToolProfile.Relative(path);
                var logical = input.GetProperty("logicalPath").GetString()!; InstalledToolProfile.Relative(logical);
                rows.Add(new(id, path, input.GetProperty("sha256").GetString()!, input.GetProperty("generated").GetBoolean()) { LogicalPath = logical });
                if (rows.Count > 100000) throw new EvidenceWriteException("Initial capture source membership exceeds the existing finite report profile.");
            }
        }
        return Array.AsReadOnly(rows.OrderBy(r => r.ContextId, StringComparer.Ordinal).ThenBy(r => r.RepositoryPath, StringComparer.Ordinal).ThenBy(r => r.LogicalPath, StringComparer.Ordinal).ThenBy(r => r.Sha256, StringComparer.Ordinal).ToArray());
    }
    internal static InitialBaselineCaptureData Callables(byte[] report, JsonElement root, IReadOnlyList<InitialBaselineSource> sources,
        InitialBaselineLineCoverage? lines, GateOutcome outcome, IReadOnlyList<string> expectedTestProjects, string repository)
    {
        if (Crap4CSharpResultSchema.Validate(root) is not null) throw new EvidenceWriteException("Complete supported callable capture report required.");
        var evaluation = root.GetProperty("evaluation"); var run = root.GetProperty("run");
        var metrics = new List<InitialBaselineCallable>();
        foreach (var metric in evaluation.GetProperty("metrics").EnumerateArray())
        {
            var context = metric.GetProperty("contextId").GetString()!; var path = metric.GetProperty("path").GetString()!;
            InstalledToolProfile.Relative(path);
            var matches = sources.Where(s => s.ContextId == context && s.LogicalPath == path && !s.Generated).Take(2).ToArray();
            decimal? Number(string name) => metric.GetProperty(name).ValueKind == JsonValueKind.Number && metric.GetProperty(name).TryGetDecimal(out var number) ? number : null;
            metrics.Add(new(context, path, metric.GetProperty("methodIdentity").GetString()!, matches.Length == 1 ? matches[0].Sha256 : null, Number("coverage"), Number("crap"))
            { RepositoryPath = matches.Length == 1 && matches[0].RepositoryPath.Length != 0 ? matches[0].RepositoryPath : null });
            if (metrics.Count > 100000) throw new EvidenceWriteException("Initial callable inventory exceeds the existing finite report profile.");
        }
        var tests = run.TryGetProperty("testOutcomes", out var outcomes) && outcomes.ValueKind == JsonValueKind.Array && outcomes.GetArrayLength() > 0 && outcomes.EnumerateArray().All(t =>
            t.GetProperty("status").GetString() == "completed" && t.GetProperty("exitCode").ValueKind == JsonValueKind.Number && t.GetProperty("exitCode").GetInt32() == 0);
        var observedTests = run.TryGetProperty("testOutcomes", out var observedOutcomes) && observedOutcomes.ValueKind == JsonValueKind.Array
            ? observedOutcomes.EnumerateArray().Select(t => t.GetProperty("project").GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [];
        // Actual producer test paths are canonical absolute paths. Compare DATA
        // identities only; no path from a report is opened or granted authority.
        string TestPath(string path)
        {
            if (path.Length > 16384 || path.Any(char.IsControl)) throw new EvidenceWriteException("Invalid captured test project path.");
            var full = Path.GetFullPath(path, repository);
            if (!InstalledToolGateProvider.Inside(full, repository)) throw new EvidenceWriteException("Captured test project escapes the actual repository.");
            return full;
        }
        var expectedTests = expectedTestProjects.Select(TestPath).ToHashSet(InstalledToolFiles.PathComparer);
        var actualTests = observedTests.Select(TestPath).ToHashSet(InstalledToolFiles.PathComparer);
        tests = tests && expectedTests.Count > 0 && expectedTests.Count == expectedTestProjects.Count && expectedTests.SetEquals(actualTests);
        var complete = (outcome is GateOutcome.Pass or GateOutcome.Fail) && tests &&
            run.GetProperty("status").GetString() == "completed" && evaluation.GetProperty("decision").GetProperty("completed").GetBoolean() &&
            root.GetProperty("complexityRulesetVersion").GetString() == "callables-v1" && metrics.Count > 0 &&
            metrics.Count == evaluation.GetProperty("coverage").GetProperty("methods").GetInt32() &&
            evaluation.GetProperty("coverage").GetProperty("unknown").GetInt32() == 0 &&
            metrics.All(m => m.SourceSha256 is not null && m.RepositoryPath is not null && m.Coverage is >= 0 and <= 1 && m.Crap is >= 0) &&
            metrics.Select(m => (m.ContextId, m.Path, m.MethodIdentity)).Distinct().Count() == metrics.Count &&
            lines is { CompilerCensusComplete: true, UnmeasuredLines: 0, ExecutableLines: > 0, Percent: not null };
        return new("crap4csharp", outcome, complete, complete, InstalledToolFiles.Identity(report), sources, metrics, lines, null,
            ["Full current coverage and callable scores are measurements; known threshold debt remains explicit and unapproved.",
             tests ? "Actual unfiltered test outcomes passed." : "Actual complete passing test outcomes were not established."]);
    }
}
