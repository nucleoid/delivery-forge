using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
namespace DeliveryForge.Tool;

// Explicit trusted existing paths; no writer token, accepted result, native ID,
// capability flag or serialized private parent object is accepted here.
public sealed record LocalWorkerPaths(string Worktree, string ResultDirectory, IReadOnlyList<string> AllowedPaths, IReadOnlyList<string> Exclusions);
public sealed class LocalWorkerAssignmentPort : IWorkerAssignmentPort
{
    readonly ToolLifetime original; readonly LocalWorkerPaths selected; readonly object gate = new(); bool entered;
    public LocalWorkerAssignmentPort(ToolLifetime actualOriginal, LocalWorkerPaths paths)
    {
        if (paths.AllowedPaths is null || paths.Exclusions is null || paths.AllowedPaths.Count is < 1 or > 128 || paths.Exclusions.Count > 128) throw new ToolUsageException("Finite independently selected local worker paths required.");
        if (paths.AllowedPaths.Concat(paths.Exclusions).Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0'))) throw new ToolUsageException("Finite complete worker path strings required before snapshot.");
        actualOriginal.Check(); original = actualOriginal; selected = paths with { AllowedPaths = Array.AsReadOnly(paths.AllowedPaths.ToArray()), Exclusions = Array.AsReadOnly(paths.Exclusions.ToArray()) };
    }
    public Task<FrozenWorkerPlan> SelectAsync(ParentAssignmentRequest request, TimeSpan remaining, CancellationToken token)
    {
        original.Check(); token.ThrowIfCancellationRequested(); lock (gate) { if (entered) throw new WorkerEnvelopeException("Actual local assignment already entered; no replay."); entered = true; }
        var config = request.Configuration; var plan = request.Plan; if (!plan.DownstreamReady || plan.Identity != config.ExpectedBundleIdentity || plan.ContractIdentity != config.ExpectedContractIdentity || plan.Repository != config.Repository || plan.WorkItem != config.ExpectedWorkItem || remaining <= TimeSpan.Zero || remaining > original.WorkLimit) throw new WorkerEnvelopeException("Actual imported plan/parent selection differs.");
        var bytes = plan.CanonicalBytes; if (bytes.Length > 524288) throw new WorkerEnvelopeException("Complete selected plan exceeds finite parent instruction profile."); using var doc = JsonDocument.Parse(bytes); original.Check(); var body = doc.RootElement.GetProperty("plan"); var changes = body.GetProperty("changeMap"); if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() is < 1 or > 128) throw new WorkerEnvelopeException("Complete finite actual change map required.");
        var paths = new HashSet<string>(StringComparer.Ordinal); foreach (var row in changes.EnumerateArray()) { var path = row.GetProperty("path").GetString() ?? throw new WorkerEnvelopeException("Actual change path missing."); if (path.Length > 4096) throw new WorkerEnvelopeException("Actual change path too long."); paths.Add(path.Replace('\\', '/')); }
        // Trusted selection may restrict, never add a path absent from the
        // actual freshly imported owner plan. Exact path checks run again in
        // WorkerEnvelope and actual ExecutionCoordinator/worktree owner.
        if (selected.AllowedPaths.Any(path => !paths.Contains(path.Replace('\\', '/')))) throw new WorkerEnvelopeException("Local allowed path exceeds actual frozen change map.");
        var worktree = BoundedFiles.Plain(selected.Worktree, true); var results = BoundedFiles.Plain(selected.ResultDirectory, true); BoundedFiles.Separate(config.RepositoryRoot, worktree); BoundedFiles.Separate(config.RepositoryRoot, results); BoundedFiles.Separate(worktree, results);
        if (request.ResultDirectory is string explicitResults && Path.GetFullPath(explicitResults) != results) throw new WorkerEnvelopeException("Explicit result selection differs.");
        var parentCeiling = body.GetProperty("requestedCeiling").GetString() ?? throw new WorkerEnvelopeException("Actual plan ceiling missing."); if (TrustedConfiguration.Ceiling(config.ExpectedPlanCeiling!) > TrustedConfiguration.Ceiling(parentCeiling) || TrustedConfiguration.Ceiling(config.ExpectedPlanCeiling!) > TrustedConfiguration.Ceiling(config.MaximumCeiling)) throw new WorkerEnvelopeException("Configured request raises immutable actual parent ceiling.");
        var actual = new FrozenWorkerPlan("run-" + Guid.NewGuid().ToString("N"), plan.ContractIdentity, plan.BaseCommit, config.Repository, config.RepositoryRoot, worktree, results, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), selected.AllowedPaths, selected.Exclusions, config.Adapter, config.AdapterVersion, [AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Stop, AgentCapability.EvidenceExport], config.ExpectedPlanCeiling!);
        _ = WorkerEnvelope.PrepareWorker(actual); original.Check(); return Task.FromResult(actual);
    }
}
