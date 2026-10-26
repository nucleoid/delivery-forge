using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Tool.Tests;

public sealed class ReviewGateReceiptEvidenceTests
{
    static byte[] Receipt(int changedMember = -1)
    {
        var receipt = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "ContractFixtures", "gate-receipt.json")))!;
        var artifacts = new JsonObject();
        for (var i = 0; i < 400; i++)
            artifacts.Add("producer-bundle/inputs/" + i.ToString("D64") + ".bin",
                "sha256:" + (i == changedMember ? new string('4', 64) : new string('3', 64)));
        // A member named identity must also participate in the inventory digest.
        artifacts.Add("identity", "sha256:" + new string('5', 64));
        receipt["artifactHashes"] = artifacts;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        receipt["identity"] = CanonicalJson.ComputeIdentity(bytes);
        return JsonSerializer.SerializeToUtf8Bytes(receipt);
    }

    [Fact]
    public void Complete_large_receipt_keeps_every_result_field_and_fits_existing_prompt_profile()
    {
        var bytes = Receipt(); var original = bytes.ToArray();
        using var raw = JsonDocument.Parse(bytes);
        var evidence = ReviewGateReceiptEvidence.Create("retained/receipt.json", bytes);
        Assert.Equal(original, bytes);
        Assert.Equal("ReviewGateReceiptEvidence-v1", evidence.GetProperty("Kind").GetString());
        var pin = evidence.GetProperty("RawReceipt");
        Assert.Equal(bytes.LongLength, pin.GetProperty("Bytes").GetInt64());
        Assert.Equal("sha256:" + BoundedFiles.Sha(bytes), pin.GetProperty("Sha256").GetString());
        Assert.Equal(raw.RootElement.GetProperty("identity").GetString(), pin.GetProperty("Identity").GetString());
        var fields = evidence.GetProperty("ReceiptFields");
        foreach (var field in raw.RootElement.EnumerateObject().Where(p => p.Name is not ("identity" or "artifactHashes")))
            Assert.Equal(CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(field.Value.GetRawText())),
                CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(fields.GetProperty(field.Name).GetRawText())));
        var map = raw.RootElement.GetProperty("artifactHashes");
        Assert.Equal(401, evidence.GetProperty("ArtifactInventory").GetProperty("Count").GetInt32());
        Assert.Equal("sha256:" + BoundedFiles.Sha(CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(map.GetRawText()))),
            evidence.GetProperty("ArtifactInventory").GetProperty("CanonicalIdentity").GetString());
        var oldPrompt = JsonSerializer.Serialize(raw.RootElement, BoundedFiles.Json);
        var newPrompt = JsonSerializer.Serialize(new
        {
            CompletePlan = "unchanged plan",
            QualityEvidence = evidence,
            CompletePatch = "unchanged complete patch"
        }, BoundedFiles.Json);
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(oldPrompt)) > 60000);
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(newPrompt)) < 60000);
        Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(
            JsonSerializer.SerializeToUtf8Bytes(evidence)));
    }

    [Fact]
    public void Changing_one_retained_artifact_changes_both_exact_receipt_and_complete_inventory_pins()
    {
        var before = ReviewGateReceiptEvidence.Create("retained/receipt.json", Receipt());
        var after = ReviewGateReceiptEvidence.Create("retained/receipt.json", Receipt(399));
        Assert.Equal(before.GetProperty("ReceiptFields").GetRawText(), after.GetProperty("ReceiptFields").GetRawText());
        Assert.NotEqual(before.GetProperty("RawReceipt").GetProperty("Sha256").GetString(),
            after.GetProperty("RawReceipt").GetProperty("Sha256").GetString());
        Assert.NotEqual(before.GetProperty("RawReceipt").GetProperty("Identity").GetString(),
            after.GetProperty("RawReceipt").GetProperty("Identity").GetString());
        Assert.NotEqual(before.GetProperty("ArtifactInventory").GetProperty("CanonicalIdentity").GetString(),
            after.GetProperty("ArtifactInventory").GetProperty("CanonicalIdentity").GetString());
    }
    // Pure presentation/refusal controls; these tuples cannot issue a completed
    // installed host binding, accepted quality gate, baseline or native authority.
    static (InstalledQualityGateSelection Gate, byte[] Profile, DeliveryForge.Evidence.GateRunResult Result) ProducerData()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-review-producer-DATA");
        var entrypoint = "producer.exe";
        var image = "sha256:" + new string('a', 64);
        var profile = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Tool = "crap4csharp",
            Fixture = false,
            InstallationRoot = root,
            ExecutableRelativePath = entrypoint,
            Payload = new[] { new { Path = entrypoint, Bytes = 123L, Sha256 = image } },
            Version = "0.1.0-private+selected-source",
            SourceCommit = new string('b', 40),
            PackageVersion = "0.1.0-private",
            PackageSha256 = "sha256:" + new string('c', 64),
            ConfigurationRelativePath = "quality/crap.json",
            ConfigurationSha256 = "sha256:" + new string('d', 64),
            SdkVersion = "10.0.401",
            Framework = "net10.0",
            Runner = "vstest",
            Collector = "coverlet.collector/6.0.4",
            Environment = new { DO_NOT_EXPORT = "private-environment-DATA" },
            Limitations = new[] { "Private selected fixture scope; no source-build receipt or publication authority." }
        });
        var gate = new InstalledQualityGateSelection("crap", "crap4csharp",
            new(Path.Combine(root, "profile.json"), profile.LongLength, BoundedFiles.Sha(profile)), "all", "Tests/Tests.csproj", 450);
        var repository = new DeliveryForge.Evidence.RepositoryIdentity(new('1', 40), new('2', 40), new('3', 40), true);
        var actual = new DeliveryForge.Evidence.GateRunResult(DeliveryForge.Evidence.GateOutcome.Pass, "DATA only", false, false, [], false,
            new(Path.Combine(root, entrypoint), [], root), new(0, "", "", false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            repository, repository, "sha256:" + new string('e', 64), image, "DATA", "all", new Dictionary<string, string>(), "unissued.json", null);
        return (gate, profile, actual);
    }

    [Fact]
    public void Selected_private_producer_metadata_is_visible_without_exporting_installation_or_environment()
    {
        var data = ProducerData();
        var projection = ReviewGateReceiptEvidence.CreateToolProvenance(data.Gate, data.Profile, data.Result);
        var producer = projection.GetProperty("ProfileDeclared");
        Assert.Equal("0.1.0-private+selected-source", producer.GetProperty("Version").GetString());
        Assert.Equal(data.Result.CapabilityIdentity, projection.GetProperty("Observed").GetProperty("ExecutableSha256").GetString());
        Assert.Equal("producer.exe", projection.GetProperty("Observed").GetProperty("EntrypointRelativePath").GetString());
        Assert.Equal("sha256:" + data.Gate.Profile.Sha256, projection.GetProperty("Profile").GetProperty("Sha256").GetString());
        Assert.Equal("producer.exe", producer.GetProperty("Executable").GetProperty("RelativePath").GetString());
        Assert.False(projection.GetProperty("ProductionCapable").GetBoolean());
        Assert.DoesNotContain("SourceBuildVerified", projection.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("not imported", projection.GetProperty("Omitted").GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-environment-DATA", projection.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("InstallationRoot", projection.GetRawText(), StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(projection.GetRawText()) < 4096);
    }

    [Fact]
    public void Omitted_fixture_flag_keeps_the_installed_profile_loaders_false_default()
    {
        var data = ProducerData();
        var profile = JsonNode.Parse(data.Profile)!.AsObject();
        Assert.True(profile.Remove("Fixture"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profile);
        var gate = data.Gate with { Profile = new(data.Gate.Profile.Path, bytes.LongLength, BoundedFiles.Sha(bytes)) };
        var projection = ReviewGateReceiptEvidence.CreateToolProvenance(gate, bytes, data.Result);
        Assert.False(projection.GetProperty("Fixture").GetBoolean());
        Assert.Equal(data.Result.CapabilityIdentity, projection.GetProperty("Observed").GetProperty("ExecutableSha256").GetString());
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("image")]
    [InlineData("entrypoint")]
    [InlineData("tool")]
    [InlineData("fixture")]
    [InlineData("source-changed")]
    [InlineData("fail")]
    [InlineData("error")]
    public void Changed_profile_or_actual_image_cannot_be_presented_as_the_selected_producer(string changed)
    {
        var data = ProducerData();
        var profile = data.Profile;
        var actual = data.Result;
        var gate = data.Gate;
        if (changed == "profile") profile = [.. profile, (byte)' '];
        if (changed == "image") actual = actual with { CapabilityIdentity = "sha256:" + new string('f', 64) };
        if (changed == "entrypoint") actual = actual with { Invocation = actual.Invocation with { FileName = Path.Combine(actual.Invocation.WorkingDirectory, "foreign.exe") } };
        if (changed == "tool") gate = gate with { Tool = "mutate4csharp" };
        if (changed == "fixture") actual = actual with { Fixture = true };
        if (changed == "source-changed") actual = actual with { SourceChanged = true };
        if (changed == "fail") actual = actual with { Outcome = DeliveryForge.Evidence.GateOutcome.Fail };
        if (changed == "error") actual = actual with { Outcome = DeliveryForge.Evidence.GateOutcome.Error };
        Assert.Throws<DeliveryForge.Execution.WorkerEnvelopeException>(() => ReviewGateReceiptEvidence.CreateToolProvenance(gate, profile, actual));
    }

    [Fact]
    public void A_value_equal_result_copy_cannot_replace_the_actual_retained_gate_reference()
    {
        var data = ProducerData();
        ReviewGateReceiptEvidence.RequireRetainedResults([data.Result], [data.Result]);
        Assert.Throws<DeliveryForge.Execution.WorkerEnvelopeException>(() => ReviewGateReceiptEvidence.RequireRetainedResults([data.Result], [data.Result with { }]));
    }


    // Pure presentation DATA: these rows cannot issue a provider/completed-frame authority.
    static (byte[] Receipt, DeliveryForge.Evidence.GateRunResult Actual, Dictionary<string, byte[]> Artifacts) RequiredTestsData(string changed = "")
    {
        var data = ProducerData(); var graph = new string('a', 64); var build = new string('b', 64);
        var count = changed == "count" ? 6 : 7;
        var ns = changed == "namespace" ? "urn:foreign" : "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var list = "00000000-0000-0000-0000-000000000099";
        string TestId(int i) => $"00000000-0000-0000-0000-{i + 1:000000000000}";
        string ExecutionId(int i) => $"00000000-0000-0000-0001-{i + 1:000000000000}";
        string Name(int i) => "case-" + (changed == "duplicate-name" ? 0 : i);
        var results = string.Concat(Enumerable.Range(0, count).Select(i => $"<UnitTestResult testId='{TestId(i)}' executionId='{ExecutionId(i)}' testListId='{list}' testName='{Name(i)}' outcome='Passed'/>"));
        if (changed == "nested") results += "<InnerResults>" + results + "</InnerResults>";
        var definitions = string.Concat(Enumerable.Range(0, count).Select(i => $"<UnitTest id='{TestId(i)}' name='{Name(i)}'><Execution id='{ExecutionId(changed == "definition" ? 0 : i)}'/></UnitTest>"));
        var entries = string.Concat(Enumerable.Range(0, count).Select(i => $"<TestEntry testId='{TestId(i)}' executionId='{ExecutionId(i)}' testListId='{list}'/>"));
        var trx = Encoding.UTF8.GetBytes((changed == "entity" ? "<!DOCTYPE TestRun [<!ENTITY x SYSTEM 'file:///not-a-test-input'>]>" : "") +
            $"<TestRun xmlns='{ns}'><Results>{results}</Results><TestDefinitions>{definitions}</TestDefinitions><TestEntries>{entries}</TestEntries>" +
            $"<ResultSummary outcome='{(changed == "summary" ? "Failed" : "Completed")}'><Counters total='7' executed='7' passed='7' failed='0' error='{(changed == "error-counter" ? 1 : 0)}'/></ResultSummary></TestRun>");
        var inventory = JsonSerializer.SerializeToUtf8Bytes(new
        {
            provider = "finite-sdk-required-test-inventory-v1", reusableRecipe = false,
            tests = new[] { new { graphId = graph, project = "Tests/Tests.csproj", trx = "required-test-" + (changed == "graph" ? build : graph) + "-trx", passed = 7, rawExitCode = 0 } }
        });
        if (changed == "duplicate-json") inventory = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(inventory).Replace("\"provider\":", "\"provider\":\"foreign\",\"provider\":", StringComparison.Ordinal));
        var artifacts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["producer-bundle/inputs/results.bin"] = trx,
            ["producer-bundle/inputs/inventory.bin"] = inventory
        };
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            revision = new { @base = data.Result.After.BaseCommit, head = data.Result.After.HeadCommit },
            capture = new { actualBindingComplete = true, captureConsistent = true },
            artifacts = new[]
            {
                new { id = "required-test-inventory", kind = "required-test-inventory", locator = "inputs/inventory.bin", length = inventory.Length, sha256 = BoundedFiles.Sha(inventory), format = "json" },
                new { id = "required-test-" + graph + "-trx", kind = "required-test-result", locator = changed == "traversal" ? "../results.bin" : "inputs/results.bin", length = changed == "oversize" ? 16 * 1024 * 1024 + 1 : trx.Length, sha256 = BoundedFiles.Sha(trx), format = "trx" }
            },
            executions = new[] { new { id = "test-" + build + "-" + graph, buildId = "build-" + build, testProject = "Tests/Tests.csproj", completed = true, exitCode = 0,
                totalTests = 7, passedTests = 7, failedTests = 0, skippedTests = 0 } }
        });
        if (changed != "missing-manifest") artifacts.Add("producer-bundle/manifest.json", manifest);
        var hashes = artifacts.ToDictionary(row => row.Key, row => "sha256:" + BoundedFiles.Sha(row.Value), StringComparer.Ordinal);
        var receipt = JsonNode.Parse(Receipt())!.AsObject();
        receipt["artifactHashes"] = JsonSerializer.SerializeToNode(hashes);
        receipt["baseCommit"] = data.Result.After.BaseCommit; receipt["headCommit"] = data.Result.After.HeadCommit;
        receipt["treeId"] = data.Result.After.TreeId; receipt["policyIdentity"] = data.Result.PolicyIdentity;
        receipt["identity"] = CanonicalJson.ComputeIdentity(JsonSerializer.SerializeToUtf8Bytes(receipt));
        var actual = data.Result with { ArtifactHashes = hashes };
        if (changed == "head") actual = actual with { Before = actual.Before with { HeadCommit = new string('9', 40) }, After = actual.After with { HeadCommit = new string('9', 40) } };
        if (changed == "nonpass") actual = actual with { Outcome = DeliveryForge.Evidence.GateOutcome.Fail };
        if (changed == "fixture") actual = actual with { Fixture = true };
        var drift = changed switch { "trx-drift" => "producer-bundle/inputs/results.bin", "manifest-drift" => "producer-bundle/manifest.json", "inventory-drift" => "producer-bundle/inputs/inventory.bin", _ => null };
        if (drift is not null) artifacts[drift] = [.. artifacts[drift], (byte)' '];
        return (JsonSerializer.SerializeToUtf8Bytes(receipt), actual, artifacts);
    }

    [Fact]
    public void Required_test_presentation_keeps_all_seven_supplied_named_outcomes_and_exact_artifact_identity()
    {
        var data = RequiredTestsData(); var reads = 0;
        var projected = ReviewGateReceiptEvidence.Create("retained/receipts/current.json", data.Receipt, data.Actual,
            (relative, maximum) => { reads++; Assert.True(data.Artifacts[relative].Length <= maximum); return data.Artifacts[relative]; });
        var required = projected.GetProperty("RequiredTests");
        Assert.True(required.GetProperty("Presented").GetBoolean());
        Assert.Contains("Producer-reported", required.GetProperty("Qualification").GetString(), StringComparison.Ordinal);
        Assert.Contains("does not independently verify", required.GetProperty("Qualification").GetString(), StringComparison.Ordinal);
        var test = Assert.Single(required.GetProperty("Tests").EnumerateArray());
        Assert.Equal(7, test.GetProperty("CompleteCaseCount").GetInt32());
        Assert.Equal(7, test.GetProperty("Cases").GetArrayLength());
        Assert.All(test.GetProperty("Cases").EnumerateArray(), row => Assert.Equal("Passed", row.GetProperty("Outcome").GetString()));
        Assert.Equal("sha256:" + BoundedFiles.Sha(data.Artifacts["producer-bundle/inputs/results.bin"]), test.GetProperty("Sha256").GetString());
        Assert.Equal(3, reads);
    }

    [Theory]
    [InlineData("trx-drift")]
    [InlineData("manifest-drift")]
    [InlineData("inventory-drift")]
    [InlineData("head")]
    [InlineData("traversal")]
    public void Required_test_artifact_ownership_or_currentness_failures_still_escape(string changed)
    {
        var data = RequiredTestsData(changed);
        Assert.Throws<DeliveryForge.Execution.WorkerEnvelopeException>(() => ReviewGateReceiptEvidence.Create("retained/receipts/current.json", data.Receipt, data.Actual,
            (relative, _) => data.Artifacts[relative]));
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("count")]
    [InlineData("entity")]
    [InlineData("duplicate-name")]
    [InlineData("definition")]
    [InlineData("namespace")]
    [InlineData("nested")]
    [InlineData("summary")]
    [InlineData("error-counter")]
    [InlineData("duplicate-json")]
    [InlineData("oversize")]
    [InlineData("missing-manifest")]
    [InlineData("nonpass")]
    [InlineData("fixture")]
    public void Unsupported_required_test_presentation_is_explicit_unavailable_without_issuing_outcomes(string changed)
    {
        var data = RequiredTestsData(changed); var reads = 0;
        var evidence = ReviewGateReceiptEvidence.Create("retained/receipts/current.json", data.Receipt, data.Actual,
            (relative, _) => { reads++; return data.Artifacts[relative]; });
        var required = evidence.GetProperty("RequiredTests");
        Assert.False(required.GetProperty("Presented").GetBoolean());
        Assert.False(required.TryGetProperty("Tests", out _));
        Assert.False(string.IsNullOrWhiteSpace(required.GetProperty("Reason").GetString()));
        Assert.Equal("ReviewGateReceiptEvidence-v1", evidence.GetProperty("Kind").GetString());
        Assert.Equal("sha256:" + BoundedFiles.Sha(data.Receipt), evidence.GetProperty("RawReceipt").GetProperty("Sha256").GetString());
        if (changed is "missing-manifest" or "nonpass" or "fixture") Assert.Equal(0, reads);
        if (changed == "oversize") Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("ownership")]
    [InlineData("cancellation")]
    [InlineData("budget")]
    [InlineData("xml-from-owner")]
    [InlineData("json-from-owner")]
    public void Admitted_callback_failures_are_never_reclassified_as_optional_presentation(string changed)
    {
        var data = RequiredTestsData();
        Exception failure = changed switch
        {
            "io" => new IOException("first-close DATA"),
            "ownership" => new DeliveryForge.Execution.WorkerEnvelopeException("owner DATA"),
            "cancellation" => new OperationCanceledException("cancel DATA"),
            "budget" => new TimeoutException("budget DATA"),
            "xml-from-owner" => new System.Xml.XmlException("callback DATA"),
            _ => new JsonException("callback DATA")
        };
        var observed = Record.Exception(() => ReviewGateReceiptEvidence.Create("retained/receipts/current.json", data.Receipt, data.Actual,
            (_, _) => throw failure));
        Assert.Same(failure, observed);
    }

}
