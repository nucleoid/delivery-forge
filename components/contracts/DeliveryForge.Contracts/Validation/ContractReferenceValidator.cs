using System.Text.Json;

namespace DeliveryForge.Contracts.Validation;

public static class ContractReferenceValidator
{
    public static void Validate(IEnumerable<ReadOnlyMemory<byte>> documents)
    {
        var parsed = documents.Select(bytes =>
        {
            var contract = ContractValidator.ParseAndValidate(bytes.Span);
            return (Contract: contract, Document: JsonDocument.Parse(bytes));
        }).ToArray();

        try
        {
            var identities = parsed.Select(item => item.Contract.Identity).ToHashSet(StringComparer.Ordinal);
            foreach (var item in parsed)
            {
                var root = item.Document.RootElement;
                switch (item.Contract.SchemaName)
                {
                    case "run-manifest":
                        RequireReference(root.GetProperty("planIdentity").GetString()!, identities, "planIdentity");
                        RequireReferences(root.GetProperty("gateReceiptIds"), identities, "gateReceiptIds");
                        break;
                    case "checkpoint":
                        RequireReferences(root.GetProperty("gateReceiptIds"), identities, "gateReceiptIds");
                        break;
                    case "replay-bundle":
                        RequireReference(root.GetProperty("runManifestIdentity").GetString()!, identities, "runManifestIdentity");
                        RequireReferences(root.GetProperty("receiptIdentities"), identities, "receiptIdentities");
                        RequireReferences(root.GetProperty("contractIdentities"), identities, "contractIdentities");
                        break;
                }
            }
        }
        finally
        {
            foreach (var item in parsed)
            {
                item.Document.Dispose();
            }
        }
    }

    private static void RequireReferences(JsonElement references, IReadOnlySet<string> identities, string field)
    {
        foreach (var reference in references.EnumerateArray())
        {
            RequireReference(reference.GetString()!, identities, field);
        }
    }

    private static void RequireReference(string reference, IReadOnlySet<string> identities, string field)
    {
        if (!identities.Contains(reference))
        {
            throw new ContractReferenceException($"{field} references missing immutable contract '{reference}'.");
        }
    }
}

public sealed class ContractReferenceException(string message) : Exception(message);
