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

public sealed class ContractValidationException(string message) : Exception(message);
public sealed class InvalidWorkflowTransitionException(string message) : Exception(message);
