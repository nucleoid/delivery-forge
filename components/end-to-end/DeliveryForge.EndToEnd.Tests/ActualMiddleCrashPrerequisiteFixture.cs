using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Evidence;
using GateOutcome = DeliveryForge.Evidence.GateOutcome;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Review;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

// Implemented RED setup only: actual public owners/prerequisites, never the missing crash choreography.
internal static class ActualMiddleCrashPrerequisiteFixture
{
    internal static async Task<MiddleCrashPrerequisites> PrepareAsync(OwnedScenarioRun artifacts, string stage,
        string nonce, long originalStarted, RealDotNetFixtureClock? originalToolClock, CancellationToken token)
    {
        if (stage == "gate-receipt-written")
            return await ActualToolAsync(artifacts, nonce, originalStarted,
                originalToolClock ?? throw new InvalidOperationException("Original real-tool clock is required."), token);
        if (originalToolClock is not null) throw new InvalidOperationException("Ordinary crash case cannot borrow real-tool budget.");
        var remaining = TimeSpan.FromSeconds(75) - Stopwatch.GetElapsedTime(originalStarted);
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Original middle prerequisite75 exhausted.");
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(token); setup.CancelAfter(remaining);
        token = setup.Token;
        var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, token);
        artifacts.MiddleLifetime.Retain(runtime);
        artifacts.MiddleLifetime.Retain(runtime.Store);
        artifacts.MiddleLifetime.Retain(runtime.Processes);
        var context = await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", token);
        var plan = await Freeze(artifacts, context, runtime.Reader, "subject.txt", nonce, token);
        string? result = null; string? receipt = null; object? ownerProof = null;
        if (stage == "command-completed")
        {
            var host = Existing("DELIVERY_FORGE_DOTNET_HOST"); var driver = Existing("DELIVERY_FORGE_ACTUAL_CRASH_DRIVER_DLL");
            var child = artifacts.Processes.StartOwned(new(host, [driver, "child", stage, runtime.Root, artifacts.Root,
                nonce, context.Commit, OwnedScenarioRun.InitialIntegratedBase, "11019"]), "middle-prerequisite:" + nonce);
            var waitStarted = Stopwatch.GetTimestamp();
            await child.Process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            ProcessControlResult observed;
            var exitSamples = new List<ProcessControlResult>();
            do
            {
                token.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(waitStarted) >= TimeSpan.FromSeconds(5) || exitSamples.Count >= 256)
                    throw new TimeoutException("Original prerequisite child exit observation expired.");
                observed = artifacts.Processes.Observe(child.Identity);
                exitSamples.Add(observed);
                if (Stopwatch.GetElapsedTime(waitStarted) >= TimeSpan.FromSeconds(5)) throw new TimeoutException("Original prerequisite child observation arrived late.");
                if (observed.Outcome != ProcessControlOutcome.AlreadyExited) await Task.Delay(10, token);
            } while (observed.Outcome != ProcessControlOutcome.AlreadyExited);
            Assert.Equal(0, child.Process.ExitCode);
            result = Path.Combine(artifacts.Root, "child-result-" + nonce + ".json");
            using var actual = JsonDocument.Parse(await File.ReadAllBytesAsync(result, token));
            Assert.Equal(nonce, actual.RootElement.GetProperty("nonce").GetString());
            Assert.Equal("completed", actual.RootElement.GetProperty("actualNativeResult").GetString());
            ownerProof = new { child.Identity, observed, exitSamples, originalNaturalExit = true, resultSha256 = Hash(await File.ReadAllBytesAsync(result, token)) };
        }
        else if (stage is "review-registration" or "review-receipt")
        {
            var source = await ProtectedReviewFixtureBootstrapTests.Setup(artifacts, token);
            using var bootstrap = await ReviewAuthorityHost.CreateProtectedGitContractFixtureAsync(source.Configuration, token);
            var root = bootstrap.RootContext;
            var contextData = new ReviewAcceptanceContext(root.RunIdentity, "middle-prerequisite-" + nonce, root.PlanIdentity,
                root.EvidencePolicyIdentity, root.ReviewPolicyContentIdentity, root.ReviewPolicySourceRevision,
                root.ReviewPolicyAuthorityKind, root.SourceAuthorityIdentity, root.SourceRoot, root.ProtectedBaseCommit, root.ProtectedObjectViewIdentity);
            var receiver = bootstrap.Host.CreateTransitionReceiver(contextData);
            var patch = await GitPatchIdentity.ComputeAsync(bootstrap.ObjectView, source.BaseCommit, source.HeadCommit,
                source.Plan.ContractIdentity, new Dictionary<string, string>(), token);
            var reviewer = new ReviewerCapability("explicit-middle-review-fixture", "1.0.0", "claude", true, true, false, ["source"]);
            var request = new ReviewRequest(patch, "gpt", reviewer, "source", bootstrap.Policy.PolicyIdentity);
            var registered = bootstrap.Host.RegisterRequest(contextData.RequestIdentity, request, contextData);
            var port = new ReviewFixturePort();
            if (stage == "review-receipt")
            {
                var now = DateTimeOffset.UtcNow;
                var capability = bootstrap.Host.IssueCapability(reviewer, "middle-cap-" + nonce, now.AddSeconds(-1), now.AddSeconds(120));
                var raw = await port.ExecuteAsync(registered, Path.Combine(artifacts.Root, "middle-prerequisite-review-raw.json"), token);
                var execution = bootstrap.Host.RecordExecution(registered, capability, "middle-execution-" + nonce,
                    true, true, raw.RawSha256, raw.Verdict, raw.CompletedAt);
                var normalized = ReviewNormalizer.Normalize(request,
                    ReviewResult.Complete(patch, "claude", "PASS", [], raw.RawSha256, raw.CompletedAt), bootstrap.Policy, capability, execution);
                Assert.Equal(ReviewOutcome.Pass, normalized.Outcome); Assert.True(normalized.AuthorityAccepted);
                Assert.True(normalized.Fixture); Assert.False(normalized.ProductionAccepted);
                var bytes = ReviewReceiptSerializer.Serialize(ReviewReceiptSerializer.Create(normalized));
                var contract = ContractValidator.ParseAndValidate(bytes);
                receipt = await new AppendOnlyContractStore(Path.Combine(artifacts.Root, "middle-prerequisite-review-contracts")).WriteImmutableAsync(bytes, token);
                bootstrap.Host.IssueAcceptance(contract, normalized, contextData, ReviewEvidenceRole.Independent);
                var input = new TransitionEvidence(AuthorizationCeiling.Pr, patch.BaseCommit, patch.HeadCommit, patch.HeadTree,
                    source.EvidencePolicy, ReviewReceipt: contract);
                bootstrap.Host.EnsureContractFixtureTransition(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, input, receiver);
                Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowTransition.EnsureAllowed(
                    WorkflowState.IndependentReview, WorkflowState.PrAuthorized, input, receiver));
            }
            Assert.Equal(stage == "review-registration" ? 0 : 1, port.Calls.Count);
            ownerProof = new
            {
                root,
                registered.RequestIdentity,
                source.BaseCommit,
                source.HeadCommit,
                reviewerFixtureCalls = port.Calls,
                productionCertified = false,
                opaqueMembershipNotSerialized = true
            };
            // The actual prerequisite issuer above is a separate positive control. Prepare ALL
            // driver policy/source inputs before freezing its distinct immutable plan/base.
            var protectedContext = await artifacts.Reader.ReadAsync(artifacts.Checkout, source.BaseCommit, token);
            var protectedFile = await artifacts.Reader.ReadFileAsync(protectedContext, "policy/review.json", token);
            Directory.CreateDirectory(Path.Combine(runtime.Repository, "policy"));
            await File.WriteAllBytesAsync(Path.Combine(runtime.Repository, "policy", "review.json"), protectedFile.Bytes, token);
            await runtime.GitAsync(token, "add", "--", "policy/review.json");
            await runtime.GitAsync(token, "-c", "user.name=CrashFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "actual middle protected review inputs");
            await runtime.GitAsync(token, "checkout", "-q", "-b", "middle-review-candidate");
            await File.WriteAllTextAsync(Path.Combine(runtime.Repository, "subject.txt"), "actual middle review candidate\n", token);
            await runtime.GitAsync(token, "add", "--", "subject.txt");
            await runtime.GitAsync(token, "-c", "user.name=CrashFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "actual middle review candidate");
            context = await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", token);
            plan = await Freeze(artifacts, context, runtime.Reader, "subject.txt", nonce, token);
        }
        else throw new ArgumentException("Unknown middle prerequisite.");
        var observation = "middle-" + stage + "-actual-prerequisite.json";
        await artifacts.WriteNewJsonAsync(observation, new
        {
            fixture = true,
            productionCertified = false,
            stage,
            nonce,
            originalStarted,
            ordinaryTotalSeconds = 120,
            originalSetupSeconds = 75,
            runtime.Root,
            frozenBaseCommit = context.Commit,
            plan,
            ownerProof,
            actualParentTerminationNotYetPerformed = true,
            remainingBoundaries = new[] { "scheduler-checkpoint", "resuming", "publication-intent", "publication-readback", "cleanup-receipt" }
        }, token);
        return new(runtime.Root, context.Commit, plan, result, null, null, receipt, Path.Combine(artifacts.Root, observation));
    }

    private static async Task<MiddleCrashPrerequisites> ActualToolAsync(OwnedScenarioRun artifacts, string nonce,
        long originalStarted, RealDotNetFixtureClock clock, CancellationToken token)
    {
        var runtime = new OwnedRealDotNetRuntime(artifacts, clock, token);
        artifacts.MiddleLifetime.Retain(runtime);
        runtime.BeginStage(clock.WorkBudget(30));
        var template = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RealDotNet");
        await runtime.InitializeAsync(template, token); // Real source copy, no binaries/counts/receipt synthesis.
        var repository = runtime.Repository;
        await OwnedRealDotNetRuntime.WriteNewAsync(Path.Combine(repository, "Subject", "Calculator.cs"),
            await File.ReadAllBytesAsync(Path.Combine(repository, "Subject", "Calculator.green.cs.txt"), token), token);
        await OwnedRealDotNetRuntime.WriteNewAsync(Path.Combine(repository, ".gitignore"),
            await File.ReadAllBytesAsync(Path.Combine(repository, "runtime.gitignore.txt"), token), token);
        var policyNode = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(repository, "evidence-policy-source.json"), token))!.AsObject();
        policyNode["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(policyNode.ToJsonString()));
        var policyBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(policyNode.ToJsonString()));
        ContractValidator.ParseAndValidate(policyBytes);
        Directory.CreateDirectory(Path.Combine(repository, ".forge"));
        await OwnedRealDotNetRuntime.WriteNewAsync(Path.Combine(repository, ".forge", "evidence-policy.json"), policyBytes, token);
        await runtime.GitAsync(token, "init", "-q", "-b", "main");
        await runtime.GitAsync(token, "add", "--", ".");
        await runtime.GitAsync(token, "-c", "user.name=CrashFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "actual middle gate prerequisite source");
        var protectedBase = await runtime.GitAsync(token, "rev-parse", "HEAD");
        runtime.BeginStage(clock.WorkBudget(60));
        var detected = await new ToolCapabilityDetector(runtime).DetectAsync(new("dotnet", ExecutableSource.TrustedExplicit,
            runtime.Dotnet, repository, null, ["--version"], ["--help"], "10.0.401", "trx-v1", true), token);
        Assert.True(detected.Capability.Supported); Assert.Equal("10.0.401", detected.Capability.Version);
        OwnedRealDotNetRuntime.RequireSuccessful(detected.VersionResult!, "middle real version");
        OwnedRealDotNetRuntime.RequireSuccessful(detected.HelpResult!, "middle real help");
        runtime.BeginStage(clock.WorkBudget(90));
        var project = Path.Combine(repository, "Subject.Tests", "Subject.Tests.csproj");
        var restored = await runtime.ExecuteAsync(new(runtime.Dotnet, ["restore", project, "--locked-mode", "--source", runtime.PackageCache,
            "--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false"], repository, runtime.DotnetEnvironment), TimeSpan.FromSeconds(90), token);
        OwnedRealDotNetRuntime.RequireSuccessful(restored, "middle actual locked restore");
        runtime.BeginStage(clock.WorkBudget(75));
        var trxDirectory = Path.Combine(repository, ".fixture-trx"); Directory.CreateDirectory(trxDirectory);
        var trx = Path.Combine(trxDirectory, "middle-prerequisite-" + nonce + ".trx");
        Assert.False(File.Exists(trx));
        var invocation = new CommandInvocation(runtime.Dotnet, ["test", project, "--no-restore", "-c", "Release",
            "--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "--logger", "trx;LogFileName=" + trx], repository, runtime.DotnetEnvironment);
        var policy = await new ProtectedGitPolicyResolver(runtime, repository, protectedBase).ResolveAsync(".forge/evidence-policy.json", token);
        var reader = new GitRepositoryIdentityReader(runtime, repository, protectedBase);
        var before = await reader.ReadAsync(token);
        var configuration = JsonSerializer.SerializeToUtf8Bytes(new { fixture = true, invocation, originalStarted, totalSeconds = 540, protectedBase });
        var gateRoot = Path.Combine(artifacts.Root, "middle-real-test-prerequisite");
        var gate = await new GateRunner(runtime, reader, repository, protectedBase).RunAsync(new("test", policy, detected.Capability,
            EvidenceAdapterKind.DotNet, Hash(configuration), before, invocation, "middle-crash-prerequisite",
            runtime.CommandBudget(TimeSpan.FromSeconds(75)), gateRoot), token);
        Assert.Equal(0, gate.Execution.ExitCode); Assert.False(gate.Execution.TimedOut); Assert.False(gate.Execution.Cancelled);
        Assert.True(gate.Execution.OwnedProcessQuiescent); Assert.True(runtime.AllCommandsQuiescent);
        Assert.Equal(GateOutcome.Incomplete, gate.Outcome); Assert.False(gate.ProductionCapable);
        using (var xml = XmlReader.Create(trx, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
        {
            var document = XDocument.Load(xml);
            var counters = Assert.Single(document.Descendants(), element => element.Name.LocalName == "Counters");
            Assert.Equal("1", counters.Attribute("total")?.Value); Assert.Equal("1", counters.Attribute("passed")?.Value);
            Assert.Equal("0", counters.Attribute("failed")?.Value);
        }
        var actualContext = await artifacts.Reader.ReadAsync(repository, protectedBase, token);
        var plan = await Freeze(artifacts, actualContext, artifacts.Reader, "Subject/Calculator.cs", nonce, token);
        await artifacts.WriteNewJsonAsync("middle-gate-actual-prerequisite.json", new
        {
            fixture = true,
            productionCertified = false,
            originalStarted,
            originalTotalSeconds = 540,
            clock.Elapsed,
            runtime.Root,
            protectedBase,
            plan,
            detected,
            restored,
            gate,
            trxPath = trx,
            actualTrxSha256 = Hash(await File.ReadAllBytesAsync(trx, token)),
            rawHashes = gate.ArtifactHashes,
            actualParentTerminationNotYetPerformed = true,
            limitation = "Genuine SDK/locked restore/cold test1of1/TRX/canonical advisory receipt prerequisite only; driver boundary/calibration must run their own distinct gates on the SAME remaining clock."
        }, token);
        return new(runtime.Root, protectedBase, plan, null, gate.ReceiptPath, trx, null, Path.Combine(artifacts.Root, "middle-gate-actual-prerequisite.json"));
    }

    private static async Task<string> Freeze(OwnedScenarioRun artifacts, RepositoryContext context,
        GitRepositoryContextReader reader, string subject, string nonce, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new PlanningRequest("fixture/delivery-forge", "#11", "implement", "Actual middle crash prerequisite",
            [subject], ["excluded"], ["Fixture native/reviewer only; no first-full certification"], "implement");
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(await reader.ReadFileAsync(context, subject, token), now, [])];
        var plan = PlanFreezer.Freeze(new(request, context, evidence, [new(subject, "fixture", "actual middle prerequisite")], [new("#11", [], "root")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], IntakePlanner.Assess(request, evidence)), "middle-" + nonce, now);
        return await new AppendOnlyContractStore(Path.Combine(artifacts.Root, "middle-prerequisite-plan-contracts")).WriteImmutableAsync(plan.PlanContractBytes, token);
    }
    private static string Existing(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null || !Path.IsPathFullyQualified(value) || !File.Exists(value)) throw new PlatformNotSupportedException("Actual middle crash input missing: " + name);
        return Path.GetFullPath(value);
    }
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
