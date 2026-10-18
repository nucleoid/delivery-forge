using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
namespace DeliveryForge.Tool;

// The trusted same-process root supplies an independently selected/conformed
// profile, not a JSON representation of an accepted session, writer or coordinator.
// The actual concrete native port is constructed here against the same owner.
// This concrete composition creates and retains the real existing owners.
public sealed record StandaloneHostSelection(string Executable,long Bytes,string Sha256,string Version);
public sealed class StandaloneOwnerComposition : IAsyncDisposable {
    static readonly List<StandaloneOwnerComposition> Unknown=[];
    readonly ToolLifetime original;readonly ProcessControl processes;readonly ExecutionCoordinator coordinator;readonly OwnerHostBridge bridge;readonly TrustedConfiguration configuration;ParentCoordinationHost? coordination;Task? coordinationCreation;bool coordinationEntered,disposeEntered;
    public ToolApplication Application {get;}
    public OwnerHostBridge ActualBridge=>bridge;
    public StandaloneOwnerComposition(TrustedConfiguration config,string actualStoreRoot,LocalWorkerPaths workerPaths,ToolLifetime actualOriginal,StandaloneHostSelection selectedHost,AgentAdapterProfile actualConformedProfile,LinuxScopeSelection? selectedLinuxScope=null,AdmissionFile? actualConfigurationPin=null){
        actualOriginal.Check();original=actualOriginal;configuration=config;
        ArgumentNullException.ThrowIfNull(actualConformedProfile);ArgumentNullException.ThrowIfNull(selectedHost);ArgumentNullException.ThrowIfNull(config);
        if(actualConformedProfile.AdapterId!=config.Adapter||actualConformedProfile.AdapterVersion!=config.AdapterVersion||selectedHost.Version!=config.AdapterVersion)throw new ToolUsageException("Actual independently conformed version/profile differs from trusted selection.");
        var executor=OperatingSystem.IsLinux()?new ProcessCommandExecutor(selectedLinuxScope??throw new ToolUsageException("Actual selected Linux scope profile required before native use."),actualOriginal):new ProcessCommandExecutor();
        if(!OperatingSystem.IsLinux()&&!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Installed root supports chosen Windows/Linux profiles only.");
        var worktrees=new WorktreeManager(config.GitExecutable,executor,actualOriginal);
        // Construct each exact supported port HERE with this SAME worktree
        // manager/executor/original. An externally built legacy Git consumer
        // cannot be smuggled in through a port factory or session DTO.
        IHostToolSessionPort actualNativePort=config.Adapter switch {
            "codex.cli"=>new DeliveryForge.Tool.Codex.CodexAppServerPort(new(selectedHost.Executable,selectedHost.Bytes,selectedHost.Sha256,selectedHost.Version),worktrees,actualConformedProfile,selectedLinuxScope),
            "claude-code.cli"=>new DeliveryForge.Tool.Claude.ClaudeStreamPort(new(selectedHost.Executable,selectedHost.Bytes,selectedHost.Sha256,selectedHost.Version),worktrees,actualConformedProfile,selectedLinuxScope),
            "pi.cli"=>new DeliveryForge.Tool.Pi.PiRpcPort(new(selectedHost.Executable,selectedHost.Bytes,selectedHost.Sha256,selectedHost.Version),worktrees,actualConformedProfile,selectedLinuxScope),
            _=>throw new ToolUsageException("Optional host has no actual standalone native transport.")};
        NamedHostAdapter adapter=config.Adapter switch {"codex.cli"=>new CodexHostAdapter(actualNativePort),"claude-code.cli"=>new ClaudeHostAdapter(actualNativePort),"pi.cli"=>new PiHostAdapter(actualNativePort),_=>throw new ToolUsageException("No real standalone transport for selected optional host.")};
        processes=new ProcessControl();var store=new RunStore(BoundedFiles.Plain(actualStoreRoot,true));coordinator=new(store,worktrees,processes,adapter);bridge=new(coordinator,adapter,requireCoordination:true);
        Application=new(bridge,assignments:new LocalWorkerAssignmentPort(actualOriginal,workerPaths),actualExecutor:executor,actualOriginal:actualOriginal,actualConfigurationPin:actualConfigurationPin);
        actualOriginal.Check();
    }
    internal async Task InitializeCoordinationAsync(){
        original.Check();if(coordinationEntered)throw new ToolUsageException("Original coordination initialization already entered; no retry.");coordinationEntered=true;
        if(configuration.Coordination is null)return; // Bridge itself refuses actual dispatch without the owner.
        var settings=configuration.Coordination;
        foreach(var path in new[]{settings.CheckpointRoot,settings.PrivateKeyPath,settings.DeploymentEvidence.Path}){
            BoundedFiles.Separate(configuration.RepositoryRoot,path);
        }
        var actual=ParentCoordinationHost.CreateAsync(settings,original);coordinationCreation=actual;
        try{coordination=await actual.WaitAsync(original.Remaining).ConfigureAwait(false);original.Check();bridge.BindCoordination(coordination);}
        catch{if(!actual.IsCompleted)original.RetainUnknown();throw;}
    }
    public async ValueTask DisposeAsync(){
        if(disposeEntered)throw new IOException("Original composition disposition already entered; no retry.");disposeEntered=true;
        if(coordinationCreation is not null&&!coordinationCreation.IsCompleted||bridge.RetainedFrames.Any(frame=>frame.Unknown)){original.RetainUnknown();lock(Unknown)Unknown.Add(this);return;}
        Exception? primary=null,post=null;
        try{_=original.CleanupRemaining;
            if(coordination is not null)await coordination.DisposeAsync().AsTask().WaitAsync(original.CleanupRemaining).ConfigureAwait(false);
            if(original.HasRetainedUnknown)throw new IOException("Original coordination retained uncertainty; dependent native owners retained.");
            coordinator.Dispose();await processes.DisposeAsync().AsTask().WaitAsync(original.CleanupRemaining).ConfigureAwait(false);}catch(Exception error){primary=error;original.RetainUnknown();lock(Unknown)Unknown.Add(this);}
        try{_=original.CleanupRemaining;}catch(Exception error){post=error;original.RetainUnknown();lock(Unknown)if(!Unknown.Contains(this))Unknown.Add(this);}
        if(primary is not null&&post is not null)throw new AggregateException("Actual standalone first disposition and independent SAME original post.",primary,post);
        if(primary is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();if(post is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
    }
}
