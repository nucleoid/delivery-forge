namespace DeliveryForge.Contracts.Serialization;

public static class CanonicalJson
{
    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json) =>
        throw new NotImplementedException("RFC 8785 canonicalization is not implemented.");

    public static string ComputeIdentity(ReadOnlySpan<byte> utf8Json) =>
        throw new NotImplementedException("Contract identity is not implemented.");
}
