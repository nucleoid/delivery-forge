using System.Collections;
using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Review;
using DeliveryForge.Tool.Claude;

namespace DeliveryForge.Tool;

// Trusted locators and image pins only. No reviewer result, accepted worker or
// production authority can be imported through this configuration.
public sealed record InstalledReviewSelection(string PolicyRelativePath, string EvidencePolicyRelativePath,
    string EvidencePolicyIdentity, AdmissionFile GitImage, string GitVersion, string ArtifactRoot,
    long MaximumEvidenceBytes = 16 * 1024 * 1024, IndependentClaudeReviewSelection? IndependentReviewer = null);

internal sealed class InstalledReviewHost : IDisposable
{
    readonly TrustedConfiguration configuration; readonly AdmissionFile configurationPin;
    readonly InstalledReviewSelection selection; readonly ToolLifetime original;
    readonly ProcessCommandExecutor executor;
    readonly LinuxScopeSelection? linuxScope;
    readonly InstalledHostAdmission.RuntimeTrust? runtimeTrust;
    readonly List<BoundedCommandCaptureResult> captures = [];
    FrozenPlan? frozen; FrozenWorkerPlan? assignment; TrustedConfiguration? boundConfiguration;
    ProductionReviewSourceOwner? sourceOwner; ProductionReviewBootstrap? bootstrap;
    ClaudeIndependentReviewer? independentReviewer;
    DeliveryForge.Contracts.Validation.ValidatedContract? protectedEvidencePolicy;
    bool entered, disposed, uncertain, dispositionEntered;
    internal bool DispositionEntered => dispositionEntered;
    internal bool DispositionReturned => disposed && (bootstrap is null || bootstrap.ActualDispositionReturned);
    internal bool Unknown => uncertain || sourceOwner?.Uncertain == true || independentReviewer?.Unknown == true;
    internal bool HasIndependentReviewer => selection.IndependentReviewer is not null;
    internal Task<bool> StopIndependentAsync(HostBridgeFrame frame)
    {
        if (!ReferenceEquals(frame.Lifetime, original) || frame.RunId != assignment?.RunId) throw new WorkerEnvelopeException("Only the retained original may stop its independent reviewer.");
        return independentReviewer?.StopAsync() ?? Task.FromResult(true);
    }

    internal InstalledReviewHost(TrustedConfiguration config, AdmissionFile actualConfigurationPin,
        ToolLifetime actualOriginal, ProcessCommandExecutor actualExecutor, LinuxScopeSelection? selectedLinuxScope = null, InstalledHostAdmission.RuntimeTrust? runtimeTrust = null)
    {
        configuration = config; configurationPin = actualConfigurationPin;
        original = actualOriginal; executor = actualExecutor;
        linuxScope = selectedLinuxScope; this.runtimeTrust = runtimeTrust;
        selection = config.InstalledReview ?? throw new ToolUsageException("No trusted installed review selection.");
        ValidateSelection(selection);
        if (!Same(config.GitExecutable, selection.GitImage.Path))
            throw new ToolUsageException("Review must use the same trusted containing Git executable.");
    }

    internal static void ValidateSelection(InstalledReviewSelection value)
    {
        Relative(value.PolicyRelativePath); Relative(value.EvidencePolicyRelativePath);
        if (value.GitImage is null || value.GitImage.Bytes is < 1 or > 64 * 1024 * 1024 ||
            !Digest(value.GitImage.Sha256) || value.GitVersion is not { Length: > 12 and <= 256 } ||
            !value.GitVersion.StartsWith("git version ", StringComparison.Ordinal) || value.GitVersion.Any(char.IsControl) ||
            value.MaximumEvidenceBytes is < 1 or > 16 * 1024 * 1024 ||
            value.EvidencePolicyIdentity is not { Length: 71 } ||
            !value.EvidencePolicyIdentity.StartsWith("sha256:", StringComparison.Ordinal) ||
            !Digest(value.EvidencePolicyIdentity[7..]))
            throw new ToolUsageException("Finite exact Git image, version, protected policy and review bounds required.");
        BoundedFiles.Plain(value.GitImage.Path); BoundedFiles.Plain(value.ArtifactRoot, true);
        if (value.IndependentReviewer is { } reviewer)
        {
            if (reviewer.Image is null || reviewer.Image.Bytes is < 1 or > BoundedFiles.MaximumNativeImageBytes || !Digest(reviewer.Image.Sha256) ||
                string.IsNullOrWhiteSpace(reviewer.Version) || reviewer.Version.Length > 256 || reviewer.Version.Any(char.IsControl) || reviewer.MaximumSeconds is < 1 or > 600)
                throw new ToolUsageException("Finite exact native independent reviewer image/version/budget required.");
            BoundedFiles.Plain(reviewer.Image.Path);
        }
    }

