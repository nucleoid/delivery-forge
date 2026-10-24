using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("plan", "--repo", "/repo", "--intent-file", "/intent")]
    [InlineData("implement", "--plan", "/plan", "--repo", "/repo")]
    [InlineData("pr", "--run-dir", "/run", "--dry-run", null)]
    [InlineData("merge", "--run-dir", "/run", null, null)]
    [InlineData("resume", "--run-dir", "/run", "--ceiling", "implement")]
    [InlineData("capabilities", "--profile", "local", null, null)]
    [InlineData("quality-baseline", "--plan", "/plan", "--config", "/config")]
    public void Exact_modes_are_parsed(string a, string b, string c, string? d, string? e)
    {
        var command = CommandLine.Parse(new[] { a, b, c, d, e }.Where(x => x is not null).Select(x => x!).ToArray()); Assert.NotNull(command.Mode);
    }
    [Theory]
    [InlineData("")]
    [InlineData("deploy")]
    [InlineData("release")]
    [InlineData("execute-replay")]
    [InlineData("plan --repo /repo")]
    [InlineData("plan --repo /repo --issue a/b#1 --intent-file /intent")]
    [InlineData("implement --plan /plan --issue a/b#1")]
    [InlineData("pr --run-dir /run --repo /repo")]
    [InlineData("merge --run-dir /run --admin")]
    [InlineData("resume --run-dir /run --ceiling deploy")]
    [InlineData("capabilities --profile invented")]
    [InlineData("--dry-run")]
    [InlineData("--help --version")]
    [InlineData("implement --plan /one --plan /two")]
    [InlineData("plan --repo=/repo --intent-file /intent")]
    [InlineData("quality-baseline")]
    [InlineData("quality-baseline --plan /plan --run-dir /saved")]
    [InlineData("quality-baseline --plan /plan --ceiling implement")]
    public void Invalid_authority_or_ambiguous_grammar_refuses_before_effect(string input) => Assert.Throws<ToolUsageException>(() => CommandLine.Parse(input.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    [Fact] public void Help_is_generated_from_same_option_table() { var help = CommandLine.Help(); foreach (var row in CommandLine.Definitions) Assert.Contains("--" + row.Name, help); Assert.DoesNotContain("--authorize", help); }
    [Fact] public async Task Capabilities_reports_unavailable_without_config_or_host_probe() { var r = await new ToolApplication().ExecuteAsync(CommandLine.Parse(["capabilities"]), TestContext.Current.CancellationToken); Assert.Equal("INCOMPLETE", r.Outcome); Assert.False(r.RuntimeCertified); }
    [Fact] public async Task No_config_cannot_create_an_implementation_owner() { var r = await new ToolApplication().ExecuteAsync(CommandLine.Parse(["implement", "--plan", "/not-read"]), TestContext.Current.CancellationToken); Assert.Equal("TRUSTED_CONFIG_REQUIRED", r.Code); }
    [Theory]
    [InlineData(ToolMode.Plan, "DRY-RUN")]
    [InlineData(ToolMode.Plan, "unknown")]
    [InlineData(ToolMode.Plan, "ceiling")]
    [InlineData(ToolMode.Resume, "plan-out")]
    public async Task Direct_command_unknown_wrongcase_or_crossmode_key_refuses_before_configuration_read(ToolMode mode, string key)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var config = Path.Combine(Path.GetTempPath(), "forge-options-unread-" + Guid.NewGuid().ToString("N"));
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["config"] = config, [key] = null };
        if (key == "DRY-RUN") Assert.True(new ToolCommand(mode, options).Has("dry-run")); // The original lookup sees the safety flag.
        var application = new ToolApplication(actualOriginal: original);
        var error = await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(new(mode, options), original.Token));
        Assert.Equal("Command options must be a stable exact-key selection for the selected mode.", error.Message);
        Assert.Empty(application.RetainedExternalCalls); Assert.False(File.Exists(config));
        BoundedFiles.RequireReadCapacity(original, 4096, OperatingSystem.IsLinux() ? 2147483648L : 1073741824L); // No configuration/file effect was admitted.
    }
    [Theory]
    [InlineData("throw")]
    [InlineData("count")]
    [InlineData("hidden-dry-run")]
    [InlineData("different-value")]
    [InlineData("duplicate")]
    public async Task Direct_command_inconsistent_or_throwing_dictionary_refuses_before_configuration_read(string behavior)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var options = new InconsistentOptions(behavior); var application = new ToolApplication(actualOriginal: original);
        var error = await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(new(ToolMode.Plan, options), original.Token));
        Assert.Equal("Command options must be a stable exact-key selection for the selected mode.", error.Message);
        Assert.DoesNotContain("secret-sentinel", error.ToString()); Assert.Null(error.InnerException);
        Assert.Empty(application.RetainedExternalCalls);
        BoundedFiles.RequireReadCapacity(original, 4096, OperatingSystem.IsLinux() ? 2147483648L : 1073741824L);
    }
    [Theory]
    [InlineData(ToolMode.Plan)]
    [InlineData(ToolMode.Resume)]
    [InlineData(ToolMode.Implement)]
    public void Canonical_programmatic_options_preserve_existing_mode_flags_and_are_detached(ToolMode mode)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["config"] = "original-config", ["json"] = null, ["dry-run"] = null };
        if (mode == ToolMode.Plan) { options["repo"] = "repo"; options["intent-file"] = "intent"; options["plan-out"] = "plan"; options["replay-out"] = "replay"; }
        else if (mode == ToolMode.Resume) { options["run-dir"] = "run"; options["ceiling"] = "implement"; options["replay-out"] = "replay"; }
        else options["plan"] = "plan"; // The batch owner also supplies exact config/json/plan.
        var selected = CommandLine.SnapshotForExecution(new(mode, options));
        Assert.Equal(options.Count, selected.Options.Count); Assert.True(selected.Has("dry-run")); Assert.True(selected.Has("json"));
        foreach (var row in options) Assert.Equal(row.Value, selected.Get(row.Key));
        options["config"] = "changed-config"; options.Remove("dry-run");
        Assert.Equal("original-config", selected.Get("config")); Assert.True(selected.Has("dry-run")); Assert.NotSame(options, selected.Options);
    }
    [Fact]
    public void Original_file_read_credit_has_platform_boundary_and_preserves_prior_complete_debits()
    {
        var maximum = OperatingSystem.IsLinux() ? 2147483648L : 1073741824L;
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var other = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        BoundedFiles.RequireReadCapacity(original, 4096, maximum);
        Assert.Throws<ToolUsageException>(() => BoundedFiles.RequireReadCapacity(original, 0, maximum + 1));
        var directory = Path.Combine(Path.GetTempPath(), "forge-read-credit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "one-byte"); File.WriteAllBytes(path, [42]);
        Assert.Equal(new byte[] { 42 }, BoundedFiles.Read(path, original, 1));
        // Actual Read debits its open and complete byte+EOF: no capacity check
        // refunds either operation or byte, and another original is independent.
        BoundedFiles.RequireReadCapacity(original, 4094, maximum - 2);
        Assert.Throws<ToolUsageException>(() => BoundedFiles.RequireReadCapacity(original, 0, maximum - 1));
        Assert.Throws<ToolUsageException>(() => BoundedFiles.RequireReadCapacity(original, 4095, 0));
        BoundedFiles.RequireReadCapacity(other, 4096, maximum);
        BoundedFiles.RequireReadCapacity(original, 4094, maximum - 2);
        TestContext.Current.TestOutputHelper!.WriteLine("Retained bounded read-credit fixture: " + directory);
    }
    sealed class InconsistentOptions(string behavior) : IReadOnlyDictionary<string, string?>
    {
        readonly Dictionary<string, string?> values = new(StringComparer.Ordinal) { ["config"] = "secret-sentinel-not-read" };
        public int Count => values.Count + (behavior == "count" ? 1 : 0);
        public IEnumerable<string> Keys => values.Keys;
        public IEnumerable<string?> Values => values.Values;
        public string? this[string key] => values[key];
        public bool ContainsKey(string key) => behavior == "hidden-dry-run" && key == "dry-run" || values.ContainsKey(key);
        public bool TryGetValue(string key, out string? value)
        {
            if (behavior == "hidden-dry-run" && key == "dry-run") { value = null; return true; }
            var present = values.TryGetValue(key, out value);
            if (present && behavior == "different-value") value = "secret-sentinel-different";
            return present;
        }
        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
        {
            if (behavior == "throw") throw new InvalidOperationException("secret-sentinel-copy-failure");
            foreach (var row in values) yield return row;
            if (behavior == "duplicate") yield return values.First();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
