using System.Text.Json;

namespace DeliveryForge.Evidence.Tests;

// Wire identity tests only; these dictionaries are DATA, not actual reports,
// approvals, gate receipts or an authority-verified baseline.
public sealed class CallableProducerIdentityTests
{
    static Dictionary<string, object> Binding() => new()
    {
        ["profile"] = "sha256:" + new string('a', 64),
        ["toolSourceCommit"] = new string('b', 40),
        ["Version"] = "1.0.0+observed",
        ["PackageVersion"] = "1.0.0-local",
        ["package"] = "sha256:" + new string('c', 64),
        ["configuration"] = "sha256:" + new string('d', 64),
        ["Scope"] = "all",
        ["ProjectRelativePath"] = "tests/Subject.Tests.csproj"
    };
    [Fact]
    public void Exact_wire_identity_matches_without_issuing_any_authority()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Binding());
        Assert.Equal(CallableProducerIdentity.FromBinding(bytes), CallableProducerIdentity.FromBinding(bytes));
    }
    [Theory]
    [InlineData("profile", "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")]
    [InlineData("toolSourceCommit", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")]
    [InlineData("Version", "2.0.0+observed")]
    [InlineData("PackageVersion", "2.0.0-local")]
    [InlineData("package", "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")]
    [InlineData("configuration", "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")]
    [InlineData("Scope", "base")]
    [InlineData("ProjectRelativePath", "tests/Other.Tests.csproj")]
    public void Changed_producer_or_configuration_cannot_be_compared_as_equivalent(string field, string replacement)
    {
        var baseline = Binding(); var current = Binding(); current[field] = replacement;
        Assert.NotEqual(CallableProducerIdentity.FromBinding(JsonSerializer.SerializeToUtf8Bytes(baseline)),
            CallableProducerIdentity.FromBinding(JsonSerializer.SerializeToUtf8Bytes(current)));
    }
    [Fact]
    public void Missing_configuration_and_redirected_project_are_refused()
    {
        var input = Binding(); input.Remove("configuration");
        Assert.Throws<EvidencePolicyException>(() => CallableProducerIdentity.FromBinding(JsonSerializer.SerializeToUtf8Bytes(input)));
        input = Binding(); input["ProjectRelativePath"] = "../Other.Tests.csproj";
        Assert.Throws<EvidencePolicyException>(() => CallableProducerIdentity.FromBinding(JsonSerializer.SerializeToUtf8Bytes(input)));
    }
}
