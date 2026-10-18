using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence.Tests;

// All new cases are UNEXECUTED at authoring. These DATA/file/API tests are not installed
// producer certification; the separate real provider tests require Root-supplied actual inputs.
public sealed class CompletedToolIntegrationTests
{
    [Theory]
    [InlineData("strong", 0, GateOutcome.Pass)]
    [InlineData("weak", 3, GateOutcome.Fail)]
    [InlineData("red", 2, GateOutcome.Incomplete)]
    [InlineData("zero", 5, GateOutcome.NotApplicable)]
    public async Task Complete_producer_reduction_preserves_all_supported_outcomes(string name, int exit, GateOutcome expected)
    {
        var bytes = await MutationReportInputs.ReadAsync(name);
        var result = Mutate4CSharpAdapter.NormalizeCurrent(bytes, MutationCapability(), exit);
        Assert.Equal(expected, result.Outcome);
        Assert.True(result.Fixture);
        Assert.False(result.ProductionCapable);
        Assert.False(result.ProducerVerified); // No current owned execution was supplied.
    }

    [Fact]
    public async Task Policy_authorized_zero_keeps_not_applicable_instead_of_becoming_pass()
    {
        var root = JsonNode.Parse(await MutationReportInputs.ReadAsync("zero"))!;
        root["policy"]!["allowNotApplicable"] = true; root["exitCode"] = 0;
        var result = Mutate4CSharpAdapter.NormalizeCurrent(Encoding.UTF8.GetBytes(root.ToJsonString()), MutationCapability(), 0);
        Assert.Equal(GateOutcome.NotApplicable, result.Outcome);
        Assert.NotNull(result.NotApplicableRationale);
    }

    [Theory]
    [InlineData("strong", 3)]
    [InlineData("weak", 0)]
    [InlineData("red", 0)]
    [InlineData("zero", 0)]
    public async Task Actual_process_exit_cannot_be_replaced_by_report_claim(string name, int foreignExit)
    {
        Assert.Equal(GateOutcome.Error, Mutate4CSharpAdapter.NormalizeCurrent(
            await MutationReportInputs.ReadAsync(name), MutationCapability(), foreignExit).Outcome);
    }

    [Fact]
    public void Obsolete_preview_is_invalid_evidence_not_a_permanent_external_blocker()
    {
        var result = new Mutate4CSharpAdapter().Normalize("{\"outcome\":\"PASS\",\"reason\":\"ENUMERATION_NOT_IMPLEMENTED\"}"u8, MutationCapability());
        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.DoesNotContain("#5", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("coverage.unsupportedGeneratedMapping", GateOutcome.Incomplete)]
    [InlineData("compiler.outputsUnproven", GateOutcome.Incomplete)]
    [InlineData("run.timedOut", GateOutcome.Incomplete)]
    [InlineData("policy.missing", GateOutcome.Error)]
    [InlineData("arguments.invalid", GateOutcome.Error)]
    [InlineData("unrecognized.reason", GateOutcome.Error)]
    public void Current_crap_uses_official_reason_map_and_ignores_passing_trust_context(string reason, GateOutcome expected)
    {
        var root = Crap(false, "unknown", 1);
        root["evaluation"]!["checks"] = new JsonArray(
            Check("policyTrust", "pass", "policy.baseTrusted"), Check("evidence", "operationalError", reason));
        Assert.Equal(expected, Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 1).Outcome);
    }

