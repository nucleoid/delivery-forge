using DeliveryForge.Tool.Claude;

namespace DeliveryForge.Tool.Tests;

// Argv and actual private-file ownership controls only. No CLI, model, auth,
// hostile-settings conformance, capability or review acceptance is executed.
public sealed class ClaudeReviewerIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_readonly_observations_use_safe_mode_and_user_settings_only(bool auth)
    {
        string[] tail = auth ? ["auth", "status"] : ["--version"];
        Assert.Equal(new[] { "--safe-mode", "--setting-sources", "user" }.Concat(tail), ClaudeIndependentReviewData.IsolatedObservationArguments(tail));
    }
    [Fact]
    public void Isolation_does_not_add_authentication_mutation_or_model_overrides()
    {
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.IsolatedObservationArguments(["auth", "login"]));
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.IsolatedObservationArguments(["settings", "set", "model", "other"]));
        var main = ClaudeIndependentReviewData.IsolatedReviewArguments(1.25m);
        Assert.Equal(new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--replay-user-messages", "--safe-mode", "--setting-sources", "user", "--no-session-persistence", "--strict-mcp-config", "--tools", "Read,Grep,Glob", "--allowedTools", "Read,Grep,Glob", "--permission-mode", "dontAsk", "--max-budget-usd", "1.25" }, main);
        Assert.DoesNotContain("--model", main); Assert.DoesNotContain("--settings", main);
    }
    [Fact]
    public void Private_cwd_is_separate_and_marker_drift_refuses_before_any_native_creation()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "forge-reviewer-cwd-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var parent = Path.Combine(root, "parent"); var worker = Path.Combine(root, "worker");
        var result = Path.Combine(root, "result"); var artifacts = Path.Combine(root, "artifacts");
        foreach (var source in new[] { parent, worker, result, artifacts }) Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(worker, "CLAUDE.md"), "Hostile source instruction is outside the neutral directory.");
        var actual = ClaudeReviewerWorkingDirectory.Create(original, "run-cwd-control", artifacts, parent, worker, result);
        foreach (var source in new[] { parent, worker, result, artifacts }) BoundedFiles.Separate(source, actual.Path);
        actual.Check(); Assert.False(File.Exists(Path.Combine(actual.Path, "CLAUDE.md")));
        var marker = Path.Combine(actual.Path, ".forge-reviewer-owner"); Assert.True(File.Exists(marker));
        File.AppendAllText(marker, "changed");
        Assert.Throws<IOException>(actual.Check); Assert.False(original.HasExplicitUnknown);
        Assert.True(File.Exists(marker)); // Preserve namespace/failed evidence; no deletion/repair.
    }
    [Fact]
    public void Source_owned_artifact_ancestor_is_refused_before_private_directory_creation()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "forge-reviewer-cwd-overlap-" + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "worker"); Directory.CreateDirectory(worker);
        var artifacts = Path.Combine(worker, "artifacts");
        Assert.Throws<ToolUsageException>(() => ClaudeReviewerWorkingDirectory.Create(original, "run-cwd-control", artifacts,
            Path.Combine(root, "parent"), worker, Path.Combine(root, "result")));
        Assert.False(Directory.Exists(artifacts)); original.Check();
    }
}
