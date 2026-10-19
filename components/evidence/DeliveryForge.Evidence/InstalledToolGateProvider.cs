using DeliveryForge.Coordination;
using System.Collections;
using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence;

public sealed record InstalledToolGateRequest(string GateId, ResolvedEvidencePolicy Policy,
    RepositoryIdentity ExpectedRepository, string Scope, string? ProjectRelativePath,
    TimeSpan Timeout, string OutputDirectory);

/// <summary>Real installed executable path. No injectable executor, caller result, report locator
/// or supported=true switch exists on this API. The parent supplies an out-of-band pinned install
/// profile; policy/baseline authority remains with the existing protected-policy resolver.</summary>
public sealed class InstalledToolGateProvider
{
    private readonly string _root;
    private readonly string _base;
    public InstalledToolGateProvider(string repositoryRoot, string protectedBaseCommit)
    {
        InstalledToolFiles.PlainPath(repositoryRoot);
        if (!InstalledToolFiles.ObjectId(protectedBaseCommit)) throw new ArgumentException("A frozen full base commit is required.");
        _root = Path.GetFullPath(repositoryRoot); _base = protectedBaseCommit;
    }

    public Task<GateRunResult> RunAsync(InstalledToolProfile profile, InstalledToolGateRequest request,
        CancellationToken cancellationToken = default) => RunCoreAsync(profile, request, null, null, cancellationToken);
    internal Task<GateRunResult> RunOwnedAsync(InstalledToolProfile profile, InstalledToolGateRequest request,
        InstalledToolFiles files, ActualExecutor owner, CancellationToken cancellationToken) =>
        RunCoreAsync(profile, request, files, owner, cancellationToken);
    private async Task<GateRunResult> RunCoreAsync(InstalledToolProfile profile, InstalledToolGateRequest request,
        InstalledToolFiles? actualFiles, ActualExecutor? actualOwner, CancellationToken cancellationToken)
    {
        // Public unowned Linux execution remains closed. Only this exact internal
        // containing files/executor pair can use the selected kernel scope; selection
        // DATA does not establish birth or successful descendant settlement.
        if (!OperatingSystem.IsWindows() && !(OperatingSystem.IsLinux() && actualFiles is not null &&
            actualOwner is not null && actualOwner.OwnsScopedFiles(actualFiles)))
            throw new EvidenceWriteException("Current installed-tool execution requires a genuine creation-owned process scope; the shared Linux owner is not yet integrated.");
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(20) ||
            profile.Tool == "mutate4csharp" && request.Timeout > TimeSpan.FromMinutes(10) ||
            request.ExpectedRepository.BaseCommit != _base || !request.ExpectedRepository.IsClean ||
            !InstalledToolFiles.ObjectId(request.ExpectedRepository.HeadCommit) || !InstalledToolFiles.ObjectId(request.ExpectedRepository.TreeId))
            throw new EvidenceWriteException("Invalid original timeout or frozen final repository identity.");
        InstalledToolProfile.Relative(request.GateId);
        if (request.GateId.Contains('/')) throw new EvidenceWriteException("Gate id must be one leaf.");
        InstalledToolFiles.PlainPath(request.OutputDirectory);

