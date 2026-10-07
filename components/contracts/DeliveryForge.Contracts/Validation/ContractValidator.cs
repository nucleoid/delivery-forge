namespace DeliveryForge.Contracts.Validation;

public sealed record ValidatedContract(string SchemaName, string SchemaVersion, string Identity, byte[] CanonicalBytes);

public static class ContractValidator
{
    public static ValidatedContract ParseAndValidate(ReadOnlySpan<byte> utf8Json) =>
        throw new NotImplementedException("Strict parsing and schema validation are not implemented.");
}
