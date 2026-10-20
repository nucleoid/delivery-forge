using System.Diagnostics;
using System.Numerics;
namespace DeliveryForge.Tool;
// Borrowed wrapper for THIS actual caller. It never adopts a PID/child, creates
// a process or pretends that changing an env string changes the running CLR.
public sealed class StandaloneResources
{
    static readonly List<StandaloneResources> Unknown = [];
    readonly ToolLifetime original; Process? actual; IntPtr before, two; bool acquisitionEntered, entryEntered, acquired, changed, closeEntered, closed, restoreEntered, restored;
    int measuredCpu; long measuredMemoryMiB;
    internal DeliveryForge.Scheduling.ResourceSlots MeasureScheduling(ToolLifetime owner)
    {
        original.Check();
        if (!ReferenceEquals(owner, original) || Primary is not null || !changed || restoreEntered || closeEntered || actual is null || ReadActualAffinity(actual) != two || measuredCpu != 2 || measuredMemoryMiB <= 0)
            throw new ToolUsageException("Scheduling requires the same live measured caller resources.");
        original.Check(); return new(1, measuredCpu, measuredMemoryMiB, 1, 1, 1);
    }
    public bool Restored => restored && closed;
    public Exception? Primary { get; private set; }
    public Exception? Post { get; private set; }
    public StandaloneResources(ToolLifetime actualOriginal) { original = actualOriginal; lock (Unknown) Unknown.Add(this); }
    // Platform admission sits directly at each guarded API invocation; no
    // warning suppression, target-platform narrowing or lifecycle reorder.
    static IntPtr ReadActualAffinity(Process process)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) return process.ProcessorAffinity;
        throw new PlatformNotSupportedException("Caller affinity profile is Windows/Linux only.");
    }
    static void WriteActualAffinity(Process process, IntPtr value)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) { process.ProcessorAffinity = value; return; }
        throw new PlatformNotSupportedException("Caller affinity profile is Windows/Linux only.");
    }
    public void Enter()
    {
        original.Check(); if (entryEntered) throw new IOException("Original caller resource entry already attempted.");
        entryEntered = true;
        foreach (var pair in new[] { ("DOTNET_PROCESSOR_COUNT", "2"), ("DOTNET_GCHeapHardLimit", "0x100000000"), ("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "1"), ("MSBUILDDISABLENODEREUSE", "1") }) { original.Check(); var actualValue = Environment.GetEnvironmentVariable(pair.Item1); original.Check(); if (actualValue != pair.Item2) throw new ToolUsageException("Launch through the admitted prebirth resource shim: " + pair.Item1); }
        original.Check(); var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; original.Check(); var cpu = Environment.ProcessorCount; original.Check(); if (cpu != 2 || available <= 0 || available > 4294967296L) throw new ToolUsageException("Actual prebirth managed CPU/heap profile is not established.");
        measuredCpu = cpu; measuredMemoryMiB = available / (1024 * 1024);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Caller affinity profile is Windows/Linux only.");
        try { original.Check(); acquisitionEntered = true; actual = Process.GetCurrentProcess(); acquired = true; original.Check(); before = ReadActualAffinity(actual); original.Check(); var mask = unchecked((ulong)before.ToInt64()); if (BitOperations.PopCount(mask) < 2) throw new ToolUsageException("At least two actual inherited CPU bits are required."); var first = mask & (~mask + 1); var rest = mask & ~first; two = unchecked((IntPtr)(long)(first | (rest & (~rest + 1)))); original.Check(); changed = true; WriteActualAffinity(actual, two); original.Check(); if (ReadActualAffinity(actual) != two) throw new IOException("Actual caller two-bit affinity set/readback failed."); original.Check(); }
        catch (Exception error) { Primary = error; original.RetainUnknown(); throw; }
    }
    public void Close()
    {
        if (restoreEntered || closeEntered) throw new IOException("Original caller disposition already entered; no retry.");
        Exception? primary = null, post = null;
        try { _ = original.CleanupRemaining; if (changed) { restoreEntered = true; if (actual is null) throw new IOException("Actual borrowed caller wrapper missing."); WriteActualAffinity(actual, before); if (ReadActualAffinity(actual) != before) throw new IOException("Actual original affinity restoration differs."); } restored = true; _ = original.CleanupRemaining; }
        catch (Exception error) { primary = error; Primary ??= error; }
        // A still-entered/unknown restore does not authorize wrapper disposal.
        if (restored && actual is not null) { try { _ = original.CleanupRemaining; closeEntered = true; actual.Dispose(); closed = true; _ = original.CleanupRemaining; } catch (Exception error) { post = error; } }
        else if (actual is null && !acquisitionEntered) closed = true;
        else if (actual is null && !acquired) post = new IOException("Actual caller wrapper acquisition entered but return remains unknown.");
        try { _ = original.CleanupRemaining; } catch (Exception error) { post = post is null ? error : new AggregateException(post, error); }
        Post = post;
        if (!Restored || primary is not null || post is not null) original.RetainUnknown(); else lock (Unknown) Unknown.Remove(this);
        if (primary is not null && post is not null) throw new AggregateException("Actual resource disposition and independent SAME original post.", primary, post); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
    }
}
