using DeliveryForge.Execution.Git;

namespace DeliveryForge.Execution;

public enum ObservedProcessState { None, Quiesced, LiveOwned, Unknown }
public enum ReconciliationAction { ResumeDispatch, Pause, Blocked, LocalComplete }

public sealed record ReconciliationResult(ReconciliationAction Action, IReadOnlyList<string> Reasons);
public sealed record CleanupDecision(bool Eligible, IReadOnlyList<string> Reasons);

internal sealed record ResumeObservation(
    AgentCheckpoint Expected,
    WorktreeObservation Actual,
    bool AssignedWorktreeMatches,
    bool WriterOwnershipConfirmed,
    bool ArtifactsValid,
    ObservedProcessState ProcessState);

internal static class Reconciler
{
    public static ReconciliationResult ReconcileResume(ResumeObservation observation)
    {
        var reasons = new List<string>();
        if (!observation.AssignedWorktreeMatches) reasons.Add("Assigned worktree is missing, moved, or replaced.");
        if (observation.Actual.HeadCommit != observation.Expected.HeadCommit) reasons.Add("Observed HEAD differs from the durable checkpoint.");
        if (observation.Actual.TreeId != observation.Expected.TreeId) reasons.Add("Observed tree differs from the durable checkpoint.");
        if (!observation.Actual.StatusEntries.SequenceEqual(observation.Expected.StatusEntries, StringComparer.Ordinal))
            reasons.Add("Dirty, untracked, or ignored worktree state differs from the durable checkpoint.");
        if (!SnapshotsEqual(observation.Expected.Snapshot, observation.Actual.Snapshot))
            reasons.Add("Worktree files differ from the durable checkpoint.");
        if (!observation.WriterOwnershipConfirmed) reasons.Add("One-writer ownership is not confirmed in this executor lifetime.");
        if (!observation.ArtifactsValid) reasons.Add("A checkpoint artifact is missing.");
        if (observation.ProcessState == ObservedProcessState.Unknown) reasons.Add("Process identity is unknown; signaling and resume are forbidden.");
        if (reasons.Count > 0) return new ReconciliationResult(ReconciliationAction.Blocked, reasons);
        if (observation.ProcessState == ObservedProcessState.LiveOwned)
            return new ReconciliationResult(ReconciliationAction.Pause, ["A positively identified task child is still live and must quiesce before dispatch."]);
        return new ReconciliationResult(ReconciliationAction.ResumeDispatch,
            ["Fresh Git, process, artifact, ownership, and frozen-request observations agree with the durable checkpoint."]);
    }

    public static CleanupDecision CleanupEligible(
        WorktreeObservation observation, bool boundaryClean, bool artifactsValid,
        bool writerOwnershipConfirmed, ObservedProcessState processState)
    {
        var reasons = new List<string>();
        if (!observation.IsClean) reasons.Add("Dirty, untracked, or ignored content must be preserved.");
        if (!boundaryClean) reasons.Add("Out-of-scope or escaped writes were observed.");
        if (!artifactsValid) reasons.Add("Required immutable artifacts are missing or corrupt.");
        if (!writerOwnershipConfirmed) reasons.Add("One-writer ownership is not confirmed.");
        if (processState is ObservedProcessState.LiveOwned or ObservedProcessState.Unknown)
            reasons.Add("Live, reused, or unknown process identity blocks cleanup.");
        return new CleanupDecision(reasons.Count == 0, reasons);
    }

    private static bool SnapshotsEqual(FileSnapshot left, FileSnapshot right) =>
        left.Files.Count == right.Files.Count &&
        left.Files.All(pair => right.Files.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
}
