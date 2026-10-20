using DeliveryForge.Coordination;
namespace DeliveryForge.Evidence;

// Same containing original, shared complete-file/capture credits and rooted real owners.
// This object grants no supported tool, policy, lease, result or publication authority.
public sealed class InstalledToolHostScope
{
    readonly IOwnedDuplexBudget original;
    readonly ICoordinatedEffectAdmission? admission;
    readonly string? runId;
    readonly LinuxScopeSelection? linuxScope;
    public InstalledToolHostScope(IOwnedDuplexBudget actualOriginal, ICoordinatedEffectAdmission actualAdmission, string actualRunId, LinuxScopeSelection? selectedLinuxScope = null) : this(actualOriginal)
    {
        ArgumentNullException.ThrowIfNull(actualAdmission);
        if (string.IsNullOrWhiteSpace(actualRunId) || actualRunId.Length > 128) throw new ArgumentException("Actual containing run required.");
        admission = actualAdmission; runId = actualRunId;
        linuxScope = selectedLinuxScope is null ? null : selectedLinuxScope with
        { BirthGateClosure = Array.AsReadOnly(selectedLinuxScope.BirthGateClosure.ToArray()) };
    }
    readonly Credits credits = new();
    readonly List<object> retained = [];
    readonly object gate = new();
    sealed class OriginalRun
    {
        internal bool Entered, Returned, ComparisonEntered;
        internal Task? Task; internal GateRunResult? Result;
        internal InstalledToolFiles? Files; internal InstalledToolGateProvider.ActualExecutor? Owner;
        internal ResolvedEvidencePolicy? Policy; internal ProtectedBaselineRegistry? Registry;
        internal InstalledBaselineComparison? Comparison;
    }
    readonly List<OriginalRun> runs = [];
    public InstalledToolHostScope(IOwnedDuplexBudget actualOriginal)
    {
        ArgumentNullException.ThrowIfNull(actualOriginal);
        actualOriginal.CheckWork(); original = actualOriginal;
    }
    internal sealed class Credits
    {
        readonly object gate = new(); long reads, writes, capture; int operations, calls;
        internal volatile bool Unknown;
        internal void Read(long count) { lock (gate) { if (count < 0 || count > InstalledToolFiles.MaximumReadBytes - reads) throw new EvidenceWriteException("Containing complete-file read credit exhausted."); reads += count; } }
        internal void Write(long count) { lock (gate) { if (count < 0 || count > 268435456L - writes) throw new EvidenceWriteException("Containing write credit exhausted."); writes += count; } }
        internal void File() { lock (gate) { if (++operations > 400000) throw new EvidenceWriteException("Containing file-operation credit exhausted."); } }
        internal void Call() { lock (gate) { if (++calls > 64 || capture > 112L * 1024 * 1024) throw new EvidenceWriteException("Containing finite process/capture credit exhausted."); } }
        internal void Capture(long count) { lock (gate) { if (count < 0 || count > 128L * 1024 * 1024 - capture) throw new EvidenceWriteException("Containing complete capture credit exhausted."); capture += count; } }
    }
    InstalledToolFiles Files(TimeSpan maximum)
    {
        original.CheckWork(); if (HasUncertainOriginals) throw new EvidenceWriteException("Containing installed owner retains uncertainty; no next gate.");
        var actual = admission is null ? new InstalledToolFiles(original, maximum, credits) :
            new InstalledToolFiles(original, maximum, credits, admission, runId!);
        retained.Add(actual); return actual;
    }
    public bool HasUncertainOriginals { get { lock (gate) return credits.Unknown || runs.Any(r => r.Entered && (!r.Returned || r.Task is null || !r.Task.IsCompleted)); } }
    public InstalledToolProfile LoadProfile(string path, string approvedSha256)
    {
        InstalledToolFiles files;
        lock (gate) files = Files(TimeSpan.FromSeconds(30));
        var profile = InstalledToolProfile.LoadPinned(path, approvedSha256, files);
        files.Check(); files.ReleaseKnown(); original.CheckWork(); return profile;
    }
    // Closed real provider path: policy is read from the protected base via the same actual
    // bounded executor. No caller-supplied executor/result or authority DTO is accepted.
    public Task<GateRunResult> RunAsync(string repositoryRoot, string protectedBaseCommit,
        string policyRelativePath, string expectedPolicyIdentity, InstalledToolProfile profile, string gateId,
        RepositoryIdentity expected, string scope, string? project, TimeSpan maximum,
        string outputDirectory, CancellationToken cancellationToken = default,
        ProtectedBaselineRegistrySelection? baselineRegistry = null)
    {
        if (!OperatingSystem.IsWindows() && !(OperatingSystem.IsLinux() && linuxScope is not null))
            throw new EvidenceWriteException("Containing installed quality requires Windows or an explicitly selected Linux creation-owned scope.");
        if (profile.Fixture) throw new EvidenceWriteException("Fixture profiles cannot enter the standalone real quality path.");
        if (admission is null || runId is null) throw new EvidenceWriteException("Actual containing coordination admission is required before any Git/tool birth.");
        InstalledToolFiles files; InstalledToolGateProvider.ActualExecutor owner; OriginalRun row;
        lock (gate)
        {
            if (runs.Count >= 8 || runs.Any(r => r.Task is { IsFaulted: true } or { IsCanceled: true })) throw new EvidenceWriteException("Containing installed route exhausted or a prior original failed; no retry.");
            files = Files(maximum);
            owner = new InstalledToolGateProvider.ActualExecutor(files, profile.Data.Environment, profile.Data.GitExecutablePath, admission, runId, linuxScope);
            retained.Add(owner);
            row = new OriginalRun { Entered = true, Files = files, Owner = owner }; runs.Add(row);
        }
        // The actual row is rooted before invocation; never acquire the original parent
        // admission lock while holding this scope lock in an arbitrary embedding.
        var actual = RunCoreAsync(files, owner);
        lock (gate) { row.Task = actual; row.Returned = true; }
        return actual;
        async Task<GateRunResult> RunCoreAsync(InstalledToolFiles files, InstalledToolGateProvider.ActualExecutor owner)
        {
            var policy = await new ProtectedGitPolicyResolver(owner, repositoryRoot, protectedBaseCommit)
                .ResolveAsync(policyRelativePath, cancellationToken).ConfigureAwait(false);
            files.Check();
            if (policy.PolicyIdentity != expectedPolicyIdentity || !policy.RequiredGates.Contains(gateId, StringComparer.Ordinal))
                throw new EvidencePolicyException("Actual protected policy identity/required gate differs from trusted selection.");
            ProtectedBaselineRegistry? registry = null;
            if (baselineRegistry is not null)
            {
                if (profile.Tool != "crap4csharp") throw new EvidencePolicyException("Only the actual callable gate binds this registry comparison.");
                registry = await ProtectedBaselineRegistry.ResolveAsync(owner, files, repositoryRoot, policy, baselineRegistry, cancellationToken).ConfigureAwait(false);
            }
            var request = new InstalledToolGateRequest(gateId, policy, expected, scope, project, maximum, outputDirectory);
            if (registry is not null && registry.CallableProducer != CallableProducerIdentity.Current(profile, request))
                throw new EvidencePolicyException("Approved baseline producer/profile differs from the selected callable gate; compatible owner approval is required before execution.");
            var result = await new InstalledToolGateProvider(repositoryRoot, protectedBaseCommit)
                .RunOwnedAsync(profile, request, files, owner, cancellationToken).ConfigureAwait(false);
            InstalledBaselineComparison? comparison = null;
            if (registry is not null && result.Outcome == GateOutcome.Pass)
            {
                // Consume the actual callable artifacts now, inside this gate's
                // original finalization window, before host receipt/currentness I/O.
                var binding = owner.MainBinding ?? throw new EvidenceWriteException("Actual callable binding is absent.");
                var compared = binding.CompareApprovedBaseline(result, registry, policy);
                comparison = new(compared.Outcome, compared.Reason, registry.RegistryCommit,
                    registry.Baseline.SourceRevision, registry.Baseline.BaselineIdentity, registry.ArtifactManifestIdentity);
            }
            lock (gate) { row.Result = result; row.Policy = policy; row.Registry = registry; row.Comparison = comparison; }
            return result;
        }
    }
    public InstalledBaselineComparison CompareBaseline(GateRunResult actual)
    {
        OriginalRun row;
        lock (gate)
        {
            row = runs.SingleOrDefault(r => ReferenceEquals(r.Result, actual)) ?? throw new EvidenceWriteException("Foreign gate result cannot consume a registry.");
            if (row.ComparisonEntered || !row.Returned || row.Task is not { IsCompletedSuccessfully: true } || row.Comparison is null)
                throw new EvidenceWriteException("Actual current completed registry gate required once; no replay.");
            row.ComparisonEntered = true;
            return row.Comparison;
        }
    }

}

// Report DATA only. Cannot be supplied back to the scope as execution or approval.
public sealed record InstalledBaselineComparison(GateOutcome Outcome, string Reason, string RegistryCommit,
    string BaselineSourceRevision, string RegistryIdentity, string ArtifactManifestIdentity);
