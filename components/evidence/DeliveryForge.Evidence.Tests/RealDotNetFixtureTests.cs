using System.Xml.Linq;

namespace DeliveryForge.Evidence.Tests;

[Collection(RealDotNetFixtureCollection.Name)]
public sealed class RealDotNetFixtureTests
{
    [Fact(Timeout = 180_000)]
    [Trait("Category", "RealFixture")]
    public async Task Earlier_red_commit_then_clean_green_head_produces_real_test_counts()
    {
        using var temp = new TempDirectory();
        using var greenOwner = new TempDirectory();
        using var evidence = new TempDirectory();
        var fixture = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "RealDotNet");
        CopyTree(fixture, temp.Path);
        File.Copy(FindRepositoryFile("global.json"),
            System.IO.Path.Combine(temp.Path, "global.json"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, ".gitignore"),
            "**/bin/\n**/obj/\n*.trx\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, ".gitattributes"),
            "* text eol=lf\n", TestContext.Current.CancellationToken);
        var subject = System.IO.Path.Combine(temp.Path, "Subject", "Calculator.cs");
        var red = await File.ReadAllTextAsync(
            System.IO.Path.Combine(temp.Path, "Subject", "Calculator.red.cs.txt"),
            TestContext.Current.CancellationToken);
        var green = await File.ReadAllTextAsync(
            System.IO.Path.Combine(temp.Path, "Subject", "Calculator.green.cs.txt"),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(subject, red, TestContext.Current.CancellationToken);

        var executor = new ProcessCommandExecutor();
        await GitAsync(executor, temp.Path, ["init"]);
        await GitAsync(executor, temp.Path, ["config", "user.name", "Delivery Forge Fixture"]);
        await GitAsync(executor, temp.Path, ["config", "user.email", "fixture@delivery-forge.invalid"]);
        await GitAsync(executor, temp.Path, ["add", "."]);
        await GitAsync(executor, temp.Path, ["commit", "-m", "red fixture"]);
        var redHead = await GitAsync(executor, temp.Path, ["rev-parse", "HEAD"]);

        var dotnet = ResolveExecutable("DELIVERY_FORGE_DOTNET_HOST", "dotnet");
        var capability = await DetectDotNetAsync(executor, dotnet, temp.Path);
        var project = System.IO.Path.Combine(temp.Path, "Subject.Tests", "Subject.Tests.csproj");
        var redTrx = System.IO.Path.Combine(temp.Path, "red.trx");
        var protectedBase = redHead.StandardOutput.Trim();
        var redResult = await RunGateAsync(executor, capability, temp.Path, evidence.Path, protectedBase, "red-gate",
            ["test", project, "--configuration", "Release", "--logger", $"trx;LogFileName={redTrx}"]);
        ArchiveFixtureEvidence(evidence.Path, redResult);
        Assert.True(redResult.Execution.ExitCode.HasValue,
            $"RED process did not reach a valid exit: outcome={redResult.Outcome}; reason={redResult.Reason}; " +
            $"timeout={redResult.Execution.TimedOut}; cancelled={redResult.Execution.Cancelled}; " +
            $"quiescent={redResult.Execution.OwnedProcessQuiescent}; " +
            $"native={redResult.Execution.LifecycleDiagnostics?.NativeFailure}; " +
            $"startup={redResult.Execution.LifecycleDiagnostics?.Startup}\n" +
            redResult.Execution.StandardOutput + "\n" + redResult.Execution.StandardError);
        Assert.NotEqual(0, redResult.Execution.ExitCode);
        Assert.True(redResult.Outcome == GateOutcome.Fail,
            $"RED gate: {redResult.Outcome}; reason: {redResult.Reason}; " +
            $"exit: {redResult.Execution.ExitCode}; timeout: {redResult.Execution.TimedOut}; " +
            $"cancelled: {redResult.Execution.Cancelled}; quiescent: {redResult.Execution.OwnedProcessQuiescent}\n" +
            redResult.Execution.StandardOutput + "\n" + redResult.Execution.StandardError);
        Assert.True(File.Exists(redResult.ReceiptPath));
        Assert.True(
            Directory.EnumerateFiles(temp.Path, "*.trx", SearchOption.AllDirectories).Any(),
            redResult.Execution.StandardOutput + Environment.NewLine + redResult.Execution.StandardError);
        Assert.Equal((1, 1, 0, 1), ReadCounts(redTrx));

        await File.WriteAllTextAsync(subject, green, TestContext.Current.CancellationToken);
        await GitAsync(executor, temp.Path, ["add", "Subject/Calculator.cs"]);
        await GitAsync(executor, temp.Path, ["commit", "-m", "green fixture"]);
        var greenHead = await GitAsync(executor, temp.Path, ["rev-parse", "HEAD"]);
        Assert.NotEqual(redHead.StandardOutput.Trim(), greenHead.StandardOutput.Trim());

        // Model a platform-specific SDK residue that is not part of either committed tree.
        // GREEN must be evaluated from committed source, never from the RED build worktree.
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temp.Path, "red-worktree-residue.tmp"),
            "red-only",
            TestContext.Current.CancellationToken);

        DeleteBuildOutputs(temp.Path);
        var contaminatedStatus = await GitAsync(
            executor, temp.Path, ["status", "--porcelain=v1", "--untracked-files=all"]);
        Assert.Contains("red-worktree-residue.tmp", contaminatedStatus.StandardOutput, StringComparison.Ordinal);

        var greenRepository = System.IO.Path.Combine(greenOwner.Path, "repository");
        await GitAsync(
            executor,
            greenOwner.Path,
            ["clone", "--no-local", "--no-hardlinks", "--", temp.Path, greenRepository]);
        var clonedHead = await GitAsync(executor, greenRepository, ["rev-parse", "HEAD"]);
        Assert.Equal(greenHead.StandardOutput.Trim(), clonedHead.StandardOutput.Trim());
        var cleanStatus = await GitAsync(
            executor, greenRepository, ["status", "--porcelain=v1", "--untracked-files=all"]);
        Assert.True(string.IsNullOrEmpty(cleanStatus.StandardOutput),
            $"Fresh GREEN clone is not clean:{Environment.NewLine}{cleanStatus.StandardOutput}");

        var beforeCapability = await CaptureRepositoryDiagnosticAsync(
            executor, greenRepository, protectedBase, "before-capability-probe");
        capability = await DetectDotNetAsync(executor, dotnet, greenRepository);
        var afterCapability = await CaptureRepositoryDiagnosticAsync(
            executor, greenRepository, protectedBase, "after-capability-probe");
        var preGate = await CaptureRepositoryDiagnosticAsync(
            executor, greenRepository, protectedBase, "pre-gate");
        var repositoryDiagnostics = new RepositoryDiagnosticBundle(
            beforeCapability, afterCapability, preGate);
        project = System.IO.Path.Combine(greenRepository, "Subject.Tests", "Subject.Tests.csproj");
        var greenTrx = System.IO.Path.Combine(greenRepository, "green.trx");
        var assembly = System.IO.Path.Combine(
            greenRepository, "Subject", "bin", "Release", "net10.0", "Subject.dll");
        Assert.False(File.Exists(assembly));
        GitStatusCommandDiagnostics? gateStatusDiagnostics = null;
        var greenResult = await RunGateAsync(
            executor, capability, greenRepository, evidence.Path, protectedBase, "green-gate",
            ["test", project, "--configuration", "Release", "--logger", $"trx;LogFileName={greenTrx}"],
            preGate.Identity,
            diagnostics => gateStatusDiagnostics = diagnostics);
        ArchiveFixtureEvidence(evidence.Path, greenResult, repositoryDiagnostics, gateStatusDiagnostics);
        Assert.True(greenResult.Execution.ExitCode == 0,
            $"GREEN process did not complete successfully: exit={greenResult.Execution.ExitCode}; " +
            $"timeout={greenResult.Execution.TimedOut}; cancelled={greenResult.Execution.Cancelled}; " +
            $"quiescent={greenResult.Execution.OwnedProcessQuiescent}; reason={greenResult.Reason}; " +
            $"native={greenResult.Execution.LifecycleDiagnostics?.NativeFailure}\n" +
            FormatRepositoryDiagnostics(
                repositoryDiagnostics, preGate.Identity, greenResult.Before, gateStatusDiagnostics) + "\n" +
            greenResult.Execution.StandardOutput + "\n" + greenResult.Execution.StandardError);
        Assert.True(File.Exists(assembly));
        Assert.Equal((1, 1, 1, 0), ReadCounts(greenTrx));
        Assert.Equal(GateOutcome.Pass, greenResult.Outcome);
        Assert.True(greenResult.ProductionCapable);
        Assert.True(File.Exists(greenResult.ReceiptPath));
        Assert.Contains("evidence.trx", greenResult.ArtifactHashes.Keys);
        Assert.StartsWith("sha256:", greenResult.ArtifactHashes["evidence.trx"]);

        var warmTrx = System.IO.Path.Combine(greenRepository, "warm.trx");
        var warmResult = await RunGateAsync(
            executor, capability, greenRepository, evidence.Path, protectedBase, "warm-gate",
            ["test", project, "--configuration", "Release", "--no-restore", "--no-build",
                "--logger", $"trx;LogFileName={warmTrx}"]);
        ArchiveFixtureEvidence(evidence.Path, warmResult);
        Assert.NotNull(warmResult.Execution.ExitCode);
        Assert.Equal(0, warmResult.Execution.ExitCode);
        Assert.Equal((1, 1, 1, 0), ReadCounts(warmTrx));
        Assert.Equal(GateOutcome.Incomplete, warmResult.Outcome);
        Assert.Contains("compiler", warmResult.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Canonical_line_ending_policy_keeps_hardened_identity_clean_after_autocrlf_checkout()
    {
        using var sourceOwner = new TempDirectory();
        using var noPolicyOwner = new TempDirectory();
        using var policyOwner = new TempDirectory();
        using var gitIsolationOwner = new TempDirectory();
        var executor = new ProcessCommandExecutor();
        var isolatedGlobalConfig = System.IO.Path.Combine(gitIsolationOwner.Path, "global.gitconfig");
        var isolatedAttributes = System.IO.Path.Combine(gitIsolationOwner.Path, "global.attributes");
        await File.WriteAllTextAsync(isolatedGlobalConfig, string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(isolatedAttributes, string.Empty, TestContext.Current.CancellationToken);
        var source = System.IO.Path.Combine(sourceOwner.Path, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(source, "sample.txt"),
            "alpha\nbeta\n",
            TestContext.Current.CancellationToken);
        await IsolatedGitAsync(executor, source, isolatedGlobalConfig, isolatedAttributes, ["init"]);
        await IsolatedGitAsync(executor, source, isolatedGlobalConfig, isolatedAttributes,
            ["config", "user.name", "Delivery Forge Fixture"]);
        await IsolatedGitAsync(executor, source, isolatedGlobalConfig, isolatedAttributes,
            ["config", "user.email", "fixture@delivery-forge.invalid"]);
        await IsolatedGitAsync(executor, source, isolatedGlobalConfig, isolatedAttributes, ["add", "."]);
        await IsolatedGitAsync(executor, source, isolatedGlobalConfig, isolatedAttributes,
            ["commit", "-m", "fixture without line ending policy"]);
        var noPolicyHead = (await IsolatedGitAsync(
                executor, source, isolatedGlobalConfig, isolatedAttributes, ["rev-parse", "HEAD"]))
            .StandardOutput.Trim();

        var noPolicyClone = System.IO.Path.Combine(noPolicyOwner.Path, "repository");
        await IsolatedGitAsync(
            executor,
            noPolicyOwner.Path,
            isolatedGlobalConfig,
            isolatedAttributes,
            ["-c", "core.autocrlf=true", "clone", "--no-local", "--no-hardlinks", "--", source, noPolicyClone]);
        var ordinaryNoPolicyStatus = await IsolatedGitAsync(
            executor,
            noPolicyClone,
            isolatedGlobalConfig,
            isolatedAttributes,
            ["-c", "core.autocrlf=true", "status", "--porcelain=v1", "--untracked-files=all"]);
        Assert.True(string.IsNullOrEmpty(ordinaryNoPolicyStatus.StandardOutput));
        var noPolicyIdentity = await new GitRepositoryIdentityReader(
                new EnvironmentOverlayCommandExecutor(
                    executor,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["GIT_ATTR_NOSYSTEM"] = "1"
                    }),
                noPolicyClone,
                noPolicyHead)
            .ReadAsync(TestContext.Current.CancellationToken);
        Assert.False(noPolicyIdentity.IsClean);
        var noPolicyIndex = (await IsolatedGitAsync(
                executor, noPolicyClone, isolatedGlobalConfig, isolatedAttributes,
                ["rev-parse", "HEAD:sample.txt"]))
            .StandardOutput.Trim();
        var noPolicyRaw = (await IsolatedGitAsync(
                executor, noPolicyClone, isolatedGlobalConfig, isolatedAttributes,
                ["hash-object", "--no-filters", "sample.txt"]))
            .StandardOutput.Trim();
        var noPolicyFiltered = (await IsolatedGitAsync(
                executor, noPolicyClone, isolatedGlobalConfig, isolatedAttributes,
                ["-c", "core.autocrlf=true", "hash-object", "sample.txt"]))
            .StandardOutput.Trim();
        Assert.NotEqual(noPolicyIndex, noPolicyRaw);
        Assert.Equal(noPolicyIndex, noPolicyFiltered);

        await File.WriteAllTextAsync(
            System.IO.Path.Combine(source, ".gitattributes"),
            "* text eol=lf\n",
            TestContext.Current.CancellationToken);
        await IsolatedGitAsync(executor, source, isolatedGlobalConfig, isolatedAttributes,
            ["add", ".gitattributes"]);
        await IsolatedGitAsync(executor, source, isolatedGlobalConfig, isolatedAttributes,
            ["commit", "-m", "canonicalize fixture line endings"]);
        var policyHead = (await IsolatedGitAsync(
                executor, source, isolatedGlobalConfig, isolatedAttributes, ["rev-parse", "HEAD"]))
            .StandardOutput.Trim();

        var policyClone = System.IO.Path.Combine(policyOwner.Path, "repository");
        await IsolatedGitAsync(
            executor,
            policyOwner.Path,
            isolatedGlobalConfig,
            isolatedAttributes,
            ["-c", "core.autocrlf=true", "clone", "--no-local", "--no-hardlinks", "--", source, policyClone]);
        var eol = await IsolatedGitAsync(
            executor, policyClone, isolatedGlobalConfig, isolatedAttributes,
            ["ls-files", "--eol", "sample.txt"]);
        Assert.Contains("i/lf", eol.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("w/lf", eol.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("attr/text eol=lf", eol.StandardOutput, StringComparison.Ordinal);
        var policyIdentity = await new GitRepositoryIdentityReader(executor, policyClone, policyHead)
            .ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(policyIdentity.IsClean);
        var policyIndex = (await IsolatedGitAsync(
                executor, policyClone, isolatedGlobalConfig, isolatedAttributes,
                ["rev-parse", "HEAD:sample.txt"]))
            .StandardOutput.Trim();
        var policyRaw = (await IsolatedGitAsync(
                executor, policyClone, isolatedGlobalConfig, isolatedAttributes,
                ["hash-object", "--no-filters", "sample.txt"]))
            .StandardOutput.Trim();
        Assert.Equal(policyIndex, policyRaw);
    }

    private static async Task<GateRunResult> RunGateAsync(
        ProcessCommandExecutor executor,
        ToolCapability capability,
        string repository,
        string outputRoot,
        string protectedBase,
        string outputName,
        IReadOnlyList<string> arguments,
        RepositoryIdentity? expectedRepository = null,
        Action<GitStatusCommandDiagnostics?>? captureStatusDiagnostics = null)
    {
        var reader = new GitRepositoryIdentityReader(executor, repository, protectedBase);
        var identity = expectedRepository ?? await reader.ReadAsync(TestContext.Current.CancellationToken);
        var policy = new ResolvedEvidencePolicy(
            TestEvidence.Identity('1'), TestEvidence.Identity('2'), protectedBase,
            PolicyAuthorityKind.ProtectedGitBase, 0m, ["test"], authorityVerified: true,
            sourceRepositoryRoot: repository,
            requiredGatePluginBindings:
            [new RequiredGatePluginBinding("test", "delivery-forge.dotnet", "1.0.0", "dotnet")]);
        var boundedArguments = arguments.Concat(
            ["--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "/nodeReuse:false"])
            .ToArray();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["UseSharedCompilation"] = "false"
        };
        var result = await new GateRunner(
            executor, reader, repository, protectedBase,
            plugins: null, authorityRegistry: null,
            hostOwnedClaimRoot: System.IO.Path.Combine(outputRoot, ".claims", outputName)).RunAsync(
            new GateRequest(
                "test", policy, capability, EvidenceAdapterKind.DotNet,
                TestEvidence.Identity('6'), identity,
                new CommandInvocation(capability.ExecutablePath!, boundedArguments, repository, environment),
                "fixture", TimeSpan.FromSeconds(90), System.IO.Path.Combine(outputRoot, outputName)),
            TestContext.Current.CancellationToken);
        captureStatusDiagnostics?.Invoke(reader.LastIdentityStatusDiagnostics);
        return result;
    }

    private static void ArchiveFixtureEvidence(
        string evidenceRoot,
        GateRunResult result,
        RepositoryDiagnosticBundle? repositoryDiagnostics = null,
        GitStatusCommandDiagnostics? gateStatusDiagnostics = null)
    {
        var destination = Environment.GetEnvironmentVariable("DELIVERY_FORGE_TEST_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(destination)) return;
        if (!Path.IsPathFullyQualified(destination))
            throw new InvalidOperationException("Fixture artifact destination must be explicit and absolute.");
        var ownedLeaf = Path.Combine(destination, "real-dotnet", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ownedLeaf);
        File.WriteAllText(Path.Combine(ownedLeaf, "observed-result.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                result.Outcome,
                result.Reason,
                result.Execution,
                result.Before,
                result.After,
                repositoryDiagnostics,
                gateStatusDiagnostics
            }));
        var files = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var source in Directory.EnumerateFiles(evidenceRoot, "*", files))
        {
            var target = Path.Combine(ownedLeaf, Path.GetRelativePath(evidenceRoot, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
        }
    }

    private static async Task<RepositoryDiagnosticSnapshot> CaptureRepositoryDiagnosticAsync(
        ProcessCommandExecutor executor,
        string repository,
        string protectedBase,
        string label)
    {
        var porcelain = await RunGitDiagnosticAsync(
            executor, repository, ["status", "--porcelain=v1", "--untracked-files=all"]);
        var topLevel = await RunGitDiagnosticAsync(
            executor, repository, ["rev-parse", "--show-toplevel"]);
        var config = await RunGitDiagnosticAsync(
            executor,
            repository,
            [
                "config", "--show-origin", "--get-regexp",
                "^(core\\.(autocrlf|eol|safecrlf|ignorecase|filemode|fsmonitor)|status\\.showUntrackedFiles)$"
            ]);
        // Keep the hardened identity read last. Diagnostic Git commands must not run
        // between the expected snapshot and GateRunner's independent observed read.
        var identityReader = new GitRepositoryIdentityReader(executor, repository, protectedBase);
        var identity = await identityReader.ReadAsync(TestContext.Current.CancellationToken);
        return new RepositoryDiagnosticSnapshot(
            label,
            Environment.CurrentDirectory,
            repository,
            Path.GetFullPath(repository),
            new DirectoryInfo(repository).FullName,
            topLevel.ExitCode,
            topLevel.StandardOutput,
            topLevel.StandardError,
            identity,
            identityReader.LastIdentityStatusDiagnostics,
            porcelain.ExitCode,
            porcelain.StandardOutput,
            porcelain.StandardError,
            config.ExitCode,
            config.StandardOutput,
            config.StandardError);
    }

    private static string FormatRepositoryDiagnostics(
        RepositoryDiagnosticBundle diagnostics,
        RepositoryIdentity expected,
        RepositoryIdentity observed,
        GitStatusCommandDiagnostics? gateStatusDiagnostics) =>
        "Repository identity diagnostics: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            ExpectedRepositoryIdentity = expected,
            ObservedRepositoryIdentity = observed,
            GateStatusCommand = gateStatusDiagnostics,
            Snapshots = diagnostics
        });

    private static async Task<CommandResult> GitAsync(
        ProcessCommandExecutor executor, string directory, IReadOnlyList<string> arguments)
    {
        var result = await RunAsync(executor, "git", directory, arguments);
        Assert.Equal(0, result.ExitCode);
        return result;
    }

    private static async Task<CommandResult> IsolatedGitAsync(
        ProcessCommandExecutor executor,
        string directory,
        string isolatedGlobalConfig,
        string isolatedAttributes,
        IReadOnlyList<string> arguments)
    {
        var isolatedArguments = new[]
        {
            "-c", "core.autocrlf=false",
            "-c", "core.eol=lf",
            "-c", "core.safecrlf=false",
            "-c", "core.fsmonitor=false",
            "-c", $"core.attributesFile={isolatedAttributes}"
        }.Concat(arguments).ToArray();
        var result = await executor.ExecuteAsync(
            new CommandInvocation(
                ResolveExecutable("DELIVERY_FORGE_GIT_HOST", "git"),
                isolatedArguments,
                directory,
                CreateIsolatedGitEnvironment(isolatedGlobalConfig),
                InheritEnvironment: false),
            TimeSpan.FromSeconds(90),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        return result;
    }

    private static IReadOnlyDictionary<string, string> CreateIsolatedGitEnvironment(string isolatedGlobalConfig)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_ATTR_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = isolatedGlobalConfig,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_OPTIONAL_LOCKS"] = "0"
        };
        foreach (var name in new[]
                 {
                     "PATH", "PATHEXT", "SystemRoot", "ComSpec", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE"
                 })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                environment[name] = value;
        }
        return environment;
    }

    private sealed class EnvironmentOverlayCommandExecutor(
        ICommandExecutor inner,
        IReadOnlyDictionary<string, string> environment) : ICommandExecutor
    {
        public Task<CommandResult> ExecuteAsync(
            CommandInvocation invocation,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            if (invocation.Environment is not null)
                foreach (var pair in invocation.Environment)
                    merged[pair.Key] = pair.Value;
            foreach (var pair in environment)
                merged[pair.Key] = pair.Value;
            return inner.ExecuteAsync(invocation with { Environment = merged }, timeout, cancellationToken);
        }
    }

    private static async Task<ToolCapability> DetectDotNetAsync(
        ProcessCommandExecutor executor,
        string dotnet,
        string repository)
    {
        var detection = await new ToolCapabilityDetector(executor).DetectAsync(
            new CapabilityProbe(
                "dotnet", ExecutableSource.TrustedExplicit, dotnet, repository, null,
                ["--version"], ["--help"], "10.0.401", "trx-v1", false),
            TestContext.Current.CancellationToken);
        var capability = detection.Capability;
        Assert.True(capability.Supported,
            $"Dotnet capability: executable={dotnet}; observed={capability.Version}; " +
            $"version exit={detection.VersionResult?.ExitCode}; " +
            $"version output={detection.VersionResult?.StandardOutput} {detection.VersionResult?.StandardError}; " +
            $"help exit={detection.HelpResult?.ExitCode}; " +
            $"help output={detection.HelpResult?.StandardOutput} {detection.HelpResult?.StandardError}");
        Assert.Equal("10.0.401", capability.Version);
        return capability;
    }

    private static string FindRepositoryFile(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, name);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"Repository file '{name}' was not found above the test output directory.");
    }

    private static Task<CommandResult> RunAsync(
        ProcessCommandExecutor executor,
        string executable,
        string directory,
        IReadOnlyList<string> arguments) =>
        executor.ExecuteAsync(
            new CommandInvocation(executable, arguments, directory),
            TimeSpan.FromSeconds(90),
            TestContext.Current.CancellationToken);

    private static Task<CommandResult> RunGitDiagnosticAsync(
        ProcessCommandExecutor executor,
        string directory,
        IReadOnlyList<string> arguments)
    {
        var hardenedArguments = new[] { "-c", "core.fsmonitor=false" }.Concat(arguments).ToArray();
        return executor.ExecuteAsync(
            new CommandInvocation(
                "git",
                hardenedArguments,
                directory,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GIT_OPTIONAL_LOCKS"] = "0"
                }),
            TimeSpan.FromSeconds(90),
            TestContext.Current.CancellationToken);
    }

    private static (int Total, int Executed, int Passed, int Failed) ReadCounts(string trx)
    {
        if (!File.Exists(trx))
            trx = Directory.GetFiles(
                System.IO.Path.GetDirectoryName(trx)!, System.IO.Path.GetFileName(trx), SearchOption.AllDirectories)
                .Single();
        var counters = XDocument.Load(trx).Descendants().First(element => element.Name.LocalName == "Counters");
        return (
            int.Parse(counters.Attribute("total")!.Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(counters.Attribute("executed")!.Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(counters.Attribute("passed")!.Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(counters.Attribute("failed")!.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string ResolveExecutable(string environmentVariable, string name)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException($"Required executable '{name}' is unavailable.");
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = System.IO.Path.Combine(destination, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void DeleteBuildOutputs(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .Where(path => System.IO.Path.GetFileName(path) is "bin" or "obj")
                     .OrderByDescending(path => path.Length).ToArray())
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
    }

    private sealed record RepositoryDiagnosticBundle(
        RepositoryDiagnosticSnapshot BeforeCapabilityProbe,
        RepositoryDiagnosticSnapshot AfterCapabilityProbe,
        RepositoryDiagnosticSnapshot PreGate);

    private sealed record RepositoryDiagnosticSnapshot(
        string Label,
        string ProcessCurrentDirectory,
        string RepositoryArgument,
        string FullRepositoryPath,
        string DirectoryInfoFullName,
        int? GitTopLevelExitCode,
        string GitTopLevelStandardOutput,
        string GitTopLevelStandardError,
        RepositoryIdentity Identity,
        GitStatusCommandDiagnostics? IdentityStatusCommand,
        int? PorcelainExitCode,
        string PorcelainStandardOutput,
        string PorcelainStandardError,
        int? RelevantConfigExitCode,
        string RelevantConfigStandardOutput,
        string RelevantConfigStandardError);
}