    internal void Bind(FrozenPlan plan, FrozenWorkerPlan actualAssignment, TrustedConfiguration config, ToolLifetime life)
    {
        original.Check();
        if (frozen is not null || !ReferenceEquals(life, original) || plan.Identity != config.ExpectedBundleIdentity ||
            plan.ContractIdentity != actualAssignment.PlanIdentity || actualAssignment.BaseCommit != plan.BaseCommit ||
            actualAssignment.Repository != configuration.Repository || !Same(actualAssignment.ParentCheckout, configuration.RepositoryRoot))
            throw new ToolUsageException("Review binds the same actual frozen plan, assignment and containing original once.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(actualAssignment.RunId, "^run-[a-f0-9]{32}$"))
            throw new ToolUsageException("Closed standalone generated review run identity required.");
        _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536);
        foreach (var root in new[] { actualAssignment.ParentCheckout, actualAssignment.Worktree, actualAssignment.ResultDirectory })
        {
            BoundedFiles.Separate(root, selection.ArtifactRoot);
            BoundedFiles.Separate(root, configurationPin.Path);
            BoundedFiles.Separate(root, selection.GitImage.Path);
        }
        BoundedFiles.Separate(selection.ArtifactRoot, configurationPin.Path);
        BoundedFiles.Separate(selection.ArtifactRoot, selection.GitImage.Path);
        if (selection.IndependentReviewer is { } reviewer)
            foreach (var root in new[] { actualAssignment.ParentCheckout, actualAssignment.Worktree, actualAssignment.ResultDirectory, selection.ArtifactRoot }) BoundedFiles.Separate(root, reviewer.Image.Path);
        if (configuration.InstalledQuality is { } quality) BoundedFiles.Separate(selection.ArtifactRoot, quality.ArtifactRoot);
        frozen = plan; assignment = actualAssignment; boundConfiguration = config;
    }

