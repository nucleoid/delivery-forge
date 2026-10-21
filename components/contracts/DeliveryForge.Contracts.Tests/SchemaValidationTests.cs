using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Contracts.Tests;

public sealed class SchemaValidationTests
{
    [Fact]
    public void Custom_authority_registry_is_honored_by_consumers()
    {
        var policy = LoadNode("evidence-policy.json");
        var gate = GateNode(policy, "custom");
        gate["gateId"] = "custom";
        gate["authorityBinding"]!["gateId"] = "custom";
        gate["authorityBinding"]!["pluginId"] = "host.custom";
        gate["authorityBinding"]!["pluginVersion"] = "2.0.0";
        gate["authorityBinding"]!["tool"] = "custom-tool";
        gate["authorityBinding"]!["toolVersion"] = "3.0.0";
        using (var envelope = JsonDocument.Parse(gate["command"]!.GetValue<string>()))
        {
            var command = JsonNode.Parse(envelope.RootElement.GetRawText())!.AsObject();
            command["capabilityTool"] = "custom-tool";
            command["capabilityVersion"] = "3.0.0";
            command["capabilityFormat"] = "custom-v1";
            gate["command"] = command.ToJsonString();
        }
        SetIdentity(gate);
        using var document = JsonDocument.Parse(Bytes(gate));
        var custom = new TrustedGatePluginRegistry(
            [new TrustedGatePluginBinding("custom", "host.custom", "2.0.0", "custom-tool", "3.0.0", "custom-v1")]);

        Assert.False(GateReceiptAuthority.TryValidateAuthoritativeOutcome(
            document.RootElement, out _, TrustedGatePluginRegistry.BuiltIns));
        Assert.True(GateReceiptAuthority.TryValidateAuthoritativeOutcome(
            document.RootElement, out var reason, custom), reason);
    }

