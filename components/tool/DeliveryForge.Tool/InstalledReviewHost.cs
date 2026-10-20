using System.Collections;
using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;

namespace DeliveryForge.Tool;

// Trusted locators and image pins only. No reviewer result, accepted worker or
// production authority can be imported through this configuration.
public sealed record InstalledReviewSelection(string PolicyRelativePath, string EvidencePolicyRelativePath,
    string EvidencePolicyIdentity, AdmissionFile GitImage, string GitVersion, string ArtifactRoot,
    long MaximumEvidenceBytes = 16 * 1024 * 1024);

internal sealed class InstalledReviewHost : IDisposable
{
    readonly TrustedConfiguration configuration; readonly AdmissionFile configurationPin;
    readonly InstalledReviewSelection selection; readonly ToolLifetime original;
    readonly ProcessCommandExecutor executor;
    readonly List<BoundedCommandCaptureResult> captures = [];
    FrozenPlan? frozen; FrozenWorkerPlan? assignment; TrustedConfiguration? boundConfiguration;
    ProductionReviewSourceOwner? sourceOwner; ProductionReviewBootstrap? bootstrap;
    bool entered, disposed, uncertain;
    internal bool Unknown => uncertain || sourceOwner?.Uncertain == true;

    internal InstalledReviewHost(TrustedConfiguration config, AdmissionFile actualConfigurationPin,
        ToolLifetime actualOriginal, ProcessCommandExecutor actualExecutor)
    {
        configuration = config; configurationPin = actualConfigurationPin;
        original = actualOriginal; executor = actualExecutor;
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
        if (configuration.InstalledQuality is { } quality) BoundedFiles.Separate(selection.ArtifactRoot, quality.ArtifactRoot);
        frozen = plan; assignment = actualAssignment; boundConfiguration = config;
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
            if (captures.Count >= 8) throw new IOException("Finite review source-discovery call bound exceeded.");
            var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
                if (!((string)item.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                    environment[(string)item.Key] = (string)item.Value!;
            var nul = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = nul;
            environment["GIT_CONFIG_SYSTEM"] = nul; environment["GIT_TERMINAL_PROMPT"] = "0";
            environment["GIT_NO_REPLACE_OBJECTS"] = "1"; environment["GIT_NO_LAZY_FETCH"] = "1";
            environment["LC_ALL"] = "C"; environment["LANG"] = "C";
            var invocation = new CommandInvocation(selection.GitImage.Path,
                ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false",
                    "-c", "core.hooksPath=" + nul, .. arguments], run.Worktree, environment) { EnvironmentIsComplete = true };
            BoundedCommandCaptureResult capture;
            Task<BoundedCommandCaptureResult>? actualCapture = null; var dispatchEntered = false;
            try
            {
                capture = await parent.EnterAsync(run.RunId, () =>
                {
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
            if (!settled || capture.CaptureBoundExceeded || capture.Command.TimedOut || capture.Command.Cancelled || capture.Command.ExitCode != 0)
                throw new IOException("Actual review source discovery failed or remained incomplete.");
            Current();
            return new UTF8Encoding(false, true).GetString(capture.StandardOutputBytes);
        }

        Current();
        var gitDirectory = (await Git("rev-parse", "--absolute-git-dir").ConfigureAwait(false)).Trim();
        var commonDirectory = (await Git("rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false)).Trim();
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
        sourceOwner = new(executor, original, parent, run.RunId, run.Worktree, gitDirectory, commonDirectory,
            selection.GitImage.Path, selection.GitImage.Bytes, selection.GitImage.Sha256, selection.GitVersion,
            run.BaseCommit, completion.HeadCommit, () =>
                ReferenceEquals(frame.Completion, completion) && frame.OwnerCompleted && !frame.OwnerStopped &&
                frame.Primary is null && frame.Post is null && actualNativeCompletedAndQuiet(),
            frozen.ContractIdentity, evidence.Identity, selection.PolicyRelativePath, selection.MaximumEvidenceBytes);
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
                Kind = "OwnedImmutableReviewSourceData-v1", run.RunId, run.PlanIdentity, run.BaseCommit,
                completion.HeadCommit, completion.TreeId, EvidencePolicyIdentity = evidence.Identity,
                preparedBootstrap.RootContext.SourceAuthorityIdentity, preparedBootstrap.RootContext.ProtectedObjectViewIdentity,
                ReviewPolicyIdentity = preparedBootstrap.Policy.PolicyIdentity, ReviewerAccepted = false, PublicationAuthority = false
            }), original);
            return true;
        });
        return ToolResult.Incomplete("IMMUTABLE_REVIEW_SOURCE_PREPARED_REVIEWER_REQUIRED",
            "Actual completed worker source and protected policies are bound to an owned immutable object view; an actual independent reviewer is still required.",
            new { run.RunId, SourcePreparation = path, ReviewerAccepted = false, PublicationAuthority = false });
    }

    public void Dispose()
    {
        if (disposed) return;
        if (Unknown) { original.RetainUnknown(); return; }
        bootstrap?.Dispose(); disposed = true;
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
