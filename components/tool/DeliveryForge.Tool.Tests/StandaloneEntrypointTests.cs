using System.Text.Json;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;
// Actual default entrypoint calls, not a fabricated host/owner result. These
// definitions require the containing test CLR's admitted two-CPU/four-GiB
// prebirth profile. No installed-host conformance is inferred from them.
public sealed class StandaloneEntrypointTests {
    static IntPtr ReadActualAffinity(System.Diagnostics.Process actual){
        if(OperatingSystem.IsWindows()||OperatingSystem.IsLinux())return actual.ProcessorAffinity;
        throw new PlatformNotSupportedException("Actual caller affinity is Windows/Linux only.");
    }

    [Theory]
    [InlineData("--help")][InlineData("--version")]
    public async Task Default_diagnostics_have_no_host_or_configuration_requirement(string flag){
        var before=Console.Out;using var output=new StringWriter();Console.SetOut(output);
        try{Assert.Equal(0,await Program.Main([flag]).WaitAsync(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken));Assert.NotEmpty(output.ToString());}
        finally{Console.SetOut(before);}
    }
    [Fact(Timeout=30000)] public async Task Default_missing_config_returns_incomplete_and_restores_actual_caller_affinity(){
        if(!OperatingSystem.IsWindows()&&!OperatingSystem.IsLinux())throw new PlatformNotSupportedException("Actual standalone caller resource profile requires Windows/Linux.");
        using var actual=System.Diagnostics.Process.GetCurrentProcess();var beforeMask=ReadActualAffinity(actual);
        var beforeOut=Console.Out;var beforeError=Console.Error;using var output=new StringWriter();using var errors=new StringWriter();Console.SetOut(output);Console.SetError(errors);
        try{
            Assert.Equal(2,await Program.Main(["capabilities"]).WaitAsync(TimeSpan.FromSeconds(20),TestContext.Current.CancellationToken));
            using var doc=JsonDocument.Parse(output.ToString());Assert.Equal("INCOMPLETE",doc.RootElement.GetProperty("Outcome").GetString());Assert.Equal("TRUSTED_CONFIG_REQUIRED",doc.RootElement.GetProperty("Code").GetString());Assert.False(doc.RootElement.GetProperty("RuntimeCertified").GetBoolean());Assert.Empty(errors.ToString());Assert.Equal(beforeMask,ReadActualAffinity(actual));
        }finally{Console.SetOut(beforeOut);Console.SetError(beforeError);}
    }
    [Fact] public async Task Bound_configuration_replacement_refuses_before_plan_or_Git_invocation(){
        var path=Path.Combine(Path.GetTempPath(),"forge-config-bound-"+Guid.NewGuid().ToString("N")+".json");
        var config=new TrustedConfiguration("nucleoid/delivery-forge",Path.GetTempPath(),"HEAD",Environment.ProcessPath??throw new IOException("Actual current test host path unavailable."),OperationSeconds:20);
        var original=JsonSerializer.SerializeToUtf8Bytes(config,BoundedFiles.Json);File.WriteAllBytes(path,original);
        using var life=new ToolLifetime(TimeSpan.FromSeconds(20),TestContext.Current.CancellationToken);
        try{
            var selected=new AdmissionFile(path,original.Length,BoundedFiles.Sha(original));var application=new ToolApplication(actualOriginal:life,actualConfigurationPin:selected);
            var command=CommandLine.Parse(["implement","--plan",path,"--config",path]);
            var positive=await application.ExecuteAsync(command,TestContext.Current.CancellationToken);Assert.Equal("PLAN_EXPECTATIONS_REQUIRED",positive.Code);Assert.Empty(application.RetainedExternalCalls);
            // Same length, valid JSON, changed identity: no parse failure can
            // accidentally stand in for the whole-byte binding guard.
            var changed=JsonSerializer.SerializeToUtf8Bytes(config with {Repository="nucleoid/delivery-FORGE"},BoundedFiles.Json);Assert.Equal(original.Length,changed.Length);File.WriteAllBytes(path,changed);
            var error=await Assert.ThrowsAsync<IOException>(()=>application.ExecuteAsync(command,TestContext.Current.CancellationToken));
            Assert.Contains("admission bytes changed",error.Message,StringComparison.Ordinal);Assert.Empty(application.RetainedExternalCalls);
        }finally{File.Delete(path);}
    }
}
