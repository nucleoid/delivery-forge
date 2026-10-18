using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
namespace DeliveryForge.Evidence;
// Independent Root build decision DATA, not a process/group issuer. The actual
// compiled gate's whole outputs remain rehashed at use; its selected source,
// SDK/compiler/PDB history must have a genuine separate Root build audit.
public sealed record LinuxGateBuildInput(string Role,string Identity,long Bytes,string Sha256);
public sealed record LinuxGateBuildEvidence(string Kind,string Purpose,string ArtifactManifestSha256,string? PackageSha256,IReadOnlyList<LinuxGateBuildInput> Sources,IReadOnlyList<LinuxGateBuildInput> CompilerInputs,IReadOnlyList<LinuxScopeImage> Outputs,bool BuildObserved,bool PdbSourceAssociationObserved,bool NativeOwnershipAuthority);
internal static class LinuxGateBuildBinding {
    static bool Hash(string? value)=>value is not null&&value.Length==64&&value.All(c=>char.IsAsciiHexDigit(c)&&!char.IsAsciiLetterUpper(c));
    internal static bool Require(string actualText,LinuxScopeSelection selection,IOwnedDuplexBudget original){
        original.CheckWork();var raw=new UTF8Encoding(false,true).GetBytes(actualText);original.CheckWork();
        var selected=selection.GateBuildReceipt;
        if(selected is null||selected.Bytes is <=0 or >65536||!Hash(selected.Sha256)||raw.LongLength!=selected.Bytes||Convert.ToHexStringLower(SHA256.HashData(raw))!=selected.Sha256)throw new IOException("Exact independent gate build receipt bytes differ.");original.CheckWork();
        _=DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(raw);original.CheckWork();
        var options=new JsonSerializerOptions {PropertyNameCaseInsensitive=false,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,MaxDepth=16};
        var actual=JsonSerializer.Deserialize<LinuxGateBuildEvidence>(raw,options)??throw new IOException("Actual gate build decision is null.");original.CheckWork();
        if(actual.Kind!="RootActualLinuxGateBuildAcceptance-v1"||actual.Purpose is not ("production-birth-gate" or "negative-partial-ready-fixture")||!actual.BuildObserved||!actual.PdbSourceAssociationObserved||actual.NativeOwnershipAuthority||!Hash(actual.ArtifactManifestSha256)||actual.Purpose=="production-birth-gate"&&!Hash(actual.PackageSha256)||actual.Purpose=="negative-partial-ready-fixture"&&actual.PackageSha256 is not null||actual.Sources is null||actual.CompilerInputs is null||actual.Outputs is null||actual.Sources.Count is <2 or >32||actual.CompilerInputs.Count is <3 or >16||actual.Outputs.Count!=5)throw new IOException("Genuine independent compiled/source/PDB gate history required; DATA cannot issue native authority.");
        void Inputs(IReadOnlyList<LinuxGateBuildInput> rows){
            var identities=new HashSet<string>(StringComparer.Ordinal);foreach(var row in rows){original.CheckWork();if(row is null||row.Role is null||row.Role.Length is <1 or >128||row.Identity is null||row.Identity.Length is <1 or >4096||row.Identity.Contains('\0')||row.Bytes is <=0 or >268435456||!Hash(row.Sha256)||!identities.Add(row.Identity))throw new IOException("Complete finite distinct source/compiler build tuple required.");}
        }
        Inputs(actual.Sources);Inputs(actual.CompilerInputs);
        if(!actual.Sources.Any(row=>row.Role=="gate-main-source")||!actual.Sources.Any(row=>row.Role=="gate-project-source")||!actual.CompilerInputs.Any(row=>row.Role=="actual-compiler")||!actual.CompilerInputs.Any(row=>row.Role=="actual-sdk-targets")||!actual.CompilerInputs.Any(row=>row.Role=="actual-runtime-reference"))throw new IOException("Exact source/compiler/reference roles missing from historical Root build audit.");
        var expected=selection.BirthGateClosure.ToDictionary(row=>Path.GetFullPath(row.Path),StringComparer.Ordinal);var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var output in actual.Outputs){original.CheckWork();if(output is null||output.Path is null||!Path.IsPathFullyQualified(output.Path)||!seen.Add(Path.GetFullPath(output.Path))||!expected.TryGetValue(Path.GetFullPath(output.Path),out var wanted)||output.Bytes!=wanted.Bytes||output.Sha256!=wanted.Sha256)throw new IOException("Actual selected gate closure differs from independent compiled/PDB build proof.");}
        if(seen.Count!=expected.Count)throw new IOException("Incomplete independent whole gate output history.");original.CheckWork();return actual.Purpose=="production-birth-gate";
    }
}
