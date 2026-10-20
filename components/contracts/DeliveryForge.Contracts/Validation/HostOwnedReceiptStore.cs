namespace DeliveryForge.Contracts.Validation;

/// <summary>
/// In-memory capability proving that exact receipt identities were appended by this host
/// through <see cref="HostOwnedReceiptStore"/>. Contract bytes alone cannot mint it.
/// </summary>
public sealed class TrustedReceiptProvenance
{
    private readonly HashSet<string> identities = new(StringComparer.Ordinal);
    internal void Add(string identity) => identities.Add(identity);
    public bool Contains(string identity) => identities.Contains(identity);
}

/// <summary>
/// Code-owned append-only receipt sink. The caller must place the root outside all
/// worker/patch-controlled trees and retain this object as the provenance capability.
/// </summary>
public sealed class HostOwnedReceiptStore
{
    private readonly string root;
    private readonly TrustedReceiptProvenance provenance = new();

    public HostOwnedReceiptStore(string absoluteRoot)
    {
        if (string.IsNullOrWhiteSpace(absoluteRoot) || !Path.IsPathFullyQualified(absoluteRoot))
            throw new ArgumentException("Receipt store root must be an absolute host-owned path.", nameof(absoluteRoot));
        root = Path.GetFullPath(absoluteRoot);
        Directory.CreateDirectory(root);
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("Receipt store root cannot be a reparse point.", nameof(absoluteRoot));
    }

    public TrustedReceiptProvenance Provenance => provenance;

    public string Append(ValidatedContract receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.SchemaName != "gate-receipt")
            throw new ArgumentException("Only validated gate receipts can enter the gate provenance store.", nameof(receipt));
        var path = Path.Combine(root, receipt.Identity.Replace(':', '-') + ".json");
        var bytes = receipt.CanonicalBytes.ToArray();
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
            if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) ||
                !File.ReadAllBytes(path).SequenceEqual(bytes))
                throw;
        }
        provenance.Add(receipt.Identity);
        return path;
    }
}
