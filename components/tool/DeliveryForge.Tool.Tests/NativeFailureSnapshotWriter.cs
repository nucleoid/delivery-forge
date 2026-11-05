using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// One test-owned failure artifact, after actual native settlement. This does not
// reopen WORK, renew the original, relax production BoundedFiles or issue proof.
internal sealed class NativeFailureSnapshotWriter(ToolLifetime original)
{
    internal FileStream? Stream;
    internal bool AttemptEntered, AcquisitionEntered, AcquisitionReturned, WriteEntered, WriteReturned, FlushEntered, FlushReturned, CloseEntered, CloseReturned, PostReturned;
    internal Exception? Primary, CloseFailure, Post;
    internal bool Unknown => AcquisitionEntered && !AcquisitionReturned || Stream is not null && !CloseReturned;
    void Check() => _ = original.CleanupRemaining;
    internal void Write(string path, byte[] bytes)
    {
        Check(); if (AttemptEntered) throw new IOException("Original failure snapshot already entered; no retry."); AttemptEntered = true;
        if (bytes.Length > 2 * 1024 * 1024) throw new IOException("Finite failure snapshot exceeds2MiB.");
        try
        {
            Check(); path = BoundedFiles.Plain(path); Check();
            // Refuse existing evidence before entering the sole constructor.
            var exists = File.Exists(path); Check(); if (exists) throw new IOException("Failure snapshot already exists; preserve it.");
            AcquisitionEntered = true; Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); AcquisitionReturned = true; Check();
            WriteEntered = true; Stream.Write(bytes, 0, bytes.Length); WriteReturned = true; Check();
            FlushEntered = true; Stream.Flush(true); FlushReturned = true; Check();
        }
        catch (Exception error) { Primary = error; }
        if (Stream is not null)
        {
            try { Check(); CloseEntered = true; Stream.Dispose(); CloseReturned = true; Check(); }
            catch (Exception error) { CloseFailure = error; }
        }
        try { Check(); PostReturned = true; } catch (Exception error) { Post = error; }
        if (Unknown) original.RetainUnknown();
        var failures = new[] { Primary, CloseFailure, Post }.OfType<Exception>().ToArray();
        if (failures.Length == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Length > 1) throw new AggregateException("Failure snapshot, first close and same-original post.", failures);
    }
}
