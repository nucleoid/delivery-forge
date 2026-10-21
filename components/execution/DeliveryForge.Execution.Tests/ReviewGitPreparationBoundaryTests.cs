using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.State;
using DeliveryForge.Evidence;
using DeliveryForge.Execution.Host;
using DeliveryForge.Review;

namespace DeliveryForge.Execution.Tests;

public sealed class ReviewGitPreparationBoundaryTests
{
    [Fact(Timeout = 120_000)]
    public async Task Startup_data_tiny_actual_capture_distinguishes_never_created_from_unknown_closure()
    {
        using var bound = Bound(); // Original30 BEFORE real source/Git/helper setup.
        var fixture = await Fixture.CreateAsync("echo", bound.Token);
        var result = await new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(
            new CommandInvocation(fixture.Helper!, [], fixture.Root), TimeSpan.FromTicks(1), 1024, bound.Token);
        await fixture.SaveCaptureAsync(result, bound.Token);
        await fixture.SaveStartupInputsAsync(TimeSpan.FromTicks(1), bound.Token);
        Assert.True(result.Command.TimedOut);
        Assert.False(result.Command.Cancelled);
        Assert.Null(result.Command.ExitCode);
        Assert.False(File.Exists(fixture.EnteredPath));
        Assert.False(result.RootExitObserved);
        Assert.False(result.StandardOutputDrainCompleted);
        Assert.False(result.StandardErrorDrainCompleted);
        // Genuine returned native capture and retained raw observations precede the missing projection.
        var data = ReviewGitPreparationCaptureData.FromCapture(result);
        Assert.True(data.CaptureReturned);
        Assert.True(data.TimedOut);
        Assert.False(data.Cancelled);
        Assert.Null(data.ExitCode);
        var startup = Assert.IsType<CommandStartupObservation>(data.Startup);
        Assert.Equal(CommandStartupState.NotAttempted, startup.Creation);
        Assert.Equal(CommandStartupState.NotAttempted, startup.RootHandle);
        Assert.Equal(OperatingSystem.IsWindows() ? CommandStartupState.NotAttempted : CommandStartupState.NotApplicable, startup.Resume);
        Assert.Equal(CommandStartupState.NotAttempted, startup.StandardOutputDrain);
        Assert.Equal(CommandStartupState.NotAttempted, startup.StandardErrorDrain);
        Assert.False(data.RootExitObserved);
        Assert.False(data.StandardOutputDrainCompleted);
        Assert.False(data.StandardErrorDrainCompleted);
    }

    [Fact(Timeout = 120_000)]
    public async Task Startup_data_actual_healthy_echo_preserves_created_and_each_original_drain()
    {
        using var bound = Bound();
        var fixture = await Fixture.CreateAsync("echo", bound.Token);
        var result = await new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(
            new CommandInvocation(fixture.Helper!, [], fixture.Root), TimeSpan.FromSeconds(5), 1024, bound.Token);
        await fixture.SaveCaptureAsync(result, bound.Token);
        await fixture.SaveStartupInputsAsync(TimeSpan.FromSeconds(5), bound.Token);
        Assert.Equal(0, result.Command.ExitCode);
        Assert.False(result.Command.TimedOut);
        Assert.False(result.Command.Cancelled);
        Assert.False(result.CaptureBoundExceeded);
        Assert.True(result.Command.OwnedProcessQuiescent);
        Assert.True(result.RootExitObserved);
        Assert.True(result.StandardOutputDrainCompleted);
        Assert.True(result.StandardErrorDrainCompleted);
        Assert.Equal(Encoding.UTF8.GetBytes("stdout:λ🙂:" + fixture.Nonce + "\n"), result.StandardOutputBytes);
        Assert.Equal(Encoding.UTF8.GetBytes("stderr:漢字:" + fixture.Nonce + "\n"), result.StandardErrorBytes);
        Assert.Equal(fixture.Nonce, await File.ReadAllTextAsync(fixture.EnteredPath, bound.Token));
        var data = ReviewGitPreparationCaptureData.FromCapture(result);
        Assert.True(data.CaptureReturned);
        Assert.Equal(result.Command.ExitCode, data.ExitCode);
        Assert.Equal(result.Command.OwnedProcessQuiescent, data.OwnedProcessQuiescent);
        Assert.True(data.RootExitObserved);
        Assert.True(data.StandardOutputDrainCompleted);
        Assert.True(data.StandardErrorDrainCompleted);
        var startup = Assert.IsType<CommandStartupObservation>(data.Startup);
        Assert.Equal(CommandStartupState.Established, startup.Creation);
        Assert.Equal(CommandStartupState.Established, startup.RootHandle);
        Assert.Equal(CommandStartupState.Established, startup.ReaderEndpoints);
        Assert.Equal(CommandStartupState.Established, startup.StandardOutputDrain);
        Assert.Equal(CommandStartupState.Established, startup.StandardErrorDrain);
        var platformState = OperatingSystem.IsWindows() ? CommandStartupState.Established : CommandStartupState.NotApplicable;
        Assert.Equal(platformState, startup.JobAssignment);
        Assert.Equal(platformState, startup.Resume);
        Assert.InRange(startup.OriginalElapsedSeconds, 0, 5);
        Assert.Equal(5d, startup.OriginalTotalSeconds);
        Assert.NotEmpty(startup.LastStage);
    }

