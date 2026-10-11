using System.Collections.ObjectModel;
using System.Security.Cryptography;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;

namespace DeliveryForge.EndToEnd.Tests;

public enum ScenarioProfile { LocalContract, LocalReal, ProductionCoordinated }

// Capability declarations are diagnostic data. They cannot mint owner-issued capability/authority receipts.
public sealed record ScenarioCapabilityClaim(string Name, bool Available, bool Fixture, string EvidenceReference);
public sealed record ScenarioBudget(TimeSpan WallTime, int CpuCores, long MemoryMiB, long Tokens, decimal Cost);
public sealed record ScenarioOptions(ScenarioProfile Profile, IReadOnlyList<ScenarioCapabilityClaim> Capabilities,
    ScenarioBudget Budget, int Seed);
public sealed record ScenarioResult(GateOutcome Outcome, ScenarioProfile Profile, bool Fixture, bool FullRealProfileCertified,
    string IntegratedBaseCommit, string ImplementationHead, int Seed, ScenarioBudget Budget,
    IReadOnlyDictionary<string, bool> Invariants, IReadOnlyList<string> ReceiptIds, IReadOnlyList<string> Limitations);

// Narrow contract scenarios only. No worker, reviewer or publication authority is minted by this component.
// Remaining scenario behavior is added only after separate compiled RED slices and actual owner integration.
public sealed class ScenarioRunner(OwnedScenarioRun run)
{
    public async Task<ScenarioResult> RunAsync(string name, ScenarioOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new ArgumentException("An explicit bounded scenario name is required.", nameof(name));
        if (!Enum.IsDefined(options.Profile)) throw new ArgumentOutOfRangeException(nameof(options.Profile));
        if (options.Budget is null || options.Budget.WallTime <= TimeSpan.Zero || options.Budget.WallTime > TimeSpan.FromSeconds(120) ||
            options.Budget.CpuCores is < 1 or > 2 || options.Budget.MemoryMiB is < 1 or > 4096 ||
            options.Budget.Tokens is < 1 or > 1000 || options.Budget.Cost != 0m)
            throw new ArgumentOutOfRangeException(nameof(options.Budget), "This development slice permits only finite approved no-cost fixture budgets.");
        if (options.Capabilities is null || options.Capabilities.Count > 16 || options.Capabilities.Any(claim => claim is null ||
            string.IsNullOrWhiteSpace(claim.Name) || claim.Name.Length > 128 || claim.EvidenceReference is null || claim.EvidenceReference.Length > 4096))
            throw new ArgumentException("Capability claims must be bounded diagnostic data.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        var captured = options with { Capabilities = Array.AsReadOnly(options.Capabilities.ToArray()) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(captured.Budget.WallTime);
        var invariants = new Dictionary<string, bool>(StringComparer.Ordinal) { ["no-publication-writes"] = true };
        var receiptIds = new List<string>();
        var outcome = GateOutcome.Incomplete;
        IReadOnlyList<string> limitations;
        if (captured.Profile == ScenarioProfile.ProductionCoordinated)
        {
            var unavailable = ProductionCoordinationProfile.CurrentUnavailable();
            limitations = [.. unavailable.Limitations, "This scenario harness supplies no deployed transport or production authority."];
        }
        else if (captured.Profile == ScenarioProfile.LocalReal)
        {
            limitations = ["Actual configured host worker, independent reviewer and installed .NET producer receipts are absent.",
                "Caller available/fixture flags are diagnostic claims, not owner-issued capability or acceptance receipts."];
        }
        else if (name == "recovery.torn-run-pointer")
        {
            var recovered = await RecoverTornPointerAsync(receiptIds, deadline.Token);
            invariants["durable-pointer-recovered"] = recovered;
            outcome = recovered ? GateOutcome.Pass : GateOutcome.Incomplete;
            limitations = ["Real durable-file contract scenario only; no worker/reviewer/tool/full-profile certification.",
                "Scheduler/publication golden path awaits actual integrated owner interfaces."];
        }
        else if (name == "recovery.interrupted-command")
        {
            var execution = await run.ExecuteBoundedChildCommandAsync(TimeSpan.FromMilliseconds(200), deadline.Token);
            var record = await run.Store.AppendAsync(run.RunId, "scenario-command", new
            {
                fixture = true, integratedBaseCommit = run.IntegratedSource.Commit,
                implementationHead = run.IntegratedSource.HeadCommit, execution
            }, deadline.Token);
            receiptIds.Add(record.Sha256);
            invariants["real-command-deadline-observed"] = execution.TimedOut && !execution.Cancelled;
            invariants["command-owned-quiescent"] = execution.OwnedProcessQuiescent;
            outcome = invariants["real-command-deadline-observed"] && execution.OwnedProcessQuiescent
                ? GateOutcome.Incomplete : GateOutcome.Error;
            limitations = ["Actual interrupted owned native command; observed timeout is non-pass and its bytes are retained.",
                "A native sleep is process-boundary evidence, not a host worker or installed quality-tool certificate."];
        }
        else if (name == "recovery.immutable-record-corruption")
        {
            var corrupt = await RejectCorruptRecordAsync(receiptIds, deadline.Token);
            invariants["immutable-corruption-rejected"] = corrupt;
            outcome = corrupt ? GateOutcome.Error : GateOutcome.Incomplete;
            limitations = ["Corrupt immutable bytes were rejected; independent diagnostic evidence does not repair or authorize the corrupt history."];
        }
        else
        {
            limitations = ["This scenario remains planned and has no executed behavior evidence: " + name,
                "No local-contract fallback can certify the complete profile or unavailable real adapters."];
        }
        if (outcome != GateOutcome.Pass) run.RetainArtifacts("Scenario remains non-pass: " + outcome);
        var observed = new ReadOnlyDictionary<string, bool>(invariants);
        var recorded = await run.Store.AppendAsync(run.RunId, "scenario-result", new
        {
            schemaVersion = "1.0.0", scenario = name, profile = captured.Profile.ToString(), fixture = true,
            fullRealProfileCertified = false, integratedBaseCommit = run.IntegratedSource.Commit,
            implementationHead = run.IntegratedSource.HeadCommit, seed = captured.Seed, budget = captured.Budget,
            capabilityClaims = captured.Capabilities, outcome = outcome.ToString().ToUpperInvariant(), invariants = observed,
            receiptIds = receiptIds.ToArray(), limitations
        }, deadline.Token);
        receiptIds.Add(recorded.Sha256);
        var result = new ScenarioResult(outcome, captured.Profile, true, false, run.IntegratedSource.Commit,
            run.IntegratedSource.HeadCommit, captured.Seed, captured.Budget, observed, receiptIds.AsReadOnly(), limitations);
        await run.WriteNewJsonAsync("scenario-" + Guid.NewGuid().ToString("N") + ".json", result, deadline.Token);
        return result;
    }

    private async Task<bool> RecoverTornPointerAsync(List<string> receiptIds, CancellationToken cancellationToken)
    {
        if ((await run.Store.RecoverAsync(run.RunId, cancellationToken)).Records.Count != 0)
            throw new InvalidOperationException("This fresh scenario cannot overwrite an existing run history.");
        var before = await run.Store.AppendAsync(run.RunId, "fixture-checkpoint",
            new { fixture = true, phase = "before", integratedBaseCommit = run.IntegratedSource.Commit }, cancellationToken);
        var after = await run.Store.AppendAsync(run.RunId, "fixture-checkpoint",
            new { fixture = true, phase = "after", integratedBaseCommit = run.IntegratedSource.Commit }, cancellationToken);
        receiptIds.Add(before.Sha256);
        receiptIds.Add(after.Sha256);
        var pointer = Path.Combine(Path.GetDirectoryName(after.Path)!, "current.json");
        await File.WriteAllTextAsync(pointer, "{interrupted-pointer-write", cancellationToken);
        var recovery = await new RunStore(Path.Combine(run.Root, "receipts")).RecoverAsync(run.RunId, cancellationToken);
        return recovery.PointerWasRecovered && recovery.Latest?.Sha256 == after.Sha256 &&
            recovery.Records.Select(record => record.Sha256).SequenceEqual([before.Sha256, after.Sha256]);
    }

    private async Task<bool> RejectCorruptRecordAsync(List<string> receiptIds, CancellationToken cancellationToken)
    {
        var corruptRunId = run.RunId + "-corrupt-fixture";
        var original = await run.Store.AppendAsync(corruptRunId, "fixture-checkpoint",
            new { fixture = true, phase = "original", integratedBaseCommit = run.IntegratedSource.Commit }, cancellationToken);
        await File.AppendAllTextAsync(original.Path, "\ncorrupt-bytes", cancellationToken);
        var observedHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(original.Path, cancellationToken)));
        var rejected = false;
        try { await new RunStore(Path.Combine(run.Root, "receipts")).RecoverAsync(corruptRunId, cancellationToken); }
        catch (RunStoreCorruptionException) { rejected = true; }
        var diagnostic = await run.Store.AppendAsync(run.RunId, "scenario-corruption-observation", new
        {
            fixture = true, corruptRunId, corruptRecordPath = original.Path,
            expectedSha256 = original.Sha256, observedSha256 = observedHash,
            typedCorruptionRejected = rejected, integratedBaseCommit = run.IntegratedSource.Commit,
            implementationHead = run.IntegratedSource.HeadCommit
        }, cancellationToken);
        receiptIds.Add(diagnostic.Sha256);
        return rejected && original.Sha256 != observedHash;
    }
}
