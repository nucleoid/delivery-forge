using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;
public sealed class ReplayWriterTests {
    static ValidatedContract Reidentify(JsonObject root) { root["identity"]="";root["identity"]=CanonicalJson.ComputeIdentity(JsonSerializer.SerializeToUtf8Bytes(root));return ContractValidator.ParseAndValidate(JsonSerializer.SerializeToUtf8Bytes(root)); }
    static (ValidatedContract Manifest,ValidatedContract Plan,ValidatedContract Policy,IReadOnlyList<ValidatedContract> Gates) Fixture(bool omitRequiredReceipts=false) {
        JsonObject Read(string name)=>JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"ContractFixtures",name+".json")))!.AsObject();
        var plan=ContractValidator.ParseAndValidate(JsonSerializer.SerializeToUtf8Bytes(Read("plan")));
        var policyNode=Read("evidence-policy");var policy=Reidentify(policyNode);
        var manifestNode=Read("run-manifest");manifestNode["planIdentity"]=plan.Identity;manifestNode["policyIdentity"]=policy.Identity;manifestNode["authorizationCeiling"]="plan";manifestNode["workflowState"]="PLANNED";
        // These are schema/reference DATA fixtures, never actual runtime gate authority.
        // Keep the full nonempty policy; a positive replay supplies all its receipts.
        var gates=new List<ValidatedContract>();
        if(!omitRequiredReceipts)foreach(var gateId in policyNode["requiredGates"]!.AsArray()) {
            var gate=Read("gate-receipt");gate["gateId"]=gateId!.GetValue<string>();gate["policyIdentity"]=policy.Identity;
            foreach(var field in new[]{"baseCommit","headCommit","treeId"})gate[field]=manifestNode[field]!.DeepClone();
            gates.Add(Reidentify(gate));
        }
        manifestNode["gateReceiptIds"]=new JsonArray(gates.Select(gate=>(JsonNode?)JsonValue.Create(gate.Identity)).ToArray());
        return(Reidentify(manifestNode),plan,policy,gates.AsReadOnly());
    }
    [Fact] public void Real_owner_validator_builds_reference_only_replay_then_missing_policy_refuses() {
        var f=Fixture();var bytes=ReplayWriter.Build(f.Manifest,[f.Plan,f.Policy,..f.Gates]);var replay=ContractValidator.ParseAndValidate(bytes);Assert.Equal("replay-bundle",replay.SchemaName);
        using var parsed=JsonDocument.Parse(bytes);Assert.False(parsed.RootElement.TryGetProperty("commands",out _));Assert.Equal(f.Manifest.Identity,parsed.RootElement.GetProperty("runManifestIdentity").GetString());
        Assert.Throws<ContractReferenceException>(()=>ReplayWriter.Build(f.Manifest,[f.Plan]));
    }
    [Fact] public void Full_policy_missing_actual_gates_is_not_relaxed_for_planning_replay() { var f=Fixture(true);Assert.Throws<ContractReferenceException>(()=>ReplayWriter.Build(f.Manifest,[f.Plan,f.Policy])); }
    [Fact] public void Duplicate_contract_and_wrong_manifest_kind_refuse() { var f=Fixture();Assert.Throws<ToolUsageException>(()=>ReplayWriter.Build(f.Manifest,[f.Plan,f.Plan,f.Policy]));Assert.Throws<ToolUsageException>(()=>ReplayWriter.Build(f.Plan,[f.Policy])); }
}
