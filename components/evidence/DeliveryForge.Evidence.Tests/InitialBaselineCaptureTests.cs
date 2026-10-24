using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence.Tests;

// DATA reducers and capability refusal controls, not installed/native authority.
// Genuine PE/PDB/source coverage controls are in CompilerLineCensusTests.
public sealed class InitialBaselineCaptureTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Unknown_purpose_cannot_enter_the_capture_route(int purpose) =>
        Assert.Throws<EvidenceWriteException>(() => InitialBaselineCapture.RequirePurpose((InstalledToolPurpose)purpose));

    [Fact]
    public void Default_request_retains_changed_code_purpose()
    {
        var request = new InstalledToolGateRequest("mutation", TestEvidence.ResolvedPolicy(), TestEvidence.Repository(),
            "base", null, TimeSpan.FromSeconds(30), Path.GetTempPath());
        Assert.Equal(InstalledToolPurpose.ChangedCode, request.Purpose);
    }

    [Fact]
    public void Caller_constructed_or_value_equivalent_gate_result_cannot_read_an_actual_capture()
    {
        var scope = new InstalledToolHostScope(new Original());
        var fake = new GateRunResult(GateOutcome.Pass, "caller", false, false, [], false,
            new("unused", [], Path.GetTempPath()), TestEvidence.CommandResult(), TestEvidence.Repository(), TestEvidence.Repository(),
            TestEvidence.Identity('a'), TestEvidence.Identity('b'), TestEvidence.Identity('c'), "all", new Dictionary<string, string>(), "unused", null);
        Assert.Throws<EvidenceWriteException>(() => scope.CaptureInitialBaseline(fake));
        Assert.Throws<EvidenceWriteException>(() => scope.CaptureInitialBaseline(fake with { }));
    }

    [Fact]
    public void Complete_mutation_capture_requires_the_actual_whole_configured_project_and_ledger()
    {
        var root = Mutation();
        var good = ReadMutation(root);
        Assert.True(good.Complete); Assert.True(good.MayCollectNextGate); Assert.False(good.ProductionAuthority);
        Assert.Equal(GateOutcome.Pass, good.RawOutcome); Assert.Equal(1, good.Mutation!.Enumerated);
        root["scopePlan"]!["projectUnits"]![0]!["expansion"] = "CHANGED_DECLARATIONS";
        var partial = ReadMutation(root);
        Assert.False(partial.Complete); Assert.False(partial.MayCollectNextGate); Assert.False(partial.Mutation!.FullProject);
    }

    [Fact]
    public void Mutation_suite_omission_cannot_become_a_complete_capture()
    {
        var root = Mutation();
        root["scopePlan"]!["projectUnits"]![0]!["tests"] = new JsonArray("foreign.csproj");
        var result = ReadMutation(root);
        Assert.False(result.Complete); Assert.False(result.MayCollectNextGate);
    }

    [Fact]
    public void Contradictory_mutation_ledger_is_refused_by_the_unchanged_official_reducer()
    {
        var root = Mutation(); root["counts"]!["executed"] = 0;
        Assert.Throws<MutationReportFormatException>(() => ReadMutation(root));
    }

    [Fact]
    public void Known_cap_omission_stays_incomplete_but_can_leave_the_same_parent_available_for_callable_measurement()
    {
        var root = Mutation(); var unit = root["units"]![0]!; var id = unit["unitId"]!.GetValue<string>();
        unit["disposition"] = "OMITTED"; root["counts"]!["killed"] = 0; root["counts"]!["executed"] = 0; root["counts"]!["omitted"] = 1;
        root["outcome"] = "INCOMPLETE"; root["exitCode"] = 4;
        root["reasons"] = new JsonArray(JsonSerializer.SerializeToNode(new { code = "UNIT_OMITTED", message = $"Evaluation unit {id} was omitted." }));
        var result = ReadMutation(root);
        Assert.False(result.Complete); Assert.True(result.MayCollectNextGate); Assert.Equal(1, result.Mutation!.Omitted);
        Assert.Equal(GateOutcome.Incomplete, result.RawOutcome); Assert.False(result.ProductionAuthority);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("unmeasured")]
    [InlineData("test-red")]
    public void Missing_source_line_measurement_or_failing_test_never_becomes_complete(string fault)
    {
        var root = Callable();
        if (fault == "test-red") root["run"]!["testOutcomes"]![0]!["exitCode"] = 1;
        using var doc = JsonDocument.Parse(root.ToJsonString());
        var sources = fault == "source" ? Array.Empty<InitialBaselineSource>() : new[] { new InitialBaselineSource("fixture-context", "Fixture.cs", new string('a', 64), false) };
        var lines = new InitialBaselineLineCoverage(10, 2, 20m, fault == "unmeasured" ? 1 : 0, true, TestEvidence.Identity('1'), "DATA fixture");
        var result = InitialBaselineCapture.Callables(Encoding.UTF8.GetBytes(root.ToJsonString()), doc.RootElement, sources, lines, GateOutcome.Fail, ["tests/App.Tests.csproj"], Path.GetTempPath());
        Assert.False(result.Complete); Assert.False(result.MayCollectNextGate); Assert.False(result.ProductionAuthority);
    }

    [Fact]
    public void Full_current_low_coverage_and_high_crap_are_retained_as_debt_without_becoming_normal_pass()
    {
        var root = Callable(); using var doc = JsonDocument.Parse(root.ToJsonString());
        var result = InitialBaselineCapture.Callables(Encoding.UTF8.GetBytes(root.ToJsonString()), doc.RootElement,
            [new("fixture-context", "Fixture.cs", new string('a', 64), false)],
            new(10, 2, 20m, 0, true, TestEvidence.Identity('1'), "DATA fixture"), GateOutcome.Fail, ["tests/App.Tests.csproj"], Path.GetTempPath());
        Assert.True(result.Complete); Assert.Equal(GateOutcome.Fail, result.RawOutcome); Assert.False(result.ProductionAuthority);
        Assert.Equal(20m, result.Lines!.Percent); Assert.Equal(40m, Assert.Single(result.Callables).Crap);
        Assert.Equal(.2m, Assert.Single(result.Callables).Coverage);
        Assert.Throws<NotSupportedException>(() => ((IList<InitialBaselineCallable>)result.Callables).Clear());
    }

    [Fact]
    public void Callable_logical_path_joins_the_exact_same_context_source_without_guessing_a_repository_path()
    {
        var root = Callable(); using var doc = JsonDocument.Parse(root.ToJsonString());
        var result = InitialBaselineCapture.Callables(Encoding.UTF8.GetBytes(root.ToJsonString()), doc.RootElement,
            [new("fixture-context", "src/Fixture.cs", new string('a', 64), false) { LogicalPath = "Fixture.cs" }],
            new(10, 2, 20m, 0, true, TestEvidence.Identity('1'), "DATA fixture"), GateOutcome.Fail,
            ["tests/App.Tests.csproj"], Path.GetTempPath());
        Assert.True(result.Complete); Assert.Equal("src/Fixture.cs", Assert.Single(result.Callables).RepositoryPath);
        Assert.Equal(new string('a', 64), Assert.Single(result.Callables).SourceSha256);
    }

    private static JsonObject Mutation()
    {
        var root = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MutationReportData", "strong.json")))!.AsObject();
        root["scopePlan"]!["projectUnits"] = new JsonArray(JsonSerializer.SerializeToNode(new
        { project = "src/App/App.csproj", tests = new[] { "tests/App.Tests/App.Tests.csproj" }, inputs = Array.Empty<string>(), expansion = "FULL_PROJECT" }));
        return root;
    }
    private static InitialBaselineCaptureData ReadMutation(JsonObject root)
    {
        using var report = JsonDocument.Parse(root.ToJsonString());
        using var configuration = JsonDocument.Parse("""
            {"version":2,"semanticProfile":"resolved-project-graph-v1","exclusions":[],
             "projects":[{"project":"src/App/App.csproj","testSuites":["tests"]}],
             "testSuites":[{"id":"tests","path":"tests/App.Tests/App.Tests.csproj"}]}
            """);
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        return InitialBaselineCapture.Mutation(bytes, report.RootElement, configuration.RootElement, Mutate4CSharpReportV2.ReadData(bytes).ClaimedOutcome);
    }
    private static JsonObject Callable()
    {
        var root = JsonNode.Parse(Crap4CSharpObservedContractTests.Report(true, "fail", "completed", 2, 1, 1, 0))!.AsObject();
        root["evaluation"]!["invocationMode"] = "check";
        root["evaluation"]!["metrics"]![0]!["coverage"] = .2m; root["evaluation"]!["metrics"]![0]!["crap"] = 40m;
        root["run"]!["testOutcomes"] = new JsonArray(JsonSerializer.SerializeToNode(new
        { contextId = "tests", project = Path.Combine(Path.GetTempPath(), "tests", "App.Tests.csproj"), status = "completed", exitCode = 0, resultsDirectory = "results", logDirectory = "logs" }));
        return root;
    }
    private sealed class Original : IOwnedDuplexBudget
    {
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(30);
        public TimeSpan WorkLimit => TimeSpan.FromSeconds(25);
        public CancellationToken Token => TestContext.Current.CancellationToken;
        public TimeSpan CleanupRemaining => WholeLimit - Stopwatch.GetElapsedTime(OriginalStarted);
        public void CheckWork() => Token.ThrowIfCancellationRequested();
    }
}
