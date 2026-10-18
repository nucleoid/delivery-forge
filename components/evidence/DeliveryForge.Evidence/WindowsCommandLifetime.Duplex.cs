namespace DeliveryForge.Evidence;

internal sealed partial class WindowsCommandLifetime
{
    static void CheckDuplexBudget(IOwnedDuplexBudget budget, bool cleanup) { if (cleanup) _ = budget.CleanupRemaining; else budget.CheckWork(); }
    internal static void OpenDuplex(CommandInvocation invocation, IOwnedDuplexBudget budget, OwnedDuplexProcess session)
    {
        budget.CheckWork(); var owner = new WindowsCommandLifetime(); var retention = new BoundedCaptureRetention(); retention.Add(owner); retention.Add(session);
        session.AttachOwner(owner, () =>
        {
            if (!owner._creationAttempted || owner._creationReturned && !owner._creationSucceeded) return new(true, true, 0, null, "Original native creator returned no child.");
            var known = owner.Observe(out var exited, out var active, out var failure); int? exit = null; if (known && exited && owner._process is not null && GetExitCodeProcess(owner._process, out var code)) exit = unchecked((int)code);
            return new(known, exited, active, exit, failure ?? "Original Windows root/Job observation.");
        }, cleanup =>
        {
            CheckDuplexBudget(budget, cleanup); if (owner._process is null) { if (owner._creationAttempted && (!owner._creationReturned || owner._creationSucceeded)) session.MarkCreationUnknown(); return; }
            if (!(owner._assigned ? TerminateJobObject(owner._job, unchecked((uint)-1)) : TerminateProcess(owner._process, unchecked((uint)-1)))) throw Native("duplex-stop-original"); CheckDuplexBudget(budget, cleanup);
        }, cleanup =>
        {
            var known = true; foreach (var resource in new IDisposable?[] { owner._duplexInput, owner._stdout, owner._stderr, owner._inRead, owner._inWrite, owner._outRead, owner._outWrite, owner._errRead, owner._errWrite, owner._thread, owner._process, owner._job })
            {
                if (resource is null) continue; try { CheckDuplexBudget(budget, cleanup); var wasInvalid = resource is KernelHandle original && original.IsInvalid; resource.Dispose(); if (resource is KernelHandle handle && !wasInvalid && (!handle.NativeCloseEntered || !handle.NativeCloseReturned || !handle.NativeClosed)) throw new IOException("Actual original CloseHandle failed or remains unknown."); CheckDuplexBudget(budget, cleanup); } catch (Exception error) { retention.Add(error); session.RecordOwnerFailure(error); known = false; cleanup = true; if (budget is IOwnedFailureCleanup originalFailure) originalFailure.BeginFailureCleanup(); }
            }
            if (known) retention.ReleaseKnown(); return known;
        });
        try { owner.Start(invocation, budget.OriginalStarted, budget.WorkLimit, budget.Token, keepInput: true); budget.CheckWork(); session.AttachStreams(owner._duplexInput!, owner._stdout!.BaseStream, owner._stderr!.BaseStream); }
        catch { if (owner._duplexInput is not null && owner._stdout is not null && owner._stderr is not null) session.AttachStreams(owner._duplexInput, owner._stdout.BaseStream, owner._stderr.BaseStream); throw; }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)][return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] private static extern bool GetProcessAffinityMask(IntPtr process, out nuint processMask, out nuint systemMask);
}