    [Fact(Timeout = 120_000)]
    public async Task Bounded_capture_healthy_helper_preserves_exact_multibyte_UTF8_bytes()
    {
        using var bound = Bound();
        var fixture = await Fixture.CreateAsync("echo", bound.Token);
        var started = Stopwatch.GetTimestamp();
        var result = await new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(
            new CommandInvocation(fixture.Helper!, [], fixture.Root), TimeSpan.FromSeconds(5), 1024, bound.Token);
        await fixture.SaveCaptureAsync(result, bound.Token);
        Assert.Equal(0, result.Command.ExitCode);
        Assert.False(result.Command.TimedOut);
        Assert.False(result.Command.Cancelled);
        Assert.False(result.CaptureBoundExceeded);
        Assert.True(result.Command.OwnedProcessQuiescent);
        Assert.True(result.RootExitObserved);
        Assert.True(result.StandardOutputDrainCompleted);
        Assert.True(result.StandardErrorDrainCompleted);
        Assert.Equal(Encoding.UTF8.GetBytes("stdout:λ🙂:" + fixture.Nonce + "\n"), result.StandardOutputBytes);
        Assert.Equal(Encoding.UTF8.GetBytes("stderr:漢字:" + fixture.Nonce + "\n"), result.StandardErrorBytes);
        Assert.Equal(fixture.Nonce, await File.ReadAllTextAsync(fixture.EnteredPath, bound.Token));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
    }

    [Fact(Timeout = 120_000)]
    public async Task Bounded_capture_real_both_stream_pressure_exceeds_byte_quota_and_physically_settles()
    {
        using var bound = Bound();
        var fixture = await Fixture.CreateAsync("pressure", bound.Token);
        var started = Stopwatch.GetTimestamp();
        var result = await new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(
            new CommandInvocation(fixture.Helper!, [], fixture.Root), TimeSpan.FromSeconds(5), 1024, bound.Token);
        await fixture.SaveCaptureAsync(result, bound.Token);
        Assert.True(result.CaptureBoundExceeded);
        Assert.False(result.Command.TimedOut);
        Assert.False(result.Command.Cancelled);
        Assert.True(result.Command.OwnedProcessQuiescent);
        Assert.True(result.RootExitObserved);
        Assert.True(result.StandardOutputDrainCompleted);
        Assert.True(result.StandardErrorDrainCompleted);
        Assert.InRange(result.StandardOutputBytes.Length, 1, 1024);
        Assert.InRange(result.StandardErrorBytes.Length, 1, 1024);
        Assert.StartsWith("negative-fixture-stdout\n", Encoding.UTF8.GetString(result.StandardOutputBytes), StringComparison.Ordinal);
        Assert.StartsWith("negative-fixture-stderr\n", Encoding.UTF8.GetString(result.StandardErrorBytes), StringComparison.Ordinal);
        Assert.Equal(fixture.Nonce, await File.ReadAllTextAsync(fixture.EnteredPath, bound.Token));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
    }

