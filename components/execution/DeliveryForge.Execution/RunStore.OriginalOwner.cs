using System.Diagnostics;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Execution;

public sealed partial class RunStore
{
    static readonly List<FileStream> UnsettledFiles = [];
    bool unsettledOriginalFile;
    public bool HasUnsettledOriginalFiles => Volatile.Read(ref unsettledOriginalFile);
    void RetainFile(FileStream stream)
    {
        lock (UnsettledFiles) UnsettledFiles.Add(stream);
        Volatile.Write(ref unsettledOriginalFile, true);
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
        if (stream is not null)
        {
            try
            {
                if (primary is not null) _ = original.CleanupRemaining; else CheckOriginal();
                stream.Dispose();
                if (primary is not null) _ = original.CleanupRemaining; else CheckOriginal();
            }
            catch (Exception error) { close = error; RetainFile(stream); }
        }
        if (primary is not null && close is not null) throw new AggregateException("Original store read and first disposition failed.", primary, close);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (close is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(close).Throw();
        return Task.FromResult(bytes ?? throw new IOException("Original store read returned no bytes."));
    }
}
