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
                        RequireReferences(
                            root.GetProperty("gateReceiptIds"),
                            identities,
                            "gateReceiptIds",
                            kind => kind == "gate-receipt",
                            root.GetProperty("headCommit").GetString(),
                            root.GetProperty("treeId").GetString());
                        break;
                    case "checkpoint":
                        RequireReferences(root.GetProperty("gateReceiptIds"), identities, "gateReceiptIds", kind => kind == "gate-receipt");
                        break;
                    case "replay-bundle":
                        RequireReference(root.GetProperty("runManifestIdentity").GetString()!, identities, "runManifestIdentity", kind => kind == "run-manifest");
                        RequireReferences(root.GetProperty("receiptIdentities"), identities, "receiptIdentities", IsReceipt);
                        RequireReferences(root.GetProperty("contractIdentities"), identities, "contractIdentities", kind => !IsReceipt(kind));
                        break;
                    case "gate-receipt":
                        ValidateGatePolicy(root, identities);
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

    private static void ValidateGatePolicy(
        JsonElement gate,
        IReadOnlyDictionary<string, (ValidatedContract Contract, JsonDocument Document)> identities)
    {
        var policyIdentity = gate.GetProperty("policyIdentity").GetString()!;
        RequireReference(policyIdentity, identities, "policyIdentity", kind => kind == "evidence-policy");
        if (gate.GetProperty("outcome").GetString() == "NOT_APPLICABLE")
        {
            var policy = identities[policyIdentity].Document.RootElement;
            var gateId = gate.GetProperty("gateId").GetString();
            if (!policy.GetProperty("allowedNotApplicable").EnumerateArray().Any(item => item.GetString() == gateId))
            {
                throw new ContractReferenceException($"Policy '{policyIdentity}' does not permit gate '{gateId}' to be NOT_APPLICABLE.");
            }
        }
    }

    private static void RequireReferences(
        JsonElement references,
        IReadOnlyDictionary<string, (ValidatedContract Contract, JsonDocument Document)> identities,
        string field,
        Func<string, bool> acceptsKind,
        string? expectedHead = null,
        string? expectedTree = null)
    {
        foreach (var reference in references.EnumerateArray())
        {
            RequireReference(reference.GetString()!, identities, field, acceptsKind, expectedHead, expectedTree);
        }
    }

    private static void RequireReference(
        string reference,
        IReadOnlyDictionary<string, (ValidatedContract Contract, JsonDocument Document)> identities,
        string field,
        Func<string, bool> acceptsKind,
        string? expectedHead = null,
        string? expectedTree = null)
    {
        if (!identities.TryGetValue(reference, out var target))
        {
            throw new ContractReferenceException($"{field} references missing immutable contract '{reference}'.");
        }

        if (!acceptsKind(target.Contract.SchemaName))
        {
            throw new ContractReferenceException($"{field} references contract '{reference}' of invalid kind '{target.Contract.SchemaName}'.");
        }

        if (expectedHead is not null && expectedTree is not null)
        {
            var root = target.Document.RootElement;
            if (!string.Equals(root.GetProperty("headCommit").GetString(), expectedHead, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("treeId").GetString(), expectedTree, StringComparison.Ordinal))
            {
                throw new ContractReferenceException($"{field} references receipt '{reference}' for a different head or tree.");
            }
        }
    }
}

public sealed class ContractReferenceException(string message) : Exception(message);
