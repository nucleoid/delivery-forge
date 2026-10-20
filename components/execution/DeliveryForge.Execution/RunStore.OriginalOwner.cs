using System.Diagnostics;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Execution;

public sealed partial class RunStore
{
    static readonly List<FileStream> UnsettledFiles = [];
    void RetainFile(FileStream stream)
    {
        lock (UnsettledFiles) UnsettledFiles.Add(stream);
        if (original is IOwnedBudgetUnknownRetention retained) retained.PreserveUnknown();
    }
    IOwnedDuplexBudget? original;
    ICoordinatedSynchronousEffectAdmission? admission;
    string? admittedRun;
    bool cleanupOnly;
    internal RunStore WithOriginalCleanup()
    {
        if (original is null || cleanupOnly) throw new InvalidOperationException("Cleanup requires this existing original store.");
        _ = original.CleanupRemaining;
        return new(_root) { original = original, admittedRun = admittedRun, cleanupOnly = true };
    }
    internal RunStore WithOriginalAdmission(IOwnedDuplexBudget owner, ICoordinatedSynchronousEffectAdmission parent, string runId)
    {
        if (original is not null || string.IsNullOrWhiteSpace(runId)) throw new InvalidOperationException("Original store binds once.");
        owner.CheckWork(); parent.CheckCurrent(runId);
        return new(_root) { original = owner, admission = parent, admittedRun = runId };
    }
    void CheckOriginal() { if (cleanupOnly) _ = original!.CleanupRemaining; else original?.CheckWork(); admission?.CheckCurrent(admittedRun!); }
    T Effect<T>(Func<T> effect)
    {
        CheckOriginal();
        var value = admission is null ? effect() : admission.EnterSynchronous(admittedRun!, effect);
        CheckOriginal(); return value;
    }
    CancellationTokenSource Deadline(CancellationToken caller)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, cleanupOnly ? default : original?.Token ?? default);
        if (original is not null) { CheckOriginal(); linked.CancelAfter(cleanupOnly ? original.CleanupRemaining : original.WorkLimit - Stopwatch.GetElapsedTime(original.OriginalStarted)); }
        return linked;
    }
    Task<byte[]> ReadOriginalAsync(string path, CancellationToken token)
    {
        if (original is null) return File.ReadAllBytesAsync(path, token);
        token.ThrowIfCancellationRequested();
        // Synchronous bounded reads are enrolled separately from later writes.
        // No parent gate is held across an asynchronous method's prefix.
        return Task.FromResult(Effect(() =>
        {
            RejectReparseAncestors(path); RejectReparseFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 16 * 1024 * 1024) throw new RunStoreCorruptionException("Store input exceeds finite extent.");
            var bytes = new byte[checked((int)stream.Length)]; var offset = 0;
            while (offset < bytes.Length) { CheckOriginal(); token.ThrowIfCancellationRequested(); var count = stream.Read(bytes, offset, Math.Min(65536, bytes.Length - offset)); if (count == 0) throw new EndOfStreamException(); offset += count; }
            if (stream.ReadByte() != -1) throw new RunStoreCorruptionException("Store input grew during read.");
            return bytes;
        }));
    }
}
