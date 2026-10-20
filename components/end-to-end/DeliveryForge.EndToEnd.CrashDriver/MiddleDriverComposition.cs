using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Review;
using DeliveryForge.Scheduling;
using GateOutcome = DeliveryForge.Evidence.GateOutcome;

namespace DeliveryForge.EndToEnd.CrashDriver;

internal static class MiddleDriverComposition
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static bool Supports(string stage) => stage is "command-completed" or "gate-receipt-written" or "review-registration" or "review-receipt";
    internal static TimeSpan Remaining(DriverInput input, int maximumSeconds, bool cleanup = false)
    {
        var started = input.OriginalCaseStarted ?? throw new InvalidOperationException("Middle original clock is unavailable.");
        var total = input.Stage == "gate-receipt-written" ? 540 : 120;
        var left = TimeSpan.FromSeconds(total - (cleanup ? 0 : 20)) - Stopwatch.GetElapsedTime(started);
        if (left <= TimeSpan.Zero) throw new TimeoutException("Original middle case allowance expired.");
        return left < TimeSpan.FromSeconds(maximumSeconds) ? left : TimeSpan.FromSeconds(maximumSeconds);
    }
    internal static async Task RunAsync(DriverInput input, RepositoryContext repository, CancellationToken token)
    {
        if (!Supports(input.Stage)) throw new ArgumentException("Unknown middle boundary.");
        _ = Remaining(input, 1);
        if (input.Mode == "recover") { await Recover(input, token); return; }
        var subject = input.Stage == "gate-receipt-written" ? "Subject/Calculator.cs" : "subject.txt";
        var plan = await Freeze(input, repository, subject, token);
        var planPath = await new AppendOnlyContractStore(Path.Combine(input.ArtifactRoot, "middle-plan-contracts")).WriteImmutableAsync(plan.PlanContractBytes, token);
        var bundlePath = Path.Combine(input.ArtifactRoot, "middle-plan-bundle.json");
        await WriteBytes(bundlePath, plan.CanonicalBytes, token);
        var files = new List<string> { planPath, bundlePath };
        var runId = "middle-" + input.Nonce;
        var store = new RunStore(Path.Combine(input.RuntimeRoot, "middle-receipts-" + input.Nonce));
        await using var processes = new ProcessControl();
        var host = new FixtureHost(processes, false);
        var worktrees = new WorktreeManager();
        using var execution = new ExecutionCoordinator(store, worktrees, processes, host);
        LocalSingleScheduler? scheduler = null;
        string boundary; string? childOutcome = null; string? gateOutcome = null;
        var reviewerCalls = 0; var ownAcceptance = false; var trxPassed = 0; var trxFailed = 0;
        ProtectedReviewFixtureBootstrap? review = null;
        RegisteredReviewRequest? registered = null; IssuedReviewerCapability? issued = null;
        ReviewTransitionReceiver? receiver = null; ReviewAcceptanceContext? reviewContext = null;
        ReviewRequest? reviewRequest = null; ValidatedContract? evidencePolicy = null;
        try
        {
            if (input.Stage == "command-completed")
            {
                var worker = new FrozenWorkerPlan(runId, plan.ContractIdentity, plan.BaseCommit, plan.Repository,
                    Path.Combine(input.RuntimeRoot, "repository"), Path.Combine(input.RuntimeRoot, "middle-worktree-" + input.Nonce),
                    Path.Combine(input.RuntimeRoot, "middle-results-" + input.Nonce), Guid.NewGuid().ToString("N"),
                    [subject], ["excluded"], "fixture-middle-native-host", "1", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
                var slots = new ResourceSlots(2, 2, 4096, 1, 1, 0);
                var budget = ResourceBudget.Measure(slots, new("parent-approved-middle-native-fixture", true, slots, TimeSpan.FromSeconds(90), 1000, 0m));
                scheduler = LocalSingleScheduler.Create(plan, worker, new(new(1, 1, 512, 0, 0, 0), [], TimeSpan.FromSeconds(90), 100, 0m),
                    budget, store, execution, worktrees, new GitRepositoryContextReader(), TimeSpan.FromSeconds(2));
                var prepared = await scheduler.ProvisionAndPrepareAsync(await scheduler.ReserveAsync(token), token);
                var accepted = Accepted(prepared, input); host.Bind(accepted);
                await execution.AcceptAsync(accepted, token);
                var child = await execution.StartTaskProcessAsync(runId, new(Environment.ProcessPath!,
                    [Path.GetFullPath(Environment.GetCommandLineArgs()[0]), "child", input.Stage, input.RuntimeRoot, input.ArtifactRoot,
                        input.Nonce, input.RuntimeBaseCommit, input.IntegratedBaseCommit, "11019"]), token);
                var started = Stopwatch.GetTimestamp();
                var limit = Remaining(input, 5);
                await child.Process.WaitForExitAsync(token).WaitAsync(limit, token);
                var samples = new List<ProcessControlResult>();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (Stopwatch.GetElapsedTime(started) >= limit || samples.Count >= 256) throw new TimeoutException("Original completed child did not prove timely exit.");
                    var sample = processes.Observe(child.Identity); samples.Add(sample);
                    if (Stopwatch.GetElapsedTime(started) >= limit) throw new TimeoutException("Original child observation arrived late.");
                    if (sample.Outcome == ProcessControlOutcome.AlreadyExited) { childOutcome = sample.Outcome.ToString(); break; }
                    await Task.Delay(10, token);
                }
                if (child.Process.ExitCode != 0) throw new InvalidOperationException("Actual finite command did not complete successfully.");
                boundary = Path.Combine(input.ArtifactRoot, "child-result-" + input.Nonce + ".json");
                using var result = JsonDocument.Parse(await Read(boundary, input, token));
                if (result.RootElement.GetProperty("nonce").GetString() != input.Nonce) throw new IOException("Actual child result nonce differs.");
                var observation = Path.Combine(input.ArtifactRoot, "original-completed-child.json");
                await Write(observation, new
                {
                    fixture = true,
                    child.Identity,
                    samples,
                    originalNaturalExit = true,
                    resultSha256 = Hash(await Read(boundary, input, token))
                }, token);
                files.Add(boundary); files.Add(observation);
            }
            else if (input.Stage == "gate-receipt-written")
            {
                var actual = await RunActualGate(input, token);
                boundary = actual.ReceiptPath; gateOutcome = actual.Outcome.ToString();
                var gateDirectory = GateArtifactDirectory(actual.ReceiptPath, input);
                var trx = Path.Combine(gateDirectory, actual.ArtifactHashes.Keys.Single(path => path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)));
                (trxPassed, trxFailed) = Counts(trx);
                if (actual.Outcome != GateOutcome.Incomplete || actual.Execution.ExitCode != 0 || trxPassed != 1 || trxFailed != 0 ||
                    actual.SourceChanged || actual.Execution.TimedOut || actual.Execution.Cancelled || !actual.Execution.OwnedProcessQuiescent)
                    throw new InvalidOperationException("Genuine advisory test/TRX gate prerequisite was not established.");
                files.Add(boundary); files.AddRange(actual.ArtifactHashes.Keys.Select(name => Path.Combine(gateDirectory, name)));
                var persistedReceipt = ContractValidator.ParseAndValidate(await Read(boundary, input, token));
                files.Add(Path.Combine(input.ArtifactRoot, "host-owned-gate-receipts", ".provenance",
                    persistedReceipt.Identity.Replace(':', '-') + ".entry"));
                files.Add(Path.Combine(input.ArtifactRoot, "actual-driver-gate-configuration.json"));
                files.Add(Path.Combine(input.ArtifactRoot, "actual-driver-gate-provenance.json"));
                var gateObservation = Path.Combine(input.ArtifactRoot, "actual-driver-gate-observation.json");
                await Write(gateObservation, new
                {
                    fixture = true,
                    productionCertified = false,
                    actual,
                    trx,
                    trxPassed,
                    trxFailed,
                    originalCaseStarted = input.OriginalCaseStarted,
                    totalSeconds = 540,
                    limitation = "Actual SDK/test/TRX/canonical advisory gate; no required producer authority."
                }, token);
                files.Add(gateObservation);
            }
            else
            {
                var policyBytes = EvidencePolicy(input.Nonce);
                var evidencePath = Path.Combine(input.ArtifactRoot, "middle-evidence-policy.json"); await WriteBytes(evidencePath, policyBytes, token); files.Add(evidencePath);
                evidencePolicy = ContractValidator.ParseAndValidate(policyBytes);
                review = await ReviewAuthorityHost.CreateProtectedGitContractFixtureAsync(new(runId, plan.PlanContractBytes, policyBytes,
                    repository.RepositoryRoot, Path.Combine(repository.RepositoryRoot, ".git"), FindGit(), "policy/review.json", 1024 * 1024, Remaining(input, 15)), token);
                var patch = await GitPatchIdentity.ComputeAsync(review.ObjectView, plan.BaseCommit, repository.HeadCommit, plan.ContractIdentity,
                    new Dictionary<string, string> { ["plan-contract"] = Hash(plan.PlanContractBytes) }, token);
                reviewContext = Context(review.RootContext, "middle-request-" + input.Nonce);
                receiver = review.Host.CreateTransitionReceiver(reviewContext);
                var capability = new ReviewerCapability("explicit-middle-external-review-fixture", "1.0.0", "claude", true, true, false, ["source"]);
                reviewRequest = new(patch, "gpt", capability, "source", review.Policy.PolicyIdentity);
                registered = review.Host.RegisterRequest(reviewContext.RequestIdentity, reviewRequest, reviewContext);
                var registrationPath = Path.Combine(input.ArtifactRoot, "actual-review-registration.json");
                await Write(registrationPath, new
                {
                    fixture = true,
                    registered.RequestIdentity,
                    context = reviewContext,
                    request = reviewRequest,
                    evidencePolicyPath = evidencePath,
                    sourceRoot = repository.RepositoryRoot,
                    limitation = "Diagnostic public context/request DATA only; no serialized opaque registration or proof."
                }, token);
                files.Add(registrationPath); boundary = registrationPath;
                if (input.Stage == "review-receipt")
                {
                    issued = review.Host.IssueCapability(capability, "middle-capability-" + input.Nonce, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(120));
                    boundary = await IssueActualReview(input, review, registered, issued, reviewContext, receiver, evidencePolicy, files, token);
                    reviewerCalls++; ownAcceptance = true;
                }
            }
            var history = await store.RecoverAsync(runId, token);
            files.AddRange(history.Records.Select(record => record.Path));
            files.AddRange((await store.RecoverAsync(runId + "-scheduler", token)).Records.Select(record => record.Path));
            var durable = files.Distinct(StringComparer.Ordinal).Select(path => new { path, sha256 = HashFile(path) }).ToArray();
            await Write(Path.Combine(input.ArtifactRoot, "setup-complete-" + input.Nonce + ".json"), new { fixture = true, input.Stage, input.Nonce }, token);
            await Write(Path.Combine(input.ArtifactRoot, "ready-" + input.Stage + "-" + input.Nonce + ".json"), new
            {
                fixture = true,
                productionCertified = false,
                input.Stage,
                input.Nonce,
                frozenBaseCommit = plan.BaseCommit,
                planContractPath = planPath,
                planBundlePath = bundlePath,
                storeRoot = store.RootPath,
                runId,
                boundaryArtifactPath = boundary,
                durableFiles = durable,
                reviewerCallsBeforeCrash = reviewerCalls,
                ownPinnedAcceptanceObserved = ownAcceptance,
                originalChildOutcome = childOutcome,
                actualGateOutcome = gateOutcome,
                actualTrxPassed = trxPassed,
                actualTrxFailed = trxFailed,
                originalCaseStarted = input.OriginalCaseStarted,
                input.IntegratedBaseCommit
            }, token);
            await WaitRelease(input, token);
            if (input.Stage == "review-registration")
            {
                var now = DateTimeOffset.UtcNow;
                issued = review!.Host.IssueCapability(reviewRequest!.Capability, "middle-calibration-capability-" + input.Nonce, now.AddSeconds(-1), now.AddSeconds(120));
                _ = await IssueActualReview(input, review, registered!, issued, reviewContext!, receiver!, evidencePolicy!, files, token);
                ownAcceptance = true;
            }
            if (scheduler is not null && !await scheduler.StopAsync(token)) throw new InvalidOperationException("Original normal-release scheduler closure is uncertain.");
            await Write(Path.Combine(input.ArtifactRoot, "normal-release-result.json"), new
            {
                fixture = true,
                input.Stage,
                actualReadyReached = true,
                normalReleaseInsteadOfKill = true,
                ownPinnedAcceptanceObserved = ownAcceptance,
                actualGateOutcome = gateOutcome,
                originalChildOutcome = childOutcome
            }, token);
        }
        finally { review?.Dispose(); }
    }

    private static async Task<FrozenPlan> Freeze(DriverInput input, RepositoryContext repository, string subject, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new PlanningRequest("fixture/delivery-forge", "#11", "implement", "Actual middle durable crash boundary",
            [subject], ["excluded"], ["Public owner proof only; fixture external responses"], input.Stage.StartsWith("review-", StringComparison.Ordinal) ? "pr" : "implement");
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(await new GitRepositoryContextReader().ReadFileAsync(repository, subject, token), now, [])];
        return PlanFreezer.Freeze(new(request, repository, evidence, [new(subject, "fixture", "actual middle durable boundary")], [new("#11", [], "root")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], IntakePlanner.Assess(request, evidence)), "middle-driver-" + input.Nonce, now);
    }
    private static byte[] EvidencePolicy(string nonce)
    {
        var node = new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["kind"] = "evidence-policy",
            ["policyId"] = "middle-" + nonce,
            ["requiredGates"] = new JsonArray("test"),
            ["authorizedCeiling"] = "pr",
            ["requireIndependentReview"] = true,
            ["allowedNotApplicable"] = new JsonArray(),
            ["createdAt"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        };
        node["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(node.ToJsonString()));
        return CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }
    private static ReviewAcceptanceContext Context(ReviewAuthorityRootContext root, string request) => new(root.RunIdentity,
        request, root.PlanIdentity, root.EvidencePolicyIdentity, root.ReviewPolicyContentIdentity, root.ReviewPolicySourceRevision,
        root.ReviewPolicyAuthorityKind, root.SourceAuthorityIdentity, root.SourceRoot, root.ProtectedBaseCommit, root.ProtectedObjectViewIdentity);
    private static async Task<string> IssueActualReview(DriverInput input, ProtectedReviewFixtureBootstrap bootstrap,
        RegisteredReviewRequest registered, IssuedReviewerCapability capability, ReviewAcceptanceContext context,
        ReviewTransitionReceiver receiver, ValidatedContract policy, List<string> files, CancellationToken token)
    {
        var request = registered.Request;
        var now = DateTimeOffset.UtcNow;
        var raw = Path.Combine(input.ArtifactRoot, "actual-external-fixture-review-raw.json");
        await Write(raw, new
        {
            fixture = true,
            actualModelCall = false,
            oppositeFamilyCertification = false,
            registered.RequestIdentity,
            request,
            verdict = "ship",
            findings = Array.Empty<ReviewFinding>(),
            completedAt = now
        }, token);
        var digest = Hash(await Read(raw, input, token));
        var execution = bootstrap.Host.RecordExecution(registered, capability, "middle-execution-" + input.Nonce,
            true, true, digest, "ship", now);
        var normalized = ReviewNormalizer.Normalize(request, ReviewResult.Complete(request.Identity, "claude", "PASS", [], digest, now),
            bootstrap.Policy, capability, execution);
        if (normalized.Outcome != ReviewOutcome.Pass || !normalized.AuthorityAccepted || !normalized.Fixture || normalized.ProductionAccepted)
            throw new InvalidOperationException("Actual fixture review chain refused or was promoted: " + normalized.Reason);
        var bytes = ReviewReceiptSerializer.Serialize(ReviewReceiptSerializer.Create(normalized));
        var contract = ContractValidator.ParseAndValidate(bytes);
        var receipt = await new AppendOnlyContractStore(Path.Combine(input.ArtifactRoot, "actual-middle-review-contracts")).WriteImmutableAsync(bytes, token);
        bootstrap.Host.IssueAcceptance(contract, normalized, context, ReviewEvidenceRole.Independent);
        var transition = new TransitionEvidence(AuthorizationCeiling.Pr, request.Identity.BaseCommit, request.Identity.HeadCommit,
            request.Identity.HeadTree, policy, ReviewReceipt: contract);
        bootstrap.Host.EnsureContractFixtureTransition(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, transition, receiver);
        var denied = false;
        try { WorkflowTransition.EnsureAllowed(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, transition, receiver); }
        catch (InvalidWorkflowTransitionException) { denied = true; }
        if (!denied) throw new InvalidOperationException("Test review became production transition authority.");
        var ledger = Path.Combine(input.ArtifactRoot, "actual-middle-reviewer-fixture-ledger.json");
        await Write(ledger, new
        {
            fixture = true,
            actualModelCalls = 0,
            calls = new[] { new { registered.RequestIdentity, raw, digest, capability.ProofIdentity } },
            ownPinnedTestAcceptanceObserved = true,
            productionTransitionDenied = denied
        }, token);
        files.Add(raw); files.Add(receipt); files.Add(ledger);
        return receipt;
    }

    private static async Task<GateRunResult> RunActualGate(DriverInput input, CancellationToken token)
    {
        var host = Environment.ProcessPath ?? throw new PlatformNotSupportedException("Original real dotnet host unavailable.");
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (!Path.IsPathFullyQualified(cache) || !Directory.Exists(cache)) throw new PlatformNotSupportedException("Existing local locked cache unavailable.");
        Plain(cache);
        var source = Path.Combine(input.RuntimeRoot, "repository");
        var clone = Path.Combine(input.RuntimeRoot, "actual-gate-" + input.Mode + "-" + input.Nonce);
        if (Directory.Exists(clone) || File.Exists(clone)) throw new IOException("Actual cold gate clone collision.");
        await CopyColdRepository(input, source, clone, token);
        Plain(clone); Plain(Path.Combine(clone, ".git"));
        var readerContext = await new GitRepositoryContextReader().ReadAsync(clone, "refs/heads/main", token);
        if (readerContext.Commit != input.RuntimeBaseCommit || readerContext.HeadCommit != input.RuntimeBaseCommit)
            throw new InvalidOperationException("Actual tool clone differs from frozen source.");
        // This new local clone contains tracked source only, never prerequisite DLLs/obj/TRX.
        if (Directory.EnumerateFiles(clone, "*.dll", SearchOption.AllDirectories).Any()) throw new IOException("Cold clone has generated DLLs.");
        var executor = new ActualExecutor(input, clone, host, cache);
        var detection = await new ToolCapabilityDetector(executor).DetectAsync(new("dotnet", ExecutableSource.TrustedExplicit,
            host, clone, null, ["--version"], ["--help"], "10.0.401", "trx-v1", true), token);
        if (!detection.Capability.Supported || detection.Capability.Version != "10.0.401") throw new InvalidOperationException("Actual pinned test SDK is unavailable.");
        Successful(detection.VersionResult!, "actual SDK version"); Successful(detection.HelpResult!, "actual SDK help");
        var project = Path.Combine(clone, "Subject.Tests", "Subject.Tests.csproj");
        var restored = await executor.ExecuteAsync(new(host, ["restore", project, "--locked-mode", "--source", cache, "--disable-build-servers",
            "-m:1", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false"], clone), Remaining(input, 90), token);
        Successful(restored, "actual driver locked restore");
        var loggerDirectory = Path.Combine(clone, ".fixture-trx"); Directory.CreateDirectory(loggerDirectory);
        var trx = Path.Combine(loggerDirectory, "driver-" + input.Nonce + ".trx");
        if (File.Exists(trx)) throw new IOException("Actual driver TRX path already exists.");
        var invocation = new CommandInvocation(host, ["test", project, "--no-restore", "-c", "Release", "--disable-build-servers",
            "-m:1", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "--logger", "trx;LogFileName=" + trx], clone);
        var policy = await new ProtectedGitPolicyResolver(executor, clone, input.RuntimeBaseCommit).ResolveAsync(".forge/evidence-policy.json", token);
        var reader = new GitRepositoryIdentityReader(executor, clone, input.RuntimeBaseCommit);
        var before = await reader.ReadAsync(token);
        var configuration = JsonSerializer.SerializeToUtf8Bytes(new
        {
            fixture = true,
            invocation,
            originalCaseStarted = input.OriginalCaseStarted,
            totalSeconds = 540,
            input.Mode,
            input.Nonce,
            frozenBase = input.RuntimeBaseCommit
        });
        var configPath = Path.Combine(input.ArtifactRoot, "actual-driver-gate-configuration.json"); await WriteBytes(configPath, configuration, token);
        var authoritativeHost = new AuthoritativeEvaluationHost(
            executor, reader, clone, input.RuntimeBaseCommit,
            Path.Combine(input.ArtifactRoot, "host-owned-gate-receipts"));
        var gate = await authoritativeHost.RunAsync(new("test", policy, detection.Capability,
            EvidenceAdapterKind.DotNet, Hash(configuration), before, invocation, "actual-middle-driver",
            Remaining(input, 75), Path.Combine(input.ArtifactRoot, "actual-driver-gates")), token);
        var receipt = ContractValidator.ParseAndValidate(await Read(gate.ReceiptPath, input, token));
        if (!authoritativeHost.ReceiptProvenance.Contains(receipt.Identity) ||
            authoritativeHost.ReadReceipt(receipt.Identity).Identity != receipt.Identity)
            throw new IOException("Authoritative host could not re-open its durable receipt provenance.");
        var provenance = Path.Combine(input.ArtifactRoot, "actual-driver-gate-provenance.json");
        await Write(provenance, new
        {
            fixture = true,
            productionCertified = false,
            clone,
            detection,
            restored,
            originalCaseStarted = input.OriginalCaseStarted,
            actualSdkHash = HashFile(host),
            actualProjectHash = HashFile(project),
            actualLockHash = HashFile(Path.Combine(clone, "Subject.Tests", "packages.lock.json")),
            actualPolicyHash = HashFile(Path.Combine(clone, ".forge", "evidence-policy.json")),
            trx,
            configurationPath = configPath,
            calls = executor.Calls,
            limitation = "Actual fresh cold test/TRX gate in distinct original/calibration clone; SAME case clock, no certified quality PASS."
        }, token);
        return gate;
    }

    private sealed class ActualExecutor(DriverInput input, string root, string host, string cache) : ICommandExecutor
    {
        private readonly ProcessCommandExecutor _actual = new();
        public List<object> Calls { get; } = [];
        public async Task<CommandResult> ExecuteAsync(CommandInvocation invocation, TimeSpan timeout, CancellationToken token)
        {
            _ = Remaining(input, 1); Plain(root);
            if (!string.Equals(Path.GetFullPath(invocation.WorkingDirectory), Path.GetFullPath(root), PathComparison())) throw new IOException("Actual executor root changed.");
            var git = invocation.FileName == "git" || string.Equals(invocation.FileName, FindGit(), PathComparison());
            if (!git && (invocation.FileName != host || invocation.Arguments.Count == 0 ||
                invocation.Arguments[0] is not ("--version" or "--help" or "restore" or "test"))) throw new IOException("Actual driver executor escaped fixed tool scope.");
            var environment = new Dictionary<string, string>(invocation.Environment ?? new Dictionary<string, string>());
            var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            if (git)
            {
                var metadata = Path.Combine(root, ".git");
                environment["GIT_DIR"] = metadata; environment["GIT_WORK_TREE"] = root; environment["GIT_COMMON_DIR"] = metadata;
                environment["GIT_OBJECT_DIRECTORY"] = Path.Combine(metadata, "objects"); environment["GIT_INDEX_FILE"] = Path.Combine(metadata, "index");
                environment["GIT_CONFIG"] = Path.Combine(metadata, "config"); environment["GIT_CONFIG_COUNT"] = "0"; environment["GIT_CONFIG_PARAMETERS"] = "";
                environment["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = ""; environment["GIT_GRAFT_FILE"] = nullDevice; environment["GIT_NO_REPLACE_OBJECTS"] = "1";
                environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = nullDevice;
                environment["GIT_CONFIG_SYSTEM"] = nullDevice; environment["GIT_TERMINAL_PROMPT"] = "0"; environment["GIT_NO_LAZY_FETCH"] = "1";
            }
            else
            {
                environment["DOTNET_ROOT"] = Path.GetDirectoryName(host)!; environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
                var cliHome = Path.Combine(input.ArtifactRoot, "dotnet-cli-home-" + input.Mode + "-" + input.Nonce);
                Plain(cliHome); Directory.CreateDirectory(cliHome); environment["DOTNET_CLI_HOME"] = cliHome;
                environment["DOTNET_PROCESSOR_COUNT"] = "2"; environment["DOTNET_GCHeapHardLimit"] = "0x100000000";
                environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1"; environment["MSBUILDDISABLENODEREUSE"] = "1";
                environment["UseSharedCompilation"] = "false"; environment["BuildInParallel"] = "false"; environment["NUGET_PACKAGES"] = cache;
                environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"; environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            }
            var limit = Remaining(input, 90); if (timeout < limit) limit = timeout;
            var captured = await _actual.ExecuteBoundedCaptureAsync(invocation with { FileName = git ? FindGit() : host, Environment = environment },
                limit, 1024 * 1024, token);
            Calls.Add(new { invocation, captured, originalCaseStarted = input.OriginalCaseStarted });
            if (captured.CaptureBoundExceeded || !captured.Command.OwnedProcessQuiescent || !captured.RootExitObserved ||
                !captured.StandardOutputDrainCompleted || !captured.StandardErrorDrainCompleted) throw new IOException("Actual driver tool capture remained uncertain or exceeded bounds.");
            _ = Remaining(input, 1);
            return captured.Command;
        }
    }

    private static async Task Recover(DriverInput input, CancellationToken token)
    {
        using var ready = JsonDocument.Parse(await Read(Path.Combine(input.ArtifactRoot, "ready-" + input.Stage + "-" + input.Nonce + ".json"), input, token));
        var root = ready.RootElement;
        if (root.GetProperty("stage").GetString() != input.Stage || root.GetProperty("nonce").GetString() != input.Nonce ||
            root.GetProperty("originalCaseStarted").GetInt64() != input.OriginalCaseStarted)
            throw new IOException("Fresh recovery experiment/clock differs.");
        var reopened = new List<object>();
        foreach (var file in root.GetProperty("durableFiles").EnumerateArray())
        {
            var path = file.GetProperty("path").GetString()!;
            var expected = file.GetProperty("sha256").GetString(); var actual = Hash(await Read(path, input, token));
            if (actual != expected) throw new IOException("Actual middle durable artifact changed after crash.");
            reopened.Add(new { path, beforeKillSha256 = expected, afterCrashSha256 = actual });
        }
        var planBytes = await Read(root.GetProperty("planContractPath").GetString()!, input, token);
        _ = ContractValidator.ParseAndValidate(planBytes);
        var storeRoot = root.GetProperty("storeRoot").GetString()!; Within(storeRoot, input);
        var runId = root.GetProperty("runId").GetString();
        if (runId != "middle-" + input.Nonce) throw new IOException("Fresh recovery run identity differs.");
        var store = new RunStore(storeRoot);
        var before = await store.RecoverAsync(runId, token);
        await using var processes = new ProcessControl();
        var host = new FixtureHost(processes, true);
        using var execution = new ExecutionCoordinator(store, new WorktreeManager(), processes, host);
        var refusal = ""; string? childOutcome = null; var rejected = false; var productionDenied = false;
        var gateOutcome = root.GetProperty("actualGateOutcome").ValueKind == JsonValueKind.Null ? null : root.GetProperty("actualGateOutcome").GetString();
        var registrationCount = before.Records.Count(record => record.Kind == "process-registered");
        if (input.Stage == "command-completed")
        {
            var registration = store.ReadPayload<ProcessRegistration>(before.Records.Single(record => record.Kind == "process-registered"));
            var fresh = processes.Observe(registration.Identity);
            childOutcome = fresh.Outcome.ToString();
            if (fresh.Outcome != ProcessControlOutcome.IdentityUnknown) throw new InvalidOperationException("Fresh controller adopted old completed child.");
            var stopped = await execution.ReconcileStopAsync(runId!, Remaining(input, 2), token);
            if (stopped.Quiescent || stopped.Processes.Any(sample => sample.Outcome != ProcessControlOutcome.IdentityUnknown))
                throw new InvalidOperationException("Stored completed child/root bytes authorized fresh cleanup.");
            refusal = "fresh-controller-completed-native-identity-unknown";
        }
        else if (input.Stage == "gate-receipt-written")
        {
            var verified = await VerifyRequiredGateRefusal(input, root.GetProperty("boundaryArtifactPath").GetString()!, token);
            gateOutcome = verified.Outcome;
            if (root.GetProperty("actualGateOutcome").GetString() != gateOutcome ||
                root.GetProperty("actualTrxPassed").GetInt32() != verified.Passed || root.GetProperty("actualTrxFailed").GetInt32() != verified.Failed)
                throw new IOException("Ready diagnostic gate fields differ from freshly verified canonical/raw bytes.");
            refusal = "stored-gate-receipt-is-not-required-pass";
        }
        else
        {
            using var registration = JsonDocument.Parse(await Read(Path.Combine(input.ArtifactRoot, "actual-review-registration.json"), input, token));
            var oldContext = JsonSerializer.Deserialize<ReviewAcceptanceContext>(registration.RootElement.GetProperty("context").GetRawText(), Json)
                ?? throw new IOException("Original public context DATA is missing.");
            var source = Path.Combine(input.RuntimeRoot, "repository");
            var policyBytes = await Read(registration.RootElement.GetProperty("evidencePolicyPath").GetString()!, input, token);
            using var fresh = await ReviewAuthorityHost.CreateProtectedGitContractFixtureAsync(new(oldContext.RunIdentity,
                planBytes, policyBytes, source, Path.Combine(source, ".git"), FindGit(), "policy/review.json", 1024 * 1024, Remaining(input, 15)), token);
            // Public context is data; opaque registrations/capabilities/executions are not imported.
            var importedContextDenied = false;
            try { _ = fresh.Host.CreateTransitionReceiver(oldContext); }
            catch (InvalidOperationException) { importedContextDenied = true; }
            if (!importedContextDenied) throw new InvalidOperationException("Old context rebound into a different issuer-owned source.");
            if (input.Stage == "review-receipt")
            {
                var oldReceipt = ContractValidator.ParseAndValidate(await Read(root.GetProperty("boundaryArtifactPath").GetString()!, input, token));
                var currentContext = Context(fresh.RootContext, oldContext.RequestIdentity);
                var currentReceiver = fresh.Host.CreateTransitionReceiver(currentContext);
                var repository = await new GitRepositoryContextReader().ReadAsync(source, "refs/heads/main", token);
                var transition = new TransitionEvidence(AuthorizationCeiling.Pr, repository.Commit, repository.HeadCommit, repository.HeadTree,
                    ContractValidator.ParseAndValidate(policyBytes), ReviewReceipt: oldReceipt);
                try { fresh.Host.EnsureContractFixtureTransition(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, transition, currentReceiver); }
                catch (InvalidWorkflowTransitionException) { rejected = true; }
                try { WorkflowTransition.EnsureAllowed(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, transition, currentReceiver); }
                catch (InvalidWorkflowTransitionException) { productionDenied = true; }
                if (!rejected || !productionDenied) throw new InvalidOperationException("Old receipt authorized a fresh or production consumer.");
                refusal = "fresh-issuer-rejects-original-review-receipt";
            }
            else refusal = "fresh-issuer-has-no-original-registration";
        }
        var after = await store.RecoverAsync(runId!, token);
        if (after.Records.Any(record => record.Kind is "worker-completed" or "resumed")) throw new InvalidOperationException("Recovery minted completion/resume from stored bytes.");
        await Write(Path.Combine(input.ArtifactRoot, "recovery-result.json"), new
        {
            fixture = true,
            productionCertified = false,
            input.Stage,
            input.Nonce,
            specificSafeRefusal = refusal,
            oldBytesBecameAuthority = false,
            newWorkerLaunches = processes.GetAllOwnedIdentities().Count,
            newCommandLaunches = processes.GetAllOwnedIdentities().Count,
            resumeCalls = host.Ledger.Count(result => result.Action == AgentControlAction.Resume),
            signalOldNativeCalls = 0,
            newGateExecutions = 0,
            newReviewerCalls = 0,
            publicationCalls = 0,
            destructiveCleanupCalls = 0,
            freshControllerOwnedNativeCount = processes.GetAllOwnedIdentities().Count,
            observedWorkerCompletedCount = after.Records.Count(record => record.Kind == "worker-completed"),
            observedProcessRegisteredCount = registrationCount,
            recordedChildFreshControllerOutcome = childOutcome,
            originalDriverPositivelyObservedChildExit = root.GetProperty("originalChildOutcome").GetString() == "AlreadyExited",
            actualGateOutcome = gateOutcome,
            actualTrxPassed = root.GetProperty("actualTrxPassed").GetInt32(),
            actualTrxFailed = root.GetProperty("actualTrxFailed").GetInt32(),
            originalReviewerCallsBeforeCrash = root.GetProperty("reviewerCallsBeforeCrash").GetInt32(),
            originalPinnedTestAcceptanceObserved = root.GetProperty("ownPinnedAcceptanceObserved").GetBoolean(),
            freshPinnedReceiverRejectedOriginalReceipt = rejected,
            productionTransitionDenied = productionDenied,
            independentlyReopenedDurableFiles = reopened,
            hostCallLedger = host.Ledger,
            limitation = "No old PID adoption, SDK/reviewer rerun, publication or original runtime deletion. New source-view Dispose is not old-runtime cleanup."
        }, token);
    }

    private static string GateArtifactDirectory(string receiptPath, DriverInput input)
    {
        Within(receiptPath, input);
        var receiptDirectory = Path.GetDirectoryName(receiptPath)!;
        string gateDirectory;
        if (Path.GetFileName(receiptDirectory) == "receipts")
            gateDirectory = Path.GetDirectoryName(receiptDirectory) ??
                throw new IOException("Public gate artifact directory is missing.");
        else if (string.Equals(
                     Path.GetFullPath(receiptDirectory),
                     Path.GetFullPath(Path.Combine(input.ArtifactRoot, "host-owned-gate-receipts")),
                     PathComparison()))
            gateDirectory = Path.Combine(input.ArtifactRoot, "actual-driver-gates", "test");
        else
            throw new IOException("Public gate receipt directory differs.");
        Within(gateDirectory, input);
        if (!Directory.Exists(gateDirectory)) throw new IOException("Public gate artifact directory is missing.");
        return gateDirectory; // GateRunner stores raw artifacts beside its receipts subdirectory.
    }

    private static async Task<(string Outcome, int Passed, int Failed)> VerifyRequiredGateRefusal(
        DriverInput input, string receiptPath, CancellationToken token)
    {
        var bytes = await Read(receiptPath, input, token);
        var receipt = ContractValidator.ParseAndValidate(bytes);
        if (receipt.SchemaName != "gate-receipt") throw new IOException("Actual boundary is not a canonical gate receipt.");
        var durable = new HostOwnedReceiptStore(Path.Combine(input.ArtifactRoot, "host-owned-gate-receipts"))
            .Read(receipt.Identity);
        if (!durable.CanonicalBytes.Span.SequenceEqual(bytes))
            throw new IOException("Fresh consumer could not verify the durable host receipt provenance.");
        using var document = JsonDocument.Parse(receipt.CanonicalBytes);
        var gate = document.RootElement;
        var artifactDirectory = GateArtifactDirectory(receiptPath, input);
        var source = Path.Combine(input.RuntimeRoot, "repository");
        var host = Environment.ProcessPath ?? throw new PlatformNotSupportedException("Original SDK host is unavailable.");
        Plain(host);
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        // Only fresh read-only Git owner operations here: no SDK probe/test producer rerun.
        var executor = new ActualExecutor(input, source, host, cache);
        var policy = await new ProtectedGitPolicyResolver(executor, source, input.RuntimeBaseCommit).ResolveAsync(".forge/evidence-policy.json", token);
        var actualRepository = await new GitRepositoryIdentityReader(executor, source, input.RuntimeBaseCommit).ReadAsync(token);
        var repositoryReader = new GitRepositoryContextReader();
        var repository = await repositoryReader.ReadAsync(source, input.RuntimeBaseCommit, token);
        var policyBytes = (await repositoryReader.ReadFileAsync(repository, ".forge/evidence-policy.json", token)).Bytes;
        var policyContract = ContractValidator.ParseAndValidate(policyBytes);
        using var sdk = JsonDocument.Parse((await repositoryReader.ReadFileAsync(repository, "global.json", token)).Bytes);
        using var command = JsonDocument.Parse(gate.GetProperty("command").GetString()!);
        var capture = command.RootElement;
        var expectedClone = Path.Combine(input.RuntimeRoot, "actual-gate-barrier-" + input.Nonce);
        Plain(expectedClone);
        var configuration = await Read(Path.Combine(input.ArtifactRoot, "actual-driver-gate-configuration.json"), input, token);
        if (!actualRepository.IsClean || repository.Dirty || actualRepository.BaseCommit != input.RuntimeBaseCommit ||
            actualRepository.HeadCommit != repository.HeadCommit || actualRepository.TreeId != repository.HeadTree ||
            gate.GetProperty("baseCommit").GetString() != actualRepository.BaseCommit ||
            gate.GetProperty("headCommit").GetString() != actualRepository.HeadCommit ||
            gate.GetProperty("treeId").GetString() != actualRepository.TreeId ||
            gate.GetProperty("outcome").GetString() != "INCOMPLETE" || gate.GetProperty("exitCode").GetInt32() != 0 ||
            gate.GetProperty("sourceChanged").GetBoolean() || gate.GetProperty("gateId").GetString() != "test" ||
            policyContract.Identity != policy.PolicyIdentity || gate.GetProperty("policyIdentity").GetString() != policy.PolicyIdentity ||
            capture.GetProperty("policySourceRevision").GetString() != input.RuntimeBaseCommit ||
            capture.GetProperty("policyContentIdentity").GetString() != policy.ContentIdentity || policy.ContentIdentity != Hash(policyBytes) ||
            !policy.RequiredGates.Contains("test", StringComparer.Ordinal) ||
            capture.GetProperty("configurationIdentity").GetString() != Hash(configuration) ||
            capture.GetProperty("fileName").GetString() != host || capture.GetProperty("capabilityExecutable").GetString() != host ||
            capture.GetProperty("executableIdentity").GetString() != HashFile(host) ||
            capture.GetProperty("capabilityTool").GetString() != "dotnet" || capture.GetProperty("capabilityFormat").GetString() != "trx-v1" ||
            !capture.GetProperty("capabilityFixture").GetBoolean() || capture.GetProperty("capabilityVersion").GetString() != "10.0.401" ||
            sdk.RootElement.GetProperty("sdk").GetProperty("version").GetString() != "10.0.401" ||
            capture.GetProperty("scope").GetString() != "actual-middle-driver" ||
            !string.Equals(capture.GetProperty("workingDirectory").GetString(), expectedClone, PathComparison()) ||
            !string.Equals(capture.GetProperty("capabilityProbeWorkingDirectory").GetString(), expectedClone, PathComparison()))
            throw new IOException("Canonical gate own outcome/source/policy/tool/configuration binding differs from fresh owner observations.");
        var rawFiles = new List<object>();
        foreach (var artifact in gate.GetProperty("artifactHashes").EnumerateObject())
        {
            if (Path.GetFileName(artifact.Name) != artifact.Name) throw new IOException("Gate artifact name escaped its public artifact root.");
            var path = Path.Combine(artifactDirectory, artifact.Name);
            var actual = Hash(await Read(path, input, token));
            if (actual != artifact.Value.GetString()) throw new IOException("Canonical gate raw artifact hash differs.");
            rawFiles.Add(new { path, sha256 = actual });
        }
        foreach (var requiredArtifact in new[] { "stdout.txt", "stderr.txt", "evidence.trx" })
            if (!gate.GetProperty("artifactHashes").TryGetProperty(requiredArtifact, out _)) throw new IOException("Genuine gate raw artifact is missing.");
        var counts = Counts(Path.Combine(artifactDirectory, "evidence.trx"));
        if (counts.Passed != 1 || counts.Failed != 0) throw new IOException("Canonical gate raw TRX does not contain the genuine passing test.");
        string? denial = null;
        try
        {
            WorkflowTransition.EnsureAllowed(WorkflowState.Evaluating, WorkflowState.IndependentReview,
                new(AuthorizationCeiling.Implement, actualRepository.BaseCommit, actualRepository.HeadCommit, actualRepository.TreeId,
                    policyContract, GateReceipts: [receipt]));
        }
        catch (InvalidWorkflowTransitionException refused) { denial = refused.Message; }
        if (denial is null) throw new InvalidOperationException("Public required-gate owner accepted the persisted advisory INCOMPLETE receipt.");
        var after = await new GitRepositoryIdentityReader(executor, source, input.RuntimeBaseCommit).ReadAsync(token);
        if (after != actualRepository) throw new IOException("Fresh source changed during required-gate reconciliation.");
        _ = Remaining(input, 1);
        await Write(Path.Combine(input.ArtifactRoot, "actual-fresh-required-gate-denial.json"), new
        {
            fixture = true,
            productionCertified = false,
            receipt.Identity,
            canonicalOutcome = gate.GetProperty("outcome").GetString(),
            freshSource = actualRepository,
            freshPolicy = policy,
            currentToolSha256 = HashFile(host),
            rawFiles,
            actualRequiredGateTransition = "Evaluating -> IndependentReview",
            denial,
            freshReadOnlyGitCalls = executor.Calls,
            newGateProducerExecutions = 0
        }, token);
        return ("Incomplete", counts.Passed, counts.Failed);
    }

    private static async Task CopyColdRepository(DriverInput input, string source, string destination, CancellationToken token)
    {
        Plain(source); Plain(destination); Plain(Path.Combine(source, ".git"));
        if (!Directory.Exists(Path.Combine(source, ".git")) || File.Exists(Path.Combine(source, ".git", "objects", "info", "alternates")))
            throw new IOException("Only plain standalone object repository can seed this cold fixture.");
        var config = await File.ReadAllTextAsync(Path.Combine(source, ".git", "config"), token);
        if (new[] { "include", "worktree", "promisor", "partialclone" }.Any(value => config.Contains(value, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Cold fixture source has external configuration.");
        Directory.CreateDirectory(destination);
        var pending = new Stack<(string Source, string Target, int Depth)>(); pending.Push((source, destination, 0));
        var count = 0; long bytes = 0;
        while (pending.TryPop(out var item))
        {
            _ = Remaining(input, 1); token.ThrowIfCancellationRequested();
            if (item.Depth > 32) throw new IOException("Cold fixture depth bound exceeded.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(item.Source))
            {
                if (++count > 4096) throw new IOException("Cold source inventory bound exceeded.");
                var name = Path.GetFileName(entry); if (name is "bin" or "obj" or ".fixture-trx") continue;
                Plain(entry); var target = Path.Combine(item.Target, name); Plain(target);
                if (Directory.Exists(entry)) { Directory.CreateDirectory(target); pending.Push((entry, target, item.Depth + 1)); }
                else
                {
                    var length = new FileInfo(entry).Length;
                    if (length > 4 * 1024 * 1024 || (bytes += length) > 64 * 1024 * 1024) throw new IOException("Cold source byte bound exceeded.");
                    await WriteBytes(target, await File.ReadAllBytesAsync(entry, token), token);
                }
            }
        }
        var original = await new GitRepositoryContextReader().ReadAsync(source, "refs/heads/main", token);
        var copied = await new GitRepositoryContextReader().ReadAsync(destination, "refs/heads/main", token);
        if (original.Commit != input.RuntimeBaseCommit || copied.Commit != original.Commit || copied.Tree != original.Tree || copied.Dirty)
            throw new IOException("Actual cold copy changed source identity.");
    }
    private static AgentAcceptedReceipt Accepted(AgentRequest request, DriverInput input) => new(WorkerEnvelope.SchemaVersion,
        request.RunId, request.RequestIdentity, request.PlanIdentity, request.BaseCommit, request.Worktree, request.AdapterId, request.AdapterVersion,
        "fixture-middle-runtime:" + input.Nonce, "fixture-middle-task:" + input.Nonce, DateTimeOffset.UtcNow,
        new[] { AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Stop, AgentCapability.Pause, AgentCapability.Resume }
            .Select(value => new AgentCapabilityEvidence(value, AgentCapabilityStatus.Supported, "FIXTURE external native contract; no model worker", "fixture-middle:" + input.Nonce)).ToArray());
    private sealed class FixtureHost(ProcessControl processes, bool freshUnknown) : IAgentControlPort
    {
        private AgentRunBinding? _binding;
        public List<AgentControlResult> Ledger { get; } = [];
        public void Bind(AgentAcceptedReceipt accepted) => _binding = new(accepted.AdapterId, accepted.AdapterVersion, accepted.RunId,
            accepted.RequestIdentity, accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity);
        public Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan deadline, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matched = !freshUnknown && _binding == binding;
            var quiet = processes.ObserveOwned(binding.RunId).All(result => result.Outcome == ProcessControlOutcome.AlreadyExited);
            var result = new AgentControlResult(binding, action, !matched ? AgentActivityState.Unknown : quiet ? AgentActivityState.Quiescent : AgentActivityState.Live,
                matched, "fixture-middle-current:" + Guid.NewGuid().ToString("N"), "External-only fixture, fresh process has no original host membership.");
            Ledger.Add(result); return Task.FromResult(result);
        }
    }
    private static (int Passed, int Failed) Counts(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Actual TRX exceeds finite bound.");
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var counters = XDocument.Load(reader).Descendants().Single(element => element.Name.LocalName == "Counters");
        if (counters.Attribute("total")?.Value != "1") throw new InvalidOperationException("Actual driver TRX is not one executed test.");
        return (int.Parse(counters.Attribute("passed")!.Value, CultureInfo.InvariantCulture), int.Parse(counters.Attribute("failed")!.Value, CultureInfo.InvariantCulture));
    }
    private static async Task WaitRelease(DriverInput input, CancellationToken token)
    {
        var path = Path.Combine(input.ArtifactRoot, "release-" + input.Nonce); var started = Stopwatch.GetTimestamp();
        var limit = Remaining(input, 15);
        while (!File.Exists(path))
        {
            token.ThrowIfCancellationRequested(); if (Stopwatch.GetElapsedTime(started) >= limit) throw new TimeoutException("Middle parent barrier was not released within original bound.");
            await Task.Delay(10, token);
        }
        Plain(path); if (await File.ReadAllTextAsync(path, token) != input.Nonce) throw new IOException("Middle release nonce differs.");
    }
    private static void Successful(CommandResult result, string purpose)
    { if (result.ExitCode != 0 || result.TimedOut || result.Cancelled || !result.OwnedProcessQuiescent) throw new InvalidOperationException(purpose + " failed or is uncertain: " + result.StandardError); }
    private static string FindGit() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
        .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "git.exe" : "git")).First(File.Exists);
    private static async Task<byte[]> Read(string path, DriverInput input, CancellationToken token)
    { Within(path, input); if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Middle diagnostic exceeds1MiB."); return await File.ReadAllBytesAsync(path, token); }
    private static void Within(string path, DriverInput input)
    {
        var full = Path.GetFullPath(path);
        if (!new[] { input.RuntimeRoot, input.ArtifactRoot }.Any(root => full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, PathComparison())))
            throw new IOException("Middle path escaped its exact owned roots.");
        Plain(full);
    }
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static void Plain(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Middle namespace is redirected."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static async Task WriteBytes(string path, byte[] bytes, CancellationToken token)
    { Plain(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); Plain(path); await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
    private static Task Write(string path, object value, CancellationToken token) => EarlyDriverComposition.WriteJson(path, value, token);
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string HashFile(string path) { Plain(path); return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))); }
}
