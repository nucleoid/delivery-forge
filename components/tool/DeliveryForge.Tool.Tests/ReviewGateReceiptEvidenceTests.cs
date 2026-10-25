using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Tool.Tests;

public sealed class ReviewGateReceiptEvidenceTests
{
    static byte[] Receipt(int changedMember = -1)
    {
        var receipt = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "ContractFixtures", "gate-receipt.json")))!;
        var artifacts = new JsonObject();
        for (var i = 0; i < 400; i++)
            artifacts.Add("producer-bundle/inputs/" + i.ToString("D64") + ".bin",
                "sha256:" + (i == changedMember ? new string('4', 64) : new string('3', 64)));
        // A member named identity must also participate in the inventory digest.
        artifacts.Add("identity", "sha256:" + new string('5', 64));
        receipt["artifactHashes"] = artifacts;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        receipt["identity"] = CanonicalJson.ComputeIdentity(bytes);
        return JsonSerializer.SerializeToUtf8Bytes(receipt);
    }

    [Fact]
    public void Complete_large_receipt_keeps_every_result_field_and_fits_existing_prompt_profile()
    {
        var bytes = Receipt(); var original = bytes.ToArray();
        using var raw = JsonDocument.Parse(bytes);
        var evidence = ReviewGateReceiptEvidence.Create("retained/receipt.json", bytes);
        Assert.Equal(original, bytes);
        Assert.Equal("ReviewGateReceiptEvidence-v1", evidence.GetProperty("Kind").GetString());
        var pin = evidence.GetProperty("RawReceipt");
        Assert.Equal(bytes.LongLength, pin.GetProperty("Bytes").GetInt64());
        Assert.Equal("sha256:" + BoundedFiles.Sha(bytes), pin.GetProperty("Sha256").GetString());
        Assert.Equal(raw.RootElement.GetProperty("identity").GetString(), pin.GetProperty("Identity").GetString());
        var fields = evidence.GetProperty("ReceiptFields");
        foreach (var field in raw.RootElement.EnumerateObject().Where(p => p.Name is not ("identity" or "artifactHashes")))
            Assert.Equal(CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(field.Value.GetRawText())),
                CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(fields.GetProperty(field.Name).GetRawText())));
        var map = raw.RootElement.GetProperty("artifactHashes");
        Assert.Equal(401, evidence.GetProperty("ArtifactInventory").GetProperty("Count").GetInt32());
        Assert.Equal("sha256:" + BoundedFiles.Sha(CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(map.GetRawText()))),
            evidence.GetProperty("ArtifactInventory").GetProperty("CanonicalIdentity").GetString());
        var oldPrompt = JsonSerializer.Serialize(raw.RootElement, BoundedFiles.Json);
        var newPrompt = JsonSerializer.Serialize(new { CompletePlan = "unchanged plan", QualityEvidence = evidence,
            CompletePatch = "unchanged complete patch" }, BoundedFiles.Json);
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(oldPrompt)) > 60000);
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(newPrompt)) < 60000);
        Assert.Throws<ContractValidationException>(() => ContractValidator.ParseAndValidate(
            JsonSerializer.SerializeToUtf8Bytes(evidence)));
    }

    [Fact]
    public void Changing_one_retained_artifact_changes_both_exact_receipt_and_complete_inventory_pins()
    {
        var before = ReviewGateReceiptEvidence.Create("retained/receipt.json", Receipt());
        var after = ReviewGateReceiptEvidence.Create("retained/receipt.json", Receipt(399));
        Assert.Equal(before.GetProperty("ReceiptFields").GetRawText(), after.GetProperty("ReceiptFields").GetRawText());
        Assert.NotEqual(before.GetProperty("RawReceipt").GetProperty("Sha256").GetString(),
            after.GetProperty("RawReceipt").GetProperty("Sha256").GetString());
        Assert.NotEqual(before.GetProperty("RawReceipt").GetProperty("Identity").GetString(),
            after.GetProperty("RawReceipt").GetProperty("Identity").GetString());
        Assert.NotEqual(before.GetProperty("ArtifactInventory").GetProperty("CanonicalIdentity").GetString(),
            after.GetProperty("ArtifactInventory").GetProperty("CanonicalIdentity").GetString());
    }
}
