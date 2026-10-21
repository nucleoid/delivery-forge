using System.Formats.Tar;
using System.Text;
using DeliveryForge.Contracts.State;
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
    static ControlledGitEvidenceContext View(Action? cleanup, Action? work, Action<bool>? checkDisposition = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "inert-view-contract-never-created");
        return new(root, root, root, Path.Combine(root, "not-an-executable"), "DATA", 1024,
            "test-source", new('a', 40), "test-view", ReviewAuthorityRealm.Test, [], new object(),
            dispose: cleanup, disposeOwnedWork: work, checkDisposition: checkDisposition);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Known_preclaim_refusal_keeps_unentered_callback_available_for_later_disposition(bool cleanupFirst)
    {
        var work = 0; var cleanup = 0; var refused = true;
        var failure = new ReviewPreflightException("Known pre-entry refusal; no callback has entered.");
        var view = View(() => cleanup++, () => work++, cleanupMode =>
        {
            Assert.Equal(cleanupFirst, cleanupMode);
            if (refused) throw failure;
        });
        Action retire = cleanupFirst ? view.Dispose : view.DisposeDuringWork;
        Assert.Same(failure, Assert.Throws<ReviewPreflightException>(retire));
        Assert.Equal(0, work); Assert.Equal(0, cleanup); Assert.False(view.DispositionReturned);
        refused = false; retire(); retire();
        Assert.Equal(cleanupFirst ? 1 : 0, cleanup); Assert.Equal(cleanupFirst ? 0 : 1, work);
        Assert.True(view.DispositionReturned);
    }
    [Fact]
    public void Known_WORK_preclaim_refusal_allows_cleanup_without_reentering_WORK_callback()
    {
        var work = 0; var cleanup = 0;
        var view = View(() => cleanup++, () => work++, cleanupMode =>
        {
            if (!cleanupMode) throw new ReviewPreflightException("Actual WORK preflight refused.");
        });
        Assert.Throws<ReviewPreflightException>(view.DisposeDuringWork);
        view.Dispose();
        Assert.Equal(0, work); Assert.Equal(1, cleanup); Assert.True(view.DispositionReturned);
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
        var work = 0; var cleanup = 0; var checks = 0;
        var failure = new IOException("Actual callback did not return known retirement.");
        var view = View(() => cleanup++, () => { work++; throw failure; }, _ => checks++);
        Assert.Same(failure, Assert.Throws<IOException>(view.DisposeDuringWork));
        Assert.Throws<ReviewPreflightException>(view.DisposeDuringWork); view.Dispose();
        Assert.Equal(1, work); Assert.Equal(0, cleanup); Assert.Equal(1, checks); Assert.False(view.DispositionReturned);
    }
    [Fact(Timeout = 15000)]
    public async Task Claimed_cleanup_excludes_concurrent_WORK_and_any_cleanup_replay()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var token = TestContext.Current.CancellationToken;
        var work = 0; var cleanup = 0;
        var view = View(() =>
        {
            Interlocked.Increment(ref cleanup); entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5), token)) throw new TimeoutException("Managed cleanup latch not released.");
        }, () => Interlocked.Increment(ref work));
        var actualCleanup = Task.Run(view.Dispose, token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), token));
            Assert.Throws<ReviewPreflightException>(view.DisposeDuringWork); view.Dispose();
            Assert.Equal(1, cleanup); Assert.Equal(0, work); Assert.False(view.DispositionReturned);
        }
        finally { release.Set(); await actualCleanup.WaitAsync(TimeSpan.FromSeconds(5), token); }
        view.Dispose(); Assert.Throws<ReviewPreflightException>(view.DisposeDuringWork);
        Assert.Equal(1, cleanup); Assert.Equal(0, work); Assert.True(view.DispositionReturned);
    }
    [Fact(Timeout = 15000)]
    public async Task Entered_WORK_excludes_cleanup_then_known_WORK_return_stays_idempotent()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var token = TestContext.Current.CancellationToken;
        var work = 0; var cleanup = 0;
        var view = View(() => Interlocked.Increment(ref cleanup), () =>
        {
            Interlocked.Increment(ref work); entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5), token)) throw new TimeoutException("Managed WORK latch not released.");
        });
        var actualWork = Task.Run(view.DisposeDuringWork, token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), token)); view.Dispose();
            Assert.Equal(1, work); Assert.Equal(0, cleanup); Assert.False(view.DispositionReturned);
        }
        finally { release.Set(); await actualWork.WaitAsync(TimeSpan.FromSeconds(5), token); }
        view.DisposeDuringWork(); view.Dispose(); Assert.Equal(1, work); Assert.Equal(0, cleanup);
        Assert.True(view.DispositionReturned);
    }
    [Fact]
    public void Thrown_cleanup_is_not_replayed_or_replaced_by_later_WORK()
    {
        var work = 0; var cleanup = 0; var failure = new IOException("Actual cleanup callback did not return.");
        var view = View(() => { cleanup++; throw failure; }, () => work++);
        Assert.Same(failure, Assert.Throws<IOException>(view.Dispose)); view.Dispose();
        Assert.Throws<ReviewPreflightException>(view.DisposeDuringWork);
        Assert.Equal(1, cleanup); Assert.Equal(0, work); Assert.False(view.DispositionReturned);
        var absent = View(null, null); absent.Dispose(); Assert.False(absent.DispositionReturned);
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
        if (scratch) view.Dispose();
        Assert.Equal(0, work); Assert.Equal(0, cleanup); Assert.False(view.DispositionReturned);
    }
}
