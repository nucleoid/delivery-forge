using System.Diagnostics;
using System.Text;
using DeliveryForge.Evidence;
namespace DeliveryForge.Planning;
public sealed partial class GitRepositoryContextReader {
    async Task<GitResult> RunOriginalGit(ProcessStartInfo start,CancellationToken caller,bool allowFailure){
        if(_ownedGitUnknown)throw new PlanningException("Prior original Git operation remains unknown; no replay.");
        _actualOriginal!.CheckWork();if(_importProfile){_importLifetime!.Check();_importView!.Verify();}
        var env=start.Environment.ToDictionary(x=>x.Key,x=>x.Value??"",OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        DeliveryForge.Evidence.BoundedCommandCaptureResult actual;
        try{actual=await _actualExecutor!.ExecuteBoundedCaptureAsync(new(start.FileName,start.ArgumentList.ToArray(),start.WorkingDirectory,env){EnvironmentIsComplete=true},GitOperationTimeout,MaximumGitOutputBytes,caller,new OwnedWorkPhaseBudget(_actualOriginal)).ConfigureAwait(false);}
        catch{_ownedGitUnknown=true;throw;}
        _actualOriginal.CheckWork();if(_importProfile){_importLifetime!.Check();_importView!.Verify();}
        var result=actual.Command;var facts=result.LifecycleDiagnostics;
        if(actual.CaptureBoundExceeded||result.TimedOut||result.Cancelled||!result.OwnedProcessQuiescent||!actual.RootExitObserved||!actual.StandardOutputDrainCompleted||!actual.StandardErrorDrainCompleted||facts?.NativeFailure is not null||facts?.OwnedTree is not {CreationOwned:true,SettlementObserved:true,ActiveProcesses:0}||OperatingSystem.IsLinux()&&actual.OwnershipKind!=BoundedCaptureOwnershipKind.LinuxCreationOwnedScope||result.ExitCode is null){_ownedGitUnknown=true;throw new PlanningException("Actual original Git capture/group/task/stream closure failed or is unknown.");}
        var returned=new GitResult(result.ExitCode.Value,actual.StandardOutputBytes,actual.StandardErrorBytes);
        if(!allowFailure&&returned.ExitCode!=0)throw new PlanningException("Git could not resolve the actual selected source: "+Encoding.UTF8.GetString(returned.StandardError).Trim());return returned;
    }
}
