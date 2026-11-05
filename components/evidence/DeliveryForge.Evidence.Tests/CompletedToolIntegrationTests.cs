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
    public void Schema_optional_current_provenance_is_incomplete_while_missing_mode_or_null_reason_is_error()
    {
        var root = Crap(true, "pass", 0);
        Assert.Equal(GateOutcome.Pass, Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 0).Outcome);
        root["evaluation"]!.AsObject().Remove("provenance");
        Assert.Equal(GateOutcome.Incomplete, Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 0).Outcome);
        root = Crap(true, "pass", 0); root["evaluation"]!.AsObject().Remove("invocationMode");
        Assert.Equal(GateOutcome.Error, Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 0).Outcome);
        root = Crap(true, "pass", 0); root["evaluation"]!["checks"] = new JsonArray(Check("coverage", "pass", "observed"));
        root["evaluation"]!["checks"]![0]!["reason"] = null;
        Assert.Equal(GateOutcome.Error, Crap4CSharpAdapter.NormalizeCurrent(Bytes(root), CrapCapability(), 0).Outcome);
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
    public void Inventory_failure_exports_closed_phase_counts_and_only_hashed_relative_keys()
    {
        const string hidden = "private/secret-λ.cs";
        var before = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [hidden] = "private-before-value",
            ["missing.cs"] = "old",
            ["unchanged.cs"] = "same"
        };
        var after = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [hidden] = "private-after-value",
            ["new.cs"] = "new",
            ["unchanged.cs"] = "same"
        };
        foreach (var phase in Enum.GetValues<InstalledToolFiles.ComparisonPhase>())
        {
            var io = new InstalledToolFiles(TimeSpan.FromSeconds(30));
            var failure = Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.Same(before, after, false, io, phase));
            Assert.Equal("Complete original source/payload membership or bytes changed.", failure.Message);
            var mismatch = Assert.IsType<InstalledToolFiles.InventoryMismatch>(InstalledToolFiles.Mismatch(failure));
            var expected = phase switch
            {
                InstalledToolFiles.ComparisonPhase.InstallationProfile => "installation-profile",
                InstalledToolFiles.ComparisonPhase.InstallationCurrent => "installation-current",
                InstalledToolFiles.ComparisonPhase.GitCurrent => "git-current",
                InstalledToolFiles.ComparisonPhase.ConsumerCurrent => "consumer-current",
                _ => throw new InvalidOperationException()
            };
            Assert.Equal(expected, mismatch.Phase);
            Assert.Equal(3, mismatch.BeforeCount); Assert.Equal(3, mismatch.AfterCount);
            Assert.Equal(1, mismatch.Missing); Assert.Equal(1, mismatch.Changed); Assert.Equal(1, mismatch.Added);
            Assert.False(mismatch.AllowNew); Assert.False(mismatch.Truncated);
            Assert.Equal(new[] { "Missing", "Added", "Changed" }, mismatch.Samples.Select(s => s.Kind));
            Assert.Equal(new[] { "missing.cs", "new.cs", hidden }.Select(k => InstalledToolFiles.Identity(Encoding.UTF8.GetBytes(k))),
                mismatch.Samples.Select(s => s.KeySha256));
            failure.Data["untrusted"] = "never-export-this-private-data";
            var raw = InstalledToolInvocation.FailureArtifact(failure, true, false, 1);
            Assert.True(raw.Length < 8192);
            var text = Encoding.UTF8.GetString(raw);
            Assert.DoesNotContain("private-before-value", text); Assert.DoesNotContain("private-after-value", text);
            Assert.DoesNotContain("secret", text); Assert.DoesNotContain("missing.cs", text);
            Assert.DoesNotContain("never-export-this-private-data", text);
            using var json = JsonDocument.Parse(raw);
            Assert.Equal(expected, json.RootElement.GetProperty("InventoryMismatch").GetProperty("Phase").GetString());
            Assert.False(json.RootElement.GetProperty("Returned").GetBoolean());
            Assert.False(json.RootElement.GetProperty("RuntimeAccepted").GetBoolean());
            Assert.False(json.RootElement.GetProperty("InventoryMismatchUnavailable").GetBoolean());
        }
    }

    [Fact]
    public void Inventory_failure_counts_every_mismatch_but_retains_only_sixteen_stable_hashes()
    {
        var before = Enumerable.Range(0, 22).ToDictionary(i => $"file-{i:D2}", _ => "before", StringComparer.Ordinal);
        var after = Enumerable.Range(0, 20).ToDictionary(i => $"file-{i:D2}", _ => "after", StringComparer.Ordinal);
        for (var i = 0; i < 3; i++) after.Add($"extra-{i}", "new");
        var failure = Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.Same(before, after, false,
            new InstalledToolFiles(TimeSpan.FromSeconds(30)), InstalledToolFiles.ComparisonPhase.ConsumerCurrent));
        var mismatch = Assert.IsType<InstalledToolFiles.InventoryMismatch>(InstalledToolFiles.Mismatch(failure));
        Assert.Equal(22, mismatch.BeforeCount); Assert.Equal(23, mismatch.AfterCount);
        Assert.Equal(2, mismatch.Missing); Assert.Equal(20, mismatch.Changed); Assert.Equal(3, mismatch.Added);
        Assert.True(mismatch.Truncated); Assert.Equal(16, mismatch.Samples.Length);
        var expected = before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(16)
            .Select(k => InstalledToolFiles.Identity(Encoding.UTF8.GetBytes(k)));
        Assert.Equal(expected, mismatch.Samples.Select(s => s.KeySha256));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inventory_diagnostics_keep_the_original_dictionary_key_comparison(bool ignoreCase)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var before = new Dictionary<string, string>(comparer) { ["Folder/Source.cs"] = "same" };
        var after = new Dictionary<string, string>(comparer) { ["folder/source.cs"] = "same" };
        var io = new InstalledToolFiles(TimeSpan.FromSeconds(30));
        if (ignoreCase)
        {
            InstalledToolFiles.Same(before, after, false);
            InstalledToolFiles.Same(before, after, false, io, InstalledToolFiles.ComparisonPhase.GitCurrent);
        }
        else
        {
            Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.Same(before, after, false));
            var failure = Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.Same(before, after, false,
                io, InstalledToolFiles.ComparisonPhase.GitCurrent));
            var mismatch = Assert.IsType<InstalledToolFiles.InventoryMismatch>(InstalledToolFiles.Mismatch(failure));
            Assert.Equal(1, mismatch.Missing); Assert.Equal(1, mismatch.Added); Assert.Equal(0, mismatch.Changed);
        }
    }

    [Fact]
    public void Crap_allows_new_real_files_but_still_refuses_changed_preexisting_obj_bytes()
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, "obj"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "cache"); File.WriteAllText(path, "original");
        var io = new InstalledToolFiles(TimeSpan.FromSeconds(30));
        var before = io.Inventory(temp.Path, 16, 4096, false);
        File.WriteAllText(Path.Combine(temp.Path, "new-output"), "new");
        InstalledToolFiles.Same(before, io.Inventory(temp.Path, 16, 4096, false), true,
            io, InstalledToolFiles.ComparisonPhase.ConsumerCurrent);
        File.WriteAllText(path, "changed");
        var failure = Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.Same(before,
            io.Inventory(temp.Path, 16, 4096, false), true, io, InstalledToolFiles.ComparisonPhase.ConsumerCurrent));
        var mismatch = Assert.IsType<InstalledToolFiles.InventoryMismatch>(InstalledToolFiles.Mismatch(failure));
        Assert.True(mismatch.AllowNew); Assert.Equal(1, mismatch.Changed); Assert.Equal(1, mismatch.Added);
        var sample = Assert.Single(mismatch.Samples, s => s.Kind == "Changed");
        Assert.Equal(InstalledToolFiles.Identity(Encoding.UTF8.GetBytes("obj/cache")), sample.KeySha256);
        Assert.Equal("changed", File.ReadAllText(path));
    }

    [Fact]
    public void Cancelled_diagnostic_keeps_original_refusal_and_retains_independent_failure_without_exporting_it()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var io = new InstalledToolFiles(TimeSpan.FromSeconds(30), cancelled.Token);
        cancelled.Cancel();
        var before = new Dictionary<string, string> { ["private/source"] = "old" };
        var after = new Dictionary<string, string> { ["private/source"] = "new" };
        var failure = Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.Same(before, after, false,
            io, InstalledToolFiles.ComparisonPhase.ConsumerCurrent));
        Assert.Equal("Complete original source/payload membership or bytes changed.", failure.Message);
        Assert.Null(InstalledToolFiles.Mismatch(failure));
        var independent = Assert.IsType<OperationCanceledException>(InstalledToolFiles.MismatchDiagnosticFailure(failure));
        Assert.Equal(cancelled.Token, independent.CancellationToken);
        using var json = JsonDocument.Parse(InstalledToolInvocation.FailureArtifact(failure, true, false, 1));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("InventoryMismatch").ValueKind);
        Assert.True(json.RootElement.GetProperty("InventoryMismatchUnavailable").GetBoolean());
        Assert.False(json.RootElement.GetProperty("RuntimeAccepted").GetBoolean());
    }

    [Fact]
    public void Allowed_additions_cannot_evict_the_blocking_changed_key_from_bounded_diagnostics()
    {
        const string hidden = "z-private/blocked-λ.cs";
        var before = new Dictionary<string, string>(StringComparer.Ordinal) { [hidden] = "private-before" };
        var after = new Dictionary<string, string>(StringComparer.Ordinal) { [hidden] = "private-after" };
        for (var i = 0; i < 20; i++) after.Add($"a-new-{i:D2}", "permitted-new-value");
        var io = new InstalledToolFiles(TimeSpan.FromSeconds(30));
        var failure = Assert.Throws<EvidenceWriteException>(() => InstalledToolFiles.Same(before, after, true,
            io, InstalledToolFiles.ComparisonPhase.ConsumerCurrent));
        Assert.Equal("Complete original source/payload membership or bytes changed.", failure.Message);
        var mismatch = Assert.IsType<InstalledToolFiles.InventoryMismatch>(InstalledToolFiles.Mismatch(failure));
        Assert.Equal(1, mismatch.BeforeCount); Assert.Equal(21, mismatch.AfterCount);
        Assert.Equal(0, mismatch.Missing); Assert.Equal(1, mismatch.Changed); Assert.Equal(20, mismatch.Added);
        Assert.True(mismatch.AllowNew); Assert.True(mismatch.Truncated); Assert.Equal(16, mismatch.Samples.Length);
        Assert.Equal("Changed", mismatch.Samples[0].Kind);
        var expected = new[] { hidden }.Concat(Enumerable.Range(0, 15).Select(i => $"a-new-{i:D2}"))
            .Select(key => InstalledToolFiles.Identity(Encoding.UTF8.GetBytes(key)));
        Assert.Equal(expected, mismatch.Samples.Select(sample => sample.KeySha256));
        Assert.All(mismatch.Samples.Skip(1), sample => Assert.Equal("Added", sample.Kind));
        var text = Encoding.UTF8.GetString(InstalledToolInvocation.FailureArtifact(failure, true, false, 1));
        Assert.DoesNotContain(hidden, text); Assert.DoesNotContain("private-before", text);
        Assert.DoesNotContain("private-after", text); Assert.DoesNotContain("permitted-new-value", text);
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
