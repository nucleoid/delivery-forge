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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Base_duplicate_requires_actual_canonical_bytes_not_compressed_equality(bool sha256)
    {
        using var temporary = new TemporaryDirectory();
        var body = new byte[] { 0, 255, 128, 10, 13, 0 };
        var raw = Encoding.ASCII.GetBytes("blob 6\0").Concat(body).ToArray();
        var oid = Convert.ToHexStringLower(sha256 ? SHA256.HashData(raw) : SHA1.HashData(raw));
        Write(temporary.Path, oid, Encode(raw));
        var calls = 0;
        var snapshot = await LooseGitObjectInventory.CaptureAsync(temporary.Path,
            new HashSet<string>(StringComparer.Ordinal) { oid }, () => { }, Read,
            (actual, type, maximum, token) =>
            {
                Assert.Equal(oid, actual); Assert.Equal("blob", type); Assert.True(maximum >= body.Length); calls++;
                return Task.FromResult(body);
            }, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.True(snapshot.IsBaseDuplicate(oid)); snapshot.Verify(() => { });
        await Assert.ThrowsAsync<WorktreeBoundaryException>(() => LooseGitObjectInventory.CaptureAsync(temporary.Path,
            new HashSet<string>(StringComparer.Ordinal) { oid }, () => { }, Read,
            (_, _, _, _) => Task.FromResult(new byte[] { 0, 255, 129, 10, 13, 0 }), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invalid_private_content_never_dispatches_a_trusted_base_read()
    {
        using var temporary = new TemporaryDirectory();
        var oid = new string('a', 40); Write(temporary.Path, oid, Encode(Encoding.UTF8.GetBytes("blob 4\0mine")));
        var calls = 0;
        await Assert.ThrowsAsync<WorktreeBoundaryException>(() => LooseGitObjectInventory.CaptureAsync(temporary.Path,
            new HashSet<string>(StringComparer.Ordinal) { oid }, () => { }, Read,
            (_, _, _, _) => { calls++; return Task.FromResult(Array.Empty<byte>()); }, TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task A_base_comparison_cannot_accept_a_private_file_replaced_during_the_await()
    {
        using var temporary = new TemporaryDirectory();
        var raw = Encoding.UTF8.GetBytes("blob 4\0base");
        var oid = Convert.ToHexStringLower(SHA1.HashData(raw));
        var path = Write(temporary.Path, oid, Encode(raw));
        await Assert.ThrowsAsync<WorktreeBoundaryException>(() => LooseGitObjectInventory.CaptureAsync(temporary.Path,
            new HashSet<string>(StringComparer.Ordinal) { oid }, () => { }, Read,
            async (_, _, _, token) =>
            {
                await Task.Yield(); token.ThrowIfCancellationRequested();
                File.Delete(path); Write(temporary.Path, oid, Encode(raw));
                return Encoding.UTF8.GetBytes("base");
            }, TestContext.Current.CancellationToken));
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
