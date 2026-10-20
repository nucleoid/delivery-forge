using System.Collections;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution.Git;
using DeliveryForge.Tool.Claude;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Packaging;

namespace DeliveryForge.Tool.Tests;

// Explicitly selected actual acceptance only. Root prepares and admits the real
// installed prefix, host conformance, owner-approved baseline and native tools.
public sealed class InstalledLocalRealGoldenTests
{
    public sealed record Fixture(string Kind, AdmissionFile Package, InstallationReceipt Installation,
        AdmissionFile Configuration, AdmissionFile Plan, AdmissionFile? LinuxScopeProfile, string RunDirectory, string ResultDirectory,
        string? ExpectedAuthorModel = null, string? ExpectedReviewerModel = null);
    [Fact(Timeout = 1_300_000)]
    [Trait("Category", "InstalledLocalRealAcceptance")]
    public async Task Actual_packaged_local_real_chain_uses_native_worker_independent_reviewer_protected_tools_and_local_fake_publication()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity(); using var original = new ToolLifetime(TimeSpan.FromSeconds(1200), TestContext.Current.CancellationToken);
        var path = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LOCAL_REAL_FIXTURE") ?? throw new IOException("Explicit real installed local-real fixture required; no skip/fake capability.");
        var sha = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LOCAL_REAL_FIXTURE_SHA256") ?? throw new IOException("Independent exact fixture pin required.");
        var raw = BoundedFiles.Read(path, original, 65536); Assert.Equal(sha, BoundedFiles.Sha(raw));
        var fixture = JsonSerializer.Deserialize<Fixture>(raw, BoundedFiles.Json) ?? throw new IOException("Null local-real fixture.");
        Assert.Equal("RootPreparedInstalledLocalRealFixture-v1", fixture.Kind);
        _ = InstalledHostAdmission.ReadExact(fixture.Package, original, 64 * 1024 * 1024);
        var planBytes = InstalledHostAdmission.ReadExact(fixture.Plan, original, 524288);
        _ = InstalledHostAdmission.ReadExact(fixture.Configuration, original, 65536);
        Assert.Equal(fixture.Package.Sha256, fixture.Installation.PackageSha256);
        var manifest = PackageLifecycle.Verify(fixture.Installation, original);
        var entry = Path.Combine(fixture.Installation.Prefix, "payload", "DeliveryForge.Tool.dll");
        Assert.Contains(manifest.Files, row => row.Path == "payload/DeliveryForge.Tool.dll");
        var config = TrustedConfiguration.Load(fixture.Configuration.Path, original);
        Assert.NotNull(config.LocalRealGolden); Assert.NotNull(config.InstalledQuality?.Registry); Assert.NotNull(config.InstalledReview?.IndependentReviewer);
        // Expectations are selected in the independently pinned fixture, not
        // learned from the receipt or used as model/provider overrides.
        var authorModel = fixture.ExpectedAuthorModel ?? throw new IOException("Root-pinned expected actual author model required.");
        var reviewerModel = fixture.ExpectedReviewerModel ?? throw new IOException("Root-pinned expected configured reviewer model required.");
        Assert.StartsWith("gpt-", authorModel); Assert.StartsWith("claude-", reviewerModel); Assert.NotEqual(authorModel, reviewerModel);
        Assert.Equal("codex.cli", config.Adapter);
        using var selectedPlan = JsonDocument.Parse(planBytes);
        var selectedContract = selectedPlan.RootElement.GetProperty("contractIdentity").GetString();
        var selectedRichPlan = selectedPlan.RootElement.GetProperty("identity").GetString();
        Assert.Equal(config.ExpectedContractIdentity, selectedContract); Assert.Equal(config.ExpectedBundleIdentity, selectedRichPlan);
        Assert.Equal(config.Repository, selectedPlan.RootElement.GetProperty("plan").GetProperty("repository").GetString());
        var registrySelection = config.InstalledQuality.Registry!; registrySelection.Validate();
        var approvalPin = new AdmissionFile(registrySelection.OwnerApproval.Path, registrySelection.OwnerApproval.Bytes, registrySelection.OwnerApproval.Sha256);
        _ = InstalledHostAdmission.ReadExact(approvalPin, original, 65536);
        BoundedFiles.Separate(fixture.Installation.Prefix, config.LocalRealGolden.ArtifactRoot);
        Assert.True(config.OperationSeconds <= Math.Floor(original.Remaining.TotalSeconds) - 60, "Installed child's complete declared interval must fit inside this already-running original; no refreshed outer budget.");
        LinuxScopeSelection? scope = null;
        if (OperatingSystem.IsLinux())
        {
            var pin = fixture.LinuxScopeProfile ?? throw new IOException("Actual current Linux creation-owned profile required.");
            scope = JsonSerializer.Deserialize<LinuxScopeSelection>(InstalledHostAdmission.ReadExact(pin, original, 65536), BoundedFiles.Json) ?? throw new IOException("Null Linux profile.");
        }
        var executor = OperatingSystem.IsLinux() ? new ProcessCommandExecutor(scope!, original) : new ProcessCommandExecutor();
        var actual = await executor.ExecuteBoundedCaptureAsync(new(manifest.RuntimeHost,
            [entry, "implement", "--config", fixture.Configuration.Path, "--plan", fixture.Plan.Path,
                "--run-dir", fixture.RunDirectory, "--result-dir", fixture.ResultDirectory, "--json"],
            config.RepositoryRoot, ActualOrdinaryCapture.ResourceEnvironment()), original.Remaining, 1048576,
            original.Token, new OwnedWorkPhaseBudget(original));
        ActualOrdinaryCapture.Require(actual); Assert.Equal(0, actual.Command.ExitCode); Assert.False(actual.Command.TimedOut); Assert.False(actual.Command.Cancelled);
        using var returned = JsonDocument.Parse(actual.StandardOutputBytes);
        Assert.Equal("ACTUAL_LOCAL_REAL_CHAIN_PASSED", returned.RootElement.GetProperty("Code").GetString());
        var receipt = returned.RootElement.GetProperty("Data").GetProperty("Receipt").GetString()!;
        var runDirectory = returned.RootElement.GetProperty("Data").GetProperty("RunDirectory").GetString()!;
        var outputRoot = Path.GetFullPath(config.LocalRealGolden.ArtifactRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(outputRoot, Path.GetFullPath(receipt));
        Assert.Equal(Path.GetDirectoryName(receipt), runDirectory);
        var actualSelection = BoundedFiles.ReadJson<RunSelection>(Path.Combine(runDirectory, "selection.json"), original, 65536);
        Assert.Equal(config.Repository, actualSelection.Repository); Assert.Equal(Path.GetFullPath(config.RepositoryRoot), Path.GetFullPath(actualSelection.RepositoryRoot));
        using var evidence = JsonDocument.Parse(BoundedFiles.Read(receipt, original, 1048576)); var row = evidence.RootElement;
        Assert.Equal("ActualInstalledLocalRealChainData-v1", row.GetProperty("Kind").GetString()); Assert.Equal("PASS", row.GetProperty("Outcome").GetString());
        Assert.Equal(actualSelection.RunId, row.GetProperty("RunId").GetString());
        Assert.Equal(selectedContract, actualSelection.PlanIdentity);
        Assert.Equal(selectedContract, row.GetProperty("PlanIdentity").GetString());
        Assert.Equal(selectedRichPlan, row.GetProperty("RichPlanIdentity").GetString());
        Assert.Equal(selectedPlan.RootElement.GetProperty("plan").GetProperty("baseCommit").GetString(), row.GetProperty("BaseCommit").GetString());
        Assert.True(row.GetProperty("ActualWorker").GetBoolean()); Assert.True(row.GetProperty("ActualQualityWithOwnerBaseline").GetBoolean());
        Assert.True(row.GetProperty("ActualOppositeFamilyReview").GetBoolean()); Assert.True(row.GetProperty("FakePublication").GetBoolean());
        Assert.Equal(0, row.GetProperty("RemoteWrites").GetInt32()); Assert.Equal(0, row.GetProperty("MergeCalls").GetInt32());
        Assert.False(row.GetProperty("ProductionPublicationAuthority").GetBoolean()); Assert.False(row.GetProperty("RecoveryCertified").GetBoolean());
        Assert.False(row.GetProperty("SchedulingCertified").GetBoolean());
        Assert.Equal(2, row.GetProperty("GateReceipts").GetArrayLength());
        var currentReceiptPaths = new List<string>();
        foreach (var gate in config.InstalledQuality!.Gates)
        {
            var gateRoot = Path.Combine(config.InstalledQuality.ArtifactRoot, actualSelection.RunId);
            using var settled = JsonDocument.Parse(BoundedFiles.Read(Path.Combine(gateRoot, gate.GateId + "-settled.json"), original, 65536));
            var current = settled.RootElement;
            Assert.Equal("Pass", current.GetProperty("Outcome").GetString()); Assert.False(current.GetProperty("Fixture").GetBoolean());
            // Genuine installed producer evidence remains distinct from production authority.
            Assert.False(current.GetProperty("ProductionCapable").GetBoolean());
            var pin = current.GetProperty("Receipt").Deserialize<AdmissionFile>(BoundedFiles.Json)!;
            currentReceiptPaths.Add(pin.Path);
            var gateReceipt = DeliveryForge.Contracts.Validation.ContractValidator.ParseAndValidate(InstalledHostAdmission.ReadExact(pin, original, 1048576));
            Assert.Contains(row.GetProperty("GateReceipts").EnumerateArray(), value => value.GetString() == gateReceipt.Identity);
            using var gateData = JsonDocument.Parse(gateReceipt.CanonicalBytes);
            Assert.Equal("PASS", gateData.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(config.InstalledQuality.PolicyIdentity, gateData.RootElement.GetProperty("policyIdentity").GetString());
            Assert.Equal(row.GetProperty("BaseCommit").GetString(), gateData.RootElement.GetProperty("baseCommit").GetString());
            Assert.Equal(row.GetProperty("HeadCommit").GetString(), gateData.RootElement.GetProperty("headCommit").GetString());
            Assert.Equal(row.GetProperty("HeadTree").GetString(), gateData.RootElement.GetProperty("treeId").GetString());
        }
        // Inspect the primary protocol/model artifacts and validated receipt;
        // the three summary booleans above are not independent acceptance.
        var standalone = JsonSerializer.Deserialize<StandaloneRootSelection>(InstalledHostAdmission.ReadExact(
            config.StandaloneRoot ?? throw new IOException("Pinned standalone source selection required."), original, 65536), BoundedFiles.Json)!;
        Assert.Equal(Path.GetFullPath(fixture.ResultDirectory), Path.GetFullPath(standalone.WorkerPaths.ResultDirectory));
        using var authorTrace = JsonDocument.Parse(BoundedFiles.Read(Path.Combine(fixture.ResultDirectory, "codex-app-server-events.json"), original, 8 * 1024 * 1024));
        var author = authorTrace.RootElement; Assert.Equal("ActualCodexAppServerTrace-v1", author.GetProperty("Kind").GetString());
        Assert.Equal(actualSelection.RunId, author.GetProperty("RunId").GetString());
        var authorStart = Assert.Single(author.GetProperty("Events").EnumerateArray(), e =>
            e.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("thread", out _) && result.TryGetProperty("model", out _));
        Assert.Equal(authorModel, authorStart.GetProperty("result").GetProperty("model").GetString());
        Assert.Equal("openai", authorStart.GetProperty("result").GetProperty("modelProvider").GetString());
        Assert.Equal(author.GetProperty("ThreadId").GetString(), authorStart.GetProperty("result").GetProperty("thread").GetProperty("id").GetString());
        var workerGit = new WorktreeManager(config.GitExecutable, executor, original);
        var committed = await workerGit.ObserveAsync(standalone.WorkerPaths.Worktree, original.Token);
        Assert.True(committed.IsClean); Assert.Equal(committed.HeadCommit, row.GetProperty("HeadCommit").GetString());
        Assert.Equal(committed.TreeId, row.GetProperty("HeadTree").GetString());
        var reviewRoot = Path.Combine(config.InstalledReview!.ArtifactRoot, actualSelection.RunId);
        var nativeBytes = BoundedFiles.Read(Path.Combine(reviewRoot, "independent-review-native.json"), original, 8 * 1024 * 1024);
        using var nativeReview = JsonDocument.Parse(nativeBytes); var native = nativeReview.RootElement;
        Assert.Equal("ActualIndependentClaudeReview-v1", native.GetProperty("Kind").GetString());
        Assert.Equal(reviewerModel, native.GetProperty("Model").GetString());
        var events = native.GetProperty("Events").EnumerateArray().ToArray();
        var init = Assert.Single(events, e => e.GetProperty("type").GetString() == "system" && e.GetProperty("subtype").GetString() == "init");
        Assert.Equal(reviewerModel, init.GetProperty("model").GetString());
        Assert.Equal(native.GetProperty("SessionId").GetString(), init.GetProperty("session_id").GetString());
        var initialized = Assert.Single(events, e => e.GetProperty("type").GetString() == "control_response");
        Assert.Equal("forge-review-initialize", initialized.GetProperty("response").GetProperty("request_id").GetString());
        Assert.Equal("success", initialized.GetProperty("response").GetProperty("subtype").GetString());
        var prompt = new UTF8Encoding(false, true).GetString(BoundedFiles.Read(Path.Combine(reviewRoot, "independent-review-prompt.txt"), original, 65536));
        var correspondence = new ClaudeStreamState(native.GetProperty("promptId").GetString()!, prompt); correspondence.AdmitPrompt();
        foreach (var message in events)
        {
            if (message.GetProperty("type").GetString() == "control_response") continue; // Existing initialization reply is handled outside the stream-state parser.
            correspondence.Observe(message);
        }
        correspondence.RequireSuccess();
        foreach (var retirement in new[] { author.GetProperty("Retirement"), native.GetProperty("Retirement") })
        {
            Assert.True(retirement.GetProperty("KnownClosed").GetBoolean()); Assert.True(retirement.GetProperty("RootExited").GetBoolean());
            Assert.Equal(0, retirement.GetProperty("ActiveProcesses").GetInt32()); Assert.Equal(0, retirement.GetProperty("ExitCode").GetInt32());
            Assert.True(retirement.GetProperty("OutputEof").GetBoolean()); Assert.True(retirement.GetProperty("ErrorEof").GetBoolean());
            Assert.True(retirement.GetProperty("InputCloseReturned").GetBoolean()); Assert.True(retirement.GetProperty("DispositionReturned").GetBoolean());
            Assert.Empty(retirement.GetProperty("Failures").EnumerateArray());
        }
        var reviewReceipt = ContractValidator.ParseAndValidate(BoundedFiles.Read(Path.Combine(reviewRoot, "independent-review-receipt.json"), original, 1048576));
        Assert.Equal(row.GetProperty("IndependentReview").GetString(), reviewReceipt.Identity);
        using var reviewed = JsonDocument.Parse(reviewReceipt.CanonicalBytes); var review = reviewed.RootElement;
        Assert.Equal("review-receipt", review.GetProperty("kind").GetString()); Assert.Equal("PASS", review.GetProperty("outcome").GetString());
        Assert.Equal("claude", review.GetProperty("reviewerFamily").GetString()); Assert.Equal(0, review.GetProperty("findingCount").GetInt32());
        Assert.Equal("sha256:" + BoundedFiles.Sha(nativeBytes), review.GetProperty("artifactHash").GetString());
        Assert.Equal(row.GetProperty("BaseCommit").GetString(), review.GetProperty("baseCommit").GetString());
        Assert.Equal(committed.HeadCommit, review.GetProperty("headCommit").GetString()); Assert.Equal(committed.TreeId, review.GetProperty("treeId").GetString());
        Assert.Equal(row.GetProperty("PatchSha256").GetString(), review.GetProperty("patchSha256").GetString());
        using var preparation = JsonDocument.Parse(BoundedFiles.Read(Path.Combine(reviewRoot, "review-source-preparation.json"), original, 1048576));
        Assert.Equal(selectedContract, preparation.RootElement.GetProperty("PlanIdentity").GetString());
        Assert.Equal(committed.HeadCommit, preparation.RootElement.GetProperty("HeadCommit").GetString());
        Assert.Equal(committed.TreeId, preparation.RootElement.GetProperty("TreeId").GetString());

        // Read the independently selected protected commit through the actual
        // bounded Git owner; compare selected identity/approval, not a PASS flag.
        var gitEnvironment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Where(e => !((string)e.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var pair in ActualOrdinaryCapture.ResourceEnvironment()) gitEnvironment[pair.Key] = pair.Value;
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        gitEnvironment["GIT_CONFIG_NOSYSTEM"] = "1"; gitEnvironment["GIT_CONFIG_GLOBAL"] = nullDevice; gitEnvironment["GIT_CONFIG_SYSTEM"] = nullDevice;
        gitEnvironment["GIT_NO_REPLACE_OBJECTS"] = "1"; gitEnvironment["GIT_NO_LAZY_FETCH"] = "1"; gitEnvironment["GIT_TERMINAL_PROMPT"] = "0";
        async Task<byte[]> GitData(params string[] arguments)
        {
            var left = original.Remaining; if (left > TimeSpan.FromSeconds(30)) left = TimeSpan.FromSeconds(30);
            var capture = await executor.ExecuteBoundedCaptureAsync(new(config.GitExecutable,
                ["--no-replace-objects", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "--no-optional-locks", .. arguments],
                config.RepositoryRoot, gitEnvironment) { EnvironmentIsComplete = true }, left, 1048576, original.Token, new OwnedWorkPhaseBudget(original));
            ActualOrdinaryCapture.Require(capture); Assert.Equal(0, capture.Command.ExitCode); return capture.StandardOutputBytes;
        }
        Assert.Equal(registrySelection.RegistryCommit, Encoding.UTF8.GetString(await GitData("rev-parse", "--verify", registrySelection.RegistryCommit + "^{commit}")).Trim());
        Assert.Equal(registrySelection.ProtectedTip, Encoding.UTF8.GetString(await GitData("rev-parse", "--verify", registrySelection.ProtectedReference + "^{commit}")).Trim());
        _ = await GitData("merge-base", "--is-ancestor", registrySelection.RegistryCommit, registrySelection.ProtectedTip);
        var registryBytes = await GitData("show", "--no-ext-diff", "--no-textconv", registrySelection.RegistryCommit + ":" + registrySelection.RegistryRelativePath);
        Assert.Equal(registrySelection.RegistryIdentity, "sha256:" + BoundedFiles.Sha(CanonicalJson.Canonicalize(registryBytes)));
        using var registry = JsonDocument.Parse(registryBytes); var declared = registry.RootElement;
        Assert.Equal("forge-protected-baseline-registry-v2", declared.GetProperty("schema").GetString());
        Assert.Equal(config.InstalledQuality.PolicyIdentity, declared.GetProperty("policyIdentity").GetString()); Assert.Empty(declared.GetProperty("exemptions").EnumerateArray());
        var approvalBytes = InstalledHostAdmission.ReadExact(approvalPin, original, 65536);
        ProtectedBaselineRegistrySelection.ValidateApprovalTime(approvalBytes);
        using var approval = JsonDocument.Parse(approvalBytes); var approved = approval.RootElement;
        Assert.Equal("forge-owner-quality-approval-v2", approved.GetProperty("schema").GetString()); Assert.Equal("mstat", approved.GetProperty("approver").GetString());
        Assert.Equal("historical-baseline-and-empty-exemptions-v2", approved.GetProperty("purpose").GetString());
        Assert.Equal(registrySelection.RegistryCommit, approved.GetProperty("registryCommit").GetString());
        Assert.Equal(registrySelection.RegistryRelativePath, approved.GetProperty("registryPath").GetString());
        Assert.Equal(registrySelection.RegistryIdentity, approved.GetProperty("registryIdentity").GetString());
        foreach (var field in new[] { "historicalPolicyBaseRevision", "historicalPolicyBaseTree", "historicalPolicyRelativePath", "baselineSourceRevision", "baselineSourceTree", "policyIdentity", "policyContentIdentity", "artifactManifestIdentity" })
            Assert.Equal(declared.GetProperty(field).GetString(), approved.GetProperty(field).GetString());
        Assert.Equal("empty-v1", approved.GetProperty("exemptionRegistry").GetString());
        var qualityRoot = Path.Combine(config.InstalledQuality.ArtifactRoot, actualSelection.RunId);
        using var compared = JsonDocument.Parse(BoundedFiles.Read(Path.Combine(qualityRoot, "protected-baseline-comparison.json"), original, 1048576));
        var comparisonRoot = compared.RootElement; var comparison = comparisonRoot.GetProperty("Comparison");
        Assert.Equal("ParentProtectedBaselineComparisonData-v1", comparisonRoot.GetProperty("Kind").GetString());
        Assert.Equal(actualSelection.RunId, comparisonRoot.GetProperty("RunId").GetString()); Assert.Equal(selectedContract, comparisonRoot.GetProperty("PlanIdentity").GetString());
        Assert.Equal(row.GetProperty("BaseCommit").GetString(), comparisonRoot.GetProperty("BaseCommit").GetString());
        Assert.Equal(committed.HeadCommit, comparisonRoot.GetProperty("HeadCommit").GetString()); Assert.Equal(committed.TreeId, comparisonRoot.GetProperty("TreeId").GetString());
        Assert.Equal(currentReceiptPaths.Order(StringComparer.Ordinal), comparisonRoot.GetProperty("CurrentReceipts").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal));
        // This artifact uses default JSON options, so its enum is numeric.
        Assert.Equal((int)GateOutcome.Pass, comparison.GetProperty("Outcome").GetInt32());
        Assert.Equal(registrySelection.RegistryCommit, comparison.GetProperty("RegistryCommit").GetString());
        Assert.Equal(registrySelection.RegistryIdentity, comparison.GetProperty("RegistryIdentity").GetString());
        Assert.Equal(declared.GetProperty("baselineSourceRevision").GetString(), comparison.GetProperty("BaselineSourceRevision").GetString());
        Assert.Equal(declared.GetProperty("artifactManifestIdentity").GetString(), comparison.GetProperty("ArtifactManifestIdentity").GetString());
        _ = PackageLifecycle.Verify(fixture.Installation, original); // Retain prefix and all real artifacts; lifecycle removal is separate.
    }

    [Fact(Timeout = 150_000)]
    [Trait("Category", "InstalledLocalRealAcceptance")]
    public async Task Actual_fresh_installed_process_refuses_to_adopt_saved_native_owner_and_leaves_history_unchanged()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity(); using var original = new ToolLifetime(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        var path = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LOCAL_REAL_RECOVERY_FIXTURE") ?? throw new IOException("Root must select a real prior run only after its original native scope has been independently observed settled.");
        var sha = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LOCAL_REAL_RECOVERY_FIXTURE_SHA256") ?? throw new IOException("Exact independent recovery fixture pin required.");
        var raw = BoundedFiles.Read(path, original, 65536); Assert.Equal(sha, BoundedFiles.Sha(raw));
        var fixture = JsonSerializer.Deserialize<Fixture>(raw, BoundedFiles.Json) ?? throw new IOException("Null recovery fixture.");
        Assert.Equal("RootPreparedInstalledLocalRealFixture-v1", fixture.Kind);
        _ = InstalledHostAdmission.ReadExact(fixture.Package, original, 64 * 1024 * 1024); Assert.Equal(fixture.Package.Sha256, fixture.Installation.PackageSha256);
        _ = InstalledHostAdmission.ReadExact(fixture.Configuration, original, 65536);
        var manifest = PackageLifecycle.Verify(fixture.Installation, original); var config = TrustedConfiguration.Load(fixture.Configuration.Path, original);
        var selected = BoundedFiles.ReadJson<RunSelection>(Path.Combine(fixture.RunDirectory, "selection.json"), original, 65536);
        var store = new RunStore(selected.StoreRoot); var before = await store.RecoverAsync(selected.RunId, original.Token); Assert.NotEmpty(before.Records);
        LinuxScopeSelection? scope = null;
        if (OperatingSystem.IsLinux())
        {
            var pin = fixture.LinuxScopeProfile ?? throw new IOException("Actual creation-owned Linux profile required.");
            scope = JsonSerializer.Deserialize<LinuxScopeSelection>(InstalledHostAdmission.ReadExact(pin, original, 65536), BoundedFiles.Json) ?? throw new IOException("Null Linux profile.");
        }
        var executor = OperatingSystem.IsLinux() ? new ProcessCommandExecutor(scope!, original) : new ProcessCommandExecutor();
        var actual = await executor.ExecuteBoundedCaptureAsync(new(manifest.RuntimeHost,
            [Path.Combine(fixture.Installation.Prefix, "payload", "DeliveryForge.Tool.dll"), "resume", "--run-dir", fixture.RunDirectory, "--config", fixture.Configuration.Path, "--json"],
            config.RepositoryRoot, ActualOrdinaryCapture.ResourceEnvironment()), original.Remaining, 1048576, original.Token, new OwnedWorkPhaseBudget(original));
        ActualOrdinaryCapture.Require(actual); Assert.Equal(2, actual.Command.ExitCode); Assert.False(actual.Command.TimedOut); Assert.False(actual.Command.Cancelled);
        using var returned = JsonDocument.Parse(actual.StandardOutputBytes); Assert.Equal("ORIGINAL_OWNER_REQUIRED", returned.RootElement.GetProperty("Code").GetString());
        var after = await store.RecoverAsync(selected.RunId, original.Token);
        Assert.Equal(before.Records.Select(row => row.Sha256), after.Records.Select(row => row.Sha256));
        _ = PackageLifecycle.Verify(fixture.Installation, original);
        // A real fresh-process refusal is not a successful worker resume, does
        // not reconcile any old unknown native owner and grants no new budget.
    }
}
