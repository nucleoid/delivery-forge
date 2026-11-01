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
    [Fact(Timeout = 45000)]
    [Trait("Category", "InstalledLocalRealObservation")]
    public async Task Actual_selected_Linux_Claude_version_observation_uses_exact_production_scope_and_environment()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Actual selected Linux reviewer observation only.");
        const string key = "DELIVERY_FORGE_NATIVE_CONFORMANCE_CLAUDE_COMPLETE";
        var path = Environment.GetEnvironmentVariable(key);
        if (path is null) Assert.Skip("Explicit pinned Linux Claude fixture is absent; no native observation acceptance.");
        var expected = Environment.GetEnvironmentVariable(key + "_SHA256")
            ?? throw new IOException("The selected Linux Claude fixture requires its exact byte pin.");
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var raw = BoundedFiles.Read(path!, original, 65536);
        Assert.True(BoundedFiles.Sha(raw) == expected, "Selected Linux Claude fixture changed.");
        _ = CanonicalJson.Canonicalize(raw);
        var selected = JsonSerializer.Deserialize<InstalledNativeHostConformanceTests.Fixture>(raw, BoundedFiles.Json)
            ?? throw new IOException("Selected Linux Claude fixture is null.");
        if (selected.Kind != "RootAdmittedNativeConformanceProvisioningFixture-v2" || selected.Adapter != "claude-code.cli" ||
            selected.CaseKind != "completion" || selected.Host is null || selected.LinuxScopeProfile is null ||
            selected.Host.Executable != "/claude-official-520bc6c1f40af466bd03a278b5ae1af1a2938bc7de5b9e5cb15526da20d71cfe" ||
            selected.Host.Bytes != 290532720 || selected.Host.Sha256 != "520bc6c1f40af466bd03a278b5ae1af1a2938bc7de5b9e5cb15526da20d71cfe" ||
            selected.Host.Version != "2.1.306 (Claude Code)")
            throw new IOException("Exact selected Linux Claude image and scope required.");
        var scopeBytes = BoundedFiles.Read(selected.LinuxScopeProfile.Path, original, 65536);
        if (scopeBytes.LongLength != selected.LinuxScopeProfile.Bytes || BoundedFiles.Sha(scopeBytes) != selected.LinuxScopeProfile.Sha256)
            throw new IOException("Selected Linux scope profile changed.");
        var scope = JsonSerializer.Deserialize<LinuxScopeSelection>(scopeBytes, BoundedFiles.Json)
            ?? throw new IOException("Selected Linux scope is null.");
        if (scope.RestrictiveChildPolicy != "nnp-user1000-ancestor-v3" || scope.ExactImageBacking != LinuxExactImageBacking.RootControlledDisk)
            throw new IOException("Existing selected confined root-disk scope required; no ordinary fallback.");
        var root = Path.Combine(Path.GetTempPath(), "forge-claude-linux-version-" + Guid.NewGuid().ToString("N"));
        var cwd = ClaudeReviewerWorkingDirectory.Create(original, "version-only-" + Guid.NewGuid().ToString("N"),
            Path.Combine(root, "artifacts"), Path.Combine(root, "parent"), Path.Combine(root, "worker"), Path.Combine(root, "results"));
        // Same production environment; do not repair missing flags only in the
        // control or inherit ambient resource/auth/model configuration.
        var environment = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(Environment.GetEnvironmentVariable);
        var arguments = ClaudeIndependentReviewData.RestrictedObservationArguments(["--version"]);
        var invocation = ClaudeIndependentReviewData.PinnedSettingsInvocation(selected.Host.Executable, arguments, cwd.Path, environment);
        ClaudeIndependentReviewData.RequirePinnedSettingsIsolation(invocation.Arguments, observation: true);
        ClaudeIndependentReviewData.RequirePinnedSettingsInvocation(invocation, environment);
        var image = new AdmissionFile(selected.Host.Executable, selected.Host.Bytes, selected.Host.Sha256);
        BoundedCommandCaptureResult? capture = null; ClaudeReviewerObservationFacts? failureFacts = null;
        var entered = false; var knownClosed = false; var versionMatches = false;
        try
        {
            cwd.Check(); BoundedFiles.VerifyNativeImage(image, original, original.Token);
            BoundedFiles.ReserveExactLinuxImageReads(image, original);
            entered = true;
            capture = await new ProcessCommandExecutor(scope, original).ExecuteBoundedCaptureAsync(invocation,
                original.Remaining, 65536, original.Token, new OwnedWorkPhaseBudget(original), scope,
                new LinuxScopeImage(image.Path, image.Bytes, image.Sha256));
            // Closure is independent of semantic success, quota, or a retained
            // native error. Preserve actual closed-tree evidence before assertions.
            knownClosed = IsKnownClosedLinuxCapture(capture);
            try
            {
                ClaudeIndependentReviewer.RequireSuccessfulObservation(ClaudeReviewerObservationOperation.Version, capture);
                ActualOrdinaryCapture.Require(capture);
                ClaudeIndependentReviewer.RequireObservedVersion(capture, selected.Host.Version); versionMatches = true;
            }
            catch (IOException failure) { failureFacts = ClaudeReviewerObservationFailure.FindSingle(failure); throw; }
            cwd.Check(); ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(environment, Environment.GetEnvironmentVariable);
            original.Check();
        }
        finally
        {
            if (capture is not null)
                TestContext.Current.TestOutputHelper!.WriteLine(JsonSerializer.Serialize(new
                {
                    Operation = "version",
                    capture.Command.ExitCode,
                    capture.Command.TimedOut,
                    capture.Command.Cancelled,
                    capture.RootExitObserved,
                    capture.StandardOutputDrainCompleted,
                    capture.StandardErrorDrainCompleted,
                    capture.CaptureBoundExceeded,
                    capture.OwnershipKind,
                    KnownClosed = knownClosed,
                    VersionMatches = versionMatches,
                    ReviewerObservation = failureFacts,
                    ProductionAuthority = false,
                    AuthOrModelDispatched = false
                }));
            if (entered && !knownClosed) original.RetainUnknown();
        }
        // The fresh neutral namespace and actual owner evidence are preserved.
    }

    internal static bool IsKnownClosedLinuxCapture(BoundedCommandCaptureResult capture) =>
        capture.OwnershipKind == BoundedCaptureOwnershipKind.LinuxCreationOwnedScope &&
        capture.Command.OwnedProcessQuiescent && capture.RootExitObserved &&
        capture.StandardOutputDrainCompleted && capture.StandardErrorDrainCompleted &&
        capture.Command.LifecycleDiagnostics?.OwnedTree is
        { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 };

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
                    ClaudeReviewerUserSettings.RequireObservedConfigurationDirectory(capture.StandardOutputBytes,
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
                    authMatches = true;
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
                    Operation = auth ? "auth-status" : "version",
                    capture.Command.ExitCode,
                    capture.Command.TimedOut,
                    capture.Command.Cancelled,
                    capture.RootExitObserved,
                    capture.StandardOutputDrainCompleted,
                    capture.StandardErrorDrainCompleted,
                    capture.CaptureBoundExceeded,
                    capture.OwnershipKind,
                    KnownClosed = knownClosed,
                    StdoutBytes = capture.StandardOutputBytes.Length,
                    StderrBytes = capture.StandardErrorBytes.Length,
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
