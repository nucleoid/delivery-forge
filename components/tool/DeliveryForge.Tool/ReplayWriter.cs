using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Tool.Packaging;
namespace DeliveryForge.Tool;

public static class ReplayWriter
{
    public static byte[] Build(ValidatedContract manifest, IReadOnlyList<ValidatedContract> contracts)
    {
        if (manifest.SchemaName != "run-manifest" || contracts.Count > 512) throw new ToolUsageException("Replay requires one actual validated run manifest and bounded complete contract graph.");
        var all = new[] { manifest }.Concat(contracts).ToArray(); if (all.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count() != all.Length) throw new ToolUsageException("Replay graph duplicate identity.");
        var receipts = contracts.Where(x => x.SchemaName is "gate-receipt" or "review-receipt" or "publication-receipt").Select(x => x.Identity).Order(StringComparer.Ordinal).ToArray();
        var others = contracts.Where(x => x.SchemaName is not ("gate-receipt" or "review-receipt" or "publication-receipt")).Select(x => x.Identity).Order(StringComparer.Ordinal).ToArray();
        var root = new JsonObject { ["schemaVersion"] = "1.0.0", ["kind"] = "replay-bundle", ["identity"] = "", ["runManifestIdentity"] = manifest.Identity, ["receiptIdentities"] = JsonSerializer.SerializeToNode(receipts), ["contractIdentities"] = JsonSerializer.SerializeToNode(others), ["createdAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture) };
        root["identity"] = CanonicalJson.ComputeIdentity(JsonSerializer.SerializeToUtf8Bytes(root)); var bytes = CanonicalJson.Canonicalize(JsonSerializer.SerializeToUtf8Bytes(root));
        ContractReferenceValidator.Validate(all.Select(x => x.CanonicalBytes).Append(bytes));
        return bytes; // Contains references, never commands, credentials or runtime adoption.
    }
    public static void Write(string output, ValidatedContract manifest, IReadOnlyList<ValidatedContract> contracts, ToolLifetime life)
    {
        var bytes = Build(manifest, contracts); PublicContentScanner.RequireJsonSafe(bytes); var root = BoundedFiles.Plain(output + ".contracts", true);
        if (Directory.Exists(root) || File.Exists(root) || File.Exists(output)) throw new IOException("Replay destination already exists.");
        foreach (var contract in new[] { manifest }.Concat(contracts)) PublicContentScanner.RequireJsonSafe(contract.CanonicalBytes.Span);
        life.Check(); Directory.CreateDirectory(root); life.Check();
        foreach (var contract in new[] { manifest }.Concat(contracts)) BoundedFiles.WriteNew(Path.Combine(root, contract.Identity.Replace(':', '-') + ".json"), contract.CanonicalBytes.Span, life);
        BoundedFiles.WriteNew(output, bytes, life);
    }
    public static void FromRunDirectory(string runDirectory, string output, ToolLifetime life)
    {
        BoundedFiles.Separate(runDirectory, output); var manifest = ContractValidator.ParseAndValidate(BoundedFiles.Read(Path.Combine(runDirectory, "manifest.json"), life));
        var directory = BoundedFiles.Plain(Path.Combine(runDirectory, "contracts"), true); var files = Directory.EnumerateFiles(directory, "*.json").Take(513).ToArray();
        if (files.Length > 512) throw new IOException("Replay contract graph exceeds finite count.");
        const long maximumRetained = 64L * 1024 * 1024; long retained = manifest.CanonicalBytes.Length;
        var contracts = new List<ValidatedContract>();
        foreach (var path in files) { life.Check(); var length = new FileInfo(BoundedFiles.Plain(path)).Length; life.Check(); if (length < 0 || length > 16 * 1024 * 1024 || retained > maximumRetained - length) throw new IOException("Replay aggregate byte admission exceeded."); var raw = BoundedFiles.Read(path, life); if (raw.LongLength != length) throw new IOException("Replay file changed after aggregate admission."); retained += raw.Length; contracts.Add(ContractValidator.ParseAndValidate(raw)); life.Check(); }
        Write(output, manifest, contracts, life);
    }
}