        if (Inside(request.OutputDirectory, _root)) throw new EvidenceWriteException("Owned artifacts must be external to the consumer.");
        var files = actualFiles ?? new InstalledToolFiles(request.Timeout, cancellationToken);
        var owner = actualOwner ?? new ActualExecutor(files, profile.Data.Environment, profile.Data.GitExecutablePath);
        try
        {
            var resourcesBefore = InstalledToolResourceAdmission.Capture(files);
            var repository = new GitRepositoryIdentityReader(owner, _root, _base);
            var before = await repository.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (before != request.ExpectedRepository) throw new EvidenceWriteException("Actual consumer is not the exact clean expected base/head/tree.");
            var original = files.Inventory(_root, 100000, 1024L * 1024 * 1024, skipGit: true);
            var gitState = CaptureGitState(files);
            var install = CaptureInstallation(profile, files);
            var environment = EnvironmentIdentity();
            var configuration = Path.GetFullPath(Path.Combine(_root, profile.Data.ConfigurationRelativePath));
            var configurationBytes = files.Read(configuration, 1024 * 1024);
            if (InstalledToolFiles.Identity(configurationBytes) != profile.Data.ConfigurationSha256)
                throw new EvidenceWriteException("Current configuration differs from the parent-approved exact bytes.");
            if (profile.Tool == "mutate4csharp") RequireMutationBudget(configurationBytes, request.Timeout);
            var sdkBefore = await ObserveSdk(profile, owner, cancellationToken).ConfigureAwait(false);
            var runRoot = Path.Combine(request.OutputDirectory, request.GateId + "-producer-" + Guid.NewGuid().ToString("N"));
            files.EnsureDirectory(runRoot);
            var emptyProbe = Path.Combine(runRoot, "capability-input.cs");
            if (profile.Tool == "crap4csharp") files.WriteNew(emptyProbe, "public class Empty {}"u8.ToArray());
            var executable = Path.Combine(profile.Data.InstallationRoot, profile.Data.ExecutableRelativePath);
            var detector = new ToolCapabilityDetector(owner, files);
            var detected = await detector.DetectAsync(new(profile.Tool, ExecutableSource.TrustedExplicit,
                executable, _root, null, profile.Tool == "mutate4csharp" ? ["--version"] :
                    ["analyze", "--syntax-only", "--format", "json", emptyProbe], ["--help"], profile.Version,
                profile.Data.Format, profile.Fixture), cancellationToken).ConfigureAwait(false);
            if (!detected.Capability.Supported || detected.Capability.Version != profile.Version ||
                detected.VersionResult is null || detected.HelpResult is null || !owner.Known(detected.VersionResult) || !owner.Known(detected.HelpResult))
                throw new EvidenceWriteException("Actual installed version/help does not match the pinned complete version.");
            byte[]? changedDiff = null;
            if (profile.Tool == "crap4csharp")
            {
                // The complete actual frozen-base diff is acquired before main finalization.
                // No Git textconv, external driver, rename sampling or worktree override.
                var diff = await owner.ExecuteAsync(new("git", ["-c", "core.quotePath=false", "diff", "--no-ext-diff", "--no-textconv",
                "--no-renames", "--no-color", "--no-relative", "--unified=0", "--src-prefix=a/", "--dst-prefix=b/",
                _base, request.ExpectedRepository.HeadCommit, "--", "*.cs"], _root), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                if (!owner.Known(diff) || diff.ExitCode != 0) throw new EvidenceWriteException("Complete actual frozen-base diff is unavailable.");
                changedDiff = owner.OriginalOutput(diff);
            }
            var reportPath = profile.Tool == "mutate4csharp" ? Path.Combine(runRoot, "mutation.json") : null;
            var arguments = BuildArguments(profile.Tool, request, _base, profile.Data.ConfigurationRelativePath, reportPath, Path.Combine(runRoot, "bundle"));
            var invocation = new CommandInvocation(executable, arguments, _root,
                new Dictionary<string, string>(profile.Data.Environment, StringComparer.Ordinal));
            var binding = new InstalledToolInvocation(profile, request, files, owner, invocation, reportPath, original, gitState,
                install, environment, sdkBefore, resourcesBefore, changedDiff, () => CaptureGitState(files), () => CaptureInstallation(profile, files));
            owner.BindMain(invocation, binding);
            var gate = new GateRequest(request.GateId, request.Policy, detected.Capability,
                profile.Tool == "mutate4csharp" ? EvidenceAdapterKind.Mutate4CSharp : EvidenceAdapterKind.Crap4CSharp,
                profile.Data.ConfigurationSha256, before, invocation, request.Scope, files.Remaining, request.OutputDirectory)
            { InstalledInvocation = binding };
            var result = await new GateRunner(owner, repository, _root, _base).RunAsync(gate, cancellationToken).ConfigureAwait(false);
            files.ReleaseKnown(); return result;
        }
        catch (Exception primary)
        {
            try { files.Effect(() => SaveFailedAttempt(profile, request, files, owner, primary)); }
            catch (Exception persistence)
            {
                // No retry, new clock or positive receipt follows an uncertain save.
                throw new AggregateException("Installed attempt failed and bounded failure publication did not complete.", primary, persistence);
            }
            throw;
        }
    }

