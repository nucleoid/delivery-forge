using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Contracts.Tests;

public sealed class SchemaValidationTests
{
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
        Assert.Equal(Path.GetFileNameWithoutExtension(path), result.SchemaName);
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
        var gate = LoadNode("gate-receipt.json");
        var manifest = LoadNode("run-manifest.json");
        manifest["planIdentity"] = plan["identity"]!.GetValue<string>();
        manifest["gateReceiptIds"] = new JsonArray(gate["identity"]!.GetValue<string>());
        SetIdentity(manifest);

        var replay = LoadNode("replay-bundle.json");
        replay["runManifestIdentity"] = manifest["identity"]!.GetValue<string>();
        replay["receiptIdentities"] = new JsonArray(gate["identity"]!.GetValue<string>());
        replay["contractIdentities"] = new JsonArray(plan["identity"]!.GetValue<string>());
        SetIdentity(replay);

        ReadOnlyMemory<byte>[] complete = [Bytes(plan), Bytes(gate), Bytes(manifest), Bytes(replay)];
        ContractReferenceValidator.Validate(complete);

        ReadOnlyMemory<byte>[] missingGate = [Bytes(plan), Bytes(manifest), Bytes(replay)];
        Assert.Throws<ContractReferenceException>(() => ContractReferenceValidator.Validate(missingGate));

        manifest["planIdentity"] = gate["identity"]!.GetValue<string>();
        SetIdentity(manifest);
        ReadOnlyMemory<byte>[] wrongKind = [Bytes(plan), Bytes(gate), Bytes(manifest)];
        var error = Assert.Throws<ContractReferenceException>(() => ContractReferenceValidator.Validate(wrongKind));
        Assert.Contains("invalid kind", error.Message);
    }

    private static void SetIdentity(JsonObject node)
    {
        node["identity"] = "sha256:" + new string('0', 64);
        node["identity"] = CanonicalJson.ComputeIdentity(Utf8(node.ToJsonString()));
    }

    private static JsonObject LoadNode(string fixture) =>
        JsonNode.Parse(File.ReadAllText(FixturePath("Valid", fixture)))!.AsObject();

    private static ReadOnlyMemory<byte> Bytes(JsonObject node) => Utf8(node.ToJsonString());

    private static string FixturePath(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "Fixtures" }.Concat(parts).ToArray());

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
