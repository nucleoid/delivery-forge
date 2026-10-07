using System.Text;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Contracts.Tests;

public sealed class ContractBehaviorTests
{
    [Fact]
    public void Canonicalization_is_property_order_independent()
    {
        var first = CanonicalJson.ComputeIdentity(Utf8("{\"b\":2,\"a\":1}"));
        var second = CanonicalJson.ComputeIdentity(Utf8("{\"a\":1,\"b\":2}"));
        Assert.Equal(second, first);
    }

    [Fact]
    public void Duplicate_members_are_rejected_at_nested_depth()
    {
        var error = Assert.Throws<ContractValidationException>(() =>
            ContractValidator.ParseAndValidate(Utf8("{\"schemaVersion\":\"1.0.0\",\"kind\":\"plan\",\"scope\":{\"a\":1,\"a\":2}}")));
        Assert.Contains("Duplicate", error.Message);
    }

    [Fact]
    public void Escaped_equivalent_duplicate_members_are_rejected()
    {
        var error = Assert.Throws<ContractValidationException>(() =>
            ContractValidator.ParseAndValidate(Utf8("{\"schemaVersion\":\"1.0.0\",\"kind\":\"plan\",\"a\":1,\"\\u0061\":2}")));
        Assert.Contains("Duplicate", error.Message);
    }

    [Theory]
    [InlineData("9007199254740993.0")]
    [InlineData("9.007199254740993e15")]
    [InlineData("1e30")]
    public void Generic_jcs_numbers_accept_finite_binary64_values(string number)
    {
        Assert.NotEmpty(CanonicalJson.Canonicalize(Utf8($"{{\"n\":{number}}}")));
    }

    [Fact]
    public void Invalid_surrogate_is_reported_as_contract_json_error()
    {
        var error = Assert.Throws<ContractJsonException>(() => CanonicalJson.Canonicalize(Utf8("{\"x\":\"\\ud800\"}")));
        Assert.Contains("strict UTF-8 JSON", error.Message);
    }

    [Theory]
    [MemberData(nameof(MalformedUtf8Cases))]
    public void Malformed_utf8_fails_closed(byte[] bytes)
    {
        Assert.Throws<ContractJsonException>(() => CanonicalJson.Canonicalize(bytes));
    }

    public static IEnumerable<object[]> MalformedUtf8Cases()
    {
        yield return new object[] { new byte[] { 0xc0, 0xaf } };
        yield return new object[] { new byte[] { 0xed, 0xa0, 0x80 } };
    }

    [Fact]
    public void Bom_and_trailing_data_are_rejected_before_contract_validation()
    {
        var valid = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Valid", "plan.json"));
        Assert.Throws<ContractJsonException>(() => CanonicalJson.Canonicalize(Encoding.UTF8.GetPreamble().Concat(valid).ToArray()));
        Assert.Throws<ContractJsonException>(() => CanonicalJson.Canonicalize(valid.Concat(valid).ToArray()));
    }

    [Fact]
    public void Canonicalization_supports_the_same_depth_as_strict_parsing()
    {
        var json = new string('[', 70) + "0" + new string(']', 70);
        Assert.NotEmpty(CanonicalJson.Canonicalize(Utf8(json)));
    }

    [Theory]
    [InlineData("9.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.0.1")]
    public void Unknown_schema_versions_fail_closed(string version)
    {
        var error = Assert.Throws<ContractValidationException>(() =>
            ContractValidator.ParseAndValidate(Utf8($"{{\"schemaVersion\":\"{version}\",\"kind\":\"plan\"}}")));
        Assert.Contains("schemaVersion", error.Message);
    }

    [Fact]
    public void Valid_linear_transition_is_allowed()
    {
        WorkflowTransition.EnsureAllowed(
            WorkflowState.Understanding,
            WorkflowState.Planned,
            new TransitionEvidence(AuthorizationCeiling.Plan));
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
