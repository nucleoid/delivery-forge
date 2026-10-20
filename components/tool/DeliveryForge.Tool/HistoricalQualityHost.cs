using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Planning;

namespace DeliveryForge.Tool;

// Independently selected expected source and installed profile locators, never
// an accepted worker, gate result, registry approval or live native identity.
public sealed record HistoricalQualitySelection(string HeadCommit, string TreeId, InstalledQualitySelection Quality);

internal sealed class HistoricalQualityHost
{
    readonly TrustedConfiguration configuration;
    readonly AdmissionFile configurationPin;
    readonly ToolLifetime original;
    readonly LinuxScopeSelection? linuxScope;
    readonly HistoricalQualitySelection selection;
    FrozenPlan? frozen;
    FrozenWorkerPlan? assignment;
    bool entered;
    InstalledToolHostScope? tools;
    internal bool Unknown => tools?.HasUncertainOriginals == true;

    internal HistoricalQualityHost(TrustedConfiguration config, AdmissionFile pin, ToolLifetime life, LinuxScopeSelection? scope = null)
    {
        selection = config.HistoricalQuality ?? throw new ToolUsageException("Explicit historical quality selection required.");
        ValidateSelection(config, selection);
        selection = selection with { Quality = selection.Quality with { Gates = Array.AsReadOnly(selection.Quality.Gates.ToArray()) } };
        configuration = config; configurationPin = pin; original = life; linuxScope = scope;
    }

    internal static void ValidateSelection(TrustedConfiguration config, HistoricalQualitySelection value)
    {
        static bool ObjectId(string? text) => text is { Length: 40 or 64 } && text.All(char.IsAsciiHexDigitLower);
        if (!ObjectId(value.HeadCommit) || !ObjectId(value.TreeId) || value.Quality is null ||
            value.Quality.Registry is not null || config.InstalledQuality is not null || config.InstalledReview is not null || config.LocalRealGolden is not null)
            throw new ToolUsageException("Historical gates require exact expected H0/tree and their own two-gate selection, without registry/review/publication selections.");
        InstalledQualityHost.ValidateSelection(value.Quality);
    }

    internal void Bind(FrozenPlan plan, FrozenWorkerPlan run, TrustedConfiguration config, ToolLifetime life)
    {
        original.Check();
        var requested = config.HistoricalQuality;
        if (frozen is not null || !ReferenceEquals(life, original) || config.ExpectedBundleIdentity != configuration.ExpectedBundleIdentity ||
            requested is null || requested.HeadCommit != selection.HeadCommit || requested.TreeId != selection.TreeId ||
            plan.Identity != config.ExpectedBundleIdentity || plan.ContractIdentity != run.PlanIdentity || plan.BaseCommit != run.BaseCommit ||
            plan.Repository != config.Repository || run.Repository != config.Repository ||
            Path.GetFullPath(run.ParentCheckout) != Path.GetFullPath(config.RepositoryRoot) || run.BaseCommit == selection.HeadCommit)
            throw new ToolUsageException("Historical gates bind once to the same freshly imported P/plan/run/original and distinct expected H0.");
        _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536);
        using var document = JsonDocument.Parse(plan.CanonicalBytes);
        var ids = document.RootElement.GetProperty("plan").GetProperty("gates").EnumerateArray().Select(g => g.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
        if (selection.Quality.Gates.Any(g => !ids.Contains(g.GateId))) throw new ToolUsageException("Historical gate absent from the frozen plan.");
        foreach (var source in new[] { run.ParentCheckout, run.Worktree, run.ResultDirectory })
        {
            BoundedFiles.Separate(source, selection.Quality.ArtifactRoot);
            BoundedFiles.Separate(source, configurationPin.Path);
            foreach (var gate in selection.Quality.Gates) BoundedFiles.Separate(source, gate.Profile.Path);
        }
        foreach (var gate in selection.Quality.Gates) BoundedFiles.Separate(selection.Quality.ArtifactRoot, gate.Profile.Path);
        BoundedFiles.Separate(selection.Quality.ArtifactRoot, configurationPin.Path);
        frozen = plan; assignment = run;
    }

    internal static void RequireNoWorker(HostBridgeFrame frame)
    {
        if (frame.Worker.Started || frame.Request is not null || frame.Accepted is not null || frame.Completion is not null || frame.OwnerCompleted || frame.OwnerStopped)
            throw new WorkerEnvelopeException("Historical gates cannot consume or manufacture a worker/session/completion.");
    }

