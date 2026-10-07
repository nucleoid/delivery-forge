using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Contracts.State;

public sealed partial class AppendOnlyContractStore
{
    private readonly string _rootDirectory;

    public AppendOnlyContractStore(string rootDirectory) => _rootDirectory = rootDirectory;

    public async Task<string> WriteImmutableAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken = default)
    {
        var validated = ContractValidator.ParseAndValidate(document.Span);
        Directory.CreateDirectory(_rootDirectory);
        var target = Path.Combine(_rootDirectory, $"{validated.Identity}.json");
        if (File.Exists(target))
        {
            var existing = await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(validated.CanonicalBytes))
            {
                throw new AppendOnlyContractException($"Existing immutable record '{validated.Identity}' has different bytes.");
            }

            return target;
        }

        var temporary = Path.Combine(_rootDirectory, $".{validated.Identity}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, validated.CanonicalBytes, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporary, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                var existing = await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(validated.CanonicalBytes))
                {
                    throw new AppendOnlyContractException($"Concurrent immutable record '{validated.Identity}' has different bytes.");
                }
            }
        }
        finally
        {
            File.Delete(temporary);
        }

        return target;
    }

    public async Task UpdateCurrentPointerAsync(string identity, CancellationToken cancellationToken = default)
    {
        if (!IdentityPattern().IsMatch(identity))
        {
            throw new AppendOnlyContractException("A current pointer requires a valid immutable identity.");
        }

        Directory.CreateDirectory(_rootDirectory);
        var immutablePath = Path.Combine(_rootDirectory, $"{identity}.json");
        if (!File.Exists(immutablePath))
        {
            throw new AppendOnlyContractException($"Immutable record '{identity}' does not exist.");
        }

        var immutableBytes = await File.ReadAllBytesAsync(immutablePath, cancellationToken).ConfigureAwait(false);
        var validated = ContractValidator.ParseAndValidate(immutableBytes);
        if (!string.Equals(validated.Identity, identity, StringComparison.Ordinal))
        {
            throw new AppendOnlyContractException($"Immutable record '{identity}' does not match its filename.");
        }

        var pointer = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["identity"] = identity });
        var current = Path.Combine(_rootDirectory, "manifest-current.json");
        var temporary = Path.Combine(_rootDirectory, $".manifest-current.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, pointer, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, current, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();
}

public sealed class AppendOnlyContractException(string message) : Exception(message);
