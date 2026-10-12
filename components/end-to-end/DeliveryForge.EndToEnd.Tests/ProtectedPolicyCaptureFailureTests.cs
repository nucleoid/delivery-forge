using System.Diagnostics;
using System.Security.Cryptography;
using DeliveryForge.Evidence;
using DeliveryForge.Review;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class ProtectedPolicyCaptureFailureTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("settled-timeout")]
    [InlineData("entered-cancellation")]
    [InlineData("settled-quota")]
    [InlineData("unobserved-launch")]
    public async Task Protected_policy_typed_refusal_preserves_actual_owner_capture_without_authority(string scenario)
    {
        var started = Stopwatch.GetTimestamp();
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wall.CancelAfter(TimeSpan.FromSeconds(120));
        await using var run = await OwnedScenarioRun.CreateAsync(wall.Token);
        run.RetainArtifacts("Actual capture before missing diagnostic routing; nonpass native facts retained.");
        var helper = Environment.GetEnvironmentVariable("DELIVERY_FORGE_REVIEW_GIT_NEGATIVE_HELPER");
        if (helper is null || !Path.IsPathFullyQualified(helper) || !File.Exists(helper))
            throw new PlatformNotSupportedException("Root-built actual bounded capture helper is unavailable.");
        var nonce = Guid.NewGuid().ToString("N");
        var mode = scenario == "settled-quota" ? "pressure" : "stall";
        await run.WriteNewJsonAsync("capture-diagnostic-setup.json", new
        {
            fixture = true,
            scenario,
            nonce,
            mode,
            helper,
            originalCaptureSeconds = scenario == "settled-timeout" ? 1 : 5,
            quotaBytesEach = 1024,
            helperSelfSeconds = 8,
            noGitFactsFromHelper = true
        }, wall.Token);
        var metadataPath = Path.Combine(run.Checkout, ".git", "prepare-negative-fixture.json");
        await using (var writer = new FileStream(metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await System.Text.Json.JsonSerializer.SerializeAsync(writer, new { nonce, mode }, cancellationToken: wall.Token);
            await writer.FlushAsync(wall.Token);
        }
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(wall.Token);
        var cwd = scenario == "unobserved-launch" ? Path.Combine(run.Checkout, "absent-owned-cwd-" + nonce) : run.Checkout;
        var operation = new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(new(helper, [], cwd),
            TimeSpan.FromSeconds(scenario == "settled-timeout" ? 1 : 5), 1024, caller.Token);
        if (scenario == "entered-cancellation")
        {
            var enteredPath = Path.Combine(run.Checkout, ".git", "helper-entered-" + nonce);
            using var entryBound = CancellationTokenSource.CreateLinkedTokenSource(wall.Token); entryBound.CancelAfter(TimeSpan.FromSeconds(5));
            async Task WaitEntered()
            {
                while (!File.Exists(enteredPath)) { entryBound.Token.ThrowIfCancellationRequested(); await Task.Delay(10, entryBound.Token); }
                Assert.Equal(nonce, await File.ReadAllTextAsync(enteredPath, entryBound.Token));
            }
            var entry = WaitEntered();
            try
            {
                if (await Task.WhenAny(operation, entry) == operation)
                {
                    var early = await operation;
                    throw new InvalidOperationException("Actual capture ended before cancellation entry: " + early.Command.StandardError);
                }
                await entry; caller.Cancel();
            }
            catch
            {
                caller.Cancel();
                var finalCapture = await operation;
                await run.WriteNewJsonAsync("capture-entry-failure-owner-observation.json", new
                { fixture = true, finalCapture.Command, finalCapture.RootExitObserved, finalCapture.StandardOutputDrainCompleted, finalCapture.StandardErrorDrainCompleted }, wall.Token);
                throw;
            }
            finally { entryBound.Cancel(); try { await entry; } catch (OperationCanceledException) { } }
        }
        var actual = await operation;
        await run.WriteNewJsonAsync("capture-diagnostic-actual-result.json", new
        {
            fixture = true,
            scenario,
            nonce,
            actual.Command,
            actual.CaptureBoundExceeded,
            actual.RootExitObserved,
            actual.StandardOutputDrainCompleted,
            actual.StandardErrorDrainCompleted,
            stdoutBytes = actual.StandardOutputBytes,
            stderrBytes = actual.StandardErrorBytes,
            stdoutSha256 = Hash(actual.StandardOutputBytes),
            stderrSha256 = Hash(actual.StandardErrorBytes),
            limitation = "Actual data only; unproved closure is not a claim that a live orphan exists."
        }, wall.Token);
        if (scenario != "unobserved-launch")
        {
            var diagnosticBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                actual.Command,
                actual.RootExitObserved,
                actual.StandardOutputDrainCompleted,
                actual.StandardErrorDrainCompleted,
                actual.CaptureBoundExceeded
            });
            var diagnosticMessage = System.Text.Encoding.UTF8.GetString(diagnosticBytes.AsSpan(0, Math.Min(diagnosticBytes.Length, 16 * 1024)));
            Assert.True(actual.Command.OwnedProcessQuiescent, diagnosticMessage);
            Assert.True(actual.RootExitObserved);
            Assert.True(actual.StandardOutputDrainCompleted); Assert.True(actual.StandardErrorDrainCompleted);
        }
        if (scenario == "settled-timeout") { Assert.True(actual.Command.TimedOut); Assert.False(actual.Command.Cancelled); }
        if (scenario == "entered-cancellation") { Assert.True(actual.Command.Cancelled); Assert.False(actual.Command.TimedOut); }
        if (scenario == "settled-quota") { Assert.True(actual.CaptureBoundExceeded); Assert.False(actual.Command.TimedOut); Assert.False(actual.Command.Cancelled); }
        if (scenario == "unobserved-launch")
        {
            Assert.False(actual.Command.OwnedProcessQuiescent); Assert.False(actual.RootExitObserved);
            Assert.Null(actual.Command.ExitCode); Assert.False(actual.Command.TimedOut); Assert.False(actual.Command.Cancelled);
        }
        // The source setup/capture controls above must pass before the missing public mapping is RED.
        var failure = ProtectedPolicyReadFailure.Create(actual, caller.Token, "Protected policy capture refusal.");
        if (scenario == "settled-timeout") Assert.IsType<TimeoutException>(failure);
        else if (scenario == "entered-cancellation") Assert.IsType<OperationCanceledException>(failure);
        else Assert.IsType<ReviewPolicyException>(failure);
        var diagnostic = Assert.IsType<ProtectedPolicyCaptureDiagnosticException>(failure.InnerException);
        Assert.Equal(!actual.Command.OwnedProcessQuiescent, diagnostic.ResidualProcessUncertain);
        Assert.Equal(actual.Command, diagnostic.ActualCapture.Command);
        Assert.Equal(actual.RootExitObserved, diagnostic.ActualCapture.RootExitObserved);
        Assert.Equal(actual.StandardOutputDrainCompleted, diagnostic.ActualCapture.StandardOutputDrainCompleted);
        Assert.Equal(actual.StandardErrorDrainCompleted, diagnostic.ActualCapture.StandardErrorDrainCompleted);
        Assert.Equal(actual.CaptureBoundExceeded, diagnostic.ActualCapture.CaptureBoundExceeded);
        Assert.Equal(Hash(actual.StandardOutputBytes), Hash(diagnostic.ActualCapture.StandardOutputBytes));
        Assert.Equal(Hash(actual.StandardErrorBytes), Hash(diagnostic.ActualCapture.StandardErrorBytes));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(120));
    }
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
