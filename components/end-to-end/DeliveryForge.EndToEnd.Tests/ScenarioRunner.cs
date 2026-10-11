using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Evidence.Adapters;
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
        else if (name is "quality.worker-policy-shadow" or "quality.caller-baseline-gaming" or "quality.incomplete-mutation-preview")
        {
            var observedQuality = await ObserveQualitySafetyAsync(name, receiptIds, invariants, deadline.Token);
            outcome = observedQuality.Outcome;
            limitations = [observedQuality.Reason, .. observedQuality.Limitations,
                "Owned untrusted-input admission only; no actual quality producer, quality PASS or full-profile certificate."];
        }
        else if (name is "scheduling.unknown-map-contention" or "scheduling.real-integrated-prerequisite" or "scheduling.stale-base-before-provision")
        {
            await SchedulingSafetyOrchestrator.ObserveAsync(run, name, receiptIds, invariants, deadline.Token);
            outcome = GateOutcome.Incomplete;
            limitations = ["Actual Git/file/owned native sleep safety observations with public fixture/local scheduling APIs.",
                "Reservation-only local admission and fixture bookkeeping; no native worker, model, quality PASS, distributed ownership or full-profile certificate."];
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

    private async Task<NormalizedEvidence> ObserveQualitySafetyAsync(string name, List<string> receiptIds,
        Dictionary<string, bool> invariants, CancellationToken cancellationToken)
    {
        var inputPath = Path.Combine(run.Root, "quality-input.json");
        var inputBytes = await ReadBoundedQualityFileAsync(inputPath, cancellationToken);
        using var input = JsonDocument.Parse(inputBytes);
        var root = input.RootElement;
        var protectedBase = root.GetProperty("ProtectedBaseCommit").GetString()!;
        if (protectedBase.Length is not (40 or 64) || !protectedBase.All(Uri.IsHexDigit))
            throw new InvalidDataException("Quality policy requires an exact immutable Git object ID.");
        var policyPath = root.GetProperty("PolicyPath").GetString()!;
        var candidatePath = ResolveQualityPath(root.GetProperty("CandidatePath").GetString()!);
        _ = ResolveQualityPath(policyPath);
        var context = await run.Reader.ReadAsync(run.Checkout, protectedBase, cancellationToken);
        var protectedFile = await run.Reader.ReadFileAsync(context, policyPath, cancellationToken);
        var policy = await new ProtectedGitPolicyResolver(new OwnedPolicyGitExecutor(run), run.Checkout, protectedBase)
            .ResolveAsync(policyPath, cancellationToken);
        var candidate = await ReadBoundedQualityFileAsync(candidatePath, cancellationToken);
        var candidateHash = Convert.ToHexStringLower(SHA256.HashData(candidate));
        var policyHash = Convert.ToHexStringLower(SHA256.HashData(protectedFile.Bytes));
        invariants["protected-policy-retained"] = policy.SourceRevision == context.Commit &&
            policy.PolicyIdentity == root.GetProperty("PolicyIdentity").GetString() &&
            policy.ContentIdentity == "sha256:" + policyHash &&
            policy.RequiredGates.SequenceEqual(new[] { "test", "coverage", "mutation" });

        NormalizedEvidence normalized;
        if (name == "quality.worker-policy-shadow")
        {
            var shadow = ContractValidator.ParseAndValidate(candidate);
            invariants["worker-policy-not-authority"] = shadow.Identity != policy.PolicyIdentity &&
                invariants["protected-policy-retained"];
            normalized = NormalizedEvidence.Incomplete(
                "Worker-branch policy and NA exemptions cannot replace the actual protected base policy; required producer receipts are absent.");
        }
        else if (name == "quality.caller-baseline-gaming")
        {
            using var baselineDocument = JsonDocument.Parse(candidate);
            var data = baselineDocument.RootElement;
            var previous = ReadMetrics(data.GetProperty("metrics"));
            var current = ReadMetrics(data.GetProperty("current"));
            // This public constructor cannot register protected baseline authority, even with exact source/nonfixture claims.
            var baseline = new ReviewedBaseline(data.GetProperty("baselineIdentity").GetString()!,
                data.GetProperty("policyIdentity").GetString()!, data.GetProperty("sourceRevision").GetString()!,
                "sha256:" + candidateHash, data.GetProperty("fixture").GetBoolean(), previous);
            normalized = BaselineComparer.Compare(current, baseline, policy);
            invariants["caller-baseline-not-authority"] = normalized.Outcome == GateOutcome.Incomplete && !normalized.ProductionCapable;
            invariants["claimed-nonfixture-not-authority"] = !baseline.Fixture && !normalized.ProductionCapable;
        }
        else
        {
            normalized = new Mutate4CSharpAdapter().Normalize(candidate,
                ToolCapability.Unsupported("mutate4csharp", "No installed-outcome certification supplied by this fixture input."));
            invariants["mutation-certification-unavailable"] = normalized.Outcome == GateOutcome.Incomplete && !normalized.ProductionCapable;
            invariants["preview-success-not-authority"] = normalized.Outcome != GateOutcome.Pass && !normalized.ProductionCapable;
        }
        if (!invariants["protected-policy-retained"])
            normalized = NormalizedEvidence.Error("Protected fixture policy/source bytes did not match the pinned parent setup.");
        else if (normalized.Outcome == GateOutcome.Pass)
            normalized = NormalizedEvidence.Incomplete("Input-only no-regression/preview observations cannot certify an actual quality producer.");
        var record = await run.Store.AppendAsync(run.RunId, "scenario-quality-observation", new
        {
            fixture = true, productionCertified = false, firstFullRealRunCertified = false, scenario = name,
            protectedBaseCommit = context.Commit, protectedBaseTree = context.Tree,
            observedHeadCommit = context.HeadCommit, observedHeadTree = context.HeadTree, context.Dirty,
            policyIdentity = policy.PolicyIdentity, policySha256 = policyHash, policy.RequiredGates,
            candidatePath, candidateSha256 = candidateHash,
            inputSha256 = Convert.ToHexStringLower(SHA256.HashData(inputBytes)),
            ownerOutcome = normalized.Outcome.ToString(), ownerReason = normalized.Reason,
            ownerProductionCapable = normalized.ProductionCapable,
            integratedBaseCommit = run.IntegratedSource.Commit, implementationHead = run.IntegratedSource.HeadCommit,
            limitation = "Untrusted input/policy admission only; no tool execution or producer capability fabricated."
        }, cancellationToken);
        receiptIds.Add(record.Sha256);
        return normalized;
    }

    private string ResolveQualityPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 4096 || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split(['/', '\\']).Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Quality input must use a plain owned relative path.");
        return Path.GetFullPath(Path.Combine(run.Checkout, relative));
    }

    private static async Task<byte[]> ReadBoundedQualityFileAsync(string path, CancellationToken cancellationToken)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Quality input crosses a reparse ancestor.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 64 * 1024)
            throw new InvalidDataException("Quality input is not a bounded plain file.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return bytes.ToArray();
            if (bytes.Length + read > 64 * 1024) throw new InvalidDataException("Quality input grew beyond its finite byte bound.");
            bytes.Write(buffer, 0, read);
        }
    }

    private static IReadOnlyDictionary<string, decimal> ReadMetrics(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Take(129).Count() > 128)
            throw new InvalidDataException("Untrusted metric input exceeds its finite object bound.");
        return new ReadOnlyDictionary<string, decimal>(data.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetDecimal(), StringComparer.Ordinal));
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
