using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeliveryForge.Evidence.Tests;

// Policy DATA controls only: these cannot issue a current invocation binding or gate receipt.
public sealed class MutationPolicyBindingTests
{
    const string SparseConfiguration = """{"policy":{"maxWorkers":1,"mutationCap":100,"overallDeadlineSeconds":600}}""";
    static JsonObject Complete() => new()
    {
        ["maxWorkers"] = 1, ["mutationCap"] = 100, ["baselineTimeoutSeconds"] = 600,
        ["mutantTimeoutSeconds"] = 120, ["overallDeadlineSeconds"] = 600,
        ["allowNotApplicable"] = false, ["stabilityRepetitions"] = 1
    };
    static void Join(string configuration, JsonNode policy)
    {
        using var before = JsonDocument.Parse(configuration);
        using var report = JsonDocument.Parse(policy.ToJsonString());
        MutationPolicyBinding.RequireSame(before.RootElement, report.RootElement);
    }

    [Fact]
    public void Actual_sparse_killed_case_policy_joins_supported_complete_defaults()
    {
        // Exact retained d3fa killed-consumer policy and actual emitted seven policy values.
        // Six actual kills remain historical process evidence; this test grants no live authority.
        Join(SparseConfiguration, Complete());
    }

    [Fact]
    public void Full_explicit_policy_preserves_every_override_without_taking_report_defaults()
    {
        var policy = Complete(); policy["mutationCap"] = 80; policy["baselineTimeoutSeconds"] = 180;
        policy["mutantTimeoutSeconds"] = 90; policy["overallDeadlineSeconds"] = 500;
        policy["allowNotApplicable"] = true; policy["stabilityRepetitions"] = 2;
        Join(new JsonObject { ["policy"] = policy.DeepClone() }.ToJsonString(), policy);
        var actual = policy.DeepClone(); actual["baselineTimeoutSeconds"] = 600;
        var error = Assert.Throws<EvidenceWriteException>(() => Join(
            new JsonObject { ["policy"] = policy.DeepClone() }.ToJsonString(), actual));
        Assert.Contains("differs", error.Message);
    }

    [Theory]
    [InlineData("maxWorkers")]
    [InlineData("mutationCap")]
    [InlineData("baselineTimeoutSeconds")]
    [InlineData("mutantTimeoutSeconds")]
    [InlineData("overallDeadlineSeconds")]
    [InlineData("allowNotApplicable")]
    [InlineData("stabilityRepetitions")]
    public void Report_cannot_change_explicit_or_defaulted_field(string field)
    {
        var policy = Complete();
        policy[field] = field switch
        {
            "maxWorkers" => JsonValue.Create(2), "mutationCap" => JsonValue.Create(99),
            "baselineTimeoutSeconds" => JsonValue.Create(599), "mutantTimeoutSeconds" => JsonValue.Create(121),
            "overallDeadlineSeconds" => JsonValue.Create(599), "allowNotApplicable" => JsonValue.Create(true),
            _ => JsonValue.Create(2)
        };
        Assert.Throws<EvidenceWriteException>(() => Join(SparseConfiguration, policy));
    }

    [Theory]
    [InlineData("maxWorkers", 2)]
    [InlineData("mutationCap", 101)]
    [InlineData("baselineTimeoutSeconds", 601)]
    [InlineData("mutantTimeoutSeconds", 601)]
    [InlineData("overallDeadlineSeconds", 601)]
    [InlineData("stabilityRepetitions", 101)]
    public void Matching_policy_cannot_widen_starting_profile_limits(string field, int value)
    {
        var policy = Complete(); policy[field] = value;
        Assert.Throws<EvidenceWriteException>(() => Join(new JsonObject { ["policy"] = policy.DeepClone() }.ToJsonString(), policy));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"policy\":{}}")]
    public void Omitted_overall_uses_real_1800_default_then_refuses_600_ceiling(string configuration)
    {
        var policy = Complete(); policy["overallDeadlineSeconds"] = 1800;
        var error = Assert.Throws<EvidenceWriteException>(() => Join(configuration, policy));
        Assert.Contains("600-second", error.Message);
    }

    [Fact]
    public void Integral_exponent_and_fraction_spellings_match_producer_exact_number_contract()
    {
        Join("""{"policy":{"maxWorkers":1.0,"mutationCap":1e2,"overallDeadlineSeconds":6.00e2}}""", Complete());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"unknown\":1}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"MaxWorkers\":1}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"maxWorkers\":1,\"maxWorkers\":1}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"maxWorkers\":\"1\"}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"maxWorkers\":true}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"maxWorkers\":0}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"maxWorkers\":-1}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"maxWorkers\":2147483648}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"maxWorkers\":1.01}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"maxWorkers\":1.0000000000000000000000000000000001}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"maxWorkers\":1e10000000000}")]
    [InlineData("{\"overallDeadlineSeconds\":600,\"allowNotApplicable\":0}")]
    public void Invalid_or_unknown_configuration_policy_refuses(string policy)
        => Assert.Throws<EvidenceWriteException>(() => Join("{\"policy\":" + policy + "}", Complete()));

    [Fact]
    public void Report_must_be_complete_and_cannot_add_unknown_fields()
    {
        var missing = Complete(); missing.Remove("stabilityRepetitions");
        Assert.Throws<EvidenceWriteException>(() => Join(SparseConfiguration, missing));
        var unknown = Complete(); unknown["futureDefault"] = 1;
        Assert.Throws<EvidenceWriteException>(() => Join(SparseConfiguration, unknown));
    }

    [Theory]
    [InlineData("maxWorkers", "true")]
    [InlineData("mutationCap", "99.5")]
    [InlineData("mutantTimeoutSeconds", "null")]
    [InlineData("allowNotApplicable", "0")]
    public void Complete_report_cannot_substitute_scalar_types(string field, string raw)
    {
        var policy = Complete(); policy[field] = JsonNode.Parse(raw);
        Assert.Throws<EvidenceWriteException>(() => Join(SparseConfiguration, policy));
    }
}
