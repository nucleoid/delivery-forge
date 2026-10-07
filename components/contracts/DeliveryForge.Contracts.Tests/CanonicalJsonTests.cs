using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;

namespace DeliveryForge.Contracts.Tests;

public sealed class CanonicalJsonTests
{
    public static IEnumerable<object[]> Rfc8785Vectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "GoldenVectors", "rfc8785.json")));
        return document.RootElement.EnumerateArray()
            .Select(item => new object[]
            {
                item.GetProperty("name").GetString()!,
                item.GetProperty("input").GetString()!,
                item.GetProperty("expected").GetString()!
            })
            .ToArray();
    }

    [Theory]
    [MemberData(nameof(Rfc8785Vectors))]
    public void Matches_rfc_8785_golden_vectors(string name, string input, string expected)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Equal(expected, Encoding.UTF8.GetString(CanonicalJson.Canonicalize(Utf8(input))));
    }

    [Fact]
    public void Accepts_finite_binary64_numbers_outside_schema_integer_range() =>
        Assert.NotEmpty(CanonicalJson.Canonicalize(Utf8("{\"n\":9007199254740992}")));

    [Fact]
    public void Rejects_numbers_outside_finite_binary64_range() =>
        Assert.Throws<ContractJsonException>(() => CanonicalJson.Canonicalize(Utf8("{\"n\":1e400}")));

    [Fact]
    public void Rejects_invalid_utf8()
    {
        byte[] invalid = [0x7b, 0x22, 0x78, 0x22, 0x3a, 0x22, 0xc3, 0x28, 0x22, 0x7d];
        Assert.Throws<ContractJsonException>(() => CanonicalJson.Canonicalize(invalid));
    }

    [Fact]
    public void Identity_removes_only_top_level_identity()
    {
        var withoutIdentity = CanonicalJson.ComputeIdentity(Utf8("{\"nested\":{\"identity\":\"kept\"},\"value\":1}"));
        var withIdentity = CanonicalJson.ComputeIdentity(Utf8("{\"identity\":\"ignored\",\"nested\":{\"identity\":\"kept\"},\"value\":1}"));
        var changedNestedIdentity = CanonicalJson.ComputeIdentity(Utf8("{\"identity\":\"ignored\",\"nested\":{\"identity\":\"changed\"},\"value\":1}"));

        Assert.Equal(withoutIdentity, withIdentity);
        Assert.NotEqual(withIdentity, changedNestedIdentity);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
