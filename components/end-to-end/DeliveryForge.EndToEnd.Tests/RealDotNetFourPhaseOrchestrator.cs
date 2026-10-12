using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Evidence;

namespace DeliveryForge.EndToEnd.Tests;

public enum RealDotNetFixturePhase { FailingTest, ColdPassing, MissingCoverage, WarmNoBuild }
public sealed record RealDotNetPhaseSnapshot(GateRunResult Gate, string InvocationTrxPath, string ProvenancePath);

// Explicit trusted test composition only. Ordinary ScenarioRunner still accepts at most120s.
internal sealed class RealDotNetFixtureBudget
{
    private RealDotNetFixtureBudget() { }
    public static RealDotNetFixtureBudget ParentApproved { get; } = new();
    public string Id => "real-dotnet-fixture-v1";
    public int TotalSeconds => 540;
    public int SetupGitSeconds => 30;
    public int ProbeSeconds => 60;
    public int InitialRestoreSeconds => 90;
    public int GateSeconds => 75;
    public int CleanupReserveSeconds => 60;
    public int CpuCores => 2;
    public long ManagedHeapMaximumBytes => 4294967296;
}

internal static class RealDotNetFourPhaseOrchestrator
{
    public static async Task<IReadOnlyDictionary<RealDotNetFixturePhase, RealDotNetPhaseSnapshot>> RunFourAsync(
        OwnedScenarioRun artifacts, string ownedSourceDirectory, RealDotNetFixtureClock budget, CancellationToken cancellationToken)
    {
        using var entireRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.WallToken);
        cancellationToken = entireRun.Token;
        var runtime = new OwnedRealDotNetRuntime(artifacts, budget, cancellationToken);
        artifacts.RetainArtifacts("Actual .NET intended FAIL/advisory/missing-coverage/warm evidence and runtime retained.");
        var phases = new Dictionary<RealDotNetFixturePhase, RealDotNetPhaseSnapshot>();
        try
        {
            runtime.BeginStage(TimeSpan.FromSeconds(budget.Profile.SetupGitSeconds) - budget.Elapsed);
            await runtime.InitializeAsync(ownedSourceDirectory, cancellationToken);
            var repo = runtime.Repository;
            var calculator = Path.Combine(repo, "Subject", "Calculator.cs");
            await OwnedRealDotNetRuntime.WriteNewAsync(calculator, await File.ReadAllBytesAsync(Path.Combine(repo, "Subject", "Calculator.red.cs.txt"), cancellationToken), cancellationToken);
            await OwnedRealDotNetRuntime.WriteNewAsync(Path.Combine(repo, ".gitignore"), await File.ReadAllBytesAsync(Path.Combine(repo, "runtime.gitignore.txt"), cancellationToken), cancellationToken);
            var policySource = await File.ReadAllBytesAsync(Path.Combine(repo, "evidence-policy-source.json"), cancellationToken);
            var policyNode = JsonNode.Parse(policySource)!.AsObject();
            policyNode["identity"] = CanonicalJson.ComputeIdentity(policySource);
            var policyBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(policyNode.ToJsonString()));
            ContractValidator.ParseAndValidate(policyBytes);
            Directory.CreateDirectory(Path.Combine(repo, ".forge"));
            await OwnedRealDotNetRuntime.WriteNewAsync(Path.Combine(repo, ".forge", "evidence-policy.json"), policyBytes, cancellationToken);
            await runtime.GitAsync(cancellationToken, "init", "-q", "-b", "main");
            await CommitAsync(runtime, "actual calculator RED", cancellationToken);
            var protectedBase = await runtime.GitAsync(cancellationToken, "rev-parse", "HEAD");
            runtime.CommandBudget(TimeSpan.FromSeconds(1));
            runtime.BeginStage(budget.WorkBudget(budget.Profile.ProbeSeconds));
            var detection = await new ToolCapabilityDetector(runtime).DetectAsync(new("dotnet", ExecutableSource.TrustedExplicit,
                runtime.Dotnet, repo, null, ["--version"], ["--help"], "10.0.401", "trx-v1", true), cancellationToken);
            await artifacts.WriteNewJsonAsync("real-dotnet-probe.json", new { fixture = true, detection }, cancellationToken);
            if (!detection.Capability.Supported || detection.Capability.Version != "10.0.401" || detection.VersionResult?.StandardOutput.Trim() != "10.0.401")
                throw new InvalidOperationException("INCOMPLETE actual pinned .NET capability/version unavailable.");
            OwnedRealDotNetRuntime.RequireSuccessful(detection.VersionResult!, "version probe");
            OwnedRealDotNetRuntime.RequireSuccessful(detection.HelpResult!, "help probe");
            var versionPath = await SaveTextAsync(artifacts, "actual-dotnet-version.stdout.txt", detection.VersionResult!.StandardOutput, cancellationToken);
            var helpPath = await SaveTextAsync(artifacts, "actual-dotnet-help.stdout.txt", detection.HelpResult!.StandardOutput, cancellationToken);
            runtime.CommandBudget(TimeSpan.FromSeconds(1));
            runtime.BeginStage(budget.WorkBudget(budget.Profile.InitialRestoreSeconds));
            var project = Path.Combine(repo, "Subject.Tests", "Subject.Tests.csproj");
            var restore = await runtime.ExecuteAsync(new(runtime.Dotnet,
                ["restore", project, "--locked-mode", "--source", runtime.PackageCache, "--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false"], repo,
                runtime.DotnetEnvironment), TimeSpan.FromSeconds(budget.Profile.InitialRestoreSeconds), cancellationToken);
            OwnedRealDotNetRuntime.RequireSuccessful(restore, "locked restore");
            var restoreStdout = await SaveTextAsync(artifacts, "actual-initial-restore.stdout.txt", restore.StandardOutput, cancellationToken);
            var restoreStderr = await SaveTextAsync(artifacts, "actual-initial-restore.stderr.txt", restore.StandardError, cancellationToken);
            var lockPath = Path.Combine(repo, "Subject.Tests", "packages.lock.json");
            var lockHash = OwnedRealDotNetRuntime.HashFile(lockPath);
            var assetsCopy = Path.Combine(artifacts.Root, "actual-initial-project.assets.json");
            await OwnedRealDotNetRuntime.WriteNewAsync(assetsCopy, await File.ReadAllBytesAsync(Path.Combine(repo, "Subject.Tests", "obj", "project.assets.json"), cancellationToken), cancellationToken);
            runtime.CommandBudget(TimeSpan.FromSeconds(1));
            foreach (var phase in Enum.GetValues<RealDotNetFixturePhase>())
            {
                runtime.BeginStage(budget.WorkBudget(budget.Profile.GateSeconds));
                if (phase == RealDotNetFixturePhase.ColdPassing)
                {
                    runtime.EnsureOwner();
                    await File.WriteAllBytesAsync(calculator, await File.ReadAllBytesAsync(Path.Combine(repo, "Subject", "Calculator.green.cs.txt"), cancellationToken), cancellationToken);
                    await CommitAsync(runtime, "actual calculator repaired GREEN", cancellationToken);
                }
                var cold = phase is RealDotNetFixturePhase.ColdPassing or RealDotNetFixturePhase.MissingCoverage;
                if (cold) runtime.DeleteOwnedBuildOutputs();
                var dllsBefore = Directory.EnumerateFiles(repo, "*.dll", SearchOption.AllDirectories).ToArray();
                if (cold && dllsBefore.Length != 0) throw new IOException("Owned cold deletion left generated DLLs.");
                var dll = Path.Combine(repo, "Subject", "bin", "Release", "net10.0", "Subject.dll");
                var dllBefore = File.Exists(dll) ? OwnedRealDotNetRuntime.HashFile(dll) : null;
                var coverageBefore = Directory.EnumerateFiles(repo, "coverage.*.xml", SearchOption.AllDirectories).ToArray();
                var trxDirectory = Path.Combine(repo, ".fixture-trx"); Directory.CreateDirectory(trxDirectory);
                var trx = Path.Combine(trxDirectory, phase + ".trx");
                if (File.Exists(trx)) throw new IOException("TRX phase path was not fresh.");
                var arguments = new List<string> { "test", project, "-c", "Release", "--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "-p:RestoreLockedMode=true", "-p:RestoreSources=" + runtime.PackageCache };
                if (phase == RealDotNetFixturePhase.FailingTest) arguments.Add("--no-restore");
                if (phase == RealDotNetFixturePhase.WarmNoBuild) arguments.AddRange(["--no-restore", "--no-build"]);
                arguments.AddRange(["--logger", "trx;LogFileName=" + trx]);
                var invocation = new CommandInvocation(detection.Capability.ExecutablePath!, arguments.AsReadOnly(), repo, runtime.DotnetEnvironment);
                var configBytes = CanonicalJson.Canonicalize(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    fixture = true,
                    phase = phase.ToString(),
                    seed = 11017,
                    budget = budget.Profile,
                    invocation,
                    protectedBase,
                    sdkVersion = detection.Capability.Version,
                    sdkExecutableIdentity = detection.Capability.ExecutableIdentity
                }));
                var phaseRoot = Path.Combine(artifacts.Root, "actual-dotnet-" + phase); Directory.CreateDirectory(phaseRoot);
                var configPath = Path.Combine(phaseRoot, "configuration.json"); await OwnedRealDotNetRuntime.WriteNewAsync(configPath, configBytes, cancellationToken);
                var policy = await new ProtectedGitPolicyResolver(runtime, repo, protectedBase).ResolveAsync(".forge/evidence-policy.json", cancellationToken);
                var reader = new GitRepositoryIdentityReader(runtime, repo, protectedBase);
                var before = await reader.ReadAsync(cancellationToken);
                var gateId = phase == RealDotNetFixturePhase.MissingCoverage ? "coverage" : "test";
                var gate = await new GateRunner(runtime, reader, repo, protectedBase).RunAsync(new(gateId, policy, detection.Capability,
                    EvidenceAdapterKind.DotNet, OwnedRealDotNetRuntime.Hash(configBytes), before, invocation,
                    "issue11-real-dotnet-fixture", runtime.CommandBudget(TimeSpan.FromSeconds(budget.Profile.GateSeconds)), phaseRoot), cancellationToken);
                if (gate.Execution.TimedOut || gate.Execution.Cancelled || !gate.Execution.OwnedProcessQuiescent)
                    throw new InvalidOperationException("Actual gate command timed out/cancelled/uncertain; intended evidence not established.");
                var immutableDll = Path.Combine(phaseRoot, "actual-Subject.dll");
                await OwnedRealDotNetRuntime.WriteNewAsync(immutableDll, await File.ReadAllBytesAsync(dll, cancellationToken), cancellationToken);
                var coverageAfter = Directory.EnumerateFiles(repo, "coverage.*.xml", SearchOption.AllDirectories).ToArray();
                var provenance = Path.Combine(phaseRoot, "provenance.json");
                await OwnedRealDotNetRuntime.WriteNewAsync(provenance, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    fixture = true,
                    productionCertified = false,
                    firstFullRealRunCertified = false,
                    phase = phase.ToString(),
                    gateId,
                    integratedBaseCommit = artifacts.IntegratedSource.Commit,
                    implementationHead = artifacts.IntegratedSource.HeadCommit,
                    budgetId = budget.Profile.Id,
                    totalBudgetSeconds = budget.Profile.TotalSeconds,
                    elapsedSeconds = budget.Elapsed.TotalSeconds,
                    observedSdkVersion = detection.Capability.Version,
                    versionStdoutPath = versionPath,
                    helpStdoutPath = helpPath,
                    helpExitCode = detection.HelpResult!.ExitCode,
                    sdkExecutableSha256 = detection.Capability.ExecutableIdentity,
                    configurationSha256 = OwnedRealDotNetRuntime.Hash(configBytes),
                    configurationPath = configPath,
                    protectedPolicySha256 = OwnedRealDotNetRuntime.Hash(policyBytes),
                    protectedPolicyPath = Path.Combine(repo, ".forge", "evidence-policy.json"),
                    protectedBaseCommit = protectedBase,
                    observedHeadCommit = gate.Before.HeadCommit,
                    observedTreeId = gate.Before.TreeId,
                    initialRestoreExitCode = restore.ExitCode,
                    initialRestoreOwnedQuiescent = restore.OwnedProcessQuiescent,
                    initialRestoreMode = "locked",
                    initialRestoreStdoutPath = restoreStdout,
                    initialRestoreStderrPath = restoreStderr,
                    assetsSha256 = OwnedRealDotNetRuntime.HashFile(assetsCopy),
                    assetsPath = assetsCopy,
                    lockSha256 = lockHash,
                    lockPath,
                    ownedBinAndObjRemoved = cold,
                    generatedDllsBefore = dllsBefore,
                    producedDllPath = immutableDll,
                    dllHashBefore = dllBefore,
                    dllHashAfter = OwnedRealDotNetRuntime.HashFile(immutableDll),
                    coverageFilesBefore = coverageBefore,
                    coverageFilesAfter = coverageAfter,
                    limitation = "Actual compiler/SDK/TRX only; current DotNet producer remains advisory/nonproduction. Outer fixture never certifies full model-worker/review/coverage/mutation profile."
                }), cancellationToken);
                phases.Add(phase, new(gate, trx, provenance));
                runtime.CommandBudget(TimeSpan.FromSeconds(1));
            }
            return new ReadOnlyDictionary<RealDotNetFixturePhase, RealDotNetPhaseSnapshot>(phases);
        }
        finally
        {
            artifacts.RetainArtifacts("Owned actual-tool runtime retained: " + runtime.Root);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0.001, Math.Min(60, budget.Remaining.TotalSeconds))));
            await artifacts.WriteNewJsonAsync("actual-dotnet-retained-runtime.json", new
            {
                fixture = true,
                productionCertified = false,
                runtime.Root,
                runtime.AllCommandsQuiescent,
                elapsedSeconds = budget.Elapsed.TotalSeconds,
                retained = true,
                limitation = "No recursive runtime cleanup is authorized by gate outcomes or file/hash evidence alone."
            }, cleanup.Token);
        }
    }
    private static async Task CommitAsync(OwnedRealDotNetRuntime runtime, string message, CancellationToken token)
    {
        await runtime.GitAsync(token, "add", "--all");
        await runtime.GitAsync(token, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", message);
    }
    private static async Task<string> SaveTextAsync(OwnedScenarioRun artifacts, string name, string text, CancellationToken token)
    {
        var path = Path.Combine(artifacts.Root, name); await OwnedRealDotNetRuntime.WriteNewAsync(path, Encoding.UTF8.GetBytes(text), token); return path;
    }
}

