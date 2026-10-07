using System.Text.Json;

namespace DeliveryForge.Contracts.Validation;

public static class ContractReferenceValidator
{
    public static void Validate(IEnumerable<ReadOnlyMemory<byte>> documents)
    {
        var parsed = new List<(ValidatedContract Contract, JsonDocument Document)>();

        try
        {
            foreach (var bytes in documents)
            {
                var contract = ContractValidator.ParseAndValidate(bytes.Span);
                parsed.Add((contract, JsonDocument.Parse(bytes)));
            }

            var identities = parsed.ToDictionary(item => item.Contract.Identity, StringComparer.Ordinal);
            foreach (var item in parsed)
            {
                var root = item.Document.RootElement;
                switch (item.Contract.SchemaName)
                {
                    case "run-manifest":
                        RequireReference(root.GetProperty("planIdentity").GetString()!, identities, "planIdentity", kind => kind == "plan");
                        RequireReferences(root.GetProperty("gateReceiptIds"), identities, "gateReceiptIds", kind => kind == "gate-receipt");
                        break;
                    case "checkpoint":
                        RequireReferences(root.GetProperty("gateReceiptIds"), identities, "gateReceiptIds", kind => kind == "gate-receipt");
                        break;
                    case "replay-bundle":
                        RequireReference(root.GetProperty("runManifestIdentity").GetString()!, identities, "runManifestIdentity", kind => kind == "run-manifest");
                        RequireReferences(root.GetProperty("receiptIdentities"), identities, "receiptIdentities", IsReceipt);
                        RequireReferences(root.GetProperty("contractIdentities"), identities, "contractIdentities", kind => !IsReceipt(kind));
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

    private static bool IsReceipt(string kind) => kind is "gate-receipt" or "review-receipt" or "publication-receipt";

    private static void RequireReferences(
        JsonElement references,
        IReadOnlyDictionary<string, (ValidatedContract Contract, JsonDocument Document)> identities,
        string field,
        Func<string, bool> acceptsKind)
    {
        foreach (var reference in references.EnumerateArray())
        {
            RequireReference(reference.GetString()!, identities, field, acceptsKind);
        }
    }

    private static void RequireReference(
        string reference,
        IReadOnlyDictionary<string, (ValidatedContract Contract, JsonDocument Document)> identities,
        string field,
        Func<string, bool> acceptsKind)
    {
        if (!identities.TryGetValue(reference, out var target))
        {
            throw new ContractReferenceException($"{field} references missing immutable contract '{reference}'.");
        }

        if (!acceptsKind(target.Contract.SchemaName))
        {
            throw new ContractReferenceException($"{field} references contract '{reference}' of invalid kind '{target.Contract.SchemaName}'.");
        }
    }
}

public sealed class ContractReferenceException(string message) : Exception(message);
