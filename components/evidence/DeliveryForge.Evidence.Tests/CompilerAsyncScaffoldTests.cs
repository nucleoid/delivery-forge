using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using DeliveryForge.Evidence.AsyncFixture;

namespace DeliveryForge.Evidence.Tests;

// Genuine normal-SDK fixture execution and PE/PDB/source bytes. This control
// creates no producer receipt, compiler-origin issuer or quality acceptance.
public sealed class CompilerAsyncScaffoldTests
{
    [Fact]
    public async Task Genuine_ordinary_async_waits_and_has_complete_source_bound_inventory()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actual = AsyncSubject.AwaitOne(pending.Task);
        Assert.False(actual.IsCompleted);
        pending.SetResult(true);
        await actual.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(actual.IsCompletedSuccessfully);

        using var files = new InstalledToolFiles(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var testOutput = Path.GetDirectoryName(typeof(CompilerAsyncScaffoldTests).Assembly.Location)!;
        var evidence = Path.GetFullPath(Path.Combine(testOutput, "../../../.."));
        const string role = "components/evidence/DeliveryForge.Evidence.AsyncFixture/AsyncSubject.cs";
        var sourcePath = Path.Combine(evidence, "DeliveryForge.Evidence.AsyncFixture", "AsyncSubject.cs");
        var source = files.Read(sourcePath, 128 * 1024 * 1024);
        var text = new UTF8Encoding(false, true).GetString(source);
        var output = typeof(AsyncSubject).Assembly.Location;
        // Normal ProjectReference related-file copying must provide this exact
        // portable PDB beside the runtime fixture; there is no fallback/reemit.
        var pe = files.Read(output, 128 * 1024 * 1024);
        var pdb = files.Read(Path.ChangeExtension(output, ".pdb"), 128 * 1024 * 1024);
        var document = new CompilerLineCensus.Document("AsyncSubject.cs", role, sourcePath,
            InstalledToolFiles.Identity(source)[7..], text, false);
        var inventory = CompilerLineCensus.Inspect(pe, pdb, Expected(pe), [document], files);

        // Expected RED before a closed actual async-scaffold implementation.
        // Never weaken this to expect INCOMPLETE or omit unknown bodies.
        Assert.True(inventory.Complete, inventory.Reason);
        Assert.Empty(inventory.Limitations);
        var lines = text.Split('\n');
        var line = Enumerable.Range(0, lines.Length)
            .Single(index => lines[index].Contains("ASYNC_AWAIT_ONE", StringComparison.Ordinal)) + 1;
        Assert.Contains(inventory.Points, point => point.LogicalPath == "AsyncSubject.cs" && point.StartLine == line);
        Assert.All(inventory.Points, point => Assert.Equal("AsyncSubject.cs", point.LogicalPath));
        Assert.Equal(1, inventory.Points.Select(point => point.StartLine).Distinct().Count());
    }

    private static CompilerLineCensus.Build Expected(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader(); var module = reader.GetModuleDefinition();
        var entry = pe.ReadDebugDirectory().Single(row => row.Type == DebugDirectoryEntryType.CodeView);
        var cv = pe.ReadCodeViewDebugDirectoryData(entry);
        // Existing producer canonical tuple, independently serialized from the
        // actual compiled module/CodeView rather than caller-supplied tokens.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Field(string value)
        {
            var raw = Encoding.UTF8.GetBytes(value); var count = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(count, raw.Length); hash.AppendData(count); hash.AppendData(raw);
        }
        Field("sha256-canonical-v1"); Field("portable-pdb-debug-identity-v1");
        foreach (var value in new[] { cv.Guid.ToString("D"), cv.Age.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     entry.Stamp.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), entry.Stamp.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) })
        { hash.AppendData([1]); Field(value); }
        return new(Path.GetFileNameWithoutExtension(reader.GetString(module.Name)), reader.GetGuid(module.Mvid).ToString("D"),
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
