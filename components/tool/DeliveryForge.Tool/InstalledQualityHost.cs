using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
namespace DeliveryForge.Tool;

// Trusted locators only. No approval, accepted-result, executor or arbitrary argv surface.
public sealed record InstalledQualityGateSelection(string GateId, string Tool, AdmissionFile Profile,
    string Scope, string? ProjectRelativePath, int MaximumSeconds);
public sealed record InstalledQualitySelection(string PolicyRelativePath, string PolicyIdentity,
    string ArtifactRoot, IReadOnlyList<InstalledQualityGateSelection> Gates, ProtectedBaselineRegistrySelection? Registry = null);

internal sealed class InstalledQualityHost
{
    readonly TrustedConfiguration configuration; readonly AdmissionFile configurationPin;
    readonly InstalledQualitySelection selection; readonly ToolLifetime original;
    readonly LinuxScopeSelection? linuxScope;
    readonly WorktreeManager worktrees; WorktreeManager? admittedCurrentness; InstalledToolHostScope? tools;
    FrozenPlan? frozen; FrozenWorkerPlan? assignment; TrustedConfiguration? boundConfiguration; bool entered;
    readonly List<GateRunResult> results = [];
    readonly Dictionary<string, GateRunResult> gateResults = new(StringComparer.Ordinal);
    internal bool Unknown => tools?.HasUncertainOriginals == true;
    internal InstalledQualityHost(TrustedConfiguration config, AdmissionFile actualConfigurationPin,
        ToolLifetime actualOriginal, WorktreeManager actualWorktrees, LinuxScopeSelection? selectedLinuxScope = null)
    {
        if (!OperatingSystem.IsWindows() && !(OperatingSystem.IsLinux() && selectedLinuxScope is not null))
            throw new PlatformNotSupportedException("Installed quality requires Windows or the trusted selected Linux scope.");
        linuxScope = selectedLinuxScope;
        configuration = config; configurationPin = actualConfigurationPin; original = actualOriginal;
        var requested = config.InstalledQuality ?? throw new ToolUsageException("No trusted installed quality selection.");
        ValidateSelection(requested);
        selection = requested with { Gates = Array.AsReadOnly(requested.Gates.ToArray()) }; worktrees = actualWorktrees;
    }
    internal static void ValidateSelection(InstalledQualitySelection value)
    {
        if (value.Gates is null || value.Gates.Count != 2 || value.Gates.Any(g => g is null) ||
            value.Gates.Select(g => g.GateId).Distinct(StringComparer.Ordinal).Count() != 2 ||
            !value.Gates.Select(g => g.Tool).Order(StringComparer.Ordinal).SequenceEqual(new[] { "crap4csharp", "mutate4csharp" }))
            throw new ToolUsageException("Select exactly one real Crap and one real mutation gate.");
        Relative(value.PolicyRelativePath);
        if (value.PolicyIdentity.Length != 71 || !value.PolicyIdentity.StartsWith("sha256:", StringComparison.Ordinal) || value.PolicyIdentity[7..].Any(c => !char.IsAsciiHexDigitLower(c))) throw new ToolUsageException("Exact protected policy identity required.");
        BoundedFiles.Plain(value.ArtifactRoot, true);
        if (value.Registry is { } registry) registry.Validate();
        foreach (var gate in value.Gates)
        {
            if (gate.GateId.Length is < 1 or > 64 || gate.GateId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                gate.MaximumSeconds is < 1 or > 1100 || gate.Tool == "mutate4csharp" && gate.MaximumSeconds > 600 ||
                gate.Profile is null || gate.Profile.Bytes is < 1 or > 1048576 || gate.Profile.Sha256.Length != 64 || gate.Profile.Sha256.Any(c => !char.IsAsciiHexDigitLower(c))) throw new ToolUsageException("Finite exact installed quality gate/profile selection required.");
            BoundedFiles.Plain(gate.Profile.Path);
            if (gate.Tool == "mutate4csharp" && (gate.Scope != "base" || gate.ProjectRelativePath is not null) ||
                gate.Tool == "crap4csharp" && (gate.Scope is not ("base" or "all") || gate.ProjectRelativePath is null)) throw new ToolUsageException("Closed supported installed gate scope required.");
            if (gate.ProjectRelativePath is { } project) { Relative(project); if (!project.EndsWith(".csproj", StringComparison.Ordinal)) throw new ToolUsageException("Exact C# test project required."); }
        }
    }
    static void Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\\') || value.Contains(':') || Path.IsPathFullyQualified(value) || value.Split('/').Any(p => p is "" or "." or "..")) throw new ToolUsageException("Closed repository-relative quality input required.");
    }
    internal void Bind(FrozenPlan actualPlan, FrozenWorkerPlan actualAssignment, TrustedConfiguration config, ToolLifetime life)
    {
        original.Check();
        if (!ReferenceEquals(life, original) || frozen is not null || actualPlan.Identity != config.ExpectedBundleIdentity ||
            actualPlan.ContractIdentity != actualAssignment.PlanIdentity || actualAssignment.BaseCommit != actualPlan.BaseCommit ||
            actualAssignment.Repository != configuration.Repository || Path.GetFullPath(actualAssignment.ParentCheckout) != Path.GetFullPath(configuration.RepositoryRoot)) throw new ToolUsageException("Installed quality binds the same actual imported plan/assignment/original once.");
        _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536);
        using var plan = JsonDocument.Parse(actualPlan.CanonicalBytes);
        var gateIds = plan.RootElement.GetProperty("plan").GetProperty("gates").EnumerateArray()
            .Select(g => g.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
        if (selection.Gates.Any(g => !gateIds.Contains(g.GateId))) throw new ToolUsageException("Quality gate absent from actual frozen plan.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(actualAssignment.RunId, "^run-[a-f0-9]{32}$")) throw new ToolUsageException("Closed standalone generated run identity required.");
        foreach (var root in new[] { actualAssignment.ParentCheckout, actualAssignment.Worktree, actualAssignment.ResultDirectory })
        {
            BoundedFiles.Separate(root, selection.ArtifactRoot);
            foreach (var gate in selection.Gates) BoundedFiles.Separate(root, gate.Profile.Path);
        }
        foreach (var gate in selection.Gates) BoundedFiles.Separate(selection.ArtifactRoot, gate.Profile.Path);
        if (selection.Registry is { } registry)
        {
            foreach (var root in new[] { actualAssignment.ParentCheckout, actualAssignment.Worktree, actualAssignment.ResultDirectory, selection.ArtifactRoot })
                foreach (var input in new[] { registry.OwnerApproval.Path, registry.ArtifactRoot }) BoundedFiles.Separate(root, input);
            BoundedFiles.Separate(registry.OwnerApproval.Path, registry.ArtifactRoot);
        }
        frozen = actualPlan; assignment = actualAssignment; boundConfiguration = config;
    }
    internal bool MatchesRetained(HostBridgeFrame frame, TrustedConfiguration? config, ToolLifetime? life) =>
        ReferenceEquals(life, original) && ReferenceEquals(config, boundConfiguration) && frozen is not null && assignment is not null &&
        frame.RunId == assignment.RunId && ReferenceEquals(frame.Lifetime, original) && frame.Request?.PlanIdentity == assignment.PlanIdentity;
    async Task CurrentAsync(HostBridgeFrame frame, AgentCompletionReceipt actualCompletion)
    {
        original.Check();
        if (tools is null || tools.HasUncertainOriginals || frozen is null || assignment is null || !ReferenceEquals(frame.Completion, actualCompletion) ||
            !ReferenceEquals(frame.Lifetime, original) || !frame.OwnerCompleted || frame.OwnerStopped || frame.RunId != assignment.RunId ||
            actualCompletion.RunId != assignment.RunId || actualCompletion.Worktree != assignment.Worktree || actualCompletion.RequestIdentity != frame.Request?.RequestIdentity)
            throw new WorkerEnvelopeException("Real completed same-owner quality binding is absent or changed.");
        _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536);
        foreach (var gate in selection.Gates) _ = InstalledHostAdmission.ReadExact(gate.Profile, original, 1048576);
        if (selection.Registry is { } registry) _ = InstalledHostAdmission.ReadExact(new(registry.OwnerApproval.Path, registry.OwnerApproval.Bytes, registry.OwnerApproval.Sha256), original, 65536);
        var actualGit = admittedCurrentness ?? throw new WorkerEnvelopeException("Same-parent currentness Git view is absent."); var observation = await actualGit.ObserveAsync(assignment.Worktree, original.Token).ConfigureAwait(false);
        original.Check();
        if (!observation.IsClean || observation.HeadCommit != actualCompletion.HeadCommit || observation.TreeId != actualCompletion.TreeId)
            throw new WorkerEnvelopeException("Current committed worker H/tree changed before installed quality effect.");
        await actualGit.EnsureDescendsFromAsync(assignment.Worktree, assignment.BaseCommit, observation.HeadCommit, original.Token).ConfigureAwait(false);
        original.Check();
    }
    internal async Task<ToolResult> RunCompletedAsync(HostBridgeFrame frame, ParentCoordinationHost parent)
    {
        if (entered || assignment is null || frozen is null || frame.Completion is not { } completion) throw new ToolUsageException("Actual quality route cannot replay or reconstruct private completion from JSON.");
        entered = true;
        var run = assignment;
        admittedCurrentness = worktrees.WithParentAdmission(parent, run.RunId);
        var actualTools = new InstalledToolHostScope(original, parent, run.RunId, linuxScope); tools = actualTools;
        var output = Path.Combine(selection.ArtifactRoot, run.RunId);
        await frame.Enter(() => parent.EnterAsync(run.RunId, async () =>
        {
            await CurrentAsync(frame, completion).ConfigureAwait(false);
            return await parent.EnterAsync(run.RunId, () =>
            {
                BoundedFiles.Plain(output, true); original.Check();
                if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Quality result namespace already exists; no replay/adoption.");
                Directory.CreateDirectory(output); original.Check();
                return Task.FromResult(true);
            }).ConfigureAwait(false);
        })).ConfigureAwait(false);
        // The actual containing frame enrolls each complete parent-admitted Task before await.
        foreach (var gate in selection.Gates)
        {
            var result = await frame.Enter(() => parent.EnterAsync(run.RunId, async () =>
            {
                await CurrentAsync(frame, completion).ConfigureAwait(false);
                return await parent.EnterAsync(run.RunId, () =>
                {
                    _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536);
                    _ = InstalledHostAdmission.ReadExact(gate.Profile, original, 1048576);
                    var profile = actualTools.LoadProfile(gate.Profile.Path, "sha256:" + gate.Profile.Sha256);
                    if (profile.Fixture || profile.Tool != gate.Tool) throw new ToolUsageException("Exact nonfixture selected producer required; no fixture flag flip.");
                    BoundedFiles.WriteNew(Path.Combine(output, gate.GateId + "-started.json"), JsonSerializer.SerializeToUtf8Bytes(new { run.RunId, run.PlanIdentity, run.BaseCommit, completion.HeadCommit, completion.TreeId, gate.GateId, gate.Profile, Policy = selection.PolicyIdentity }), original);
                    return actualTools.RunAsync(run.Worktree, run.BaseCommit, selection.PolicyRelativePath, selection.PolicyIdentity,
                        profile, gate.GateId, new(run.BaseCommit, completion.HeadCommit, completion.TreeId, true), gate.Scope,
                        gate.ProjectRelativePath, TimeSpan.FromSeconds(gate.MaximumSeconds), output, original.Token,
                        gate.Tool == "crap4csharp" ? selection.Registry : null);
                }).ConfigureAwait(false);
            })).ConfigureAwait(false);
            results.Add(result);
            gateResults.Add(gate.GateId, result);
            await frame.Enter(() => parent.EnterAsync(run.RunId, async () =>
            {
                await CurrentAsync(frame, completion).ConfigureAwait(false);
                return await parent.EnterAsync(run.RunId, () =>
                {
                    _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536);
                    _ = InstalledHostAdmission.ReadExact(gate.Profile, original, 1048576);
                    var bytes = BoundedFiles.Read(result.ReceiptPath, original, 1048576);
                    BoundedFiles.WriteNew(Path.Combine(output, gate.GateId + "-settled.json"), JsonSerializer.SerializeToUtf8Bytes(new { run.RunId, run.PlanIdentity, run.BaseCommit, completion.HeadCommit, completion.TreeId, gate.GateId, Outcome = result.Outcome.ToString(), result.Fixture, result.ProductionCapable, Receipt = new AdmissionFile(result.ReceiptPath, bytes.LongLength, BoundedFiles.Sha(bytes)), BaselineApproved = false, ProductionAuthority = false }), original);
                    return Task.FromResult(true);
                }).ConfigureAwait(false);
            })).ConfigureAwait(false);
            if (result.Outcome != GateOutcome.Pass)
                return result.Outcome switch
                {
                    GateOutcome.Fail => new("BLOCKED", "INSTALLED_QUALITY_FAILED", "Actual current installed report failed; retained receipts remain non-pass.", new { run.RunId, result.Outcome, result.ReceiptPath }),
                    GateOutcome.Error => new("ERROR", "INSTALLED_QUALITY_ERROR", "Actual current installed evidence binding failed.", new { run.RunId, result.ReceiptPath }),
                    _ => ToolResult.Incomplete("INSTALLED_QUALITY_INCOMPLETE", "Actual installed quality is incomplete/not applicable; no baseline or publication authority.", new { run.RunId, result.Outcome, result.ReceiptPath })
                };
        }
        if (selection.Registry is null)
            return ToolResult.Incomplete("PROTECTED_BASELINE_REGISTRY_REQUIRED", "Current installed gates are advisory until an independent owner approves the exact complete protected registry artifact identity.", new { run.RunId, Receipts = results.Select(r => r.ReceiptPath).ToArray() });
        var crapGateId = selection.Gates.Single(g => g.Tool == "crap4csharp").GateId;
        var crapResult = gateResults[crapGateId];
        var comparison = await frame.Enter(() => parent.EnterAsync(run.RunId, async () =>
        {
            await CurrentAsync(frame, completion).ConfigureAwait(false);
            return await parent.EnterAsync(run.RunId, () =>
            {
                var compared = actualTools.CompareBaseline(crapResult);
                BoundedFiles.WriteNew(Path.Combine(output, "protected-baseline-comparison.json"), JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Kind = "ParentProtectedBaselineComparisonData-v1", run.RunId, run.PlanIdentity, run.BaseCommit,
                    completion.HeadCommit, completion.TreeId, Comparison = compared,
                    CurrentReceipts = results.Select(r => r.ReceiptPath).ToArray(), PublicationAuthority = false
                }), original);
                return Task.FromResult(compared);
            }).ConfigureAwait(false);
        })).ConfigureAwait(false);
        if (comparison.Outcome == GateOutcome.Fail)
            return new("BLOCKED", "PROTECTED_BASELINE_REGRESSION", comparison.Reason, new { run.RunId, comparison });
        if (comparison.Outcome != GateOutcome.Pass)
            return ToolResult.Incomplete("PROTECTED_BASELINE_INCOMPLETE", comparison.Reason, new { run.RunId, comparison });
        return ToolResult.Incomplete("PROTECTED_QUALITY_PASSED_INDEPENDENT_REVIEW_REQUIRED", "Actual installed gates and owner-approved protected baseline comparison passed; independent review and later publication authorization remain separate.", new { run.RunId, comparison, PublicationAuthority = false });
    }
}
