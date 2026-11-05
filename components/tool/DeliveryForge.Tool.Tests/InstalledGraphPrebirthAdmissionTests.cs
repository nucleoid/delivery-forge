using DeliveryForge.Evidence;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// The real production admission class is used with local Tasks. No native,
// live coordination, fixture lease or producer capability is fabricated.
public sealed class InstalledGraphPrebirthAdmissionTests
{
    [Theory]
    [InlineData("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST")]
    [InlineData("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256")]
    [InlineData("mutate4csharp_graph_capture_manifest")]
    public async Task Deterministic_graph_override_refuses_before_entering_actual_parent_effect(string key)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var gate = new object(); var revoked = false;
        var admission = new ParentEffectAdmission(gate,
            run => run == "owned" && !revoked && ParentCoordinationHost.OriginalCanAdmitEffects(original), _ => revoked = true);
        var files = new InstalledToolFiles(original, TimeSpan.FromSeconds(20), new InstalledToolHostScope.Credits(), admission, "owned");
        var missing = Path.Combine(Path.GetTempPath(), "must-not-start-" + Guid.NewGuid().ToString("N") + ".exe");
        var executor = new InstalledToolGateProvider.ActualExecutor(files, new Dictionary<string, string>(), missing,
            admission, "owned", installedTool: "mutate4csharp");
        var refusal = await Assert.ThrowsAsync<EvidenceWriteException>(() => executor.ExecuteAsync(
            new(missing, ["--version"], Path.GetTempPath(), new Dictionary<string, string> { [key] = "" }),
            TimeSpan.FromSeconds(10), original.Token));
        Assert.Contains("graph manifest override keys must be absent", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Calls); Assert.Equal(0, admission.EnteredCount);
        Assert.True(admission.AllKnownSettled); Assert.False(revoked); Assert.False(original.HasExplicitUnknown);
        original.Check();
        var actual = Task.FromResult(7);
        Assert.Same(actual, admission.EnterAsync("owned", () => actual));
        Assert.Equal(7, await actual); Assert.True(admission.AllKnownSettled); Assert.False(revoked);
    }
}
