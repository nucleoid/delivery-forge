using System.Formats.Tar;
using System.Text;
using DeliveryForge.Execution.Host;
using DeliveryForge.Review;

namespace DeliveryForge.Execution.Tests;

// Strict byte-parser controls only. These construct no Git/native result,
// original source owner, capability, accepted execution or production receipt.
public sealed class OwnedSourceArchiveTests
{
    const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static readonly byte[] Contents = Encoding.UTF8.GetBytes("complete raw source\n$Format:%H$\n");
    static Dictionary<string, OwnedSourceTreeEntry> Tree() => new(StringComparer.Ordinal)
    { ["subject.txt"] = new("100644", new('b', 40)) };
    static byte[] Archive(string mode)
    {
        using var bytes = new MemoryStream();
        using (var writer = new TarWriter(bytes, leaveOpen: true))
        {
            if (mode != "missing-global")
                writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string>
                { [mode == "unknown-global" ? "unknown" : "comment"] = mode == "foreign-head" ? new('c', 40) : Head }));
            var name = mode == "traversal" ? "../subject.txt" : "subject.txt";
            if (mode == "hardlink") writer.WriteEntry(new UstarTarEntry(TarEntryType.HardLink, name) { LinkName = "foreign" });
            else
            {
                for (var index = 0; index < (mode == "duplicate" ? 2 : 1); index++)
                {
                    using var data = new MemoryStream(Contents, false);
                    TarEntry entry = mode switch
                    {
                        "unknown-pax" => new PaxTarEntry(TarEntryType.RegularFile, name, new Dictionary<string, string> { ["unknown"] = "value" }),
                        "gnu" => new GnuTarEntry(TarEntryType.RegularFile, name),
                        "v7" => new V7TarEntry(TarEntryType.V7RegularFile, name),
                        _ => new UstarTarEntry(TarEntryType.RegularFile, name)
                    };
                    entry.Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                    entry.DataStream = data; writer.WriteEntry(entry);
                }
            }
        }
        return bytes.ToArray();
    }
    [Fact]
    public void Complete_archive_keeps_exact_raw_bytes_and_mode_without_issuing_source_authority()
    {
        var checks = 0;
        var result = ProductionReviewSourceOwner.ParseSourceArchive(Archive("valid"), Tree(), Head, 1048576, () => checks++);
        Assert.Equal(Contents, Assert.Single(result).Value.Bytes);
        Assert.Equal("100644", result["subject.txt"].Mode); Assert.True(checks > 0);
    }
    [Theory]
    [InlineData("missing-global")]
    [InlineData("foreign-head")]
    [InlineData("unknown-global")]
    [InlineData("unknown-pax")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("hardlink")]
    [InlineData("gnu")]
    [InlineData("v7")]
    public void Incomplete_foreign_or_ambiguous_archive_cannot_supply_expected_source(string mode) =>
        Assert.Throws<ReviewPreflightException>(() => ProductionReviewSourceOwner.ParseSourceArchive(Archive(mode), Tree(), Head, 1048576, () => { }));
}

// Private callback-barrier contract only; Test realm cannot issue production
// review/source authority. The linked real-Git control separately owns its view.
public sealed class OwnedViewRetirementTests
{
    static ControlledGitEvidenceContext View(Action cleanup, Action? work)
    {
        var root = Path.Combine(Path.GetTempPath(), "inert-view-contract-never-created");
        return new(root, root, root, Path.Combine(root, "not-an-executable"), "DATA", 1024,
            "test-source", new('a', 40), "test-view", ReviewAuthorityRealm.Test, [], new object(),
            dispose: cleanup, disposeOwnedWork: work);
    }
    [Fact]
    public void Explicit_work_retirement_invokes_exact_callback_once_without_later_cleanup()
    {
        var work = 0; var cleanup = 0;
        var view = View(() => cleanup++, () => work++);
        view.DisposeDuringWork(); view.DisposeDuringWork(); view.Dispose();
        Assert.Equal(1, work); Assert.Equal(0, cleanup);
    }
    [Fact]
    public void Thrown_work_retirement_is_not_retried_by_explicit_or_ordinary_dispose()
    {
        var work = 0; var cleanup = 0; var failure = new IOException("Actual callback did not return known retirement.");
        var view = View(() => cleanup++, () => { work++; throw failure; });
        Assert.Same(failure, Assert.Throws<IOException>(view.DisposeDuringWork));
        Assert.Throws<ReviewPreflightException>(view.DisposeDuringWork); view.Dispose();
        Assert.Equal(1, work); Assert.Equal(0, cleanup);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_callback_or_retained_scratch_refuses_without_entering_any_retirement(bool scratch)
    {
        var work = 0; var cleanup = 0;
        var view = View(() => cleanup++, scratch ? () => work++ : null);
        if (scratch) view.RegisterScratch("inert-owned-retained-scratch");
        Assert.Throws<ReviewPreflightException>(view.DisposeDuringWork);
        Assert.Equal(0, work); Assert.Equal(0, cleanup);
    }
}
