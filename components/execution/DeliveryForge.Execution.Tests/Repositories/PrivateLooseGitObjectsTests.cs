using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Execution.Tests.Repositories;

public sealed class PrivateLooseGitObjectsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Canonical_loose_object_requires_its_real_content_identity_and_inflated_allowance(bool sha256)
    {
        var raw = Encoding.UTF8.GetBytes("blob 8\0content\n");
        var oid = Convert.ToHexStringLower(sha256 ? SHA256.HashData(raw) : SHA1.HashData(raw));
        var encoded = Encode(raw);
        Assert.Equal(raw.Length, LooseGitObjectInventory.ValidateObject(oid, encoded, raw.Length, () => { }));
        Assert.Throws<WorktreeBoundaryException>(() => LooseGitObjectInventory.ValidateObject(new string('0', oid.Length), encoded, raw.Length, () => { }));
        Assert.Throws<WorktreeBoundaryException>(() => LooseGitObjectInventory.ValidateObject(oid, encoded, raw.Length - 1, () => { }));
        Assert.Throws<WorktreeBoundaryException>(() => LooseGitObjectInventory.ValidateObject(oid, encoded[..^1], raw.Length, () => { }));
    }

    [Theory]
    [InlineData("blob 08\0content\n")]
    [InlineData("blob 7\0content\n")]
    [InlineData("routing 8\0content\n")]
    [InlineData("blob -1\0")]
    public void A_matching_hash_does_not_admit_a_noncanonical_Git_header(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        var oid = Convert.ToHexStringLower(SHA1.HashData(raw));
        Assert.Throws<WorktreeBoundaryException>(() => LooseGitObjectInventory.ValidateObject(oid, Encode(raw), 1024, () => { }));
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("info")]
    [InlineData("ab/temporary")]
    [InlineData("AB")]
    public void Private_packs_routing_and_in_progress_shapes_are_not_authority(string relative)
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Path, relative));
        Assert.Throws<WorktreeBoundaryException>(() => Capture(temporary.Path));
    }

    [Fact]
    public void Protected_object_ids_refuse_shadowing_even_when_the_base_copy_is_packed_elsewhere()
    {
        using var temporary = new TemporaryDirectory();
        var raw = Encoding.UTF8.GetBytes("blob 4\0base");
        var oid = Convert.ToHexStringLower(SHA1.HashData(raw));
        Write(temporary.Path, oid, Encode(raw));
        // Protected names come from the real base-only native inventory, not
        // from whether a same-name loose file happens to exist in Common.
        Assert.Throws<WorktreeBoundaryException>(() => LooseGitObjectInventory.Capture(temporary.Path,
            new HashSet<string>(StringComparer.Ordinal) { oid }, () => { }, Read));
    }

    [Fact]
    public void An_admitted_namespace_cannot_gain_or_change_an_object_before_trusted_dispatch()
    {
        using var temporary = new TemporaryDirectory();
        var raw = Encoding.UTF8.GetBytes("blob 4\0mine");
        var oid = Convert.ToHexStringLower(SHA1.HashData(raw));
        var path = Write(temporary.Path, oid, Encode(raw));
        var snapshot = Capture(temporary.Path);
        snapshot.Verify(() => { });
        using (var file = new FileStream(path, FileMode.Append, FileAccess.Write)) file.WriteByte(0);
        Assert.Throws<WorktreeBoundaryException>(() => snapshot.Verify(() => { }));
    }

    static LooseGitObjectInventory Capture(string root) => LooseGitObjectInventory.Capture(root,
        new HashSet<string>(StringComparer.Ordinal) { new string('a', 40) }, () => { }, Read);
    static byte[] Read(string path, int maximum)
    {
        var bytes = File.ReadAllBytes(path); Assert.True(bytes.Length <= maximum); return bytes;
    }
    static string Write(string root, string oid, byte[] bytes)
    {
        Directory.CreateDirectory(Path.Combine(root, oid[..2]));
        var path = Path.Combine(root, oid[..2], oid[2..]); File.WriteAllBytes(path, bytes); return path;
    }
    static byte[] Encode(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Fastest, true)) compressor.Write(raw);
        return output.ToArray();
    }
}
