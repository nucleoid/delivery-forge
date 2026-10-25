using System.Security.Cryptography;

namespace DeliveryForge.Tool.Tests;

// Actual inert-file handles and credits only: never host/session/review authority.
public sealed class BatchWindowsImageTests
{
    sealed class Frame : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "forge-batch-image-" + Guid.NewGuid().ToString("N"));
        internal readonly ToolLifetime Original;
        internal readonly BatchWindowsImages Images;
        bool closed;
        internal Frame()
        {
            if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows deny-write/delete image ownership only.");
            Directory.CreateDirectory(Root); Original = new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); Images = new(Original);
        }
        internal AdmissionFile File(string name)
        {
            var path = Path.Combine(Root, name); var bytes = "inert shared image bytes\n"u8.ToArray();
            System.IO.File.WriteAllBytes(path, bytes);
            return new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        internal void Close() { closed = true; Images.CloseAfterOwners(); }
        public void Dispose()
        {
            try { if (!closed) Close(); }
            finally
            {
                Original.Dispose();
                // Unknown ownership intentionally retains its inert evidence/handle.
                if (!Original.HasRetainedUnknown) Directory.Delete(Root, true);
            }
        }
    }
    static void NoWriter(AdmissionFile pin) => Assert.Throws<IOException>(() =>
    {
        using var writer = new FileStream(pin.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    });
    const long Cap = 1073741824;

    [Fact(Timeout = 30000)]
    public void One_actual_handle_spans_two_node_uses_without_reread_or_early_physical_close()
    {
        using var f = new Frame(); var pin = f.File("host.data");
        f.Images.Plan(pin, f.Original); f.Images.Plan(pin, f.Original);
        f.Images.SealAndRequireCapacity(f.Original, 0, 0); f.Images.Verify(pin, f.Original);
        var first = f.Images.Borrow(pin, f.Original, f.Original.Token); first.Check(TestContext.Current.CancellationToken); first.Close(false);
        Assert.True(first.IsReleased); Assert.False(first.Unknown); NoWriter(pin);
        BoundedFiles.RequireReadCapacity(f.Original, 4094, Cap - pin.Bytes - 1);
        var next = f.Images.Borrow(pin, f.Original, f.Original.Token); next.Check(TestContext.Current.CancellationToken); NoWriter(pin); next.Close(false);
        // A second complete read or two file credits would make this exact bound fail.
        BoundedFiles.RequireReadCapacity(f.Original, 4094, Cap - pin.Bytes - 1);
        Assert.Throws<IOException>(() => first.Check(TestContext.Current.CancellationToken));
        f.Close(); using var exclusive = new FileStream(pin.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact(Timeout = 30000)]
    public void Distinct_physical_paths_with_identical_bytes_are_each_hashed_and_charged()
    {
        using var f = new Frame(); var a = f.File("a.data"); var b = f.File("b.data");
        Assert.Equal(a.Sha256, b.Sha256); f.Images.Plan(a, f.Original); f.Images.Plan(b, f.Original);
        f.Images.SealAndRequireCapacity(f.Original, 0, 0); f.Images.Verify(a, f.Original); f.Images.Verify(b, f.Original);
        BoundedFiles.RequireReadCapacity(f.Original, 4092, Cap - a.Bytes - b.Bytes - 2);
        Assert.Throws<ToolUsageException>(() => BoundedFiles.RequireReadCapacity(f.Original, 4093, 0));
        NoWriter(a); NoWriter(b); f.Close();
        using var first = new FileStream(a.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var second = new FileStream(b.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory(Timeout = 30000)]
    [InlineData("foreign-original")]
    [InlineData("changed-pin")]
    [InlineData("unselected-path")]
    [InlineData("sealed-plan")]
    public void A_borrow_cannot_change_the_original_or_frozen_selection(string change)
    {
        using var f = new Frame(); var pin = f.File("host.data"); var other = f.File("other.data");
        f.Images.Plan(pin, f.Original); f.Images.SealAndRequireCapacity(f.Original, 0, 0); f.Images.Verify(pin, f.Original);
        using var foreign = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Throws<ToolUsageException>(() =>
        {
            if (change == "sealed-plan") f.Images.Plan(other, f.Original);
            else _ = f.Images.Borrow(change == "changed-pin" ? pin with { Sha256 = new string('a', 64) } : change == "unselected-path" ? other : pin,
                change == "foreign-original" ? foreign : f.Original, f.Original.Token);
        });
        BoundedFiles.RequireReadCapacity(f.Original, 4094, Cap - pin.Bytes - 1); NoWriter(pin); f.Close();
    }

    [Fact(Timeout = 30000)]
    public void Cancelled_first_acquisition_cannot_retry_or_consume_a_second_allowance()
    {
        using var f = new Frame(); var pin = f.File("host.data");
        f.Images.Plan(pin, f.Original); f.Images.SealAndRequireCapacity(f.Original, 0, 0);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => f.Images.Borrow(pin, f.Original, cancel.Token));
        Assert.Throws<IOException>(() => f.Images.Verify(pin, f.Original));
        BoundedFiles.RequireReadCapacity(f.Original, 4096, Cap);
        using var exclusive = new FileStream(pin.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact(Timeout = 30000)]
    public void Uncertain_use_stops_next_borrow_and_retains_the_original_physical_handle()
    {
        using var f = new Frame(); var pin = f.File("host.data");
        f.Images.Plan(pin, f.Original); f.Images.SealAndRequireCapacity(f.Original, 0, 0);
        var use = f.Images.Borrow(pin, f.Original, f.Original.Token); use.RetainUnknown();
        Assert.True(use.Unknown); Assert.False(use.IsReleased); Assert.True(f.Original.HasExplicitUnknown);
        Assert.Throws<IOException>(() => f.Images.Borrow(pin, f.Original, f.Original.Token));
        f.Close(); NoWriter(pin); Assert.True(f.Original.HasExplicitUnknown);
    }
}
