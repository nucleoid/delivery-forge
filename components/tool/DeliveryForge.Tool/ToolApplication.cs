using DeliveryForge.Evidence;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
namespace DeliveryForge.Tool;

public sealed record PlanIntent(PlanningRequest Request, IReadOnlyList<string> EvidencePaths, IReadOnlyList<ChangeTarget> ChangeMap,
    IReadOnlyList<DependencyNode> Dependencies, IReadOnlyList<PlanGate> Gates, RolloutPlan Rollout, IReadOnlyList<PlanUnknown> Unknowns, string Revision);
public interface IIssueContentPort { Task<PlanIntent> ReadPublicIssueAsync(string issue, TimeSpan remaining, CancellationToken cancellationToken); }
public sealed record RunSelection(string RunId, string StoreRoot, string Repository, string RepositoryRoot, string StoredCeiling, string PlanIdentity);
public sealed class ToolApplication(OwnerHostBridge? host = null, IIssueContentPort? issues = null, ParentPublicationBinding? publication = null, IWorkerAssignmentPort? assignments = null, ProcessCommandExecutor? actualExecutor = null, ToolLifetime? actualOriginal = null, AdmissionFile? actualConfigurationPin = null)
{
    readonly List<ToolCallObservation> externalCalls = [];
    readonly object retainedConfigurationGate = new(); TrustedConfiguration? retainedQualityConfiguration; string? retainedQualityConfigurationPath;
    public IReadOnlyList<ToolCallObservation> RetainedExternalCalls { get { lock (externalCalls) return externalCalls.ToArray(); } }
    async Task<T> Observe<T>(string kind, Func<Task<T>> invoke, ToolLifetime life)
    {
        life.Check(); var row = new ToolCallObservation(kind, life); lock (externalCalls) { if (externalCalls.Count >= 512) throw new IOException("Finite containing tool-call ledger exhausted."); externalCalls.Add(row); }
        row.Entered = true; T value = default!; Exception? primary = null; Exception? post = null;
        try
        {
            var actual = invoke(); row.ActualTask = actual; row.InvocationReturned = true; if (actual is null) throw new IOException("Owner operation returned no actual task.");// Observe the already-terminal original task without evaluating a fresh
                                                                                                                                                                                 // work allowance first; its actual fault/cancellation is the primary.
                                                                                                                                                                                 // An incomplete original still uses the existing bounded wait below.
            value = actual.IsCompleted ? await actual.ConfigureAwait(false) : await actual.WaitAsync(life.Remaining).ConfigureAwait(false);
        }
        catch (Exception error) { primary = error; row.Primary = error; }
        try { life.Check(); } catch (Exception error) { post = error; row.Post = error; }
        if (row.Unknown) life.RetainUnknown();
        if (primary is not null && post is not null) throw new AggregateException("Actual tool operation and independent original endpoint.", primary, post);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw(); return value;
    }
    public async Task<ToolResult> ExecuteAsync(ToolCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Mode == ToolMode.Capabilities) return Capabilities(command.Get("profile") ?? "local");
        if (command.Get("config") is not string configPath) return ToolResult.Incomplete("TRUSTED_CONFIG_REQUIRED", "Select explicit owner-reviewed --config; ambient paths and capabilities are not inferred.");
        var life = actualOriginal ?? new ToolLifetime(TimeSpan.FromSeconds(1200), cancellationToken);
        using var locallyOwnedLifetime = actualOriginal is null ? life : null;
        life.Check();
        var config = TrustedConfiguration.LoadSelected(configPath, life, actualConfigurationPin);
        if (config.InstalledQuality is not null && actualOriginal is not null && actualConfigurationPin is not null)
        {
            // Reload verifies the exact current admitted bytes; retain the same
            // actual selected configuration object rather than adopt saved DATA.
            lock (retainedConfigurationGate)
            {
                if (retainedQualityConfiguration is null) { retainedQualityConfiguration = config; retainedQualityConfigurationPath = configPath; }
                else if (!Same(configPath, retainedQualityConfigurationPath!)) throw new ToolUsageException("Retained quality configuration path changed.");
                config = retainedQualityConfiguration;
            }
        }
        life.Restrict(TimeSpan.FromSeconds(config.OperationSeconds));
        if (config.PolicyFile is string policyPath)
        {
            var policy = ContractValidator.ParseAndValidate(BoundedFiles.Read(policyPath, life)); life.Check();
            if (policy.SchemaName != "evidence-policy") throw new ToolUsageException("PolicyFile requires the actual canonical evidence-policy schema.");
            using var policyJson = JsonDocument.Parse(policy.CanonicalBytes); var protectedCeiling = policyJson.RootElement.GetProperty("authorizedCeiling").GetString() ?? throw new ToolUsageException("Missing policy ceiling.");
            if (TrustedConfiguration.Ceiling(config.MaximumCeiling) > TrustedConfiguration.Ceiling(protectedCeiling)) throw new ToolUsageException("Configured maximum exceeds independently selected policy ceiling.");
            if ((command.Mode is ToolMode.Pr or ToolMode.Merge) && publication is not null && publication.PolicyIdentity != policy.Identity) throw new ToolUsageException("Actual publication owner policy differs from selected policy identity.");
        }
        if (command.Get("replay-out") is string replayPath)
        {
            BoundedFiles.Separate(config.RepositoryRoot, replayPath);
            if (command.Get("result-dir") is string resultPath) BoundedFiles.Separate(resultPath, replayPath);
            if (command.Get("run-dir") is string runPath) BoundedFiles.Separate(runPath, replayPath);
            if (command.Get("plan-out") is string planPath) BoundedFiles.Separate(planPath, replayPath);
        }
        if (command.Get("plan-out") is string planOutput) BoundedFiles.Separate(config.RepositoryRoot, planOutput);
        var result = command.Mode switch
        {
            ToolMode.Plan => await PlanAsync(command, config, life).ConfigureAwait(false),
            ToolMode.Implement => await ImplementAsync(command, config, life).ConfigureAwait(false),
            ToolMode.Resume => await RunAsync(command, config, life).ConfigureAwait(false),
            ToolMode.Pr or ToolMode.Merge => await RunAsync(command, config, life).ConfigureAwait(false),
            _ => throw new ToolUsageException("An explicit mode is required.")
        };
        if (command.Get("replay-out") is string replay && !command.Has("dry-run"))
        {
            BoundedFiles.Separate(config.RepositoryRoot, replay);
            if (command.Get("run-dir") is not string runDirectory) return ToolResult.Incomplete("REPLAY_OWNER_GRAPH_REQUIRED", "A complete validated run manifest/policy/plan/gate graph is required; no synthetic run or PASS receipts are created.", result);
            ReplayWriter.FromRunDirectory(runDirectory, replay, life);
        }
        return result;
    }
    ToolResult Capabilities(string profile)
    {
        if (host is not null) return ToolResult.Incomplete("BOUND_HOST_PROFILE_REQUIRES_CONFORMANCE_AUDIT", "Actual containing port supplied; profile evidence is exposed without certifying the installed live pipeline.", new { Profile = profile, ProvidedProfile = host.AdapterProfile, Realm = host.Realm.ToString(), RuntimeCertified = false });
        var profiles = new[] { KnownAgentProfiles.CodexCli("unverified"), KnownAgentProfiles.ClaudeCodeCli("unverified"), KnownAgentProfiles.PiCliUnavailable(), KnownAgentProfiles.OpenClawOptional("unverified") };
        return ToolResult.Incomplete("PROFILE_REQUIRES_ACTUAL_CONFORMANCE", "Host interfaces are diagnostics; no installed command or saved DATA grants runtime authority.", new { Profile = profile, Agents = profiles, ContractSchemas = "1.0.0", WorkerEnvelope = WorkerEnvelope.SchemaVersion, ProductionCoordination = "unavailable", ProductionPublication = "unavailable", RuntimeCertified = false });
    }
    async Task<ToolResult> PlanAsync(ToolCommand command, TrustedConfiguration config, ToolLifetime life)
    {
        var repo = BoundedFiles.Plain(command.Get("repo")!, true); if (!Same(repo, config.RepositoryRoot)) throw new ToolUsageException("Repository differs from explicit trusted configuration.");
        PlanIntent intent;
        if (command.Get("issue") is string issue)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(issue, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+#[1-9][0-9]*$")) throw new ToolUsageException("Expected OWNER/REPO#N.");
            if (!issue.StartsWith(config.Repository + "#", StringComparison.Ordinal)) throw new ToolUsageException("Issue repository differs from trusted selection.");
            if (issues is null) return ToolResult.Incomplete("ISSUE_CONNECTOR_UNAVAILABLE", "The skill must supply the actual supported GitHub issue connector; no network command is invented.", new { Issue = issue });
            intent = await Observe("ActualIssueRead", () => issues.ReadPublicIssueAsync(issue, life.Remaining, life.Token), life).ConfigureAwait(false);
        }
        else intent = BoundedFiles.ReadJson<PlanIntent>(command.Get("intent-file")!, life);
        if (intent.Request.Repository != config.Repository || intent.Request.Mode != "plan" || TrustedConfiguration.Ceiling(intent.Request.RequestedCeiling) > TrustedConfiguration.Ceiling(config.MaximumCeiling)) throw new ToolUsageException("Intent identity/mode/ceiling differs from protected selection.");
        var reader = actualExecutor is null ? new GitRepositoryContextReader(config.GitExecutable) : new GitRepositoryContextReader(config.GitExecutable, actualExecutor, life); var context = await Observe("ActualRepositoryRead", () => reader.ReadAsync(repo, config.MutableReference, life.Token), life).ConfigureAwait(false); life.Check();
        if (intent.EvidencePaths.Count is < 1 or > 128) throw new ToolUsageException("Explicit bounded repository evidence paths are required.");
        var evidence = new List<EvidenceItem>();
        foreach (var path in intent.EvidencePaths) { life.Check(); var file = await Observe("ActualRepositoryFile", () => reader.ReadFileAsync(context, path, life.Token), life).ConfigureAwait(false); life.Check(); evidence.Add(EvidenceItem.FromRepositoryFile(file, DateTimeOffset.UtcNow, [])); }
        var intake = IntakePlanner.Assess(intent.Request, evidence); var draft = new PlanDraft(intent.Request, context, evidence, intent.ChangeMap, intent.Dependencies, intent.Gates, intent.Rollout, intent.Unknowns, intake);
        if (!intake.Ready) return ToolResult.Incomplete("INTAKE_INCOMPLETE", "Owner intake requires reconciliation.", new { intake.Limitations, intake.RecommendedQuestion });
        var frozen = PlanFreezer.Freeze(draft, intent.Revision, DateTimeOffset.UtcNow); life.Check();
        if (command.Get("replay-out") is string replay && command.Get("plan-out") is string destination) BoundedFiles.Separate(replay, destination);
        if (!command.Has("dry-run") && !command.Has("replay-out") && command.Get("plan-out") is string output) BoundedFiles.WriteNew(output, frozen.CanonicalBytes, life);
        return new(command.Has("dry-run") ? "DRY_RUN" : "COMPLETE", "PLAN_FROZEN", "Owner-produced rich plan; no implementation or publication occurred.", new { frozen.Identity, frozen.ContractIdentity, frozen.DownstreamReady, Plan = JsonSerializer.Deserialize<JsonElement>(frozen.CanonicalBytes), frozen.Limitations });
    }
    async Task<ToolResult> ImplementAsync(ToolCommand command, TrustedConfiguration config, ToolLifetime life)
    {
        if (command.Get("repo") is string guard && !Same(guard, config.RepositoryRoot)) throw new ToolUsageException("--repo differs from frozen repository selection.");
        if (config.ExpectedBundleIdentity is null || config.ExpectedContractIdentity is null || config.ExpectedWorkItem is null || config.ExpectedPlanCeiling is null) return ToolResult.Incomplete("PLAN_EXPECTATIONS_REQUIRED", "Trusted independent rich-plan identity, contract identity and work item are required; input bytes do not select their own authority.");
        if (TrustedConfiguration.Ceiling(config.ExpectedPlanCeiling) > TrustedConfiguration.Ceiling(config.MaximumCeiling)) throw new ToolUsageException("Expected plan ceiling exceeds protected maximum.");
        var bytes = BoundedFiles.Read(command.Get("plan")!, life);
        var imported = await Observe("ActualPlanImport", () => ImportPlan(bytes, config, life), life).ConfigureAwait(false);
        if (imported.Plan is null || !imported.DownstreamReady) return ToolResult.Incomplete("PLAN_RECONCILIATION_INCOMPLETE", "Owner rich-plan import did not establish readiness.", imported.Diagnostics);
        if (TrustedConfiguration.Ceiling(config.MaximumCeiling) < AuthorizationCeiling.Implement) throw new ToolUsageException("Implement exceeds selected immutable ceiling.");
        if (command.Get("result-dir") is string results && command.Get("replay-out") is string replay) BoundedFiles.Separate(results, replay);
        if (command.Has("dry-run")) return new("DRY_RUN", "IMPLEMENT_PREPARED", "Rich plan freshly reconciled; no worker dispatched.", new { imported.Plan.Identity });
        // Actual assignment/writer identity is generated by the parent owner, never reconstructed
        // from imported plan fields. A typed bridge must be supplied by that embedding composition.
        if (host is null || assignments is null) return ToolResult.Incomplete(host is null ? "HOST_BRIDGE_UNAVAILABLE" : "OWNED_ASSIGNMENT_REQUIRED", "A real host bridge and parent-owned FrozenWorkerPlan assignment are required; no assignment is reconstructed from JSON.", new { imported.Plan.Identity, Adapter = config.Adapter });
        if (config.InstalledQuality is not null && !host.HasInstalledQuality) return ToolResult.Incomplete("CONTAINING_QUALITY_OWNER_REQUIRED", "Selected installed quality requires the actual closed standalone owner composition; the selection cannot be ignored by an embedding.");
        var request = new ParentAssignmentRequest(imported.Plan, config, command.Get("run-dir"), command.Get("result-dir"));
        var assignment = await SelectAssignmentAsync(request, life).ConfigureAwait(false);
        RequireAssignment(assignment, request); life.Check();
        host.BindCoordinationSelection(imported.Plan, assignment, config, life); life.Check();
        var instructions = HostInstructionContext.FromFrozenPlan(imported.Plan, assignment); life.Check();
        return await host.ImplementAsync(assignment, instructions, life, life.Token).ConfigureAwait(false);
    }
    readonly List<AssignmentObservation> assignmentObservations = [];
    public IReadOnlyList<AssignmentObservation> RetainedAssignments { get { lock (assignmentObservations) return assignmentObservations.ToArray(); } }
    async Task<FrozenWorkerPlan> SelectAssignmentAsync(ParentAssignmentRequest request, ToolLifetime life)
    {
        var frame = new AssignmentObservation(request, life);
        lock (assignmentObservations) { if (assignmentObservations.Count >= 128 || assignmentObservations.Any(x => x.Request.Plan.Identity == request.Plan.Identity && x.Unknown)) throw new WorkerEnvelopeException("Prior assignment is unknown or finite assignment ledger exhausted; no retry."); assignmentObservations.Add(frame); }
        life.Check(); frame.Entered = true;
        try { frame.ActualTask = assignments!.SelectAsync(request, life.Remaining, life.Token); frame.InvocationReturned = true; if (frame.ActualTask is null) throw new IOException("Parent assignment returned no task."); var assignment = frame.ActualTask.IsCompleted ? await frame.ActualTask.ConfigureAwait(false) : await frame.ActualTask.WaitAsync(life.Remaining).ConfigureAwait(false); life.Check(); frame.Result = assignment; return assignment; }
        catch (Exception error) { frame.Primary = error; if (frame.Unknown) life.RetainUnknown(); throw; }
    }
    static void RequireAssignment(FrozenWorkerPlan assignment, ParentAssignmentRequest request)
    {
        var config = request.Configuration;
        if (assignment.PlanIdentity != request.Plan.ContractIdentity || assignment.BaseCommit != request.Plan.BaseCommit || assignment.Repository != config.Repository || !Same(assignment.ParentCheckout, config.RepositoryRoot) || assignment.AdapterId != config.Adapter || assignment.AdapterVersion != config.AdapterVersion || TrustedConfiguration.Ceiling(assignment.AuthorizationCeiling) > TrustedConfiguration.Ceiling(config.ExpectedPlanCeiling!) || TrustedConfiguration.Ceiling(assignment.AuthorizationCeiling) > TrustedConfiguration.Ceiling(config.MaximumCeiling)) throw new WorkerEnvelopeException("Parent assignment differs from actual imported plan/config/current immutable ceiling.");
        BoundedFiles.Plain(assignment.Worktree, true); BoundedFiles.Plain(assignment.ResultDirectory, true); BoundedFiles.Separate(config.RepositoryRoot, assignment.Worktree); BoundedFiles.Separate(config.RepositoryRoot, assignment.ResultDirectory); BoundedFiles.Separate(assignment.Worktree, assignment.ResultDirectory);
        if (request.ResultDirectory is string selected && !Same(selected, assignment.ResultDirectory)) throw new WorkerEnvelopeException("Parent result directory differs from explicit selection.");
        _ = WorkerEnvelope.PrepareWorker(assignment); // DATA validation only; owner provisions later.
    }
    public Task<ToolResult> ExecuteOwnedAssignmentAsync(FrozenWorkerPlan assignment, TimeSpan total, CancellationToken cancellationToken = default) =>
        host is null ? Task.FromResult(ToolResult.Incomplete("HOST_BRIDGE_UNAVAILABLE", "No actual host bridge was supplied.")) : host.ImplementAsync(assignment, total, cancellationToken);
    public Task<ToolResult> ExecuteOwnedAssignmentAsync(FrozenWorkerPlan assignment, HostInstructionContext instructions, TimeSpan total, CancellationToken cancellationToken = default) =>
        host is null ? Task.FromResult(ToolResult.Incomplete("HOST_BRIDGE_UNAVAILABLE", "No actual host bridge was supplied.")) : host.ImplementAsync(assignment, instructions, total, cancellationToken);
    async Task<ToolResult> RunAsync(ToolCommand command, TrustedConfiguration config, ToolLifetime life)
    {
        if (command.Mode == ToolMode.Resume && config.InstalledQuality is not null && !command.Has("dry-run") && (actualOriginal is null || actualConfigurationPin is null || host is null)) return ToolResult.Incomplete("ORIGINAL_QUALITY_BINDING_REQUIRED", "The exact live containing owner/configuration is required; a new process cannot adopt it from JSON.");
        var directory = BoundedFiles.Plain(command.Get("run-dir")!, true); var selection = BoundedFiles.ReadJson<RunSelection>(Path.Combine(directory, "selection.json"), life, 65536);
        if (selection.Repository != config.Repository || !Same(selection.RepositoryRoot, config.RepositoryRoot)) throw new ToolUsageException("Stored run identity differs from protected repository selection.");
        var ceiling = TrustedConfiguration.Ceiling(selection.StoredCeiling); if (ceiling > TrustedConfiguration.Ceiling(config.MaximumCeiling)) throw new ToolUsageException("Stored ceiling exceeds current protected ceiling.");
        if (command.Get("ceiling") is string requested && TrustedConfiguration.Ceiling(requested) > ceiling) throw new ToolUsageException("Resume cannot raise the immutable stored ceiling.");
        var required = command.Mode == ToolMode.Merge ? AuthorizationCeiling.Merge : command.Mode == ToolMode.Pr ? AuthorizationCeiling.Pr : AuthorizationCeiling.Plan;
        if (required > ceiling) throw new ToolUsageException("Explicit mode exceeds the immutable stored run ceiling.");
        if (command.Mode == ToolMode.Resume && command.Get("ceiling") is string lower && TrustedConfiguration.Ceiling(lower) < ceiling) return ToolResult.Incomplete("OWNER_CEILING_REVISION_REQUIRED", "A lower ceiling needs a new actual owner-issued request/revision before resume; the original higher request is never resumed under an ignored flag.");
        var store = new RunStore(BoundedFiles.Plain(selection.StoreRoot, true));
        var recovery = await Observe("ActualRunRecovery", () => store.RecoverAsync(selection.RunId, life.Token), life).ConfigureAwait(false); life.Check();
        if (recovery.Records.Count == 0) return ToolResult.Incomplete("RUN_NOT_FOUND", "No immutable owner run records exist.");
        var preparedRecords = recovery.Records.Where(x => x.Kind == "worker-prepared").ToArray();
        if (preparedRecords.Length != 1) throw new ToolUsageException("Exact original prepared-worker record required.");
        var prepared = store.ReadPayload<PreparedWorkerRecord>(preparedRecords[0]);
        if (prepared.Request.RunId != selection.RunId || prepared.Request.Repository != selection.Repository || prepared.Request.PlanIdentity != selection.PlanIdentity || prepared.Request.AuthorizationCeiling != selection.StoredCeiling) throw new ToolUsageException("Selection cannot change immutable owner run/plan/ceiling fields.");
        if (host is not null && !Same(host.StoreRootPath, selection.StoreRoot)) throw new ToolUsageException("Actual owner store differs from selected immutable history.");
        if (command.Get("replay-out") is string replay) BoundedFiles.Separate(directory, replay);
        if (command.Mode == ToolMode.Resume)
        {
            if (command.Has("dry-run")) return new("DRY_RUN", "RECOVERY_OBSERVED", "Immutable history inspected; saved history is not live adapter/writer authority.", new { selection.RunId, Records = recovery.Records.Count });
            return host is null ? ToolResult.Incomplete("RESUME_HOST_UNAVAILABLE", "Fresh same-owner host/writer/control reconciliation is required; a new process cannot adopt them from JSON.") : await host.ResumeRetainedAsync(selection.RunId, life, config).ConfigureAwait(false);
        }
        if (publication is not null)
        {
            if (publication.RunIdentity != selection.RunId || publication.PlanIdentity != selection.PlanIdentity) throw new ToolUsageException("Actual parent publication object does not bind selected run/plan.");
            if (command.Has("title-file") || command.Has("body-file")) return ToolResult.Incomplete("PARENT_METADATA_REVISION_REQUIRED", "Title/body changes require explicit parent reconciliation into the immutable publication revision.");
            return await Observe("ActualPublication", () => publication.ExecuteAsync(command.Mode!.Value, command.Has("dry-run"), life.Token), life).ConfigureAwait(false);
        }
        return ToolResult.Incomplete("PUBLICATION_AUTHORITY_UNAVAILABLE", "Current production publication factory is unavailable. Later explicit pr/merge needs a new owner-issued immutable revision plus fresh quality/review/root authorization; no saved ceiling or fixture fallback can publish.", new { Mode = command.Mode.ToString(), StoredCeiling = selection.StoredCeiling, ForbiddenWrites = 0 });
    }
    Task<PlanImportResult> ImportPlan(byte[] bytes, TrustedConfiguration config, ToolLifetime life)
    {
        var source = new PlanImportSource(config.RepositoryRoot, config.MutableReference, config.GitExecutable);
        var expected = new PlanImportExpectations(config.ExpectedBundleIdentity!, config.ExpectedContractIdentity!, config.Repository, config.ExpectedWorkItem!, config.RepositoryRoot, config.MutableReference, config.ExpectedPlanCeiling!);
        return actualExecutor is null ? PlanImporter.ImportAndReconcileAsync(bytes, source, expected, ImportRemaining(life), cancellationToken: life.Token) : PlanImporter.ImportAndReconcileOwnedAsync(bytes, source, expected, ImportRemaining(life), actualExecutor, life, cancellationToken: life.Token);
    }
    static TimeSpan ImportRemaining(ToolLifetime life) { var remaining = life.Remaining; return remaining < TimeSpan.FromSeconds(120) ? remaining : TimeSpan.FromSeconds(120); }
    static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
