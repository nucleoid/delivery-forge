using System.Text;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence.Tests;

public sealed class EvidenceBehaviorTests
{
    [Fact]
    public void Policy_requires_content_bound_protected_authority()
    {
        var candidate = TestEvidence.PolicyCandidate();
        var resolved = EvidencePolicy.Resolve(candidate, new PolicyAuthority(
            PolicyAuthorityKind.ProtectedGitBase, candidate.SourceRevision, candidate.ContentIdentity));

        Assert.Equal(candidate.PolicyIdentity, resolved.PolicyIdentity);
        Assert.Throws<EvidencePolicyException>(() => EvidencePolicy.Resolve(candidate,
            new PolicyAuthority(PolicyAuthorityKind.WorkerBranch, candidate.SourceRevision, candidate.ContentIdentity)));
        Assert.Throws<EvidencePolicyException>(() => EvidencePolicy.Resolve(candidate,
            new PolicyAuthority(PolicyAuthorityKind.ParentAdmin, candidate.SourceRevision, TestEvidence.Identity('9'))));
    }

    [Fact]
    public void Fixture_policy_cannot_grant_production_authority()
    {
        var candidate = TestEvidence.PolicyCandidate(fixture: true);
        var error = Assert.Throws<EvidencePolicyException>(() => EvidencePolicy.Resolve(candidate,
            new PolicyAuthority(PolicyAuthorityKind.ProtectedGitBase, candidate.SourceRevision, candidate.ContentIdentity)));
        Assert.Contains("fixture", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reviewed_baseline_detects_regression()
    {
        var policy = TestEvidence.ResolvedPolicy();
        var baseline = new ReviewedBaseline(
            TestEvidence.Identity('3'), policy.PolicyIdentity, TestEvidence.Commit('a'),
            TestEvidence.Identity('4'), false, new Dictionary<string, decimal> { ["changed.crap"] = 10m });

        var result = BaselineComparer.Compare(
            new Dictionary<string, decimal> { ["changed.crap"] = 10.01m }, baseline, policy);

        Assert.Equal(GateOutcome.Fail, result.Outcome);
    }

    [Theory]
    [InlineData(true, false, "fixture")]
    [InlineData(false, true, "stale")]
    public void Untrusted_or_stale_baseline_never_passes(bool fixture, bool stale, string expected)
    {
        var policy = TestEvidence.ResolvedPolicy();
        var baseline = new ReviewedBaseline(
            TestEvidence.Identity('3'), policy.PolicyIdentity,
            stale ? TestEvidence.Commit('b') : TestEvidence.Commit('a'),
            TestEvidence.Identity('4'), fixture, new Dictionary<string, decimal> { ["changed.crap"] = 10m });

        var result = BaselineComparer.Compare(
            new Dictionary<string, decimal> { ["changed.crap"] = 10m }, baseline, policy);

        Assert.NotEqual(GateOutcome.Pass, result.Outcome);
        Assert.Contains(expected, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Complete_dotnet_evidence_passes()
    {
        Assert.Equal(GateOutcome.Pass, new DotNetAdapter().Normalize(TestEvidence.DotNetRun()).Outcome);
    }

    [Theory]
    [InlineData(false, 4, 4, true, "compiler")]
    [InlineData(true, 0, 0, true, "zero tests")]
    [InlineData(true, 4, 4, false, "coverage")]
    public void Missing_dotnet_evidence_never_passes(
        bool compiled, int discovered, int executed, bool coverage, string expected)
    {
        var result = new DotNetAdapter().Normalize(
            TestEvidence.DotNetRun(compiled, discovered, executed, coverage));

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains(expected, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Omitted_dotnet_project_is_incomplete_even_with_exit_zero()
    {
        var result = new DotNetAdapter().Normalize(TestEvidence.DotNetRun(
            expected: ["A.Tests", "B.Tests"], observed: ["A.Tests"]));

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("omitted", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "pass", 0, GateOutcome.Incomplete)]
    [InlineData(true, "pass", 1, GateOutcome.Incomplete)]
    [InlineData(true, "fail", 0, GateOutcome.Fail)]
    [InlineData(true, "pass", 0, GateOutcome.Pass)]
    public void Crap4CSharp_public_v1_fixture_is_normalized_honestly(
        bool completed, string decision, int unknownCoverage, GateOutcome expected)
    {
        var result = new Crap4CSharpAdapter().Normalize(
            Encoding.UTF8.GetBytes(TestEvidence.CrapReport(completed, decision, unknownCoverage)),
            new ToolCapability("crap4csharp", "1.2.0", "1.2", true, true));

        Assert.Equal(expected, result.Outcome);
        Assert.True(result.Fixture);
        Assert.False(result.ProductionCapable);
    }

    [Fact]
    public void Crap4CSharp_unknown_format_is_incomplete_and_malformed_is_error()
    {
        var adapter = new Crap4CSharpAdapter();
        var unsupported = adapter.Normalize(
            Encoding.UTF8.GetBytes(TestEvidence.CrapReport(true, "pass", 0, "2.0")),
            new ToolCapability("crap4csharp", "2.0.0", "2.0", true, true));
        var malformed = adapter.Normalize("{"u8.ToArray(),
            new ToolCapability("crap4csharp", "1.2.0", "1.2", true, true));

        Assert.Equal(GateOutcome.Incomplete, unsupported.Outcome);
        Assert.Equal(GateOutcome.Error, malformed.Outcome);
    }

    [Fact]
    public void Mutate4CSharp_preview_is_not_a_usable_gate()
    {
        var result = new Mutate4CSharpAdapter().Normalize(
            Encoding.UTF8.GetBytes("{\"outcome\":\"PASS\",\"reason\":\"ENUMERATION_NOT_IMPLEMENTED\"}"),
            new ToolCapability("mutate4csharp", "0.1.0-preview", "preview", true, false));

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.False(result.ProductionCapable);
        Assert.Contains("certification", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