    private static void SaveFailedAttempt(InstalledToolProfile profile, InstalledToolGateRequest request,
        InstalledToolFiles files, ActualExecutor owner, Exception primary)
    {
        _ = files.CleanupRemaining;
        var directory = Path.Combine(request.OutputDirectory, request.GateId + "-failed-" + Guid.NewGuid().ToString("N"));
        files.EnsureDirectory(directory);
        Save("failure.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = "forge-installed-attempt-failure-v1",
            profile.Identity,
            profile.Tool,
            profile.Fixture,
            request.GateId,
            request.ExpectedRepository,
            Timing = files.TimingData,
            Type = primary.GetType().FullName,
            Message = Clip(primary.Message),
            Stack = Clip(primary.StackTrace ?? ""),
            ActualCalls = owner.Calls.Count,
            RuntimeAccepted = false,
            ProductionAuthority = false
        }));
        for (var index = 0; index < owner.Calls.Count; index++)
        {
            files.Check(); var call = owner.Calls[index];
            Save($"process-{index:D2}.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                call.Invocation.FileName,
                call.Invocation.Arguments,
                call.Invocation.WorkingDirectory,
                call.Result.ExitCode,
                call.Result.TimedOut,
                call.Result.Cancelled,
                call.Result.OwnedProcessQuiescent,
                call.Capture.Timing,
                call.Capture.CaptureBoundExceeded,
                call.Capture.RootExitObserved,
                call.Capture.StandardOutputDrainCompleted,
                call.Capture.StandardErrorDrainCompleted,
                OriginalKnown = owner.Known(call.Result)
            }));
            Save($"process-{index:D2}.stdout.bin", call.Capture.StandardOutputBytes);
            Save($"process-{index:D2}.stderr.bin", call.Capture.StandardErrorBytes);
        }
        files.Check(); files.ReleaseKnown();
        void Save(string leaf, byte[] bytes)
        {
            if (bytes.Length > 16 * 1024 * 1024) throw new EvidenceWriteException("Finite failure-artifact bound exceeded.");
            files.WriteNew(Path.Combine(directory, leaf), bytes);
        }
        static string Clip(string text) => text.Length <= 4096 ? text : text[..4096];
    }

    internal static string[] BuildArguments(string tool, InstalledToolGateRequest request, string frozenBase,
        string configuration, string? report, string bundle)
    {
        if (tool == "mutate4csharp")
        {
            if (request.Scope != "base" || request.ProjectRelativePath is not null || report is null)
                throw new EvidenceWriteException("Supported mutation profile evaluates the entire frozen direct-base scope, with no state/cache/targeted rerun.");
            return ["check", "--base", frozenBase, "--report", report, "--no-state"];
        }
        if (request.Scope is not ("all" or "base" or "worktree") || request.ProjectRelativePath is null)
            throw new EvidenceWriteException("A complete policy-owned check project/scope is required.");
        InstalledToolProfile.Relative(request.ProjectRelativePath);
        InstalledToolProfile.Relative(configuration);
        return ["check", "--project", request.ProjectRelativePath, "--policy", configuration, "--base", frozenBase,
            "--scope", request.Scope, "--artifact-output", bundle, "--format", "json",
            "--timeout-seconds", Math.Max(1, (int)Math.Floor(request.Timeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)];
    }

    private Dictionary<string, string> CaptureGitState(InstalledToolFiles files) => files.Effect(() => CaptureGitStateCore(files));
private Dictionary<string, string> CaptureGitStateCore(InstalledToolFiles files)
    {
        var git = Path.Combine(_root, ".git");
        if (File.Exists(git))
        {
            var text = new UTF8Encoding(false, true).GetString(files.Read(git, 8192)).Trim();
            if (!text.StartsWith("gitdir: ", StringComparison.Ordinal)) throw new EvidenceWriteException("Unknown worktree Git metadata.");
            git = Path.GetFullPath(text[8..], _root);
        }
        InstalledToolFiles.PlainPath(git);
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var leaf in new[] { "HEAD", "index", "commondir", "config", "config.worktree" })
        {
            var path = Path.Combine(git, leaf);
            rows[leaf] = File.Exists(path) ? files.FileHash(path, 32 * 1024 * 1024) : "absent";
        }
        var commonPath = Path.Combine(git, "commondir");
        var common = File.Exists(commonPath) ? Path.GetFullPath(new UTF8Encoding(false, true).GetString(files.Read(commonPath, 8192)).Trim(), git) : git;
        foreach (var leaf in new[] { "config", "packed-refs" })
        {
            var path = Path.Combine(common, leaf);
            rows["common/" + leaf] = File.Exists(path) ? files.FileHash(path, 32 * 1024 * 1024) : "absent";
        }
        foreach (var (name, directory) in new[] { ("worktree-refs", Path.Combine(git, "refs")), ("common-refs", Path.Combine(common, "refs")) })
        {
            files.Check(); InstalledToolFiles.PlainPath(directory);
            if (!Directory.Exists(directory)) { rows.Add(name, "absent"); continue; }
            var refs = files.Inventory(directory, 10000, 32L * 1024 * 1024, false);
            rows.Add(name, "present");
            foreach (var pair in refs) rows.Add(name + "/" + pair.Key, pair.Value);
        }
        return rows;
    }

    internal static Dictionary<string, string> CaptureInstallation(InstalledToolProfile profile, InstalledToolFiles files) => files.Effect(() => CaptureInstallationCore(profile, files));
