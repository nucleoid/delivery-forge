using DeliveryForge.Execution.Host;
namespace DeliveryForge.Tool;

// Exact independent trust-root pins selected by the human-owned containing
// configuration. None is a live process/session/writer or accepted worker DTO.
public sealed record AdmissionFile(string Path,long Bytes,string Sha256);
public sealed record InstalledHostCase(string Name,string Outcome,string ArtifactSha256);
public sealed record InstalledHostRootAcceptance(string Kind,string Platform,string Adapter,string Version,
    StandaloneHostSelection Host,AdmissionFile ToolAssembly,IReadOnlyList<AdmissionFile> Artifacts,IReadOnlyList<InstalledHostCase> Cases);
public sealed class InstalledHostAdmission {
    readonly InstalledHostRootAcceptance actual;readonly AgentAdapterProfile profile;
    public StandaloneHostSelection Host=>actual.Host;
    public AgentAdapterProfile Profile=>profile;
    InstalledHostAdmission(InstalledHostRootAcceptance actual,AgentAdapterProfile profile){this.actual=actual;this.profile=profile;}
    internal static byte[] ReadExact(AdmissionFile pin,ToolLifetime original,int maximum){
        if(pin is null||pin.Path is null||pin.Bytes<=0||pin.Bytes>maximum||pin.Sha256 is null||pin.Sha256.Length!=64||pin.Sha256.Any(c=>!char.IsAsciiHexDigit(c)||char.IsAsciiLetterUpper(c)))throw new ToolUsageException("Finite exact independent lowercase admission pin required.");
        var bytes=BoundedFiles.Read(pin.Path,original,maximum);original.Check();var digest=BoundedFiles.Sha(bytes);original.Check();if(bytes.LongLength!=pin.Bytes||digest!=pin.Sha256)throw new IOException("Actual independently selected admission bytes changed.");return bytes;
    }
    public static InstalledHostAdmission ReadRootAccepted(AdmissionFile rootPin,TrustedConfiguration config,ToolLifetime original){
        var bytes=ReadExact(rootPin,original,1048576);_=DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(bytes);original.Check();var receipt=System.Text.Json.JsonSerializer.Deserialize<InstalledHostRootAcceptance>(bytes,BoundedFiles.Json)??throw new IOException("Root host admission is null.");original.Check();
        var os=OperatingSystem.IsWindows()?"windows":OperatingSystem.IsLinux()?"linux":throw new PlatformNotSupportedException("Installed host profile is Windows/Linux only.");
        if(receipt.Host is null||receipt.ToolAssembly is null||receipt.Artifacts is null||receipt.Cases is null||receipt.Kind!="RootActualInstalledHostConformanceAcceptance-v1"||receipt.Platform!=os||receipt.Adapter!=config.Adapter||receipt.Version!=config.AdapterVersion||receipt.Host.Version!=config.AdapterVersion||receipt.Artifacts.Count is <1 or >32||receipt.Cases.Count!=5)throw new ToolUsageException("Actual independent installed host admission does not bind this platform/profile.");
        var self=System.Reflection.Assembly.GetExecutingAssembly().Location;if(Path.GetFullPath(receipt.ToolAssembly.Path)!=Path.GetFullPath(self))throw new ToolUsageException("Actual installed Tool assembly differs from independently accepted host conformance.");_=ReadExact(receipt.ToolAssembly,original,16777216);
        _=ReadExact(new(receipt.Host.Executable,receipt.Host.Bytes,receipt.Host.Sha256),original,268435456);
        var artifacts=new HashSet<string>(StringComparer.Ordinal);long total=0;
        foreach(var pin in receipt.Artifacts){if(pin is null)throw new ToolUsageException("Missing actual conformance artifact.");if(pin.Bytes<=0||pin.Bytes>1048576||total>33554432-pin.Bytes||!artifacts.Add(pin.Sha256))throw new ToolUsageException("Actual conformance artifacts exceed finite complete profile or collide.");total+=pin.Bytes;_=ReadExact(pin,original,1048576);}
        var required=new HashSet<string>(new[]{"actual_launch_assignment","actual_completion_commit","actual_stop_same_original","foreign_completion_refused","malformed_protocol_refused"},StringComparer.Ordinal);
        foreach(var row in receipt.Cases){if(row is null)throw new ToolUsageException("Missing actual conformance case.");if(row.Outcome!="PASS"||!required.Remove(row.Name)||!artifacts.Contains(row.ArtifactSha256))throw new ToolUsageException("Actual independent host conformance case proof is missing, duplicated or incomplete.");}
        if(required.Count!=0)throw new ToolUsageException("Complete selected installed conformance required.");
        // This derives version-specific capability EVIDENCE only from a pinned
        // independent Root decision. No live native/task/session is adopted.
        var reference="sha256:"+rootPin.Sha256;var rows=new[]{
            new AgentCapabilityEvidence(AgentCapability.Launch,AgentCapabilityStatus.Supported,"actual source-owned initialized native transport",reference),
            new AgentCapabilityEvidence(AgentCapability.CompletionEvidence,AgentCapabilityStatus.Supported,"actual exact accepted assignment/session/task and original native closure",reference),
            new AgentCapabilityEvidence(AgentCapability.Stop,AgentCapabilityStatus.Supported,"actual same-original stop attempt and native group/IO disposition",reference),
            new AgentCapabilityEvidence(AgentCapability.EvidenceExport,AgentCapabilityStatus.Supported,"actual complete bounded protocol/native evidence",reference),
            new AgentCapabilityEvidence(AgentCapability.Pause,AgentCapabilityStatus.Unsupported,"No actual same-turn pause capability is issued.",reference),
            new AgentCapabilityEvidence(AgentCapability.Resume,AgentCapabilityStatus.Unsupported,"No actual same-turn resume capability is issued.",reference)};
        original.Check();return new(receipt,new(config.Adapter,config.AdapterVersion,Array.AsReadOnly(rows)));
    }
}
