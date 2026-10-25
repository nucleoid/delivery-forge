using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Tool;

// Review presentation only. The original canonical receipt and every artifact
// remain retained; this wrapper cannot be parsed or consumed as a gate receipt.
internal static class ReviewGateReceiptEvidence
{
    internal static JsonElement Create(string path, byte[] exactReceipt)
    {
        var validated = ContractValidator.ParseAndValidate(exactReceipt);
        if (validated.SchemaName != "gate-receipt")
            throw new IOException("Review quality evidence requires an actual gate receipt.");
        using var document = JsonDocument.Parse(exactReceipt);
        var root = document.RootElement;
        var artifacts = root.GetProperty("artifactHashes");
        var completeInventory = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(artifacts.GetRawText()));
        var fields = root.EnumerateObject()
            .Where(field => field.Name is not ("artifactHashes" or "identity"))
            .ToDictionary(field => field.Name, field => field.Value.Clone(), StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(new
        {
            Kind = "ReviewGateReceiptEvidence-v1",
            RawReceipt = new
            {
                Path = path,
                Bytes = exactReceipt.LongLength,
                Sha256 = "sha256:" + BoundedFiles.Sha(exactReceipt),
                Identity = validated.Identity
            },
            ReceiptFields = fields,
            ArtifactInventory = new
            {
                Count = artifacts.EnumerateObject().Count(),
                CanonicalIdentity = "sha256:" + BoundedFiles.Sha(completeInventory)
            }
        }, BoundedFiles.Json);
    }
}
