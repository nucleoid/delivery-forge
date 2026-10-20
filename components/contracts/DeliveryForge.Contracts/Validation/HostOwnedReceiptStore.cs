using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Contracts.Validation;

/// <summary>
/// Durable verifier for receipts admitted to a protected append-only host ledger. A new
/// verifier instance can validate provenance after restart or from another host process.
/// </summary>
public sealed class TrustedReceiptProvenance
{
    private const int MaximumReceiptBytes = 8 * 1024 * 1024;
    private readonly string root;
    private readonly string ledger;

    internal TrustedReceiptProvenance(string root, string ledger)
    {
        this.root = root;
        this.ledger = ledger;
    }

    public bool Contains(string identity)
    {
        if (!TryFileSegment(identity, out var segment))
            return false;
        var receiptPath = Path.Combine(root, segment + ".json");
        var ledgerPath = Path.Combine(ledger, segment + ".entry");
        try
        {
            if (!RegularFile(receiptPath) || !RegularFile(ledgerPath))
                return false;
            var receiptInfo = new FileInfo(receiptPath);
            var ledgerInfo = new FileInfo(ledgerPath);
            if (receiptInfo.Length <= 0 || receiptInfo.Length > MaximumReceiptBytes ||
                ledgerInfo.Length <= 0 || ledgerInfo.Length > 256)
                return false;
            var bytes = File.ReadAllBytes(receiptPath);
            var receipt = ContractValidator.ParseAndValidate(bytes);
            if (receipt.SchemaName != "gate-receipt" || receipt.Identity != identity ||
                !receipt.CanonicalBytes.Span.SequenceEqual(bytes))
                return false;
            var expectedLedger = LedgerBytes(identity, bytes);
            return File.ReadAllBytes(ledgerPath).AsSpan().SequenceEqual(expectedLedger);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ContractValidationException)
        {
            return false;
        }
    }

    internal static byte[] LedgerBytes(string identity, ReadOnlySpan<byte> canonicalBytes) =>
        Encoding.ASCII.GetBytes(identity + " " + Convert.ToHexStringLower(SHA256.HashData(canonicalBytes)) + "\n");

    internal static bool TryFileSegment(string identity, out string segment)
    {
        segment = string.Empty;
        if (!identity.StartsWith("sha256:", StringComparison.Ordinal) || identity.Length != 71 ||
            identity.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            return false;
        segment = identity.Replace(':', '-');
        return true;
    }

    private static bool RegularFile(string path) =>
        File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
}

/// <summary>
/// Code-owned append-only receipt sink with a durable, independently verifiable ledger.
/// The root must be provisioned outside worker/patch-controlled trees before producers are enabled.
/// </summary>
public sealed class HostOwnedReceiptStore
{
    private readonly string root;
    private readonly string ledger;
    private readonly TrustedReceiptProvenance provenance;

    public HostOwnedReceiptStore(string absoluteRoot)
    {
        if (string.IsNullOrWhiteSpace(absoluteRoot) || !Path.IsPathFullyQualified(absoluteRoot))
            throw new ArgumentException("Receipt store root must be an absolute host-owned path.", nameof(absoluteRoot));
        root = Path.GetFullPath(absoluteRoot);
        Directory.CreateDirectory(root);
        EnsureProtectedDirectory(root, nameof(absoluteRoot));
        ledger = Path.Combine(root, ".provenance");
        Directory.CreateDirectory(ledger);
        EnsureProtectedDirectory(ledger, nameof(absoluteRoot));
        provenance = new(root, ledger);
    }

    public TrustedReceiptProvenance Provenance => provenance;

    public string Append(ValidatedContract receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.SchemaName != "gate-receipt" ||
            !TrustedReceiptProvenance.TryFileSegment(receipt.Identity, out var segment))
            throw new ArgumentException("Only validated gate receipts can enter the gate provenance store.", nameof(receipt));
        var path = Path.Combine(root, segment + ".json");
        var bytes = receipt.CanonicalBytes.ToArray();
        WriteCreateOnce(path, bytes);
        var ledgerPath = Path.Combine(ledger, segment + ".entry");
        WriteCreateOnce(ledgerPath, TrustedReceiptProvenance.LedgerBytes(receipt.Identity, bytes));
        if (!provenance.Contains(receipt.Identity))
            throw new IOException("Durable receipt provenance could not be verified after append.");
        return path;
    }

    public ValidatedContract Read(string identity)
    {
        if (!provenance.Contains(identity) ||
            !TrustedReceiptProvenance.TryFileSegment(identity, out var segment))
            throw new IOException("Receipt lacks valid durable host provenance.");
        return ContractValidator.ParseAndValidate(File.ReadAllBytes(Path.Combine(root, segment + ".json")));
    }

    private static void WriteCreateOnce(string path, byte[] bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
            if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) ||
                !File.ReadAllBytes(path).SequenceEqual(bytes))
                throw;
        }
    }

    private static void EnsureProtectedDirectory(string path, string parameter)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Receipt store path cannot traverse a reparse point.", parameter);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