    [Fact(Timeout = 120_000)]
    public async Task Real_healthy_Git_preparation_returns_opaque_context_with_actual_version_head_and_index()
    {
        using var bound = Bound();
        var fixture = await Fixture.CreateAsync(null, bound.Token);
        var host = ReviewAuthorityHost.CreateIsolatedTest();
        var started = Stopwatch.GetTimestamp();
        var result = await host.PrepareIsolatedTestGitEvidenceAsync(fixture.Root, fixture.GitDirectory, fixture.Git, 1024 * 1024, TimeSpan.FromSeconds(15), bound.Token);
        await fixture.SaveAsync(result, bound.Token);
        Assert.Equal(ReviewGitPreparationOutcome.Prepared, result.Observation.Outcome);
        Assert.True(result.Observation.OwnedProcessQuiescent);
        Assert.True(result.Observation.RootExitObserved);
        Assert.True(result.Observation.StandardOutputDrainCompleted);
        Assert.True(result.Observation.StandardErrorDrainCompleted);
        var context = Assert.IsType<ControlledGitEvidenceContext>(result.Context);
        using (context)
        {
            Assert.Equal(fixture.Head, context.FrozenHeadCommit);
            Assert.Equal(fixture.Version, context.ExpectedGitVersion);
            Assert.Equal(ReviewAuthorityRealm.Test, context.SourceRealm);
            Assert.Equal(fixture.IndexSha256, Fixture.Hash(File.ReadAllBytes(Path.Combine(fixture.GitDirectory, "index"))));
            using var capture = CancellationTokenSource.CreateLinkedTokenSource(bound.Token); capture.CancelAfter(TimeSpan.FromSeconds(15));
            var identity = await GitPatchIdentity.ComputeAsync(context, fixture.Head, fixture.Head, fixture.SpecSha256,
                new Dictionary<string, string> { ["fixture-spec"] = fixture.SpecSha256 }, capture.Token);
            Assert.Equal(fixture.Head, identity.HeadCommit);
            Assert.Equal(fixture.Version, identity.GitVersion);
        }
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15));
    }

    [Theory(Timeout = 120_000)]
    [InlineData("stall", false, ReviewGitPreparationOutcome.TimedOut)]
    [InlineData("stall", true, ReviewGitPreparationOutcome.Cancelled)]
    [InlineData("pressure", false, ReviewGitPreparationOutcome.CaptureBoundExceeded)]
    public async Task Actual_negative_helper_is_bounded_settled_and_never_mints_Git_context(string mode, bool cancelAfterEntry, ReviewGitPreparationOutcome expected)
    {
        using var bound = Bound();
        var fixture = await Fixture.CreateAsync(mode, bound.Token);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(bound.Token);
        var host = ReviewAuthorityHost.CreateIsolatedTest();
        var started = Stopwatch.GetTimestamp();
        // The helper has its own eight-second fail-safe. Keep five seconds for every actual
        // negative launch so Windows process creation can reach the entry barrier before the
        // owner lifetime expires; the one-second budget could expire inside startup itself.
        var operation = host.PrepareIsolatedTestGitEvidenceAsync(fixture.Root, fixture.GitDirectory, fixture.Helper!, 1024 * 1024,
            TimeSpan.FromSeconds(5), caller.Token);
        if (cancelAfterEntry)
        {
            using var entryBound = CancellationTokenSource.CreateLinkedTokenSource(bound.Token);
            var entry = fixture.WaitEnteredAsync(entryBound.Token);
            try
            {
                if (await Task.WhenAny(operation, entry) == operation) await operation; // missing API must fail immediately, never abandon a created child
                await entry;
                caller.Cancel();
            }
            finally { entryBound.Cancel(); try { await entry; } catch (OperationCanceledException) { } }
        }
        var result = await operation;
        await fixture.SaveAsync(result, bound.Token);
        Assert.Equal(expected, result.Observation.Outcome);
        Assert.Null(result.Context);
        var diagnosticBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            mode,
            cancelAfterEntry,
            expected,
            fixture.Nonce,
            elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
            result.Observation
        });
        var diagnosticMessage = Encoding.UTF8.GetString(diagnosticBytes.AsSpan(0, Math.Min(diagnosticBytes.Length, 16 * 1024)));
        Assert.True(result.Observation.OwnedProcessQuiescent, diagnosticMessage);
        Assert.True(result.Observation.RootExitObserved);
        Assert.True(result.Observation.StandardOutputDrainCompleted);
        Assert.True(result.Observation.StandardErrorDrainCompleted);
        Assert.Equal(fixture.Nonce, await File.ReadAllTextAsync(fixture.EnteredPath, bound.Token));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(7));
        if (mode == "pressure")
        {
            Assert.NotEmpty(result.Observation.CapturedStandardOutput);
            Assert.NotEmpty(result.Observation.CapturedStandardError);
        }
    }

    private static CancellationTokenSource Bound()
    {
        var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); bound.CancelAfter(TimeSpan.FromSeconds(30)); return bound;
    }
    private sealed record Fixture(string Root, string GitDirectory, string Git, string Head, string Version,
        string IndexSha256, string SpecSha256, string Nonce, string? Helper)
    {
        public string EnteredPath => Path.Combine(GitDirectory, "helper-entered-" + Nonce);
        public static async Task<Fixture> CreateAsync(string? mode, CancellationToken token)
        {
            DirectoryInfo? source = new(AppContext.BaseDirectory);
            while (source is not null && !Directory.Exists(Path.Combine(source.FullName, "components", "end-to-end"))) source = source.Parent;
            if (source is null) throw new IOException("Retained source artifact root is absent.");
            var root = Path.Combine(source.FullName, "components", "end-to-end", "artifacts", "test-runs", "review-preparation-" + Guid.NewGuid().ToString("N"));
            for (DirectoryInfo? ancestor = new(root); ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture path is redirected.");
            Directory.CreateDirectory(root);
            var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
                .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "git.exe" : "git")).First(File.Exists);
            var gitDirectory = Path.Combine(root, ".git");
            var environment = new Dictionary<string, string>
            {
                ["GIT_DIR"] = gitDirectory,
                ["GIT_WORK_TREE"] = root,
                ["GIT_COMMON_DIR"] = gitDirectory,
                ["GIT_OBJECT_DIRECTORY"] = Path.Combine(gitDirectory, "objects"),
                ["GIT_INDEX_FILE"] = Path.Combine(gitDirectory, "index"),
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "",
                ["GIT_CONFIG_COUNT"] = "0",
                ["GIT_CONFIG_PARAMETERS"] = "",
                ["GIT_CONFIG"] = Path.Combine(gitDirectory, "config"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_CONFIG_SYSTEM"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_NO_REPLACE_OBJECTS"] = "1",
                ["GIT_NO_LAZY_FETCH"] = "1",
                ["GIT_TERMINAL_PROMPT"] = "0"
            };
            Directory.CreateDirectory(Path.Combine(gitDirectory, "objects"));
            async Task<string> Git(params string[] args)
            {
                var result = await new ProcessCommandExecutor().ExecuteAsync(new(git, args, root, environment), TimeSpan.FromSeconds(15), token);
                if (result.ExitCode != 0 || result.TimedOut || result.Cancelled || !result.OwnedProcessQuiescent) throw new IOException("Actual fixture Git failed/unknown: " + result.StandardError);
                return result.StandardOutput.Trim();
            }
            await Git("init", "-q", "-b", "main");
            await File.WriteAllTextAsync(Path.Combine(root, "subject.txt"), "committed preparation fixture\n", token);
            await Git("add", "--", "subject.txt");
            await Git("-c", "user.name=PreparationFixture", "-c", "user.email=fixture@example.invalid", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"), "commit", "-q", "-m", "actual fixture base");
            var head = await Git("rev-parse", "HEAD"); var version = await Git("--version");
            var nonce = Guid.NewGuid().ToString("N");
            var spec = JsonSerializer.SerializeToUtf8Bytes(new { fixture = true, mode, nonce, originalPreparationSeconds = 15, helperSelfSeconds = 8, noGitFactsFromHelper = true });
            string? helper = null;
            if (mode is not null)
            {
                helper = Environment.GetEnvironmentVariable("DELIVERY_FORGE_REVIEW_GIT_NEGATIVE_HELPER");
                if (helper is null || !Path.IsPathFullyQualified(helper) || !File.Exists(helper)) throw new PlatformNotSupportedException("Root-built actual negative helper apphost is unavailable.");
                await WriteNew(Path.Combine(gitDirectory, "prepare-negative-fixture.json"), spec, token);
            }
            await WriteNew(Path.Combine(gitDirectory, "owned-test-spec.json"), spec, token);
            return new(root, gitDirectory, git, head, version, Hash(File.ReadAllBytes(Path.Combine(gitDirectory, "index"))), Hash(spec), nonce, helper);
        }
        public async Task WaitEnteredAsync(CancellationToken token)
        {
            var started = Stopwatch.GetTimestamp();
            while (!File.Exists(EnteredPath))
            { token.ThrowIfCancellationRequested(); if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5)) throw new TimeoutException("Actual negative helper never entered."); await Task.Delay(10, token); }
        }
        public Task SaveAsync(ReviewGitPreparationResult result, CancellationToken token) => WriteNew(Path.Combine(GitDirectory, "actual-preparation-result.json"), JsonSerializer.SerializeToUtf8Bytes(new { fixture = true, result.Observation, contextIssued = result.Context is not null, retained = true }), token);
        public async Task SaveStartupInputsAsync(TimeSpan actualBudget, CancellationToken token)
        {
            var helperBytes = await File.ReadAllBytesAsync(Helper!, token);
            var sourceBytes = await File.ReadAllBytesAsync(Path.Combine(Root, "subject.txt"), token);
            await WriteNew(Path.Combine(GitDirectory, "actual-startup-inputs.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                fixture = true,
                productionCertified = false,
                Nonce,
                Head,
                Version,
                IndexSha256,
                SpecSha256,
                invocation = new CommandInvocation(Helper!, [], Root),
                helperSha256 = Hash(helperBytes),
                helperLength = helperBytes.Length,
                sourceSha256 = Hash(sourceBytes),
                originalCaptureTicks = actualBudget.Ticks,
                originalFixtureSeconds = 30,
                helperSelfSeconds = 8,
                perStreamMaximumBytes = 1024,
                retained = true
            }), token);
        }
        public Task SaveCaptureAsync(BoundedCommandCaptureResult result, CancellationToken token) => WriteNew(
            Path.Combine(GitDirectory, "actual-bounded-capture-result.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                fixture = true,
                nonce = Nonce,
                result.Command,
                result.CaptureBoundExceeded,
                result.RootExitObserved,
                result.StandardOutputDrainCompleted,
                result.StandardErrorDrainCompleted,
                stdoutBytes = result.StandardOutputBytes,
                stderrBytes = result.StandardErrorBytes,
                stdoutSha256 = Hash(result.StandardOutputBytes),
                stderrSha256 = Hash(result.StandardErrorBytes),
                retained = true
            }), token);
        public static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        private static async Task WriteNew(string path, byte[] bytes, CancellationToken token)
        { await using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await writer.WriteAsync(bytes, token); }
    }
}
