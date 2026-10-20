using System.Diagnostics;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Execution;

public sealed partial class RunStore
{
    static readonly List<(RunStore Owner, FileStream Stream)> UnsettledFiles = [];
    sealed class OriginalFileState { internal bool Unsettled; }
    OriginalFileState originalFiles = new();
    public bool HasUnsettledOriginalFiles => Volatile.Read(ref originalFiles.Unsettled);
    void RetainFile(FileStream stream)
    {
        lock (UnsettledFiles) UnsettledFiles.Add((this, stream));
        Volatile.Write(ref originalFiles.Unsettled, true);
        Exception? rootFailure = null, parentFailure = null;
        try { if (original is IOwnedBudgetUnknownRetention retained) retained.PreserveUnknown(); }
        catch (Exception failure) { rootFailure = failure; }
        try { uncertaintyObserver?.RetainUncertain(admittedRun!, "Actual original store file first disposition did not return."); }
        catch (Exception failure) { parentFailure = failure; }
        if (rootFailure is not null && parentFailure is not null) throw new AggregateException("Original store root and parent uncertainty observations failed.", rootFailure, parentFailure);
        if (rootFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(rootFailure).Throw();
        if (parentFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(parentFailure).Throw();
    }
    IOwnedDuplexBudget? original;
    ICoordinatedSynchronousEffectAdmission? admission;
    ICoordinatedEffectAdmission? uncertaintyObserver;
    string? admittedRun;
    bool cleanupOnly;
    internal RunStore WithOriginalCleanup()
    {
        if (original is null || cleanupOnly) throw new InvalidOperationException("Cleanup requires this existing original store.");
        _ = original.CleanupRemaining;
        // Only existing-owner stop/checkpoint persistence and pointer reconciliation
        // use this view. It drops revoked forward admission, never grants dispatch,
        // and shares both the original cleanup endpoint and unknown-file state.
        return new(_root) { original = original, admittedRun = admittedRun, cleanupOnly = true, originalFiles = originalFiles, uncertaintyObserver = uncertaintyObserver };
    }
    internal RunStore WithOriginalAdmission(IOwnedDuplexBudget owner, ICoordinatedSynchronousEffectAdmission parent, string runId)
    {
        if (original is not null || string.IsNullOrWhiteSpace(runId)) throw new InvalidOperationException("Original store binds once.");
        CheckOriginal();
        owner.CheckWork(); parent.CheckCurrent(runId);
        return new(_root) { original = owner, admission = parent, admittedRun = runId, originalFiles = originalFiles, uncertaintyObserver = parent };
    }
    void CheckOriginal()
    {
        if (HasUnsettledOriginalFiles) throw new IOException("Original store file disposition requires explicit reconciliation.");
        if (cleanupOnly) _ = original!.CleanupRemaining; else original?.CheckWork();
        admission?.CheckCurrent(admittedRun!);
    }
    T Effect<T>(Func<T> effect)
    {
        CheckOriginal();
        var value = admission is null ? effect() : admission.EnterSynchronous(admittedRun!, effect);
        CheckOriginal(); return value;
    }
    CancellationTokenSource Deadline(CancellationToken caller)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, cleanupOnly ? default : original?.Token ?? default);
        try
        {
            if (original is not null)
            {
                CheckOriginal();
                var remaining = cleanupOnly ? original.CleanupRemaining : original.WorkLimit - Stopwatch.GetElapsedTime(original.OriginalStarted);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Original store deadline expired before wait admission.");
                linked.CancelAfter(remaining);
            }
            return linked;
        }
        catch { linked.Dispose(); throw; }
    }
    Exception? CloseOriginalFile(FileStream stream, ref Exception? primary)
    {
        // A forward refusal must not skip the first close. Successful work does
        // not latch cleanup early; failure uses the SAME existing cleanup grant.
        if (primary is null) { try { CheckOriginal(); } catch (Exception error) { primary = error; } }
        var returned = false;
        try
        {
            if (primary is not null && original is not null) _ = original.CleanupRemaining;
            stream.Dispose(); returned = true;
            if (primary is not null && original is not null) _ = original.CleanupRemaining; else CheckOriginal();
            return null;
        }
        catch (Exception error)
        {
            if (!returned)
            {
                try { RetainFile(stream); }
                catch (Exception observation) { return new AggregateException("Original store first disposition and uncertainty observation failed.", error, observation); }
            }
            return error;
        }
    }
    Task<byte[]> ReadOriginalAsync(string path, CancellationToken token)
    {
        if (original is null) return File.ReadAllBytesAsync(path, token);
        token.ThrowIfCancellationRequested();
        // Synchronous bounded reads are enrolled separately from later writes.
        // No parent gate is held across an asynchronous method's prefix.
        FileStream? stream = null; byte[]? bytes = null; Exception? primary = null, close = null;
        try
        {
            bytes = Effect(() =>
            {
                RejectReparseAncestors(path); RejectReparseFile(path);
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 16 * 1024 * 1024) throw new RunStoreCorruptionException("Store input exceeds finite extent.");
                var read = new byte[checked((int)stream.Length)]; var offset = 0;
                while (offset < read.Length) { CheckOriginal(); token.ThrowIfCancellationRequested(); var count = stream.Read(read, offset, Math.Min(65536, read.Length - offset)); if (count == 0) throw new EndOfStreamException(); offset += count; }
                if (stream.ReadByte() != -1) throw new RunStoreCorruptionException("Store input grew during read.");
                return read;
            });
        }
        catch (Exception error) { primary = error; }
        if (stream is not null) close = CloseOriginalFile(stream, ref primary);
        if (primary is not null && close is not null) throw new AggregateException("Original store read and first disposition failed.", primary, close);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (close is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(close).Throw();
        return Task.FromResult(bytes ?? throw new IOException("Original store read returned no bytes."));
    }
}