    internal async Task<ToolResult> RunIndependentAsync(HostBridgeFrame frame, ParentCoordinationHost parent, string? actualAuthorFamily,
        Func<bool> actualNativeCompletedAndQuiet, IReadOnlyList<GateRunResult> actualQualityResults)
    {
        if (selection.IndependentReviewer is null) return ToolResult.Incomplete("INDEPENDENT_REVIEWER_REQUIRED", "No actual independent runtime reviewer was selected.");
        if (bootstrap is null || sourceOwner is null || assignment is null || frozen is null || frame.Completion is not { } completion ||
            !ReferenceEquals(frame.Lifetime, original) || !frame.OwnerCompleted || frame.OwnerStopped || !actualNativeCompletedAndQuiet())
            throw new WorkerEnvelopeException("Same original immutable source preparation and completed author required.");
        if (actualAuthorFamily != "gpt") return ToolResult.Incomplete("ACTUAL_AUTHOR_FAMILY_REQUIRED", "The completed native author did not expose unambiguous opposite-family provenance.");
        if (independentReviewer is not null) throw new ToolUsageException("Actual independent reviewer cannot replay.");
        // Refuse this unsupported production profile before source/settings/CWD I/O.
        // RunAsync keeps the same guard as defense at its own boundary.
        if (ClaudeIndependentReviewer.UnsupportedProductionProfile(runtimeTrust, original, selection.IndependentReviewer) is { } profileRefusal) return profileRefusal;
        sourceOwner.Check();
        async Task<(PatchIdentity Patch, byte[] FrozenPlan)> PrepareEvidence(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); sourceOwner.Check();
            var identities = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["plan-contract"] = frozen.ContractIdentity,
                ["rich-plan"] = frozen.Identity,
                ["evidence-policy"] = bootstrap.RootContext.EvidencePolicyIdentity,
                ["trusted-configuration"] = "sha256:" + configurationPin.Sha256
            };
            var qualityEvidence = new List<JsonElement>();
            foreach (var gate in actualQualityResults)
            {
                var bytes = parent.EnterSynchronous(assignment.RunId, () => { token.ThrowIfCancellationRequested(); sourceOwner.Check(); return BoundedFiles.Read(gate.ReceiptPath, original); });
                token.ThrowIfCancellationRequested();
                identities.Add("quality-receipt:" + gate.ReceiptPath, "sha256:" + BoundedFiles.Sha(bytes));
                using var doc = JsonDocument.Parse(bytes); qualityEvidence.Add(doc.RootElement.Clone());
            }
            var patch = await GitPatchIdentity.ComputeAsync(bootstrap.ObjectView, assignment.BaseCommit, completion.HeadCommit, frozen.ContractIdentity, identities, token).ConfigureAwait(false);
            var planAndEvidence = JsonSerializer.SerializeToUtf8Bytes(new { FrozenPlan = JsonSerializer.Deserialize<JsonElement>(frozen.CanonicalBytes), QualityEvidence = qualityEvidence, MissingQualityEvidence = actualQualityResults.Count == 0 }, BoundedFiles.Json);
            token.ThrowIfCancellationRequested(); sourceOwner.Check(); return (patch, planAndEvidence);
        }
        var userSettings = parent.EnterSynchronous(assignment.RunId, () =>
        {
            sourceOwner.Check(); original.Check(); runtimeTrust?.CheckReviewer(original, selection.IndependentReviewer);
            return ClaudeReviewerUserSettings.CaptureActual(original, runtimeTrust, selection.IndependentReviewer,
                assignment.ParentCheckout, assignment.Worktree, assignment.ResultDirectory, selection.ArtifactRoot);
        });
        var reviewerDirectory = parent.EnterSynchronous(assignment.RunId, () =>
        {
            sourceOwner.Check(); original.Check();
            _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536);
            if (!actualNativeCompletedAndQuiet()) throw new WorkerEnvelopeException("Actual author must be closed before private reviewer CWD creation.");
            return ClaudeReviewerWorkingDirectory.Create(original, assignment.RunId, selection.ArtifactRoot,
                assignment.ParentCheckout, assignment.Worktree, assignment.ResultDirectory);
        });
        independentReviewer = new(selection.IndependentReviewer, original, executor, parent, assignment.RunId, reviewerDirectory, userSettings, bootstrap,
            () => ReferenceEquals(frame.Completion, completion) && frame.OwnerCompleted && !frame.OwnerStopped && frame.Primary is null && frame.Post is null && actualNativeCompletedAndQuiet(),
            () => parent.EnterSynchronous(assignment.RunId, () => { sourceOwner.Check(); _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536); return true; }), linuxScope, runtimeTrust);
        return await parent.EnterAsync(assignment.RunId, () => independentReviewer.RunAsync(actualAuthorFamily,
            Path.Combine(selection.ArtifactRoot, assignment.RunId), PrepareEvidence)).ConfigureAwait(false);
    }

    internal bool MatchesRetained(HostBridgeFrame frame, TrustedConfiguration? config, ToolLifetime? life) =>
        ReferenceEquals(life, original) && ReferenceEquals(config, boundConfiguration) && frozen is not null &&
        assignment is not null && frame.RunId == assignment.RunId && ReferenceEquals(frame.Lifetime, original) &&
        frame.Request?.PlanIdentity == assignment.PlanIdentity;

    internal async Task<ToolResult> PrepareCompletedAsync(HostBridgeFrame frame, ParentCoordinationHost parent,
        Func<bool> actualNativeCompletedAndQuiet)
    {
        if (entered || frozen is null || assignment is null || frame.Completion is not { } completion)
            throw new ToolUsageException("Review preparation requires the retained completed native worker once.");
        entered = true;
        var run = assignment;
        // Obtained only from this frame's actual provisioning/writer owner,
        // before any worker-controlled Git/config/status discovery can run.
        var provisioned = frame.Owner.RequireOriginalWorktreeBinding(run.RunId, original);
        void Current()
        {
            original.Check();
            if (disposed || Unknown || !ReferenceEquals(frame.Lifetime, original) ||
                !ReferenceEquals(frame.Completion, completion) || !frame.OwnerCompleted || frame.OwnerStopped ||
                frame.RunId != run.RunId || completion.RunId != run.RunId || completion.Worktree != run.Worktree ||
                completion.RequestIdentity != frame.Request?.RequestIdentity || !actualNativeCompletedAndQuiet())
                throw new WorkerEnvelopeException("Same original completed native worker changed before review effect.");
            parent.EnterSynchronous(run.RunId, () =>
            {
                _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536);
                _ = InstalledHostAdmission.ReadExact(selection.GitImage, original, 64 * 1024 * 1024);
                return true;
            });
        }
        async Task<string> Git(params string[] arguments)
        {
            Current();
            provisioned.Check();
            var filterProbe = arguments.SequenceEqual(new[] { "config", "--includes", "--name-only", "--get-regexp", "^filter\\." });
            var filters = new HashSet<string>(StringComparer.Ordinal);
            if (arguments[0] == "status")
            {
                var keys = await Git("config", "--includes", "--name-only", "--get-regexp", "^filter\\.").ConfigureAwait(false);
                foreach (var key in keys.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => value.TrimEnd('\r')))
                {
                    var split = key.LastIndexOf('.');
                    if (!key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) || split <= 7 || key.Any(char.IsControl))
                        throw new WorktreeBoundaryException("Review filter configuration is ambiguous.");
                    var name = key[7..split];
                    if (name.Length > 256 || name.Contains('='))
                        throw new WorktreeBoundaryException("Review filters exceed their finite profile.");
                    filters.Add(name);
                    if (filters.Count > 128) throw new WorktreeBoundaryException("Review filters exceed128 names.");
                }
                provisioned.Check();
            }
            if (captures.Count >= 8) throw new IOException("Finite review source-discovery call bound exceeded.");
            var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
                if (!((string)item.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                    environment[(string)item.Key] = (string)item.Value!;
            var nul = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = nul;
            environment["GIT_CONFIG_SYSTEM"] = nul; environment["GIT_TERMINAL_PROMPT"] = "0";
            environment["GIT_NO_REPLACE_OBJECTS"] = "1"; environment["GIT_NO_LAZY_FETCH"] = "1";
            environment["GIT_GRAFT_FILE"] = nul;
            environment["GIT_COMMON_DIR"] = provisioned.CommonDirectory;
            environment["GIT_OBJECT_DIRECTORY"] = provisioned.ObjectsPath;
            environment["GIT_INDEX_FILE"] = provisioned.IndexPath;
            environment["LC_ALL"] = "C"; environment["LANG"] = "C";
            var disabledFilters = filters.SelectMany(name => new[] { "process=", "smudge=", "clean=", "required=false" }
                .SelectMany(setting => new[] { "-c", "filter." + name + "." + setting })).ToArray();
            var invocation = new CommandInvocation(selection.GitImage.Path,
                ["--no-optional-locks", "--git-dir=" + provisioned.GitDirectory, "--work-tree=" + provisioned.Worktree,
                    "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-c", "core.attributesFile=" + nul,
                    "-c", "diff.external=", "-c", "core.hooksPath=" + nul, .. disabledFilters, .. arguments], provisioned.Worktree, environment)
            { EnvironmentIsComplete = true };
            BoundedCommandCaptureResult capture;
            Task<BoundedCommandCaptureResult>? actualCapture = null; var dispatchEntered = false;
            try
            {
                capture = await parent.EnterAsync(run.RunId, () =>
                {
                    provisioned.Check();
                    dispatchEntered = true;
                    actualCapture = executor.ExecuteBoundedCaptureAsync(invocation,
                        original.Remaining < TimeSpan.FromSeconds(30) ? original.Remaining : TimeSpan.FromSeconds(30),
                        1024 * 1024, original.Token, new OwnedWorkPhaseBudget(original));
                    return actualCapture;
                }).ConfigureAwait(false);
            }
            catch
            {
                if (actualCapture is { IsCompletedSuccessfully: true })
                {
                    var actualReturned = actualCapture.Result; captures.Add(actualReturned);
                    if (Settled(actualReturned)) throw;
                }
                if (dispatchEntered)
                {
                    uncertain = true; original.RetainUnknown(); parent.RetainUncertain(run.RunId, "Review source discovery did not return a settled actual native capture.");
                }
                throw;
            }
            captures.Add(capture);
            var settled = Settled(capture);
            if (!settled)
            {
                uncertain = true; original.RetainUnknown(); parent.RetainUncertain(run.RunId, "Review discovery retains its original native scope or streams.");
            }
            if (!settled || capture.CaptureBoundExceeded || capture.Command.TimedOut || capture.Command.Cancelled ||
                capture.Command.ExitCode != 0 && !(filterProbe && capture.Command.ExitCode == 1))
                throw new IOException("Actual review source discovery failed or remained incomplete.");
            Current();
            return new UTF8Encoding(false, true).GetString(capture.StandardOutputBytes);
        }

        Current();
        var gitDirectory = provisioned.GitDirectory;
        var commonDirectory = provisioned.CommonDirectory;
        BoundedFiles.Plain(gitDirectory); BoundedFiles.Plain(commonDirectory);
        var head = (await Git("rev-parse", "HEAD^{commit}").ConfigureAwait(false)).Trim();
        var tree = (await Git("rev-parse", "HEAD^{tree}").ConfigureAwait(false)).Trim();
        if (head != completion.HeadCommit || tree != completion.TreeId ||
            (await Git("status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching").ConfigureAwait(false)).Length != 0)
            throw new WorkerEnvelopeException("Actual completed worker source changed before immutable review preparation.");
        var evidenceBytes = Encoding.UTF8.GetBytes(await Git("show", run.BaseCommit + ":" + selection.EvidencePolicyRelativePath).ConfigureAwait(false));
        var evidence = DeliveryForge.Contracts.Validation.ContractValidator.ParseAndValidate(evidenceBytes);
        if (evidence.SchemaName != "evidence-policy" || evidence.Identity != selection.EvidencePolicyIdentity)
            throw new ToolUsageException("Actual protected-base evidence policy differs from the selected identity.");
        protectedEvidencePolicy = evidence;
        sourceOwner = new(executor, original, parent, run.RunId, run.Worktree, gitDirectory, commonDirectory,
            selection.GitImage.Path, selection.GitImage.Bytes, selection.GitImage.Sha256, selection.GitVersion,
            run.BaseCommit, completion.HeadCommit, () =>
                ReferenceEquals(frame.Completion, completion) && frame.OwnerCompleted && !frame.OwnerStopped &&
                frame.Primary is null && frame.Post is null && actualNativeCompletedAndQuiet(),
            frozen.ContractIdentity, evidence.Identity, selection.PolicyRelativePath, selection.MaximumEvidenceBytes, provisioned);
        var preparedBootstrap = await ReviewAuthorityHost.CreateProductionFromOwnedSourceAsync(sourceOwner, frozen.PlanContractBytes,
            evidence.CanonicalBytes.ToArray(), selection.PolicyRelativePath, selection.MaximumEvidenceBytes, original.Token).ConfigureAwait(false);
        bootstrap = preparedBootstrap;
        Current();
        var output = Path.Combine(selection.ArtifactRoot, run.RunId);
        var path = Path.Combine(output, "review-source-preparation.json");
        parent.EnterSynchronous(run.RunId, () =>
        {
            original.Check(); BoundedFiles.Plain(output, true);
            if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Review result namespace already exists; no replay.");
            Directory.CreateDirectory(output);
            BoundedFiles.WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Kind = "OwnedImmutableReviewSourceData-v1",
                run.RunId,
                run.PlanIdentity,
                run.BaseCommit,
                completion.HeadCommit,
                completion.TreeId,
                EvidencePolicyIdentity = evidence.Identity,
                preparedBootstrap.RootContext.SourceAuthorityIdentity,
                preparedBootstrap.RootContext.ProtectedObjectViewIdentity,
                ReviewPolicyIdentity = preparedBootstrap.Policy.PolicyIdentity,
                ReviewerAccepted = false,
                PublicationAuthority = false
            }), original);
            return true;
        });
        return ToolResult.Incomplete("IMMUTABLE_REVIEW_SOURCE_PREPARED_REVIEWER_REQUIRED",
            "Actual completed worker source and protected policies are bound to an owned immutable object view; an actual independent reviewer is still required.",
            new { run.RunId, SourcePreparation = path, ReviewerAccepted = false, PublicationAuthority = false });
    }

    internal void DisposeDuringWork()
    {
        if (disposed) return;
        original.Check();
        if (Unknown) { original.RetainUnknown(); throw new IOException("Actual review object view remains uncertain; retain its owner."); }
        bootstrap?.CheckDisposition(cleanup: false);
        dispositionEntered = true;
        bootstrap?.DisposeDuringWork();
        disposed = bootstrap is null || bootstrap.ActualDispositionReturned;
        if (!disposed) throw new IOException("Original review callback did not return its disposition; exact view retained.");
        original.Check();
    }
    public void Dispose()
    {
        if (disposed) return;
        if (Unknown) { original.RetainUnknown(); return; }
        bootstrap?.CheckDisposition(cleanup: true);
        dispositionEntered = true;
        bootstrap?.Dispose();
        disposed = bootstrap is null || bootstrap.ActualDispositionReturned;
        if (!disposed) throw new IOException("Original review callback did not return its disposition; exact view retained.");
    }
    internal async Task<(ClaudeIndependentReviewer.AcceptedLocalRevision Accepted, DeliveryForge.Contracts.Validation.ValidatedContract Policy)?> LocalRealEvidenceAsync(HostBridgeFrame frame, ParentCoordinationHost parent)
    {
        var accepted = independentReviewer?.AcceptedRevision;
        if (accepted is null || protectedEvidencePolicy is null) return null;
        if (disposed || Unknown || sourceOwner is null || bootstrap is null || assignment is null || frozen is null ||
            !ReferenceEquals(frame.Lifetime, original) || !ReferenceEquals(accepted.Original, original) || frame.RunId != assignment.RunId ||
            frame.Completion is not { } completion || !frame.OwnerCompleted || frame.OwnerStopped || frame.Primary is not null || frame.Post is not null ||
            accepted.Patch.HeadCommit != completion.HeadCommit || accepted.Patch.HeadTree != completion.TreeId || accepted.Patch.PlanIdentity != assignment.PlanIdentity)
            throw new WorkerEnvelopeException("Actual accepted local-real review original/source binding changed.");
        parent.EnterSynchronous(frame.RunId, () => { sourceOwner.Check(); _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536); return true; });
        _ = await GitPatchIdentity.ExportVerifiedPatchAsync(bootstrap.ObjectView, accepted.Patch, original.Token).ConfigureAwait(false);
        original.Check(); sourceOwner.Check();
        return (accepted, protectedEvidencePolicy);
    }
    static bool Digest(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    static bool Settled(BoundedCommandCaptureResult capture) => capture.Command.OwnedProcessQuiescent &&
        capture.RootExitObserved && capture.StandardOutputDrainCompleted && capture.StandardErrorDrainCompleted &&
        capture.Command.LifecycleDiagnostics is { NativeFailure: null, OwnedTree: { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 } } &&
        (!OperatingSystem.IsLinux() || capture.OwnershipKind == BoundedCaptureOwnershipKind.LinuxCreationOwnedScope);
    static bool Same(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    static void Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\\') || path.Contains(':') ||
            Path.IsPathFullyQualified(path) || path.Split('/').Any(p => p is "" or "." or ".." or ".git"))
            throw new ToolUsageException("Closed repository-relative protected review input required.");
    }
}