private static Dictionary<string, string> CaptureInstallationCore(InstalledToolProfile profile, InstalledToolFiles files)
    {
        var data = profile.Data;
        var actual = files.Inventory(data.InstallationRoot, 20000, 1024L * 1024 * 1024, false);
        var expected = data.Payload.ToDictionary(p => p.Path, p => p.Bytes + ":" + p.Sha256, InstalledToolFiles.PathComparer);
        InstalledToolFiles.Same(expected, actual, false, files, InstalledToolFiles.ComparisonPhase.InstallationProfile);
        if (files.FileHash(data.PackagePath, 128 * 1024 * 1024) != data.PackageSha256 ||
            files.FileHash(data.DotNetHostPath, 32 * 1024 * 1024) != data.DotNetHostSha256 ||
            files.FileHash(data.GitExecutablePath, 32 * 1024 * 1024) != data.GitExecutableSha256)
            throw new EvidenceWriteException("Approved package/host bytes changed.");
        return actual;
    }

    private async Task<string> ObserveSdk(InstalledToolProfile profile, ActualExecutor owner, CancellationToken cancellationToken)
    {
        var observed = await owner.ExecuteAsync(new(profile.Data.DotNetHostPath, ["--version"], _root), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (!owner.Known(observed) || observed.ExitCode != 0 || observed.StandardOutput.Trim() != profile.Data.SdkVersion)
            throw new EvidenceWriteException("Actual consumer SDK resolution differs from the approved SDK profile.");
        return observed.StandardOutput.Trim();
    }

    private static void RequireMutationBudget(byte[] bytes, TimeSpan timeout)
    {
        using var document = ProducerJson.Read(bytes);
        var policy = document.RootElement.GetProperty("policy");
        foreach (var (name, max) in new[] { ("maxWorkers", 1), ("mutationCap", 100), ("overallDeadlineSeconds", Math.Min(600, (int)timeout.TotalSeconds)) })
            if (!policy.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number < 1 || number > max)
                throw new EvidenceWriteException("Explicit approved one-worker/100-mutant/10-minute policy is missing or exceeded.");
    }

    internal static bool Inside(string path, string root) => InstalledToolFiles.PathComparer.Equals(Path.GetFullPath(path), Path.GetFullPath(root)) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string EnvironmentIdentity()
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            rows.Add((string)entry.Key, (string)entry.Value!);
        return InstalledToolFiles.InventoryIdentity(rows);
    }

    // This class has no injectable owner and retains each exact creator result, not a copied DTO.
    internal sealed class ActualExecutor(InstalledToolFiles files, IReadOnlyDictionary<string, string> environment, string gitExecutable, ICoordinatedEffectAdmission? actualAdmission = null, string? actualRunId = null, LinuxScopeSelection? selectedLinuxScope = null) : ICommandExecutor
    {
        private readonly ProcessCommandExecutor _owner = new();
        internal bool OwnsScopedFiles(InstalledToolFiles actualFiles) =>
            ReferenceEquals(files, actualFiles) && selectedLinuxScope is not null && actualAdmission is not null && actualRunId is not null;
        private readonly Dictionary<CommandResult, BoundedCommandCaptureResult> _actual = new(ReferenceEqualityComparer.Instance);
        internal sealed record ActualCall(CommandInvocation Invocation, CommandResult Result, BoundedCommandCaptureResult Capture);
        private readonly List<ActualCall> _calls = [];
        internal IReadOnlyList<ActualCall> Calls => _calls;
        private long _capturedBytes;
        private readonly List<BoundedCommandCaptureResult> _retainedOriginalCaptures = [];
        private CommandInvocation? _main;
        private Func<CommandResult, Task>? _complete;
        internal InstalledToolInvocation? MainBinding { get; private set; }
        internal void BindMain(CommandInvocation original, InstalledToolInvocation binding)
        {
            if (_main is not null || MainBinding is not null) throw new EvidenceWriteException("Main invocation is already bound.");
            _main = original; MainBinding = binding; _complete = binding.CompleteAsync;
        }
        public async Task<CommandResult> ExecuteAsync(CommandInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken)
        {
            files.AdmitCaptureCall();
            if (_calls.Count >= 64) throw new EvidenceWriteException("Finite actual process-observation count exceeds64.");
            if (_capturedBytes > 112L * 1024 * 1024) throw new EvidenceWriteException("Insufficient cumulative128MiB complete process-capture credit.");
            if (files.IsClosing && !IsClosingIdentityAudit(invocation)) throw new EvidenceWriteException("No new tool/SDK forward work after terminal cleanup begins.");
            var remaining = files.Remaining;
            var limit = remaining < timeout ? remaining : timeout;
            if (limit > TimeSpan.FromMinutes(15)) limit = TimeSpan.FromMinutes(15);
            var effectiveEnvironment = new Dictionary<string, string>(environment, StringComparer.Ordinal);
            foreach (var pair in invocation.Environment ?? new Dictionary<string, string>()) effectiveEnvironment[pair.Key] = pair.Value;
            var effective = invocation with
            {
                FileName = invocation.FileName == "git" ? gitExecutable : invocation.FileName,
                Arguments = invocation.FileName == "git" ? ["-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "--no-optional-locks", .. invocation.Arguments] : invocation.Arguments,
                Environment = effectiveEnvironment
            };
            var selectedBudget = ReferenceEquals(invocation, _main) ? (IOwnedDuplexBudget)files : new NestedBudget(files, files.IsClosing);
            Task<BoundedCommandCaptureResult> StartActual() => OperatingSystem.IsLinux()
                ? _owner.ExecuteBoundedCaptureAsync(effective, limit, ProducerJson.MaximumBytes, cancellationToken,
                    selectedBudget, selectedLinuxScope ?? throw new EvidenceWriteException("Explicit containing Linux scope required before process birth."))
                : _owner.ExecuteBoundedCaptureAsync(effective, limit, ProducerJson.MaximumBytes, cancellationToken, selectedBudget);
            var actualCapture = actualAdmission is null
                ? StartActual()
                : actualAdmission.EnterAsync(actualRunId ?? throw new EvidenceWriteException("Missing actual containing run."), StartActual);
            var capture = await actualCapture.ConfigureAwait(false);
            _retainedOriginalCaptures.Add(capture);
            // Retain and charge the actual observation before decoding or joining
            // timing can refuse it. Recording facts is not a new external effect.
            var rawOut = capture.StandardOutputBytes; var rawError = capture.StandardErrorBytes;
            _capturedBytes += rawOut.LongLength + rawError.LongLength;
            var command = capture.Command;
            Exception? decodingFailure = null;
            if (!capture.CaptureBoundExceeded)
            {
                var utf8 = new UTF8Encoding(false, true);
                try { command = command with { StandardOutput = utf8.GetString(rawOut), StandardError = utf8.GetString(rawError) }; }
                catch (DecoderFallbackException error) { decodingFailure = error; }
            }
            _actual.Add(command, capture);
            _calls.Add(new(effective, command, capture));
            files.AdmitCaptureBytes(rawOut.LongLength + rawError.LongLength);
            if (decodingFailure is not null)
            {
                _ = files.CleanupRemaining;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(decodingFailure).Throw();
            }
            if ((ReferenceEquals(invocation, _main) || capture.Command.ExitCode != 0 || !capture.Command.OwnedProcessQuiescent) && capture.Timing is { } actualTiming)
            {
                // A normal Linux WORK retirement can settle every actual resource
                // without entering child cleanup. Close this files phase locally;
                // do not fabricate a child stamp or close the containing original.
                if (actualTiming.CleanupStartedTimestamp is null &&
                    capture.OwnershipKind == BoundedCaptureOwnershipKind.LinuxCreationOwnedScope &&
                    capture.RootExitObserved && capture.StandardOutputDrainCompleted && capture.StandardErrorDrainCompleted &&
                    !capture.CaptureBoundExceeded && capture.Command.OwnedProcessQuiescent &&
                    !capture.Command.TimedOut && !capture.Command.Cancelled && capture.Command.ExitCode is not null &&
                    capture.Command.LifecycleDiagnostics is { NativeFailure: null })
                    files.FinishSettledWorkCapture(actualTiming);
                else files.ImportActualCleanup(actualTiming);
            }
            if (ReferenceEquals(invocation, _main))
            {
                var complete = _complete ?? throw new EvidenceWriteException("Original main invocation cannot repeat.");
                _complete = null; await complete(command).ConfigureAwait(false);
            }
            return command;
        }
        // Closed original GitRepositoryIdentityReader grammar; cleanup cannot become a new
        // command executor merely by spelling its executable 'git'. No checkout/write form.
        private static bool IsClosingIdentityAudit(CommandInvocation invocation)
        {
            if (invocation.FileName != "git") return false;
            var a = invocation.Arguments;
            return a.SequenceEqual(new[] { "rev-parse", "HEAD" }) ||
                a.SequenceEqual(new[] { "rev-parse", "HEAD^{tree}" }) ||
                a.SequenceEqual(new[] { "status", "--porcelain=v1", "--untracked-files=all" }) ||
                a.Count == 3 && a[0] == "rev-parse" && a[1] == "--verify" &&
                    a[2].EndsWith("^{commit}", StringComparison.Ordinal) && InstalledToolFiles.ObjectId(a[2][..^9]) ||
                a.Count == 4 && a[0] == "merge-base" && a[1] == "--is-ancestor" &&
                    InstalledToolFiles.ObjectId(a[2]) && InstalledToolFiles.ObjectId(a[3]);
        }
        // Successful ordinary preflight child close remains WORK. Only the exact fixed read-only
        // Git audit may be born after finalization, inside the SAME shrinking cleanup allowance.
        private sealed class NestedBudget(InstalledToolFiles original, bool cleanupAudit) : IOwnedDuplexBudget, IOwnedFailureCleanup, IOwnedBudgetUnknownRetention
        {
            public void BeginFailureCleanup() => original.BeginFailureCleanup();
            public void PreserveUnknown() => original.PreserveUnknown();
            public long OriginalStarted => original.OriginalStarted;
            public TimeSpan WholeLimit => original.WholeLimit;
            public TimeSpan WorkLimit => original.WorkLimit;
            public CancellationToken Token => cleanupAudit ? CancellationToken.None : original.Token;
            public void CheckWork() { if (cleanupAudit) _ = original.CleanupRemaining; else original.CheckWork(); }
            public TimeSpan CleanupRemaining => cleanupAudit ? original.CleanupRemaining : original.Remaining;
        }
        internal byte[] OriginalOutput(CommandResult command) => _actual.TryGetValue(command, out var proof)
            ? proof.StandardOutputBytes : throw new EvidenceWriteException("Foreign result cannot supply actual diff bytes.");
        internal bool Known(CommandResult command) => _actual.TryGetValue(command, out var proof) && !proof.CaptureBoundExceeded &&
            proof.RootExitObserved && proof.StandardOutputDrainCompleted && proof.StandardErrorDrainCompleted && command.OwnedProcessQuiescent &&
            !command.TimedOut && !command.Cancelled && command.ExitCode is not null;
    }
}
