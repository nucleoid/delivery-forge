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
        Assert.Equal(new[] { "--safe-mode", "--setting-sources", "user", "--settings", "{\"disableAllHooks\":true,\"permissions\":{\"defaultMode\":\"dontAsk\"}}" }.Concat(tail), ClaudeIndependentReviewData.IsolatedObservationArguments(tail));
    }
    [Fact]
    public void Isolation_does_not_add_authentication_mutation_or_model_overrides()
    {
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.IsolatedObservationArguments(["auth", "login"]));
        Assert.Throws<ToolUsageException>(() => ClaudeIndependentReviewData.IsolatedObservationArguments(["settings", "set", "model", "other"]));
        var main = ClaudeIndependentReviewData.IsolatedReviewArguments(1.25m);
        Assert.Equal(new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--replay-user-messages", "--safe-mode", "--setting-sources", "user", "--settings", "{\"disableAllHooks\":true,\"permissions\":{\"defaultMode\":\"dontAsk\"}}", "--no-session-persistence", "--strict-mcp-config", "--tools", "", "--permission-mode", "dontAsk", "--max-budget-usd", "1.25" }, main);
        Assert.DoesNotContain("--model", main); Assert.DoesNotContain("--fallback-model", main);
        Assert.Single(main.Where(x => x == "--settings"));
        Assert.Equal("{\"disableAllHooks\":true,\"permissions\":{\"defaultMode\":\"dontAsk\"}}", main[Array.IndexOf(main, "--settings") + 1]);
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
        foreach (var source in new[] { parent, worker, result }) BoundedFiles.Separate(source, actual.Path);
        Assert.Equal(Path.GetFullPath(artifacts), Path.GetDirectoryName(actual.Path));
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deterministic_observation_precheck_refusal_does_not_enter_async_parent_effect(bool expired)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "forge-reviewer-preadmission-" + Guid.NewGuid().ToString("N"));
        var artifacts = Path.Combine(root, "artifacts");
        var cwd = ClaudeReviewerWorkingDirectory.Create(original, "run-preadmission", artifacts,
            Path.Combine(root, "parent"), Path.Combine(root, "worker"), Path.Combine(root, "result"));
        if (!expired) File.AppendAllText(Path.Combine(cwd.Path, ".forge-reviewer-owner"), "drift");
        var revoked = false; var calls = 0;
        var parent = new ParentEffectAdmission(new object(), run => run == "owned", _ => revoked = true);
        void Attempt()
        {
            ClaudeIndependentReviewer.PrecheckObservationInputs(parent, "owned", cwd, expired ? TimeSpan.Zero : original.Remaining);
            _ = parent.EnterAsync("owned", () => { calls++; return Task.FromResult(1); });
        }
        if (expired) Assert.Throws<TimeoutException>(Attempt);
        else Assert.Throws<IOException>(Attempt);
        Assert.Equal(0, calls); Assert.Equal(1, parent.EnteredCount);
        // A returned synchronous validation failure revokes new work under the
        // existing policy, but it is known settled rather than async UNKNOWN.
        Assert.True(parent.AllKnownSettled); Assert.True(revoked); Assert.False(original.HasExplicitUnknown);
    }
}
