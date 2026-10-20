using System.Diagnostics;
namespace DeliveryForge.Evidence;

internal static partial class BoundedProcessCapture
{
    sealed class DuplexFirstDisposition(IDisposable actual, string kind) { internal readonly IDisposable Actual = actual; internal readonly string Kind = kind; internal bool Entered, Returned; internal Exception? Primary, Post; }
    static void CheckDuplexBudget(IOwnedDuplexBudget budget, bool cleanup) { if (cleanup) _ = budget.CleanupRemaining; else budget.CheckWork(); }
    // Shared exactly by legacy bounded capture and the duplex extension.
    static ProcessStartInfo DuplexStartInfo(CommandInvocation invocation)
    {
        var info = new ProcessStartInfo(invocation.FileName) { WorkingDirectory = invocation.WorkingDirectory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        CommandEnvironment.Complete(info, invocation); foreach (var argument in invocation.Arguments) info.ArgumentList.Add(argument); foreach (var pair in invocation.Environment ?? new Dictionary<string, string>()) info.Environment[pair.Key] = pair.Value; return info;
    }
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static void OpenDuplex(CommandInvocation invocation, IOwnedDuplexBudget budget, OwnedDuplexProcess session, LinuxScopeSelection? selection)
    {
        budget.CheckWork(); var process = new Process(); var retention = new BoundedCaptureRetention(); retention.Add(process); retention.Add(session);
        LinuxCommandScope? scope = null; StreamWriter? input = null; StreamReader? output = null, error = null; var inputCloseEntered = false; var inputClosed = false; var lastFailure = 0;
        void Failures() { if (scope is not null) foreach (var failure in scope.Failures.Skip(lastFailure)) { session.RecordOwnerFailure(failure); lastFailure++; } }
        session.AttachOwner(process, cleanup => { try { return scope?.ObserveNative(cleanup) ?? new(true, true, 0, null, "Actual original Process.Start never entered."); } finally { Failures(); } }, cleanup => { try { scope?.Stop(cleanup); } finally { Failures(); } }, cleanup =>
        {
            var known = true; try { if (scope is not null && !scope.DisposeKnown(cleanup)) return false; } finally { Failures(); }
            if (inputCloseEntered && !inputClosed) return false;
            bool FirstClose(IDisposable resource, string kind)
            {
                var row = new DuplexFirstDisposition(resource, kind); retention.Add(row);
                try { CheckDuplexBudget(budget, cleanup); row.Entered = true; if (ReferenceEquals(resource, input)) inputCloseEntered = true; resource.Dispose(); row.Returned = true; if (ReferenceEquals(resource, input)) inputClosed = true; } catch (Exception failure) { row.Primary = failure; retention.Add(failure); session.RecordOwnerFailure(failure); known = false; cleanup = true; }
                try { CheckDuplexBudget(budget, cleanup); } catch (Exception failure) { row.Post = failure; retention.Add(failure); session.RecordOwnerFailure(failure); known = false; cleanup = true; }
                return !row.Entered || row.Returned;
            }
            // Preserve each actual stream first-close row. Do not dispose the
            // containing process while any entered stream close is unreturned.
            if (!inputClosed && input is not null) { if (inputCloseEntered) return false; if (!FirstClose(input, "stdin-writer")) return false; }
            if (output is not null && !FirstClose(output, "stdout-reader")) return false;
            if (error is not null && !FirstClose(error, "stderr-reader")) return false;
            if (!known) return false;
            if (!FirstClose(process, "original-process")) return false;
            if (known) retention.ReleaseKnown(); return known;
        });
        try
        {
            if (selection is null) throw new PlatformNotSupportedException("A real selected creation-owned Linux scope profile is required; root-only fallback cannot establish duplex support.");
            scope = new LinuxCommandScope(process, budget, selection); session.AttachScopeProof(() => scope.HasCreationOwnedScope); retention.Add(scope); process.StartInfo = DuplexStartInfo(invocation); scope.Prepare(process.StartInfo);
            scope.BeforeCreation(); var created = process.Start(); scope.CreationReturned(created); if (!created) throw new IOException("Original scoped process did not start.");
            input = process.StandardInput; retention.Add(input); budget.CheckWork(); output = process.StandardOutput; retention.Add(output); budget.CheckWork(); error = process.StandardError; retention.Add(error); budget.CheckWork();
            // The SAME root is held at the genuine compiled birth barrier until
            // actual group/identity/resource proof has returned before release.
            scope.BindAndRelease(input.BaseStream, output.BaseStream); budget.CheckWork();
            session.AttachStreams(input.BaseStream, output.BaseStream, error.BaseStream, cleanup => { CheckDuplexBudget(budget, cleanup); if (inputCloseEntered) throw new IOException("Original writer first-close already entered."); inputCloseEntered = true; input.Dispose(); inputClosed = true; });
        }
        catch { if (input is not null && output is not null && error is not null) session.AttachStreams(input.BaseStream, output.BaseStream, error.BaseStream, cleanup => { CheckDuplexBudget(budget, cleanup); if (inputCloseEntered) throw new IOException("Original writer first-close already entered."); inputCloseEntered = true; input.Dispose(); inputClosed = true; }); Failures(); throw; }
    }
}
