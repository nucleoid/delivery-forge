using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Evidence;
using DeliveryForge.Tool.Claude;

namespace DeliveryForge.Tool.Tests;

// Opt-in ordinary captures only: no model prompt, task, lease, parent/frame,
// runtime-trust binding, review receipt or capability is created by this test.
public sealed class ClaudeReviewerObservationNativeTests
{
    [Theory(Timeout = 45000)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "InstalledLocalRealObservation")]
    public async Task Actual_selected_Claude_readonly_observation_settles_with_production_argv_and_environment(bool auth)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows native reviewer observation only.");
        const string key = "DELIVERY_FORGE_NATIVE_CONFORMANCE_CLAUDE_COMPLETE";
        var path = Environment.GetEnvironmentVariable(key);
        if (path is null) Assert.Skip("Explicit pinned local Claude fixture is absent; no native observation acceptance.");
        var expected = Environment.GetEnvironmentVariable(key + "_SHA256")
            ?? throw new IOException("The selected local Claude fixture requires its exact byte pin.");
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var started = original.OriginalStarted;
        Assert.Equal(TimeSpan.FromSeconds(22.5), original.WorkLimit);
        var raw = BoundedFiles.Read(path!, original, 65536);
        Assert.True(BoundedFiles.Sha(raw) == expected, "Selected local Claude fixture changed.");
        _ = CanonicalJson.Canonicalize(raw);
        var selected = JsonSerializer.Deserialize<InstalledNativeHostConformanceTests.Fixture>(raw, BoundedFiles.Json)
            ?? throw new IOException("Selected local Claude fixture is null.");
        if (selected.Kind != "RootAdmittedNativeConformanceProvisioningFixture-v2" || selected.Adapter != "claude-code.cli" ||
            selected.CaseKind != "completion" || selected.Host is null || selected.LinuxScopeProfile is not null ||
            selected.ObserveRuntimeTrustRoute || selected.LinuxOwnerTrustedHelper is not null ||
            !string.Equals(Path.GetFileName(selected.Host.Executable), "claude.exe", StringComparison.OrdinalIgnoreCase) ||
            selected.Host.Version != "2.1.306 (Claude Code)")
            throw new IOException("Exact selected Windows Claude observation image required.");
        // Only the Host tuple is selected from this existing pinned DATA shape.
        // No fixture worktree, task, plan or expected-result field is consumed.
        var root = Path.Combine(Path.GetTempPath(), "forge-claude-observe-" + Guid.NewGuid().ToString("N"));
        var cwd = ClaudeReviewerWorkingDirectory.Create(original, "readonly-observation-" + Guid.NewGuid().ToString("N"),
            Path.Combine(root, "artifacts"), Path.Combine(root, "parent"), Path.Combine(root, "worker"), Path.Combine(root, "results"));
        var environment = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(Environment.GetEnvironmentVariable);
        var arguments = ClaudeIndependentReviewData.RestrictedObservationArguments(auth ? ["auth", "status"] : ["--version"]);
        var invocation = ClaudeIndependentReviewData.PinnedSettingsInvocation(selected.Host.Executable, arguments, cwd.Path, environment);
        ClaudeIndependentReviewData.RequirePinnedSettingsIsolation(invocation.Arguments, observation: true);
        ClaudeIndependentReviewData.RequirePinnedSettingsInvocation(invocation, environment);
        ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(environment, Environment.GetEnvironmentVariable);
        BoundedFiles.RetainedNativeImage? image = null;
        BoundedCommandCaptureResult? capture = null;
        var entered = false; var knownClosed = false; bool? authMatches = null;
        try
        {
            image = BoundedFiles.RetainVerifiedNativeImage(new(selected.Host.Executable, selected.Host.Bytes, selected.Host.Sha256), original, original.Token);
            image.Check(original.Token); cwd.Check();
            entered = true;
            capture = await ActualOrdinaryCapture.Select(original).ExecuteBoundedCaptureAsync(invocation,
                original.Remaining, 65536, original.Token, new OwnedWorkPhaseBudget(original));
            ActualOrdinaryCapture.Require(capture); knownClosed = true;
            Assert.Equal(0, capture.Command.ExitCode);
            Assert.False(capture.Command.TimedOut); Assert.False(capture.Command.Cancelled);
            Assert.True(capture.StandardErrorBytes.Length == 0, "Actual observation emitted stderr; raw bytes remain private.");
            if (auth)
            {
                authMatches = false;
                try
                {
                    ClaudeIndependentReviewData.RequireSubscription(capture.StandardOutputBytes);
                    using var document = JsonDocument.Parse(capture.StandardOutputBytes);
                    var value = document.RootElement;
                    authMatches = value.TryGetProperty("configDirectory", out var directory) && directory.ValueKind == JsonValueKind.String &&
                        directory.GetString() is { } observed && Path.IsPathFullyQualified(observed) &&
                        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(observed)),
                            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"))),
                            StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    // Parsing/account values are never exposed by assertion diagnostics.
                }
                Assert.True(authMatches == true, "Actual subscription/configuration predicates failed; raw account output remains private.");
            }
            else
                Assert.True(capture.StandardOutputBytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(selected.Host.Version + "\n")),
                    "Actual complete reviewer version bytes differ from the selected version.");
            cwd.Check(); image.Check(original.Token);
            ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(environment, Environment.GetEnvironmentVariable);
            Assert.Equal(started, original.OriginalStarted); original.Check();
        }
        finally
        {
            // Never publish Command/exception Data/raw auth/account/environment.
            if (capture is not null)
                TestContext.Current.TestOutputHelper!.WriteLine(JsonSerializer.Serialize(new
                {
                    Operation = auth ? "auth-status" : "version", capture.Command.ExitCode, capture.Command.TimedOut, capture.Command.Cancelled,
                    capture.RootExitObserved, capture.StandardOutputDrainCompleted, capture.StandardErrorDrainCompleted,
                    capture.CaptureBoundExceeded, capture.OwnershipKind, KnownClosed = knownClosed,
                    StdoutBytes = capture.StandardOutputBytes.Length, StderrBytes = capture.StandardErrorBytes.Length,
                    AuthSubscriptionAndConfigDirectoryMatch = authMatches,
                    StdoutSha256 = Convert.ToHexStringLower(SHA256.HashData(capture.StandardOutputBytes)),
                    StderrSha256 = Convert.ToHexStringLower(SHA256.HashData(capture.StandardErrorBytes)),
                    ProductionAuthority = false
                }));
            if (entered && !knownClosed) { original.RetainUnknown(); image?.RetainUnknown(); }
            else image?.Close(cleanup: true);
        }
        // Fresh neutral namespace is retained. No cleanup/delete, retry or adoption.
    }
}
