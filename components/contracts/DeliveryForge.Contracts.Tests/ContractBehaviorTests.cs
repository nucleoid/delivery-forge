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
    public void Unsafe_integral_number_forms_are_rejected(string number)
    {
        var error = Assert.Throws<ContractJsonException>(() => CanonicalJson.Canonicalize(Utf8($"{{\"n\":{number}}}")));
        Assert.Contains("safe integer", error.Message);
    }

    [Fact]
    public void Invalid_surrogate_is_reported_as_contract_validation_error()
    {
        Assert.Throws<ContractValidationException>(() =>
            ContractValidator.ParseAndValidate(Utf8("{\"schemaVersion\":\"1.0.0\",\"kind\":\"plan\",\"x\":\"\\ud800\"}")));
    }

    [Theory]
    [MemberData(nameof(MalformedUtf8Cases))]
    public void Malformed_utf8_bom_and_trailing_data_fail_closed(byte[] bytes)
    {
        Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(bytes));
    }

    public static IEnumerable<object[]> MalformedUtf8Cases()
    {
        yield return new object[] { new byte[] { 0xc0, 0xaf } };
        yield return new object[] { new byte[] { 0xed, 0xa0, 0x80 } };
        yield return new object[] { Encoding.UTF8.GetPreamble().Concat(Utf8("{}")).ToArray() };
        yield return new object[] { Utf8("{}{}") };
    }

    [Fact]
    public void Canonicalization_supports_the_same_depth_as_strict_parsing()
    {
        var json = new string('[', 70) + "0" + new string(']', 70);
        Assert.NotEmpty(CanonicalJson.Canonicalize(Utf8(json)));
    }

    [Fact]
    public void Unknown_schema_versions_fail_closed()
    {
        var error = Assert.Throws<ContractValidationException>(() =>
            ContractValidator.ParseAndValidate(Utf8("{\"schemaVersion\":\"9.0.0\",\"kind\":\"plan\"}")));
        Assert.Contains("schemaVersion", error.Message);
    }

    [Fact]
    public void Publication_requires_pr_ceiling_and_receipt()
    {
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
            WorkflowState.PrAuthorized,
            WorkflowState.PrPublished,
            new TransitionEvidence(AuthorizationCeiling.Implement)));
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
