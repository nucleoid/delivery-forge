using DeliveryForge.Evidence;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;
// Test-only composition over the SAME production owner; no launcher or issuer.
internal static class ActualOrdinaryCapture {
    internal static ProcessCommandExecutor Select(ToolLifetime actualOriginal){
        if(OperatingSystem.IsLinux()){
            var path=Environment.GetEnvironmentVariable("DELIVERY_FORGE_LINUX_SCOPE_PROFILE")??throw new IOException("Actual independently admitted Linux scope profile required; no legacy root-only fallback.");
            var selected=BoundedFiles.ReadJson<LinuxScopeSelection>(path,actualOriginal,65536);return new(selected,actualOriginal);
        }
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Actual chosen install validation profiles require Windows/Linux.");
        return new();
    }
    internal static void Require(BoundedCommandCaptureResult actual){
        Assert.True(actual.RootExitObserved);Assert.True(actual.StandardOutputDrainCompleted);Assert.True(actual.StandardErrorDrainCompleted);Assert.False(actual.CaptureBoundExceeded);Assert.True(actual.Command.OwnedProcessQuiescent);
        var facts=actual.Command.LifecycleDiagnostics??throw new IOException("Actual native lifecycle proof absent.");Assert.Null(facts.NativeFailure);Assert.NotNull(facts.OwnedTree);Assert.True(facts.OwnedTree.CreationOwned);Assert.True(facts.OwnedTree.SettlementObserved);Assert.Equal(0,facts.OwnedTree.ActiveProcesses);
        if(OperatingSystem.IsLinux())Assert.Equal(BoundedCaptureOwnershipKind.LinuxCreationOwnedScope,actual.OwnershipKind);
    }
    internal static Dictionary<string,string> ResourceEnvironment()=>new(){["DOTNET_PROCESSOR_COUNT"]="2",["DOTNET_GCHeapHardLimit"]="0x100000000",["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"]="1",["MSBUILDDISABLENODEREUSE"]="1"};
}
