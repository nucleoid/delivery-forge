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
    [Fact]
    public void Failed_binding_retains_only_noncontinuable_error_DATA_and_the_report_identity()
    {
        var result = InitialBaselineCapture.Failed("crap4csharp", TestEvidence.Identity('d'), new IOException("private failure message"));
        Assert.Equal(GateOutcome.Error, result.RawOutcome); Assert.True(result.BindingFailed); Assert.False(result.ArtifactPublicationComplete);
        Assert.False(result.Complete); Assert.False(result.MayCollectNextGate); Assert.False(result.ProductionAuthority);
        Assert.Equal(TestEvidence.Identity('d'), result.ReportIdentity);
        Assert.Empty(result.Sources); Assert.Empty(result.Callables); Assert.Null(result.Lines); Assert.Null(result.Mutation);
        Assert.Equal(["PRODUCER_BINDING_FAILED", typeof(IOException).FullName!], result.Diagnostics);
        Assert.DoesNotContain("private failure message", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Actual_publication_and_gate_postvalidation_both_bound_capture_completeness(bool published, bool gateAccepted)
    {
        var measured = ReadMutation(Mutation());
        Assert.True(measured.Complete); Assert.True(measured.MayCollectNextGate); Assert.False(measured.BindingFailed);
        var result = InitialBaselineCapture.RetainAfterGate(measured, published, gateAccepted);
        Assert.Equal(published, result.ArtifactPublicationComplete); Assert.False(result.BindingFailed);
        Assert.Equal(published && gateAccepted, result.Complete); Assert.Equal(published && gateAccepted, result.MayCollectNextGate);
        Assert.Equal(measured.RawOutcome, result.RawOutcome); Assert.Equal(measured.Mutation, result.Mutation); Assert.False(result.ProductionAuthority);
    }

    [Fact]
    public void Raw_error_DATA_is_not_binding_failure_and_partial_publication_does_not_invent_one()
    {
        var measured = new InitialBaselineCaptureData("crap4csharp", GateOutcome.Error, false, false,
            TestEvidence.Identity('d'), [], [], null, null, []);
        var retained = InitialBaselineCapture.RetainAfterGate(measured, false, false);
        Assert.False(retained.BindingFailed); Assert.False(retained.ArtifactPublicationComplete);
        Assert.False(retained.Complete); Assert.False(retained.MayCollectNextGate);
        Assert.Contains("ARTIFACT_PUBLICATION_INCOMPLETE", retained.Diagnostics);
    }

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
        var root = BudgetOmission();
        Assert.Empty(root["incompleteConditions"]!.AsArray()); // Genuine full-cap shape has no top-level condition.
        var result = ReadMutation(root);
        Assert.False(result.Complete); Assert.True(result.MayCollectNextGate); Assert.Equal(1, result.Mutation!.Omitted);
        Assert.Equal(2, result.Mutation!.Enumerated); Assert.Equal(1, result.Mutation!.Executed); Assert.Equal(1, result.Mutation!.Killed);
        Assert.Equal(GateOutcome.Incomplete, result.RawOutcome); Assert.False(result.ProductionAuthority);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("multiple")]
    [InlineData("cap")]
    [InlineData("all-omitted")]
    public void Generic_omission_or_inconsistent_cap_cannot_leave_the_parent_available_for_another_gate(string fault)
    {
        var root = BudgetOmission();
        var unit = root["units"]![1]!;
        if (fault == "missing") unit["evidence"]![0]!["kind"] = "FORMAT_ONLY"; // Required evidence exists but carries no budget proof.
        if (fault == "foreign") unit["evidence"]![0]!["kind"] = "TARGET_NOT_REQUESTED";
        if (fault == "multiple") unit["evidence"]!.AsArray().Add(unit["evidence"]![0]!.DeepClone());
        if (fault == "cap") root["policy"]!["mutationCap"] = 2;
        if (fault == "all-omitted")
        {
            root["units"]!.AsArray().RemoveAt(0);
            root["counts"]!["enumerated"] = 1; root["counts"]!["selected"] = 1;
            root["counts"]!["executed"] = 0; root["counts"]!["killed"] = 0;
        }
        var result = ReadMutation(root); // These remain internally valid official reducer DATA.
        Assert.False(result.Complete); Assert.False(result.MayCollectNextGate);
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

    [Fact]
    public void Budget_capture_requires_the_exact_suite_SDK_and_fresh_accounting_before_continuing()
    {
        var root = BudgetOmission(); var configuration = MutationJoinConfiguration(root);
        Assert.True(ReadMutation(root, configuration).MayCollectNextGate);
        var result = JoinMutation(root, configuration);
        Assert.NotNull(result); Assert.True(result.MayCollectNextGate); Assert.False(result.Complete); Assert.False(result.ProductionAuthority);
    }

    [Theory]
    [InlineData("suite-path")]
    [InlineData("runner")]
    [InlineData("framework")]
    [InlineData("configuration")]
    [InlineData("members")]
    [InlineData("accounted-members")]
    [InlineData("accounting-run")]
    [InlineData("sdk")]
    [InlineData("configuration-bytes")]
    public void Budget_capture_refuses_substituted_suite_accounting_SDK_or_unbound_configuration(string fault)
    {
        var root = BudgetOmission(); var configuration = MutationJoinConfiguration(root);
        var observed = root["suites"]![0]!["accounting"]!["configuration"]!;
        if (fault == "suite-path") observed["path"] = "tests/Foreign/Foreign.csproj";
        if (fault == "runner") observed["runner"] = "foreign";
        if (fault == "framework") observed["framework"] = "net9.0";
        if (fault == "configuration") observed["configuration"] = "Debug";
        if (fault == "members") observed["expectedMembers"] = new JsonArray("Foreign.Tests.dll");
        if (fault == "accounted-members") root["suites"]![0]!["accounting"]!["accountedMembers"] = new JsonArray("Foreign.Tests.dll");
        if (fault == "accounting-run") root["suites"]![0]!["accounting"]!["runId"] = "foreign";
        if (fault == "sdk") root["evidence"]!.AsArray().Last()!["diagnostics"] = new JsonArray("dotnet-sdk=foreign;dependencies=fixture");
        if (fault is "runner" or "framework")
        {
            Assert.Throws<MutationReportFormatException>(() => ReadMutation(root, configuration)); // Existing pinned schema already refuses these.
            return;
        }
        Assert.True(ReadMutation(root, configuration).MayCollectNextGate); // Budget DATA alone does not bind suite accounting/SDK/config.
        if (fault is "sdk" or "configuration-bytes")
            Assert.Throws<EvidenceWriteException>(() => JoinMutation(root, configuration, drift: fault == "configuration-bytes"));
        else Assert.Throws<MutationReportFormatException>(() => JoinMutation(root, configuration));
    }

    [Fact]
    public void Noncontinuable_capture_retains_a_valid_incomplete_refusal_without_demanding_conclusive_suites()
    {
        var root = BudgetOmission(); var configuration = MutationJoinConfiguration(root);
        root["units"]![1]!["evidence"]![0]!["kind"] = "FORMAT_ONLY"; // No positive cap proof, so this capture cannot continue.
        root["evidence"]!.AsArray().RemoveAt(root["evidence"]!.AsArray().Count - 1);
        root["suites"]![0]!["accounting"]!["runId"] = "foreign";
        var result = JoinMutation(root, configuration);
        Assert.NotNull(result); Assert.False(result.Complete); Assert.False(result.MayCollectNextGate);
        Assert.Equal(GateOutcome.Incomplete, result.RawOutcome);
    }

    [Fact]
    public void Normal_incomplete_mutation_retains_its_existing_nonconclusive_suite_branch()
    {
        var root = BudgetOmission(); var configuration = MutationJoinConfiguration(root);
        root["evidence"]!.AsArray().RemoveAt(root["evidence"]!.AsArray().Count - 1);
        root["suites"]![0]!["accounting"]!["configuration"]!["path"] = "tests/Foreign/Foreign.csproj";
        Assert.Null(JoinMutation(root, configuration, purpose: InstalledToolPurpose.ChangedCode));
    }

    private static InitialBaselineCaptureData? JoinMutation(JsonObject root, JsonObject configuration,
        bool drift = false, InstalledToolPurpose purpose = InstalledToolPurpose.InitialBaselineCapture)
    {
        var bytes = Encoding.UTF8.GetBytes(configuration.ToJsonString()); var expectedHash = InstalledToolFiles.Identity(bytes);
        if (drift) bytes = bytes.Concat(" "u8.ToArray()).ToArray(); // Same JSON semantics, different approved bytes.
        var reportBytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        var data = Mutate4CSharpReportV2.ReadData(reportBytes); using var report = JsonDocument.Parse(reportBytes);
        return InstalledToolInvocation.JoinMutationConfiguration(reportBytes, report.RootElement, bytes,
            expectedHash, "fixture-sdk", purpose, data.ClaimedOutcome);
    }
    private static JsonObject MutationJoinConfiguration(JsonObject root)
    {
        // Keep the strong fixture's complete suite digest/accounting unchanged.
        // All of this is DATA: no SDK probe, native owner, receipt or authority.
        var suite = root["suites"]![0]!["accounting"]!["configuration"]!.DeepClone();
        var id = suite["id"]!.GetValue<string>(); var path = suite["path"]!.GetValue<string>();
        root["scopePlan"]!["projectUnits"]![0]!["tests"] = new JsonArray(path);
        root["evidence"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
        { kind = "TOOL_INTERNAL_SDK", summary = "Explicit DATA join fixture only.", diagnostics = new[] { "dotnet-sdk=fixture-sdk;dependencies=fixture" } }));
        return new JsonObject
        {
            ["version"] = 2,
            ["semanticProfile"] = "resolved-project-graph-v1",
            ["exclusions"] = new JsonArray(),
            ["projects"] = new JsonArray(JsonSerializer.SerializeToNode(new
            { project = "src/App/App.csproj", testSuites = new[] { id } })),
            ["testSuites"] = new JsonArray(suite),
            ["policy"] = root["policy"]!.DeepClone()
        };
    }

    private static JsonObject Mutation()
    {
        var root = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MutationReportData", "strong.json")))!.AsObject();
        root["scopePlan"]!["projectUnits"] = new JsonArray(JsonSerializer.SerializeToNode(new
        { project = "src/App/App.csproj", tests = new[] { "tests/App.Tests/App.Tests.csproj" }, inputs = Array.Empty<string>(), expansion = "FULL_PROJECT" }));
        return root;
    }
    private static JsonObject BudgetOmission()
    {
        var root = Mutation(); var omitted = root["units"]![0]!.DeepClone();
        var id = "mutation:v1:" + new string('c', 64);
        var evaluationId = "evaluation:v1:" + new string('d', 64);
        omitted["unitId"] = id; omitted["evaluationUnitId"] = evaluationId; omitted["disposition"] = "OMITTED";
        omitted["evidence"] = new JsonArray(JsonSerializer.SerializeToNode(new
        { kind = "BUDGET_OMITTED", summary = $"Evaluation unit {evaluationId} exceeded the deterministic cap of 1.", diagnostics = (string[]?)null }));
        root["units"]!.AsArray().Add(omitted);
        root["policy"]!["mutationCap"] = 1;
        root["counts"]!["enumerated"] = 2; root["counts"]!["selected"] = 2; root["counts"]!["omitted"] = 1;
        root["outcome"] = "INCOMPLETE"; root["exitCode"] = 4;
        root["reasons"] = new JsonArray(JsonSerializer.SerializeToNode(new
        { code = "UNIT_OMITTED", message = $"Evaluation unit {id} was omitted." }));
        return root;
    }
    private static InitialBaselineCaptureData ReadMutation(JsonObject root, JsonObject? approvedConfiguration = null)
    {
        using var report = JsonDocument.Parse(root.ToJsonString());
        using var configuration = JsonDocument.Parse(approvedConfiguration?.ToJsonString() ?? """
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
