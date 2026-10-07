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
                        RequireReference(root.GetProperty("policyIdentity").GetString()!, identities, "policyIdentity", kind => kind == "evidence-policy");
                        RequireReferences(
                            root.GetProperty("gateReceiptIds"),
                            identities,
                            "gateReceiptIds",
                            kind => kind == "gate-receipt",
                            root.GetProperty("baseCommit").GetString(),
                            root.GetProperty("headCommit").GetString(),
                            root.GetProperty("treeId").GetString());
                        ValidateRunManifest(root, identities);
                        break;
                    case "checkpoint":
                        RequireReferences(
                            root.GetProperty("gateReceiptIds"), identities, "gateReceiptIds", kind => kind == "gate-receipt",
                            expectedHead: root.GetProperty("headCommit").GetString(),
                            expectedTree: root.GetProperty("treeId").GetString());
                        break;
                    case "replay-bundle":
                        RequireReference(root.GetProperty("runManifestIdentity").GetString()!, identities, "runManifestIdentity", kind => kind == "run-manifest");
                        var manifest = identities[root.GetProperty("runManifestIdentity").GetString()!].Document.RootElement;
                        RequireReferences(
                            root.GetProperty("receiptIdentities"), identities, "receiptIdentities", IsReceipt,
                            manifest.GetProperty("baseCommit").GetString(), manifest.GetProperty("headCommit").GetString(), manifest.GetProperty("treeId").GetString());
                        RequireReferences(root.GetProperty("contractIdentities"), identities, "contractIdentities", kind => !IsReceipt(kind));
                        ValidateReplayBundle(root, manifest);
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

    private static void ValidateRunManifest(
        JsonElement manifest,
        IReadOnlyDictionary<string, (ValidatedContract Contract, JsonDocument Document)> identities)
    {
        var plan = identities[manifest.GetProperty("planIdentity").GetString()!].Document.RootElement;
        var policy = identities[manifest.GetProperty("policyIdentity").GetString()!].Document.RootElement;
        if (plan.GetProperty("baseCommit").GetString() != manifest.GetProperty("baseCommit").GetString())
        {
            throw new ContractReferenceException("run-manifest baseCommit does not match its immutable plan.");
        }

        var ceilings = new Dictionary<string, int>(StringComparer.Ordinal) { ["plan"] = 0, ["implement"] = 1, ["pr"] = 2, ["merge"] = 3 };
        if (ceilings[manifest.GetProperty("authorizationCeiling").GetString()!] > ceilings[policy.GetProperty("authorizedCeiling").GetString()!])
        {
            throw new ContractReferenceException("run-manifest authorizationCeiling exceeds its immutable policy.");
        }

        var required = policy.GetProperty("requiredGates").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gateIdentity in manifest.GetProperty("gateReceiptIds").EnumerateArray().Select(item => item.GetString()!))
        {
            var gate = identities[gateIdentity].Document.RootElement;
            if (gate.GetProperty("policyIdentity").GetString() != manifest.GetProperty("policyIdentity").GetString() ||
                gate.GetProperty("outcome").GetString() != "PASS")
            {
                throw new ContractReferenceException("run-manifest gate receipt is not a PASS under its immutable policy.");
            }

            supplied.Add(gate.GetProperty("gateId").GetString()!);
        }

        if (!required.IsSubsetOf(supplied))
        {
            throw new ContractReferenceException("run-manifest does not include every gate required by its immutable policy.");
        }
    }

    private static void ValidateReplayBundle(JsonElement bundle, JsonElement manifest)
    {
        var contracts = bundle.GetProperty("contractIdentities").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var receipts = bundle.GetProperty("receiptIdentities").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        if (!contracts.Contains(manifest.GetProperty("planIdentity").GetString()!) ||
            !contracts.Contains(manifest.GetProperty("policyIdentity").GetString()!) ||
            !manifest.GetProperty("gateReceiptIds").EnumerateArray().All(item => receipts.Contains(item.GetString()!)))
        {
            throw new ContractReferenceException("replay-bundle omits manifest plan, policy, or gate evidence.");
        }
    }

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
        string? expectedBase = null,
        string? expectedHead = null,
        string? expectedTree = null)
    {
        foreach (var reference in references.EnumerateArray())
        {
            RequireReference(reference.GetString()!, identities, field, acceptsKind, expectedBase, expectedHead, expectedTree);
        }
    }

    private static void RequireReference(
        string reference,
        IReadOnlyDictionary<string, (ValidatedContract Contract, JsonDocument Document)> identities,
        string field,
        Func<string, bool> acceptsKind,
        string? expectedBase = null,
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
            if ((expectedBase is not null && !string.Equals(root.GetProperty("baseCommit").GetString(), expectedBase, StringComparison.Ordinal)) ||
                !string.Equals(root.GetProperty("headCommit").GetString(), expectedHead, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("treeId").GetString(), expectedTree, StringComparison.Ordinal))
            {
                throw new ContractReferenceException($"{field} references receipt '{reference}' for a different head or tree.");
            }
        }
    }
}

public sealed class ContractReferenceException(string message) : Exception(message);
