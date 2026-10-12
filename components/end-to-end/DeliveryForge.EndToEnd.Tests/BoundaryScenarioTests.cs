using System.Security.Cryptography;
using DeliveryForge.Evidence;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class BoundaryScenarioTests
{
    private static ScenarioOptions Options => new(ScenarioProfile.LocalContract, [],
        new(TimeSpan.FromSeconds(90), 2, 4096, 1000, 0m), 11017);

    [Fact(Timeout = 120_000)]
    public async Task Actual_interrupted_command_retains_deadline_and_quiescence_receipt()
    {
        var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await new ScenarioRunner(run).RunAsync("recovery.interrupted-command", Options, TestContext.Current.CancellationToken);
            Assert.Equal(GateOutcome.Incomplete, result.Outcome);
            Assert.True(result.Invariants.TryGetValue("real-command-deadline-observed", out var observed) && observed,
                "A generic incomplete result is not an observed real command timeout.");
            Assert.True(result.Invariants["command-owned-quiescent"]);
            Assert.False(result.FullRealProfileCertified);
            var records = await run.Store.RecoverAsync(run.RunId, TestContext.Current.CancellationToken);
            var command = Assert.Single(records.Records, record => record.Kind == "scenario-command");
            var execution = command.Payload.GetProperty("execution");
            Assert.True(execution.GetProperty("timedOut").GetBoolean());
            Assert.False(execution.GetProperty("cancelled").GetBoolean());
            Assert.True(execution.GetProperty("ownedProcessQuiescent").GetBoolean());
            Assert.Contains(command.Sha256, result.ReceiptIds);
            run.MarkAssertionsPassed();
        }
        finally { await run.DisposeAsync(); }
        Assert.True(Directory.Exists(run.Root), "Observed non-pass command evidence must survive a passing safety assertion.");
    }

    [Fact(Timeout = 120_000)]
    public async Task Corrupt_immutable_record_is_rejected_and_kept_with_independent_diagnostic()
    {
        var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await new ScenarioRunner(run).RunAsync("recovery.immutable-record-corruption", Options, TestContext.Current.CancellationToken);
            Assert.Equal(GateOutcome.Error, result.Outcome);
            Assert.True(result.Invariants["immutable-corruption-rejected"]);
            var records = await run.Store.RecoverAsync(run.RunId, TestContext.Current.CancellationToken);
            var diagnostic = Assert.Single(records.Records, record => record.Kind == "scenario-corruption-observation");
            var path = diagnostic.Payload.GetProperty("corruptRecordPath").GetString()!;
            Assert.StartsWith(run.Root + Path.DirectorySeparatorChar, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            var observed = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
            Assert.NotEqual(diagnostic.Payload.GetProperty("expectedSha256").GetString(), observed);
            Assert.Equal(diagnostic.Payload.GetProperty("observedSha256").GetString(), observed);
            Assert.Contains(diagnostic.Sha256, result.ReceiptIds);
            Assert.False(result.FullRealProfileCertified);
            run.MarkAssertionsPassed();
        }
        finally { await run.DisposeAsync(); }
        Assert.True(Directory.Exists(run.Root), "Corrupt bytes must not be erased by a passing rejection assertion.");
    }
}
