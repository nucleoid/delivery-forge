using System.Diagnostics;
using System.Numerics;

namespace DeliveryForge.Evidence;

// Observation/admission only. This never changes host affinity, environment or global runtime.
internal static class InstalledToolResourceAdmission
{
    internal sealed record Data(long AffinityMask, long AvailableManagedBytes);
    private sealed class Frame
    {
        internal Process? Process;
        internal bool Entered, Returned, CloseEntered, CloseReturned;
        internal Exception? Primary, CloseFailure, Post;
    }
    internal static Data Capture(InstalledToolFiles files) => files.Effect(() => CaptureCore(files));
    private static Data CaptureCore(InstalledToolFiles files)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The installed producer owner supports Windows and Linux only.");
        var retention = new BoundedCaptureRetention(); var frame = new Frame(); retention.Add(frame);
        long mask = 0, heap = 0;
        try
        {
            files.Check(); frame.Entered = true; frame.Process = Process.GetCurrentProcess(); frame.Returned = true; files.Check();
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                mask = frame.Process.ProcessorAffinity.ToInt64();
            else
                throw new PlatformNotSupportedException("The installed producer owner supports Windows and Linux only.");
            files.Check();
            heap = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; files.Check();
            if (mask <= 0 || BitOperations.PopCount((ulong)mask) != 2 || heap <= 0 || heap > 4L * 1024 * 1024 * 1024)
                throw new EvidenceWriteException("Actual containing process is not admitted to exactly2CPU/at most4GiB managed memory.");
        }
        catch (Exception error) { frame.Primary = error; }
        finally
        {
            if (frame.Returned && frame.Process is not null)
            {
                try { files.Check(); frame.CloseEntered = true; frame.Process.Dispose(); frame.CloseReturned = true; }
                catch (Exception error) { frame.CloseFailure = error; }
                try { files.Check(); } catch (Exception error) { frame.Post = error; }
            }
        }
        if (frame.CloseReturned) retention.ReleaseKnown();
        var errors = new[] { frame.Primary, frame.CloseFailure, frame.Post }.Where(e => e is not null).Select(e => e!).ToArray();
        if (errors.Length > 0) throw new AggregateException("Actual resource admission failed; originals retained if unknown.", errors);
        if (!frame.Returned || !frame.CloseEntered || !frame.CloseReturned) throw new EvidenceWriteException("Actual admission holder remains unknown.");
        return new(mask, heap);
    }
}