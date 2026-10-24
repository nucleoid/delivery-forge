using System.Diagnostics;
using System.Text;
using DeliveryForge.Evidence;
namespace DeliveryForge.Planning;

public sealed partial class GitRepositoryContextReader
{
    DeliveryForge.Coordination.ICoordinatedEffectAdmission? parentAdmission;
    string? parentRun;
    readonly object admissionBindingGate = new();
    bool sourceAdmissionTransferred;
    int originalGitOperations;
    public GitRepositoryContextReader WithParentAdmission(DeliveryForge.Coordination.ICoordinatedEffectAdmission parent, string runId)
    {
        ArgumentNullException.ThrowIfNull(parent);
        lock (admissionBindingGate)
        {
            if (_ownedGitUnknown) throw new PlanningException("Unknown original Git history cannot be rebound to another reader.");
            if (_actualOriginal is null || _actualExecutor is null || _importProfile || parentAdmission is not null || sourceAdmissionTransferred || originalGitOperations != 0 ||
                string.IsNullOrWhiteSpace(runId) || runId.Length > 256 || runId.Any(char.IsControl))
                throw new PlanningException("Actual original reader binds once, only before dispatch and without outstanding operations.");
            _actualOriginal.CheckWork(); sourceAdmissionTransferred = true;
            return new(_gitExecutable, _actualExecutor, _actualOriginal) { parentAdmission = parent, parentRun = runId };
        }
    }
    async Task<GitResult> RunOriginalGit(ProcessStartInfo start, CancellationToken caller, bool allowFailure)
    {
        lock (admissionBindingGate)
        {
            if (sourceAdmissionTransferred) throw new PlanningException("Original reader transferred its admission; the unadmitted source cannot be reused.");
            if (_ownedGitUnknown) throw new PlanningException("Prior original Git operation remains unknown; no replay.");
            originalGitOperations++;
        }
        try
        {
            if (_ownedGitUnknown) throw new PlanningException("Prior original Git operation remains unknown; no replay.");
            _actualOriginal!.CheckWork(); if (_importProfile) { _importLifetime!.Check(); _importView!.Verify(); }
            var env = start.Environment.ToDictionary(x => x.Key, x => x.Value ?? "", OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            DeliveryForge.Evidence.BoundedCommandCaptureResult actual;
            Task<BoundedCommandCaptureResult> Birth() => _actualExecutor!.ExecuteBoundedCaptureAsync(new(start.FileName, start.ArgumentList.ToArray(), start.WorkingDirectory, env) { EnvironmentIsComplete = true }, GitOperationTimeout, MaximumGitOutputBytes, caller, new OwnedWorkPhaseBudget(_actualOriginal!));
            try { actual = await (parentAdmission is null ? Birth() : parentAdmission.EnterAsync(parentRun!, Birth)).ConfigureAwait(false); }
            catch { _ownedGitUnknown = true; throw; }
            _actualOriginal.CheckWork(); if (_importProfile) { _importLifetime!.Check(); _importView!.Verify(); }
            var result = actual.Command; var facts = result.LifecycleDiagnostics;
            if (actual.CaptureBoundExceeded || result.TimedOut || result.Cancelled || !result.OwnedProcessQuiescent || !actual.RootExitObserved || !actual.StandardOutputDrainCompleted || !actual.StandardErrorDrainCompleted || facts?.NativeFailure is not null || facts?.OwnedTree is not { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 } || OperatingSystem.IsLinux() && actual.OwnershipKind != BoundedCaptureOwnershipKind.LinuxCreationOwnedScope || result.ExitCode is null) { _ownedGitUnknown = true; throw new PlanningException("Actual original Git capture/group/task/stream closure failed or is unknown."); }
            var returned = new GitResult(result.ExitCode.Value, actual.StandardOutputBytes, actual.StandardErrorBytes);
            if (!allowFailure && returned.ExitCode != 0) throw new PlanningException("Git could not resolve the actual selected source: " + Encoding.UTF8.GetString(returned.StandardError).Trim()); return returned;
        }
        finally { lock (admissionBindingGate) originalGitOperations--; }
    }
}