    [Fact]
    public void Completed_failed_test_stays_primary_over_later_interrupted_analysis()
    {
        var root = Crap(false, "unknown", 1);
        root["evaluation"]!["checks"] = new JsonArray(Check("testExecution", "fail", "tests.failed"),
            Check("coverage", "operationalError", "coverage.unacceptedPartialEvidence"));
        root["run"]!["testOutcomes"] = new JsonArray(Outcome(1), Outcome(null));
        root["run"]!["cancellation"]!["timedOut"] = true;
        var result = Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 1);
        Assert.Equal(GateOutcome.Fail, result.Outcome);
        Assert.False(result.ProductionCapable);
    }

    [Fact]
    public void Null_test_exit_is_incomplete_launch_even_if_status_says_failed()
    {
        var root = Crap(false, "unknown", 1);
        root["evaluation"]!["checks"] = new JsonArray(Check("testExecution", "operationalError", "tests.incomplete"));
        root["run"]!["testOutcomes"] = new JsonArray(Outcome(null));
        Assert.Equal(GateOutcome.Incomplete, Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 1).Outcome);
    }

    [Theory]
    [InlineData("captured", "captureConsistency")]
    [InlineData("unverified", "none")]
    public void Historical_and_unverified_reports_never_become_current_gate(string status, string basis)
    {
        var root = Crap(true, "pass", 0);
        root["evaluation"]!["provenance"]!["status"] = status;
        root["evaluation"]!["provenance"]!["basis"] = basis;
        Assert.Equal(GateOutcome.Incomplete, Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 0).Outcome);
    }

    [Fact]
    public void Genuine_current_shape_positive_precedes_replay_and_exit_refusals()
    {
        var root = Crap(true, "pass", 0);
        Assert.Equal(GateOutcome.Pass, Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 0).Outcome);
        root["evaluation"]!["invocationMode"] = "analyze";
        Assert.Equal(GateOutcome.Error, Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 0).Outcome);
        root["evaluation"]!["invocationMode"] = "check";
        Assert.Equal(GateOutcome.Error, Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 2).Outcome);
    }

    [Fact]
    public void Duplicate_and_truncated_crap_wire_are_not_last_value_success()
    {
        var source = Encoding.UTF8.GetString(Bytes(Crap(true, "pass", 0)));
        var duplicate = "{\"schemaVersion\":\"1.0\"," + source[1..];
        Assert.Equal(GateOutcome.Error, new Crap4CSharpAdapter().Normalize(Encoding.UTF8.GetBytes(duplicate), CrapCapability()).Outcome);
        Assert.Equal(GateOutcome.Error, new Crap4CSharpAdapter().Normalize("{"u8, CrapCapability()).Outcome);
    }

    [Fact]
    public void Fixed_mutation_argv_does_not_offer_sampling_cache_or_worker_override()
    {
        var request = Request("base", null);
        var argv = InstalledToolGateProvider.BuildArguments("mutate4csharp", request, request.ExpectedRepository.BaseCommit, "unused", "/owned/report.json", "/owned/bundle");
        Assert.Equal(new[] { "check", "--base", request.ExpectedRepository.BaseCommit, "--report", "/owned/report.json", "--no-state" }, argv);
        Assert.Throws<EvidenceWriteException>(() => InstalledToolGateProvider.BuildArguments("mutate4csharp", request with { Scope = "sample" }, request.ExpectedRepository.BaseCommit, "", "x", "x"));
    }

    [Fact]
    public void Complete_crap_policy_scope_and_base_are_preserved_in_fixed_argv()
    {
        var request = Request("all", "Forge.slnx");
        var argv = InstalledToolGateProvider.BuildArguments("crap4csharp", request, request.ExpectedRepository.BaseCommit, "approved/policy.json", null, "/owned/bundle");
        Assert.Equal("check", argv[0]);
        Assert.Contains("--policy", argv); Assert.Contains("approved/policy.json", argv);
        Assert.Contains(request.ExpectedRepository.BaseCommit, argv); Assert.Contains("all", argv);
        Assert.DoesNotContain("--allow-missing-coverage", argv); Assert.DoesNotContain("--reuse-artifacts", argv);
    }

    [Theory]
    [InlineData("/approved/policy.json")]
    [InlineData(@"C:\approved\policy.json")]
    public void Crap_policy_argument_refuses_absolute_paths_before_building_argv(string policy)
    {
        var request = Request("all", "Forge.slnx");
        Assert.Throws<EvidenceWriteException>(() => InstalledToolGateProvider.BuildArguments(
            "crap4csharp", request, request.ExpectedRepository.BaseCommit, policy, null, "/owned/bundle"));
    }

    [Fact]
    public void Actual_real_file_snapshot_detects_mutation_new_members_and_deleted_members()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "source.cs"); File.WriteAllText(path, "public class Original {}");
        var io = new InstalledToolFiles(TimeSpan.FromSeconds(10));
        var before = io.Inventory(temp.Path, 16, 4096, false);
        InstalledToolFiles.Same(before, io.Inventory(temp.Path, 16, 4096, false), false);
        File.WriteAllText(path, "public class Changed {}");
        Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.Same(before, io.Inventory(temp.Path, 16, 4096, false), false));
        File.WriteAllText(path, "public class Original {}"); File.WriteAllText(Path.Combine(temp.Path, "extra.cs"), "class Extra {}");
        Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.Same(before, io.Inventory(temp.Path, 16, 4096, false), false));
        File.Delete(path);
        Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.Same(before, io.Inventory(temp.Path, 16, 4096, false), true));
    }

    [Fact]
    public void Bounded_real_file_read_refuses_before_oversized_allocation_and_preserves_file()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Path, "evidence");
        File.WriteAllText(path, "complete"); var io = new InstalledToolFiles(TimeSpan.FromSeconds(10));
        Assert.Equal("complete", Encoding.UTF8.GetString(io.Read(path, 8)));
        var failure = Assert.Throws<AggregateException>(() => io.Read(path, 7));
        Assert.Contains(failure.InnerExceptions, e => e is EvidenceWriteException);
        Assert.Equal("complete", File.ReadAllText(path));
    }

    [Fact]
    public void Current_provider_has_no_injected_executor_or_public_current_result_issuer()
    {
        Assert.All(typeof(InstalledToolGateProvider).GetConstructors(), c =>
            Assert.DoesNotContain(c.GetParameters(), p => p.ParameterType == typeof(ICommandExecutor)));
        Assert.Empty(typeof(InstalledToolProfile).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(typeof(NormalizedEvidence).GetProperty("ProducerVerified", BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(typeof(InstalledToolGateProvider).GetMethods(BindingFlags.Public | BindingFlags.Instance), m => m.Name.Contains("Adopt", StringComparison.Ordinal));
    }

    private static ToolCapability MutationCapability() => new("mutate4csharp", "format-only", "2", true, true);
    private static ToolCapability CrapCapability() => new("crap4csharp", "0.1.0+1129c9d3280168b294f9fed1542f81ca716c6d53", "1.0", true, true);
    private static InstalledToolGateRequest Request(string scope, string? project) => new("quality", TestEvidence.ResolvedPolicy(), TestEvidence.Repository(), scope, project, TimeSpan.FromMinutes(5), Path.GetTempPath());
    private static JsonObject Check(string name, string status, string reason) => new() { ["name"] = name, ["status"] = status, ["reason"] = reason, ["required"] = true };
    private static JsonObject Outcome(int? exit) => new() { ["contextId"] = "fixture-context", ["project"] = "Fixture.csproj", ["status"] = "failed", ["exitCode"] = exit, ["resultsDirectory"] = null, ["logDirectory"] = null };
    private static byte[] Bytes(JsonNode root) => Encoding.UTF8.GetBytes(root.ToJsonString());
    private static JsonNode Crap(bool complete, string decision, int exit)
    {
        var root = JsonNode.Parse(Crap4CSharpObservedContractTests.Report(complete, decision, complete ? "completed" : "operationalError", exit, 1, complete ? 1 : 0, complete ? 0 : 1))!;
        root["evaluation"]!["invocationMode"] = "check";
        root["evaluation"]!["provenance"] = new JsonObject { ["status"] = "verified", ["basis"] = "currentWorkspaceMatch", ["postflightVerified"] = true, ["reusable"] = false, ["reasons"] = new JsonArray() };
        return root;
    }
}