    [Fact]
    public void Canonical_v1_gate_receipt_rejects_v2_authority_binding()
    {
        var path = FixturePath("Valid", "gate-receipt.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["authorityBinding"] = new JsonObject
        {
            ["gateId"] = json["gateId"]!.GetValue<string>(),
            ["policyIdentity"] = json["policyIdentity"]!.GetValue<string>(),
            ["headCommit"] = json["headCommit"]!.GetValue<string>(),
            ["pluginId"] = "delivery-forge.dotnet",
            ["pluginVersion"] = "1.0.0",
            ["tool"] = "dotnet",
            ["toolVersion"] = "10.0.401",
            ["toolIdentity"] = "sha256:" + new string('a', 64),
            ["configurationIdentity"] = "sha256:" + new string('b', 64),
            ["runtimeClosureIdentity"] = "sha256:" + new string('b', 64),
            ["inputSetIdentity"] = "sha256:" + new string('c', 64),
            ["enabled"] = true,
            ["status"] = "EXECUTED",
            ["productionCapable"] = true,
            ["authority"] = "production"
        };
        SetIdentity(json);

        Assert.Throws<ContractValidationException>(() =>
            ContractValidator.ParseAndValidate(Utf8(json.ToJsonString())));
    }
    public static IEnumerable<object[]> ValidFixtures() =>
        Directory.EnumerateFiles(FixturePath("Valid"), "*.json")
            .Order(StringComparer.Ordinal)
            .Select(path => new object[] { path });

    public static IEnumerable<object[]> InvalidFixtures() =>
        Directory.EnumerateFiles(FixturePath("Invalid"), "*.json")
            .Order(StringComparer.Ordinal)
            .Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Every_authoritative_schema_has_a_valid_canonical_fixture(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var result = ContractValidator.ParseAndValidate(bytes);
        Assert.StartsWith(result.SchemaName, Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void Invalid_contract_fixtures_fail_for_the_intended_rule(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        SetIdentity(node);

        var error = Assert.Throws<ContractValidationException>(() =>
            ContractValidator.ParseAndValidate(Utf8(node.ToJsonString())));
        var expected = Path.GetFileName(path) switch
        {
            "missing-exact-head.json" => "headCommit is required",
            "not-applicable-without-rationale.json" => "notApplicableRationale is required",
            "unknown-mode.json" => "unknown enum value",
            _ => throw new InvalidOperationException("Unmapped invalid fixture.")
        };
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Every_embedded_schema_has_a_fixture()
    {
        var schemaNames = typeof(ContractValidator).Assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".schema.json", StringComparison.Ordinal))
            .Select(name => name.Split('.')[^3])
            .Order(StringComparer.Ordinal)
            .ToArray();
        var fixtureNames = Directory.EnumerateFiles(FixturePath("Valid"), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(schemaNames, fixtureNames);
    }

    [Fact]
    public void Unknown_fields_fail_closed_even_when_identity_is_recomputed()
    {
        var node = JsonNode.Parse(File.ReadAllText(FixturePath("Valid", "plan.json")))!.AsObject();
        node["futureField"] = true;
        SetIdentity(node);

        var error = Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Utf8(node.ToJsonString())));
        Assert.Contains("not a recognized field", error.Message);
    }

    [Fact]
    public void Mutation_after_hashing_is_rejected()
    {
        var json = File.ReadAllText(FixturePath("Valid", "plan.json")).Replace("revision-1", "revision-2", StringComparison.Ordinal);
        var error = Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Utf8(json)));
        Assert.Contains("Identity mismatch", error.Message);
    }

    [Theory]
    [InlineData("plan.json", "baseCommit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    [InlineData("review-receipt.json", "patchSha256", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    public void Schema_patterns_reject_trailing_newlines(string fixture, string property, string value)
    {
        var node = LoadNode(fixture);
        node[property] = value;
        SetIdentity(node);

        Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Bytes(node).Span));
    }

    [Theory]
    [InlineData("2026-10-07 05:00:00Z")]
    [InlineData("2026-10-07T05:00Z")]
    [InlineData("2026-13-07T05:00:00Z")]
    public void Timestamps_require_invariant_rfc3339_utc(string value)
    {
        var node = LoadNode("plan.json");
        node["createdAt"] = value;
        SetIdentity(node);

        Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Bytes(node).Span));
    }

    [Fact]
    public void Passing_gate_cannot_claim_source_changed_during_evaluation()
    {
        var node = JsonNode.Parse(File.ReadAllText(FixturePath("Valid", "gate-receipt.json")))!.AsObject();
        node["sourceChanged"] = true;
        SetIdentity(node);

        var error = Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Utf8(node.ToJsonString())));
        Assert.Contains("sourceChanged", error.Message);
    }

    [Theory]
    [InlineData("run-manifest.json", "workflowState", "FUTURE_STATE")]
    [InlineData("gate-receipt.json", "outcome", "SKIPPED")]
    [InlineData("plan.json", "baseCommit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Unknown_enums_and_malformed_hashes_fail_closed(string fixture, string property, string value)
    {
        var node = JsonNode.Parse(File.ReadAllText(FixturePath("Valid", fixture)))!.AsObject();
        node[property] = value;
        SetIdentity(node);

        Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Utf8(node.ToJsonString())));
    }

    [Fact]
    public void Cross_references_must_resolve_to_documents_in_the_bundle()
    {
        var plan = LoadNode("plan.json");
        var policy = LoadNode("evidence-policy.json");
        policy["requiredGates"] = new JsonArray("build", "test");
        SetIdentity(policy);
        var gates = new[] { "build", "test" }.Select(gateId => GateNode(policy, gateId)).ToArray();
        var manifest = LoadNode("run-manifest.json");
        manifest["planIdentity"] = plan["identity"]!.GetValue<string>();
        manifest["policyIdentity"] = policy["identity"]!.GetValue<string>();
        manifest["gateReceiptIds"] = new JsonArray(gates.Select(gate => JsonValue.Create(gate["identity"]!.GetValue<string>())).ToArray());
        SetIdentity(manifest);

        var replay = LoadNode("replay-bundle.json");
        replay["runManifestIdentity"] = manifest["identity"]!.GetValue<string>();
        replay["receiptIdentities"] = new JsonArray(gates.Select(gate => JsonValue.Create(gate["identity"]!.GetValue<string>())).ToArray());
        replay["contractIdentities"] = new JsonArray(plan["identity"]!.GetValue<string>(), policy["identity"]!.GetValue<string>());
        SetIdentity(replay);

        ReadOnlyMemory<byte>[] complete = [Bytes(plan), Bytes(policy), .. gates.Select(Bytes), Bytes(manifest), Bytes(replay)];
        ValidateReferences(complete);

        ReadOnlyMemory<byte>[] missingGate = [Bytes(plan), Bytes(policy), .. gates.Skip(1).Select(Bytes), Bytes(manifest), Bytes(replay)];
        Assert.Throws<ContractReferenceException>(() => ValidateReferences(missingGate));

        manifest["planIdentity"] = gates[0]["identity"]!.GetValue<string>();
        SetIdentity(manifest);
        ReadOnlyMemory<byte>[] wrongKind = [Bytes(plan), Bytes(policy), .. gates.Select(Bytes), Bytes(manifest)];
        var error = Assert.Throws<ContractReferenceException>(() => ValidateReferences(wrongKind));
        Assert.Contains("invalid kind", error.Message);
    }

    [Fact]
    public void Explicit_future_v2_authority_rejects_gate_files_without_host_store_provenance()
    {
        var bundle = ReferenceBundle.Create();

        var error = Assert.Throws<ContractReferenceException>(() =>
            ValidateReferences(bundle.Documents(), includeProvenance: false));

        Assert.Contains("host-owned append-only store provenance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_future_v2_authority_requires_the_pinned_registry_identity()
    {
        var bundle = ReferenceBundle.Create();

        var error = Assert.Throws<ContractReferenceException>(() => ValidateReferences(
            bundle.Documents(),
            expectedRegistryIdentity: "sha256:" + new string('0', 64)));

        Assert.Contains("registry identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_validator_exposes_no_caller_authority_activation_parameters()
    {
        var validate = typeof(ContractReferenceValidator).GetMethods()
            .Single(method => method.Name == nameof(ContractReferenceValidator.Validate));

        Assert.Single(validate.GetParameters());
    }

    [Fact]
    public void Host_receipt_store_is_durable_across_instances_and_detects_replacement()
    {
        var policy = LoadNode("evidence-policy.json");
        var receipt = ContractValidator.ParseAndValidate(Bytes(GateNode(policy, "test")).Span);
        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-store-test", Guid.NewGuid().ToString("N"));
        try
        {
            var key = SHA256.HashData(Encoding.UTF8.GetBytes("restart-test-authority"));
            var store = new HostOwnedReceiptStore(root, key);
            var path = store.Append(receipt);
            var reopened = new HostOwnedReceiptStore(root, key);
            Assert.True(reopened.Provenance.Contains(receipt.Identity));
            Assert.Equal(receipt.Identity, reopened.Read(receipt.Identity).Identity);

            var ledgerPath = Path.Combine(root, ".provenance", receipt.Identity.Replace(':', '-') + ".entry");
            var genuineLedger = File.ReadAllBytes(ledgerPath);
            var publicDigest = Convert.ToHexStringLower(SHA256.HashData(receipt.CanonicalBytes.Span));
            File.WriteAllText(ledgerPath, receipt.Identity + " " + publicDigest + " " + new string('0', 64) + "\n");
            Assert.False(reopened.Provenance.Contains(receipt.Identity));
            File.WriteAllBytes(ledgerPath, genuineLedger);
            Assert.True(reopened.Provenance.Contains(receipt.Identity));

            File.WriteAllText(path, "replaced");

            Assert.Throws<IOException>(() => store.Append(receipt));
            Assert.False(store.Provenance.Contains(receipt.Identity));
            Assert.False(reopened.Provenance.Contains(receipt.Identity));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("baseCommit")]
    [InlineData("headCommit")]
    [InlineData("treeId")]
    public void Manifest_receipts_bind_every_exact_revision_field(string field)
    {
        var bundle = ReferenceBundle.Create();
        bundle.Gates[0][field] = new string('f', 40);
        bundle.Relink();

        AssertReferenceMessage("different head or tree", () => ValidateReferences(bundle.Documents()));
    }

    [Theory]
    [InlineData("baseCommit")]
    [InlineData("headCommit")]
    [InlineData("treeId")]
    public void Replay_receipts_bind_every_manifest_revision_field(string field)
    {
        var bundle = ReferenceBundle.Create();
        var review = LoadNode("review-receipt.json");
        review[field] = new string('f', 40);
        SetIdentity(review);
        bundle.Replay["receiptIdentities"] = new JsonArray(
            bundle.Gates.Select(gate => JsonValue.Create(gate["identity"]!.GetValue<string>()))
                .Append(JsonValue.Create(review["identity"]!.GetValue<string>())).ToArray());
        SetIdentity(bundle.Replay);

        AssertReferenceMessage("different head or tree", () =>
            ValidateReferences(bundle.Documents(review)));
    }

    [Fact]
    public void Manifest_base_commit_must_match_its_plan()
    {
        var bundle = ReferenceBundle.Create();
        bundle.Plan["baseCommit"] = new string('f', 40);
        bundle.Relink();

        AssertReferenceMessage("baseCommit does not match", () => ValidateReferences(bundle.Documents()));
    }

    [Fact]
    public void Manifest_repository_must_match_its_plan()
    {
        var bundle = ReferenceBundle.Create();
        bundle.Manifest["repository"] = "other/repository";
        bundle.Relink();

        AssertReferenceMessage("repository does not match", () => ValidateReferences(bundle.Documents()));
    }

    [Fact]
    public void Manifest_authority_cannot_exceed_its_policy()
    {
        var bundle = ReferenceBundle.Create();
        bundle.Manifest["authorizationCeiling"] = "merge";
        bundle.Relink();

        AssertReferenceMessage("authorizationCeiling exceeds", () => ValidateReferences(bundle.Documents()));
    }

    [Fact]
    public void Manifest_gates_must_have_policy_satisfying_outcomes()
    {
        var failing = ReferenceBundle.Create();
        failing.Gates[0]["outcome"] = "FAIL";
        failing.Relink();
        AssertReferenceMessage("not a policy-satisfying outcome", () =>
            ValidateReferences(failing.Documents()));

        var wrongPolicy = ReferenceBundle.Create();
        var alternatePolicy = LoadNode("evidence-policy.json");
        alternatePolicy["policyId"] = "alternate";
        SetIdentity(alternatePolicy);
        wrongPolicy.Gates[0]["policyIdentity"] = alternatePolicy["identity"]!.GetValue<string>();
        wrongPolicy.Relink();
        AssertReferenceMessage("not a policy-satisfying outcome", () =>
            ValidateReferences(wrongPolicy.Documents(alternatePolicy)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Default_validator_preserves_canonical_v1_manifest_and_replay_acceptance(bool notApplicable)
    {
        var legacy = ReferenceBundle.Create();
        if (notApplicable)
        {
            legacy.Policy["allowedNotApplicable"] = new JsonArray("build");
            SetIdentity(legacy.Policy);
            foreach (var gate in legacy.Gates)
                gate["policyIdentity"] = legacy.Policy["identity"]!.GetValue<string>();
            legacy.Gates[0]["outcome"] = "NOT_APPLICABLE";
            legacy.Gates[0]["notApplicableRationale"] = "Not required by the protected policy on this host";
        }
        foreach (var gate in legacy.Gates)
        {
            gate.Remove("authorityBinding");
            gate["schemaVersion"] = "1.0.0";
        }
        legacy.Relink();

        ContractReferenceValidator.Validate(legacy.Documents());
    }

    [Fact]
    public void Explicit_future_v2_authority_rejects_legacy_or_impersonated_receipts()
    {
        var legacy = ReferenceBundle.Create();
        legacy.Gates[0].Remove("authorityBinding");
        legacy.Gates[0]["schemaVersion"] = "1.0.0";
        legacy.Relink();
        AssertReferenceMessage("lacks a canonical authority binding", () =>
            ValidateReferences(legacy.Documents()));

        var impersonated = ReferenceBundle.Create();
        impersonated.Gates[0]["authorityBinding"]!["pluginId"] = "delivery-forge.anti-slop";
        impersonated.Gates[0]["authorityBinding"]!["tool"] = "oxlint";
        impersonated.Gates[0]["authorityBinding"]!["toolVersion"] = "1.78.0";
        impersonated.Relink();
        AssertReferenceMessage("impersonates the trusted gate/plugin mapping", () =>
            ValidateReferences(impersonated.Documents()));
    }

    [Fact]
    public void Default_validator_accepts_mixed_v1_and_v2_manifest_and_replay_evidence()
    {
        var bundle = ReferenceBundle.Create();
        bundle.Gates[0].Remove("authorityBinding");
        bundle.Gates[0]["schemaVersion"] = "1.0.0";
        bundle.Relink();

        ContractReferenceValidator.Validate(bundle.Documents());
    }

    [Fact]
    public void One_trusted_registry_covers_crap_and_allows_code_owned_external_registration()
    {
        Assert.True(TrustedGatePluginRegistry.BuiltIns.TryGet("crap", out var crap));
        Assert.Equal("delivery-forge.crap4csharp", crap.PluginId);

        var policy = LoadNode("evidence-policy.json");
        var gate = GateNode(policy, "custom");
        gate["authorityBinding"]!["gateId"] = "custom";
        gate["authorityBinding"]!["pluginId"] = "example.custom";
        gate["authorityBinding"]!["pluginVersion"] = "2.1.0";
        gate["authorityBinding"]!["tool"] = "custom-tool";
        gate["command"] = CommandEnvelope("custom-tool", "10.0.401", "trx-v1");
        SetIdentity(gate);
        using var document = System.Text.Json.JsonDocument.Parse(Bytes(gate));

        Assert.False(GateReceiptAuthority.TryValidateAuthoritativePass(document.RootElement, out _));
        var registry = new TrustedGatePluginRegistry(
            [new TrustedGatePluginBinding("custom", "example.custom", "2.1.0", "custom-tool")]);
        Assert.True(GateReceiptAuthority.TryValidateAuthoritativePass(
            document.RootElement, out var reason, registry), reason);
    }

    [Fact]
    public void Manifest_must_include_each_policy_required_gate()
    {
        var bundle = ReferenceBundle.Create();
        bundle.Manifest["gateReceiptIds"] = new JsonArray(
            bundle.Gates.Take(1).Select(gate => JsonValue.Create(gate["identity"]!.GetValue<string>())).ToArray());
        SetIdentity(bundle.Manifest);
        bundle.Replay["runManifestIdentity"] = bundle.Manifest["identity"]!.GetValue<string>();
        SetIdentity(bundle.Replay);

        AssertReferenceMessage("does not include every gate required", () =>
            ValidateReferences(bundle.Documents()));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("policy")]
    [InlineData("gate")]
    public void Replay_bundle_must_include_all_manifest_evidence(string omitted)
    {
        var bundle = ReferenceBundle.Create();
        if (omitted == "gate")
        {
            bundle.Replay["receiptIdentities"] = new JsonArray(
                bundle.Gates.Skip(1).Select(gate => JsonValue.Create(gate["identity"]!.GetValue<string>())).ToArray());
        }
        else
        {
            var excluded = omitted == "plan"
                ? bundle.Plan["identity"]!.GetValue<string>()
                : bundle.Policy["identity"]!.GetValue<string>();
            bundle.Replay["contractIdentities"] = new JsonArray(
                bundle.Replay["contractIdentities"]!.AsArray()
                    .Select(item => item!.GetValue<string>()).Where(identity => identity != excluded)
                    .Select(identity => JsonValue.Create(identity)).ToArray());
        }
        SetIdentity(bundle.Replay);

        AssertReferenceMessage("omits manifest plan, policy, or gate evidence", () =>
            ValidateReferences(bundle.Documents()));
    }

    [Fact]
    public void Duplicate_contract_identities_fail_as_reference_errors()
    {
        var plan = LoadNode("plan.json");
        AssertReferenceMessage("Duplicate immutable contract identity", () =>
            ValidateReferences([Bytes(plan), Bytes(plan)]));
    }

    [Fact]
    public void Evidence_policy_requires_at_least_one_gate()
    {
        var policy = LoadNode("evidence-policy.json");
        policy["requiredGates"] = new JsonArray();
        SetIdentity(policy);

        Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Bytes(policy).Span));
    }

    [Fact]
    public void Passing_gate_requires_zero_exit_code()
    {
        var gate = LoadNode("gate-receipt.json");
        gate["exitCode"] = 1;
        SetIdentity(gate);

        var error = Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Bytes(gate).Span));
        Assert.Contains("exitCode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Passing_gate_accepts_integral_zero_without_leaking_parser_exceptions()
    {
        var gate = LoadNode("gate-receipt.json");
        gate["exitCode"] = 0.0m;
        SetIdentity(gate);

        var contract = ContractValidator.ParseAndValidate(Bytes(gate).Span);
        Assert.Equal("gate-receipt", contract.SchemaName);
    }

    [Fact]
    public void Present_authority_binding_must_be_complete_and_canonical()
    {
        var policy = LoadNode("evidence-policy.json");
        var gate = GateNode(policy, "test");
        gate["authorityBinding"]!.AsObject().Remove("toolIdentity");
        SetIdentity(gate);

        Assert.Throws<ContractValidationException>(() =>
            ContractValidator.ParseAndValidate(Bytes(gate).Span));
    }

    [Fact]
    public void Authoritative_schema_encodes_the_passing_exit_code_rule()
    {
        var assembly = typeof(ContractValidator).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".gate-receipt.schema.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var schema = JsonNode.Parse(stream)!.AsObject();
        var passRule = schema["allOf"]!.AsArray().Single(rule =>
            rule!["if"]!["properties"]!["outcome"]!["const"]!.GetValue<string>() == "PASS");

        Assert.Equal(0, passRule!["then"]!["properties"]!["exitCode"]!["const"]!.GetValue<int>());
    }

    [Fact]
    public void Extreme_number_exponents_fail_as_contract_validation_errors()
    {
        var json = File.ReadAllText(FixturePath("Valid", "gate-receipt.json"))
            .Replace("\"exitCode\":0", "\"exitCode\":1e-2147483649", StringComparison.Ordinal);

        Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Utf8(json)));
    }

    [Fact]
    public void Receipt_references_must_match_checkpoint_head_and_tree()
    {
        var policy = LoadNode("evidence-policy.json");
        var gate = GateNode(policy, "test");
        var checkpoint = LoadNode("checkpoint.json");
        checkpoint["gateReceiptIds"] = new JsonArray(gate["identity"]!.GetValue<string>());
        checkpoint["headCommit"] = new string('f', 40);
        SetIdentity(checkpoint);

        ReadOnlyMemory<byte>[] documents = [Bytes(policy), Bytes(gate), Bytes(checkpoint)];
        var error = Assert.Throws<ContractReferenceException>(() => ContractReferenceValidator.Validate(documents));
        Assert.Contains("different head or tree", error.Message);
    }

    [Fact]
    public void Schema_integer_fields_enforce_safe_interoperable_range()
    {
        var checkpoint = LoadNode("checkpoint.json");
        checkpoint["sequence"] = 9007199254740992m;
        SetIdentity(checkpoint);
        Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Bytes(checkpoint).Span));
    }

    [Fact]
    public void Unique_items_compare_decoded_values()
    {
        var plan = LoadNode("plan.json");
        plan["scope"]!["included"] = new JsonArray("build", "build");
        SetIdentity(plan);
        var escaped = plan.ToJsonString().Replace("\"build\",\"build\"", "\"build\",\"\\u0062uild\"", StringComparison.Ordinal);
        Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(Utf8(escaped)));
    }

    [Fact]
    public void Not_applicable_gate_must_be_allowed_by_its_immutable_policy()
    {
        var policy = LoadNode("evidence-policy.json");
        var gate = LoadNode("gate-receipt.json");
        gate["gateId"] = "build";
        gate["outcome"] = "NOT_APPLICABLE";
        gate["notApplicableRationale"] = "Unavailable on this host";
        SetIdentity(gate);

        ReadOnlyMemory<byte>[] documents = [Bytes(policy), Bytes(gate)];
        var error = Assert.Throws<ContractReferenceException>(() => ContractReferenceValidator.Validate(documents));
        Assert.Contains("does not permit", error.Message);
    }

    [Fact]
    public void Policy_satisfying_not_applicable_requires_v2_central_authority()
    {
        var policy = LoadNode("evidence-policy.json");
        var legacy = LoadNode("gate-receipt.json");
        legacy["gateId"] = "mutation";
        legacy["outcome"] = "NOT_APPLICABLE";
        legacy["notApplicableRationale"] = "No mutation targets";
        SetIdentity(legacy);
        using (var legacyDocument = System.Text.Json.JsonDocument.Parse(Bytes(legacy)))
            Assert.False(GateReceiptAuthority.TryValidateAuthoritativeOutcome(legacyDocument.RootElement, out _));

        var authoritative = GateNode(policy, "mutation");
        authoritative["outcome"] = "NOT_APPLICABLE";
        authoritative["exitCode"] = null;
        authoritative["notApplicableRationale"] = "No mutation targets in protected scope";
        authoritative["authorityBinding"]!["status"] = "NOT_APPLICABLE";
        SetIdentity(authoritative);
        using var document = System.Text.Json.JsonDocument.Parse(Bytes(authoritative));

        Assert.True(GateReceiptAuthority.TryValidateAuthoritativeOutcome(
            document.RootElement, out var reason), reason);
    }

    [Theory]
    [InlineData("capabilityVersion", "1.77.0")]
    [InlineData("capabilityFormat", "forged-oxlint-json-v1")]
    public void Authoritative_not_applicable_requires_trusted_pinned_metadata(string field, string staleValue)
    {
        var policy = LoadNode("evidence-policy.json");
        var gate = GateNode(policy, "anti-slop");
        gate["outcome"] = "NOT_APPLICABLE";
        gate["exitCode"] = null;
        gate["notApplicableRationale"] = "No JavaScript-family inputs in protected scope";
        gate["authorityBinding"]!["status"] = "NOT_APPLICABLE";
        var command = JsonNode.Parse(gate["command"]!.GetValue<string>())!.AsObject();
        command[field] = staleValue;
        gate["command"] = command.ToJsonString();
        if (field == "capabilityVersion")
            gate["authorityBinding"]!["toolVersion"] = staleValue;
        SetIdentity(gate);
        using var document = System.Text.Json.JsonDocument.Parse(Bytes(gate));

        Assert.False(GateReceiptAuthority.TryValidateAuthoritativeOutcome(
            document.RootElement, out var reason));
        Assert.Contains("trusted pinned tool version/format", reason, StringComparison.Ordinal);
    }

    private sealed record ReferenceBundle(
        JsonObject Plan,
        JsonObject Policy,
        JsonObject[] Gates,
        JsonObject Manifest,
        JsonObject Replay)
    {
        public static ReferenceBundle Create()
        {
            var bundle = new ReferenceBundle(
                LoadNode("plan.json"),
                LoadNode("evidence-policy.json"),
                [],
                LoadNode("run-manifest.json"),
                LoadNode("replay-bundle.json"));
            bundle.Policy["requiredGates"] = new JsonArray("build", "test");
            SetIdentity(bundle.Policy);
            bundle = bundle with
            {
                Gates = new[] { "build", "test" }.Select(gateId => GateNode(bundle.Policy, gateId)).ToArray()
            };
            foreach (var gate in bundle.Gates)
            {
                gate["policyIdentity"] = bundle.Policy["identity"]!.GetValue<string>();
                SetIdentity(gate);
            }
            bundle.Relink();
            return bundle;
        }

        public void Relink()
        {
            SetIdentity(Plan);
            SetIdentity(Policy);
            foreach (var gate in Gates)
            {
                if (gate["authorityBinding"] is JsonObject binding)
                {
                    binding["gateId"] = gate["gateId"]!.GetValue<string>();
                    binding["policyIdentity"] = gate["policyIdentity"]!.GetValue<string>();
                    binding["headCommit"] = gate["headCommit"]!.GetValue<string>();
                }
                SetIdentity(gate);
            }
            Manifest["planIdentity"] = Plan["identity"]!.GetValue<string>();
            Manifest["policyIdentity"] = Policy["identity"]!.GetValue<string>();
            Manifest["gateReceiptIds"] = new JsonArray(
                Gates.Select(gate => JsonValue.Create(gate["identity"]!.GetValue<string>())).ToArray());
            SetIdentity(Manifest);
            Replay["runManifestIdentity"] = Manifest["identity"]!.GetValue<string>();
            Replay["receiptIdentities"] = new JsonArray(
                Gates.Select(gate => JsonValue.Create(gate["identity"]!.GetValue<string>())).ToArray());
            Replay["contractIdentities"] = new JsonArray(
                Plan["identity"]!.GetValue<string>(), Policy["identity"]!.GetValue<string>());
            SetIdentity(Replay);
        }

        public ReadOnlyMemory<byte>[] Documents(params JsonObject[] extra) =>
            [Bytes(Plan), Bytes(Policy), .. Gates.Select(Bytes), Bytes(Manifest), Bytes(Replay), .. extra.Select(Bytes)];
    }

    private static void AssertReferenceMessage(string expected, Action action)
    {
        var error = Assert.Throws<ContractReferenceException>(action);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    private static void ValidateReferences(
        IEnumerable<ReadOnlyMemory<byte>> documents,
        bool includeProvenance = true,
        string? expectedRegistryIdentity = null)
    {
        var materialized = documents.ToArray();
        var store = new HostOwnedReceiptStore(Path.Combine(
            Path.GetTempPath(), "delivery-forge-reference-receipts", Guid.NewGuid().ToString("N")), new byte[32]);
        foreach (var bytes in materialized)
        {
            try
            {
                var contract = ContractValidator.ParseAndValidate(bytes.Span);
                if (contract.SchemaName == "gate-receipt") store.Append(contract);
            }
            catch (ContractValidationException)
            {
                // The validator under test owns the canonical error for malformed input.
            }
        }
        var policy = GateReceiptConsumerPolicy.ForIsolatedTest(new TransitionEvidence(
            AuthorizationCeiling.Plan,
            GateAuthorityRegistry: TrustedGatePluginRegistry.BuiltIns,
            GateReceiptProvenance: includeProvenance ? store.Provenance : null,
            EnforceAuthoritativeGateReceipts: true,
            GateAuthorityRegistryIdentity: expectedRegistryIdentity ?? TrustedGatePluginRegistry.BuiltIns.Identity));
        ContractReferenceValidator.ValidateForIsolatedTest(materialized, policy);
    }

    private static void SetIdentity(JsonObject node)
    {
        node["identity"] = "sha256:" + new string('0', 64);
        node["identity"] = CanonicalJson.ComputeIdentity(Utf8(node.ToJsonString()));
    }

    private static JsonObject LoadNode(string fixture) =>
        JsonNode.Parse(File.ReadAllText(FixturePath("Valid", fixture)))!.AsObject();

    private static JsonObject GateNode(JsonObject policy, string gateId)
    {
        var gate = LoadNode("gate-receipt.json");
        gate["schemaVersion"] = "2.0.0";
        gate["policyIdentity"] = policy["identity"]!.GetValue<string>();
        gate["gateId"] = gateId;
        var (plugin, pluginVersion, tool, toolVersion, format) = gateId switch
        {
            "mutation" => ("delivery-forge.mutate4csharp", "1.0.0", "mutate4csharp", "0.1.0-format-only", "2"),
            "anti-slop" => ("dmmulroy.anti-slop", "0.1.2+13909c9e07590f5a85d93c62712259fe14a883f7", "oxlint", "1.78.0", "oxlint-json-v1"),
            _ => ("delivery-forge.dotnet", "1.0.0", "dotnet", "10.0.401", "trx-v1")
        };
        var runtimeClosure = gateId == "anti-slop"
            ? "sha256:f3c39acbe45af98d4ea005347660992ac47a75fb6511a9ba6c2563792a3be267"
            : "sha256:" + new string('b', 64);
        gate["command"] = CommandEnvelope(tool, toolVersion, format, runtimeClosure);
        gate["authorityBinding"] = new JsonObject
        {
            ["gateId"] = gateId,
            ["policyIdentity"] = policy["identity"]!.GetValue<string>(),
            ["headCommit"] = gate["headCommit"]!.GetValue<string>(),
            ["pluginId"] = plugin,
            ["pluginVersion"] = pluginVersion,
            ["tool"] = tool,
            ["toolVersion"] = toolVersion,
            ["toolIdentity"] = "sha256:" + new string('a', 64),
            ["configurationIdentity"] = "sha256:" + new string('b', 64),
            ["runtimeClosureIdentity"] = runtimeClosure,
            ["inputSetIdentity"] = "sha256:" + new string('c', 64),
            ["enabled"] = true,
            ["status"] = "EXECUTED",
            ["productionCapable"] = true,
            ["authority"] = "production"
        };
        SetIdentity(gate);
        return gate;
    }

    private static string CommandEnvelope(string tool, string version, string format,
        string? runtimeClosure = null) => new JsonObject
        {
            ["capabilityTool"] = tool,
            ["capabilityVersion"] = version,
            ["capabilityFormat"] = format,
            ["executableIdentity"] = "sha256:" + new string('a', 64),
            ["configurationIdentity"] = "sha256:" + new string('b', 64),
            ["plugin"] = new JsonObject
            {
                ["runtimeClosureIdentity"] = runtimeClosure ?? "sha256:" + new string('b', 64),
                ["inputSetIdentity"] = "sha256:" + new string('c', 64)
            }
        }.ToJsonString();

    private static ReadOnlyMemory<byte> Bytes(JsonObject node) => Utf8(node.ToJsonString());

    private static string FixturePath(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "Fixtures" }.Concat(parts).ToArray());

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
