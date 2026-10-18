using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Packaging;
namespace DeliveryForge.Tool.Tests;
public sealed class InstalledPackageGoldenTests {
    [Fact]
    public async Task Actual_packaged_binary_on_this_OS_help_version_capabilities_dryrun_and_reinstall() {
        // REQUIRED actual fixture, never silently skipped. Root/CI prepares a real fresh package
        // and explicit host pin before full tests. This proves package mechanics, not a live worker.
        var archive=Environment.GetEnvironmentVariable("DELIVERY_FORGE_TOOL_PACKAGE")??throw new InvalidOperationException("Actual packaged Tool fixture is required; do not substitute a fake DLL.");
        var archiveSha=Environment.GetEnvironmentVariable("DELIVERY_FORGE_TOOL_PACKAGE_SHA256")??throw new InvalidOperationException("Actual package hash fixture is required.");
        var host=Environment.GetEnvironmentVariable("DELIVERY_FORGE_DOTNET_HOST")??throw new InvalidOperationException("Actual explicit runtime host is required.");
        using var affinity=new OwnedDuplexProcessTests.Affinity();using var life=new ToolLifetime(TimeSpan.FromSeconds(240),TestContext.Current.CancellationToken);var packageBytes=BoundedFiles.Read(archive,life,64*1024*1024);Assert.Equal(archiveSha,BoundedFiles.Sha(packageBytes));var hostBytes=BoundedFiles.Read(host,life);var hostPin=new FilePin(host,hostBytes.Length,BoundedFiles.Sha(hostBytes));
        var root=Path.Combine(Path.GetTempPath(),"delivery-forge-installed-"+Guid.NewGuid().ToString("N"));var prefix=Path.Combine(root,"prefix with spaces");Directory.CreateDirectory(root);var outside=Path.Combine(root,"user.txt");File.WriteAllText(outside,"preserve");
        var receipt=PackageLifecycle.Install(archive,prefix,hostPin,life);var entry=Path.Combine(prefix,"payload","DeliveryForge.Tool.dll");var executor=ActualOrdinaryCapture.Select(life);var allKnown=true;
        async Task<string> Run(string[] args,int exit) {
            allKnown=false;var result=await executor.ExecuteBoundedCaptureAsync(new(host,[entry,..args],root,ActualOrdinaryCapture.ResourceEnvironment()),life.Remaining,1048576,life.Token,new OwnedWorkPhaseBudget(life));
            ActualOrdinaryCapture.Require(result);Assert.True(result.RootExitObserved);Assert.True(result.StandardOutputDrainCompleted);Assert.True(result.StandardErrorDrainCompleted);Assert.False(result.CaptureBoundExceeded);Assert.True(result.Command.OwnedProcessQuiescent);allKnown=true;
            Assert.Equal(exit,result.Command.ExitCode);Assert.False(result.Command.TimedOut);Assert.False(result.Command.Cancelled);return new UTF8Encoding(false,true).GetString(result.StandardOutputBytes);
        }
        Assert.Contains("delivery-forge",await Run(["--help"],0));Assert.Contains("workerEnvelope",await Run(["--version"],0));Assert.Contains("INCOMPLETE",await Run(["capabilities","--json"],2));
        // Dry-run refuses missing trusted config without dispatching anything, rather than
        // passing because unavailable hosts were silently replaced by a fixture.
        Assert.Contains("TRUSTED_CONFIG_REQUIRED",await Run(["implement","--plan",Path.Combine(root,"not-read.json"),"--dry-run","--json"],2));
        var shim=Path.Combine(prefix,OperatingSystem.IsWindows()?"delivery-forge.cmd":"delivery-forge");
        var shimImage=OperatingSystem.IsWindows()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"):shim;
        string[] shimArgs=OperatingSystem.IsWindows()?["/d","/s","/c", "\"\""+shim+"\" --help\""]: ["--help"];
        allKnown=false;var discovered=await executor.ExecuteBoundedCaptureAsync(new(shimImage,shimArgs,root,ActualOrdinaryCapture.ResourceEnvironment()),life.Remaining,1048576,life.Token,new OwnedWorkPhaseBudget(life));
        ActualOrdinaryCapture.Require(discovered);Assert.True(discovered.RootExitObserved&&discovered.StandardOutputDrainCompleted&&discovered.StandardErrorDrainCompleted&&discovered.Command.OwnedProcessQuiescent);Assert.False(discovered.CaptureBoundExceeded);allKnown=true;Assert.Equal(0,discovered.Command.ExitCode);Assert.Contains("delivery-forge",new UTF8Encoding(false,true).GetString(discovered.StandardOutputBytes));
        Assert.True(File.Exists(Path.Combine(prefix,OperatingSystem.IsWindows()?"delivery-forge.cmd":"delivery-forge")));Assert.True(File.Exists(Path.Combine(prefix,"payload","content","SKILL.md")));
        if(!allKnown)throw new InvalidOperationException("Preserve prefix: an actual process capture is unresolved.");
        var next=PackageLifecycle.Reinstall(archive,receipt,hostPin,life);Assert.Equal(receipt.PayloadDigest,next.PayloadDigest);PackageLifecycle.Uninstall(next,life);Assert.False(Directory.Exists(prefix));Assert.Equal("preserve",File.ReadAllText(outside));// Preserve the owned removal journals as actual recovery evidence; no recursive cleanup.
        // True skill-host discovery and installed LIVE worker+independent reviewer+real quality
        // golden/recovery remain separate mandatory acceptance; this Fact does not certify them.
    }
}