// Constructed before shared initialization, so source/artifact/Git setup consumes the same540s.
internal sealed class RealDotNetFixtureClock : IDisposable
{
    private readonly long _started;
    private readonly CancellationTokenSource _wall;
    public RealDotNetFixtureClock() : this(Stopwatch.GetTimestamp()) { }
    internal RealDotNetFixtureClock(long originalStarted)
    {
        _started = originalStarted;
        var remaining = TimeSpan.FromSeconds(540) - Stopwatch.GetElapsedTime(_started);
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromSeconds(540))
            throw new ArgumentException("Actual real-tool fixture must retain its original finite clock.");
        _wall = new(remaining);
    }
    public RealDotNetFixtureBudget Profile => RealDotNetFixtureBudget.ParentApproved;
    public CancellationToken WallToken => _wall.Token;
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);
    public TimeSpan Remaining => TimeSpan.FromSeconds(Profile.TotalSeconds) - Elapsed;
    public TimeSpan WorkBudget(int stageSeconds)
    {
        _wall.Token.ThrowIfCancellationRequested();
        var work = Remaining - TimeSpan.FromSeconds(Profile.CleanupReserveSeconds);
        if (work <= TimeSpan.Zero) throw new OperationCanceledException("Trusted real-tool work budget exhausted; cleanup reserve retained.", _wall.Token);
        return work < TimeSpan.FromSeconds(stageSeconds) ? work : TimeSpan.FromSeconds(stageSeconds);
    }
    public void Dispose() => _wall.Dispose();
}
