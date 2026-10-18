using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeliveryForge.Evidence.Tests;

// UNEXECUTED. Stock policy bytes below are DATA, not baseline/exemption or gate approval.
public sealed class CrapPolicyBindingTests
{
    [Fact]
    public void Raw_formatting_and_sorted_canonical_sets_join_before_changed_policy_refuses()
    {
        var raw = Policy(); var canonical = Policy();
        canonical["requiredChecks"] = new JsonArray("coverage", "crap", "tests");
        canonical["exemptionFiles"] = new JsonArray();
        canonical["allowedOverrides"] = new JsonObject
        {
            ["frameworks"] = false,
            ["scope"] = false,
            ["includeTests"] = false,
            ["includeGenerated"] = false,
            ["coveragePathMappings"] = false
        };
        CrapPolicyBinding.RequireSame(Bytes(raw), Bytes(canonical, true));
        canonical["threshold"] = 29;
        Assert.Throws<EvidenceWriteException>(() => CrapPolicyBinding.RequireSame(Bytes(raw), Bytes(canonical)));
    }

    [Fact]
    public void Full_required_checks_precede_missing_check_or_relaxed_ceiling_refusal()
    {
        var policy = Policy(); CrapPolicyBinding.RequireSame(Bytes(policy), Bytes(policy));
        policy["requiredChecks"] = new JsonArray("tests", "crap");
        Assert.Throws<EvidenceWriteException>(() => CrapPolicyBinding.RequireSame(Bytes(policy), Bytes(policy)));
        policy = Policy(); policy["threshold"] = 31;
        Assert.Throws<EvidenceWriteException>(() => CrapPolicyBinding.RequireSame(Bytes(policy), Bytes(policy)));
    }

    [Fact]
    public void Baseline_and_exemption_identities_cannot_be_dropped_during_semantic_join()
    {
        var original = Policy(); var canonical = Policy();
        original["baseline"] = "approved-baseline.json"; canonical["baseline"] = "approved-baseline.json";
        original["exemptionFiles"] = new JsonArray("approved-exemptions.json");
        canonical["exemptionFiles"] = new JsonArray("approved-exemptions.json");
        CrapPolicyBinding.RequireSame(Bytes(original), Bytes(canonical));
        canonical.Remove("baseline");
        Assert.Throws<EvidenceWriteException>(() => CrapPolicyBinding.RequireSame(Bytes(original), Bytes(canonical)));
        canonical["baseline"] = "approved-baseline.json";
        canonical["exemptionFiles"] = new JsonArray();
        Assert.Throws<EvidenceWriteException>(() => CrapPolicyBinding.RequireSame(Bytes(original), Bytes(canonical)));
    }
    private static JsonObject Policy() => new()
    {
        ["schemaVersion"] = "repository-policy-v1",
        ["mode"] = "strict",
        ["configuration"] = "Release",
        ["productionProjects"] = new JsonArray("src/Subject.csproj"),
        ["testProjects"] = new JsonArray("tests/Subject.Tests.csproj"),
        ["targetFrameworks"] = new JsonArray("net10.0"),
        ["scope"] = "all",
        ["threshold"] = 30,
        ["missingCoverage"] = "fail",
        ["requiredChecks"] = new JsonArray("tests", "coverage", "crap"),
        ["exclusions"] = new JsonArray(),
        ["ruleset"] = "callables-v1"
    };
    private static byte[] Bytes(JsonNode node, bool pretty = false) => JsonSerializer.SerializeToUtf8Bytes(node, new JsonSerializerOptions { WriteIndented = pretty });
}
