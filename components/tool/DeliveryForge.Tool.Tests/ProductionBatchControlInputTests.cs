using System.Text;
using System.Text.Json;

namespace DeliveryForge.Tool.Tests;

// Actual bounded local input/file and restrictive latch behavior only. Installed
// two-node/Continuum/native pause or host authority is not certified here.
public sealed class ProductionBatchControlInputTests
{
    const string Run = "batch-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static string Identity => "sha256:" + new string('b', 64);
    static string Record(string action, string run = Run) => JsonSerializer.Serialize(new
    { Kind = "ProductionBatchControl-v1", BatchRunId = run, SelectionIdentity = Identity, Action = action });
    static (string Root, string Path) Fresh()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-batch-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return (root, System.IO.Path.Combine(root, "operator.json"));
    }
    [Fact]
    public void Actual_acknowledged_pause_and_stop_are_sticky_and_fence_existing_birth_gate()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var (root, path) = Fresh(); ProductionBatchControlInput.ValidatePath(root, path);
        ProductionBatchControlInput.RequireInitiallyAbsent(path, original);
        var control = new ProductionBatchControl(); var input = new ProductionBatchControlInput(path, Run, Identity, original, control);
        Assert.False(input.ReadBoundary()); File.WriteAllText(path, Record("pause"), new UTF8Encoding(false));
        Assert.True(input.ReadBoundary()); Assert.True(control.Paused); Assert.False(control.Stopped);
        var calls = 0;
        Assert.Throws<ToolUsageException>(() => control.EnterNodeStart(() => ++calls)); Assert.Equal(0, calls);
        File.Delete(path); Assert.False(input.ReadBoundary()); Assert.True(control.Paused);
        File.WriteAllText(path, Record("stop"), new UTF8Encoding(false));
        Assert.True(input.ReadBoundary()); Assert.True(control.Stopped);
        File.WriteAllText(path, Record("pause"), new UTF8Encoding(false));
        Assert.False(input.ReadBoundary()); Assert.True(control.Stopped);
        Assert.Throws<ToolUsageException>(() => control.EnterNodeStart(() => ++calls)); Assert.Equal(0, calls);
        Assert.False(original.HasExplicitUnknown); // Preserve all actual files.
    }
    [Theory]
    [InlineData("foreign-run")]
    [InlineData("foreign-selection")]
    [InlineData("resume")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("overbound")]
    public void Invalid_actual_input_never_sets_a_latch_or_creates_a_dispatch(string invalid)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var (_, path) = Fresh(); var control = new ProductionBatchControl();
        var text = invalid switch
        {
            "foreign-run" => Record("stop", "batch-" + new string('c', 32)),
            "foreign-selection" => Record("stop").Replace(Identity, "sha256:" + new string('d', 64), StringComparison.Ordinal),
            "resume" => Record("resume"),
            "unknown" => Record("stop")[..^1] + ",\"Extra\":true}",
            "duplicate" => Record("stop")[..^1] + ",\"Action\":\"pause\"}",
            _ => new string('x', 1025)
        };
        File.WriteAllText(path, text, new UTF8Encoding(false));
        var input = new ProductionBatchControlInput(path, Run, Identity, original, control);
        Assert.ThrowsAny<Exception>(() => input.ReadBoundary());
        Assert.False(control.Paused); Assert.False(control.Stopped); Assert.Equal(text, File.ReadAllText(path));
    }
    [Theory]
    [InlineData("batch-selection.json")]
    [InlineData("runs")]
    [InlineData("nested/operator.json")]
    public void Reserved_or_indirect_control_namespace_refuses(string relative)
    {
        var (root, _) = Fresh();
        Assert.Throws<ToolUsageException>(() => ProductionBatchControlInput.ValidatePath(root, System.IO.Path.Combine(root, relative)));
    }
    [Fact]
    public void Existing_actual_control_is_not_adopted_by_a_fresh_original()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var (_, path) = Fresh(); File.WriteAllText(path, Record("stop"));
        Assert.Throws<ToolUsageException>(() => ProductionBatchControlInput.RequireInitiallyAbsent(path, original));
        Assert.Equal(Record("stop"), File.ReadAllText(path)); Assert.False(original.HasExplicitUnknown);
    }
}