    internal async Task<ToolResult> RunAsync(HostBridgeFrame frame, ParentCoordinationHost parent, Func<bool> actualNoWorkerStarted)
    {
        if (entered || frozen is null || assignment is null || frame.RunId != assignment.RunId || !ReferenceEquals(frame.Lifetime, original))
            throw new ToolUsageException("Same original historical gate route enters once; no saved result replay.");
        entered = true;
        var run = assignment; var actualPlan = frozen;
        RequireNoWorker(frame);
        var actualTools = new InstalledToolHostScope(original, parent, run.RunId, linuxScope); tools = actualTools;
        var output = Path.Combine(selection.Quality.ArtifactRoot, run.RunId);
        var expected = new RepositoryIdentity(run.BaseCommit, selection.HeadCommit, selection.TreeId, true);
        var references = new List<object>();
        void Current()
        {
            original.Check(); parent.CheckCurrent(run.RunId); RequireNoWorker(frame);
            if (!actualNoWorkerStarted() || actualTools.HasUncertainOriginals) throw new WorkerEnvelopeException("Historical original has a worker start or uncertain gate operation.");
            _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536);
        }
        parent.EnterSynchronous(run.RunId, () =>
        {
            Current(); BoundedFiles.Plain(output, true);
            if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Historical artifact namespace already exists; no replay.");
            Directory.CreateDirectory(output); original.Check(); return true;
        });
        foreach (var gate in selection.Quality.Gates)
        {
            var profile = parent.EnterSynchronous(run.RunId, () =>
            {
                Current(); _ = InstalledHostAdmission.ReadExact(gate.Profile, original, 1048576);
                var value = actualTools.LoadProfile(gate.Profile.Path, "sha256:" + gate.Profile.Sha256);
                if (value.Fixture || value.Tool != gate.Tool) throw new ToolUsageException("Fresh independently selected nonfixture installed producer required; no fixture profile promotion.");
                return value;
            });
            // The scope owns real policy/Git/native calls and checks exact clean
            // P/H0/tree before and after every tool. No owner callback supplies
            // success. Retain each returned actual task before awaiting it.
            var result = await frame.Enter(() => actualTools.RunAsync(run.Worktree, run.BaseCommit,
                selection.Quality.PolicyRelativePath, selection.Quality.PolicyIdentity, profile, gate.GateId,
                expected, gate.Scope, gate.ProjectRelativePath, TimeSpan.FromSeconds(gate.MaximumSeconds), output,
                original.Token, baselineRegistry: null)).ConfigureAwait(false);
            parent.EnterSynchronous(run.RunId, () =>
            {
                Current(); _ = InstalledHostAdmission.ReadExact(gate.Profile, original, 1048576);
                var bytes = BoundedFiles.Read(result.ReceiptPath, original, 1048576);
                var gateRoot = Path.GetDirectoryName(Path.GetDirectoryName(result.ReceiptPath))!;
                result.ArtifactHashes.TryGetValue("producer-report.json", out var report);
                result.ArtifactHashes.TryGetValue("producer-current-binding.json", out var binding);
                references.Add(new
                {
                    gate.Tool,
                    gate.GateId,
                    result.Outcome,
                    result.Fixture,
                    result.ProductionCapable,
                    Receipt = new AdmissionFile(result.ReceiptPath, bytes.LongLength, BoundedFiles.Sha(bytes)),
                    ArtifactRoot = gateRoot,
                    Report = new { Path = Path.Combine(gateRoot, "producer-report.json"), Identity = report },
                    Binding = new { Path = Path.Combine(gateRoot, "producer-current-binding.json"), Identity = binding }
                });
                return true;
            });
            if (!InstalledQualityHost.IsExactCurrentGatePass(result, gate, selection.Quality.PolicyIdentity, expected))
                return ToolResult.Incomplete("HISTORICAL_QUALITY_NONPASS", "Actual historical gates did not both pass; original artifacts remain evidence and no baseline is approved.", new { run.RunId, expected, Gates = references.ToArray(), BaselineApproved = false });
        }
        return parent.EnterSynchronous(run.RunId, () =>
        {
            Current();
            var data = new
            {
                Kind = "HistoricalQualityCandidate-DATA-v1",
                run.RunId,
                FrozenPlan = actualPlan.ContractIdentity,
                Repository = expected,
                selection.Quality.PolicyIdentity,
                Gates = references.ToArray(),
                WorkerStarted = false,
                BaselineApproved = false,
                RegistryIssued = false,
                ProductionAuthority = false,
                SchedulingCertified = false
            };
            var path = Path.Combine(output, "baseline-candidate-DATA.json");
            BoundedFiles.WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(data, BoundedFiles.Json), original);
            return ToolResult.Incomplete("HISTORICAL_BASELINE_CANDIDATE_UNAPPROVED", "Both real historical gates passed. Complete immutable artifacts are a baseline candidate requiring independent owner review and approval.", new { Candidate = path, Data = data });
        });
    }
}
