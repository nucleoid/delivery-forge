namespace DeliveryForge.Evidence.Tests;

// Pure role validation is not artifact, approval or execution authority. UNEXECUTED.
public sealed class ProtectedHistoricalBaselineRoleTests
{
    static readonly string P = new('1', 40), H0 = new('2', 40), Other = new('3', 40);
    [Fact]
    public void Genuine_historical_transition_does_not_require_zero_work_self_diff()
    {
        ProtectedBaselineRegistry.RequireHistoricalRoles(P, H0, H0);
    }
    [Fact]
    public void Self_diff_cannot_bootstrap_a_complete_mutation_baseline()
    {
        Assert.Throws<EvidencePolicyException>(() => ProtectedBaselineRegistry.RequireHistoricalRoles(H0, H0, H0));
    }
    [Theory]
    [InlineData("")]
    [InlineData("not-a-commit")]
    public void Missing_or_malformed_historical_base_refuses(string invalid)
    {
        Assert.Throws<EvidencePolicyException>(() => ProtectedBaselineRegistry.RequireHistoricalRoles(invalid, H0, H0));
    }
    [Fact]
    public void Historical_source_cannot_be_relabelled_as_another_current_base()
    {
        Assert.Throws<EvidencePolicyException>(() => ProtectedBaselineRegistry.RequireHistoricalRoles(P, H0, Other));
    }
}
