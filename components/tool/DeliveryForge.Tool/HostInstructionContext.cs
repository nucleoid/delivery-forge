using System.Text.Json;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
namespace DeliveryForge.Tool;

public interface IHostInstructionPort { void BindInstructions(HostInstructionContext instructions); }
// Immutable parent-selected DATA, not a capability or a new canonical contract.
// The owner supplies the actual reconciled FrozenPlan (normal CLI path), or an
// explicit selected step with its independently reviewed rich-plan identity.
public sealed class HostInstructionContext
{
    readonly string requestIdentity, planIdentity, runId, baseCommit, repository, worktree, resultDirectory, adapter, version, ceiling;
    readonly string[] allowed, excluded; readonly JsonElement instructions;
    public string FrozenBundleIdentity { get; }
    HostInstructionContext(AgentRequest request, string bundle, JsonElement instructions)
    {
        RequireRequest(request); if (bundle.Length != 71 || !bundle.StartsWith("sha256:", StringComparison.Ordinal) || bundle.AsSpan(7).ContainsAnyExcept("0123456789abcdef")) throw new WorkerEnvelopeException("Independently selected rich-plan identity required.");
        FrozenBundleIdentity = bundle; requestIdentity = request.RequestIdentity; planIdentity = request.PlanIdentity; runId = request.RunId; baseCommit = request.BaseCommit; repository = request.Repository; worktree = request.Worktree; resultDirectory = request.ResultDirectory; adapter = request.AdapterId; version = request.AdapterVersion; ceiling = request.AuthorizationCeiling;
        allowed = request.AllowedPaths.ToArray(); excluded = request.Exclusions.ToArray(); var bytes = JsonSerializer.SerializeToUtf8Bytes(instructions); if (bytes.Length > 24576) throw new WorkerEnvelopeException("Complete selected instructions exceed24KiB; parent must select a finite step without truncation."); this.instructions = instructions.Clone();
    }
    static void RequireRequest(AgentRequest request) { if (request.SchemaVersion != WorkerEnvelope.SchemaVersion || request.RequestIdentity != WorkerEnvelope.ComputeRequestIdentity(request)) throw new WorkerEnvelopeException("Instructions require the exact immutable prepared request."); }
    public static HostInstructionContext FromFrozenPlan(FrozenPlan plan, FrozenWorkerPlan assignment)
    {
        if (!plan.DownstreamReady || plan.ContractIdentity != assignment.PlanIdentity || plan.BaseCommit != assignment.BaseCommit || plan.Repository != assignment.Repository) throw new WorkerEnvelopeException("Instruction plan differs from actual reconciled assignment.");
        var bytes = plan.CanonicalBytes; if (bytes.Length > 524288) throw new WorkerEnvelopeException("Frozen plan exceeds512KiB instruction input profile."); using var document = JsonDocument.Parse(bytes); var body = document.RootElement.GetProperty("plan");
        if (body.GetProperty("requestedCeiling").GetString() is not string parentCeiling || TrustedConfiguration.Ceiling(assignment.AuthorizationCeiling) > TrustedConfiguration.Ceiling(parentCeiling)) throw new WorkerEnvelopeException("Assignment exceeds frozen instruction ceiling.");
        var intent = body.GetProperty("intent").GetString(); if (string.IsNullOrWhiteSpace(intent) || body.GetProperty("acceptanceCriteria").GetArrayLength() == 0) throw new WorkerEnvelopeException("Complete task and acceptance instructions required.");
        var projection = JsonSerializer.SerializeToElement(new { Intent = intent, AcceptanceCriteria = body.GetProperty("acceptanceCriteria"), Scope = body.GetProperty("scope"), ChangeMap = body.GetProperty("changeMap"), Dependencies = body.GetProperty("dependencyDag"), Gates = body.GetProperty("gates"), Rollout = body.GetProperty("rollout") });
        return new(WorkerEnvelope.PrepareWorker(assignment), plan.Identity, projection);
    }
    public static HostInstructionContext FromParentSelection(AgentRequest request, string independentlySelectedBundle, string task, IReadOnlyList<string> acceptance, IReadOnlyList<string> steps)
    {
        if (string.IsNullOrWhiteSpace(task) || task.Length > 8192 || acceptance.Count is < 1 or > 64 || steps.Count is < 1 or > 64 || acceptance.Concat(steps).Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 8192)) throw new WorkerEnvelopeException("Explicit finite selected task, acceptance and steps are required.");
        return new(request, independentlySelectedBundle, JsonSerializer.SerializeToElement(new { Intent = task, AcceptanceCriteria = acceptance.ToArray(), SelectedSteps = steps.ToArray() }));
    }
    public void Require(AgentRequest request)
    {
        RequireRequest(request);
        if (requestIdentity != request.RequestIdentity || planIdentity != request.PlanIdentity || runId != request.RunId || baseCommit != request.BaseCommit || repository != request.Repository || worktree != request.Worktree || resultDirectory != request.ResultDirectory || adapter != request.AdapterId || version != request.AdapterVersion || ceiling != request.AuthorizationCeiling || !allowed.SequenceEqual(request.AllowedPaths, StringComparer.Ordinal) || !excluded.SequenceEqual(request.Exclusions, StringComparer.Ordinal)) throw new WorkerEnvelopeException("Parent instructions cannot be rebound to a foreign task/request/path/ceiling.");
    }
    public JsonElement Project(AgentRequest actual) { Require(actual); return instructions.Clone(); }
}
