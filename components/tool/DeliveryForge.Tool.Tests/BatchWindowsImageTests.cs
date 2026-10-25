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
        using var errors = new StringWriter(); f.Original.BindAttention(new(f.Original, errors));
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
        Assert.Empty(errors.ToString()); Assert.False(f.Original.HasRetainedUnknown);
        Assert.Throws<TimeoutException>(f.Original.Check); // SAME cleanup began; no renewed WORK.
        f.Original.BeginFailureCleanup(); var marker = errors.ToString();
        Assert.StartsWith("DFCLI_FAILURE|", marker);
        f.Original.BeginFailureCleanup(); Assert.Equal(marker, errors.ToString());
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
    static (InstalledHostRootAcceptance Host, TrustedConfiguration Config, AdmissionFile Root, AdmissionFile ConfigPin) Declared(Frame f, AdmissionFile worker, AdmissionFile reviewer)
    {
        // Cost/refusal DATA only, matching the existing DeclaredTrust seam.
        // Never read as an accepted root or construct a host/session/reviewer.
        var hash = new string('a', 64);
        AdmissionFile Pin(string leaf) => new(Path.Combine(f.Root, leaf), 100, hash);
        var host = new InstalledHostRootAcceptance("RootActualInstalledHostConformanceAcceptance-v1", "windows", "codex.cli", "observed",
            new(worker.Path, worker.Bytes, worker.Sha256, "observed"), Pin("tool.dll"), [Pin("artifact.json")], [],
            new("Issue17OwnerTrustedPersonalHost-v1", Pin("owner.json"), [new(Path.Combine(f.Root, "absent1"), null, null), new(Path.Combine(f.Root, "absent2"), null, null)]));
        var review = new InstalledReviewSelection("policy.json", "evidence.json", "fixture", Pin("git.exe"), "observed", Path.Combine(f.Root, "review"),
            IndependentReviewer: new(reviewer, "observed"));
        return (host, new TrustedConfiguration("fixture/repo", Path.Combine(f.Root, "repo"), "main", Path.Combine(f.Root, "git.exe"), InstalledReview: review), Pin("root.json"), Pin("config.json"));
    }

    [Fact(Timeout = 30000)]
    public void Real_read_costs_match_two_node_pending_images_at_the_original_credit_edges()
    {
        using var f = new Frame(); var worker = f.File("worker.data"); var reviewer = f.File("reviewer.data");
        var data = Declared(f, worker, reviewer);
        var complete = InstalledHostAdmission.RuntimeTrust.ReadCapacity(data.Host, data.Root, data.ConfigPin, data.Config);
        var images = InstalledHostAdmission.RuntimeTrust.ImageReadCapacity(data.Host, data.Config);
        Assert.Equal(6, images.Operations); Assert.Equal(2 * (worker.Bytes + 1) + reviewer.Bytes + 1, images.Bytes);
        var nonImageOps = complete.Operations - images.Operations; var nonImageBytes = complete.Bytes - images.Bytes;
        Assert.Equal(500, complete.Operations); Assert.Equal(494, nonImageOps); Assert.Equal(482797L, nonImageBytes);
        f.Images.Plan(worker, f.Original); f.Images.Plan(reviewer, f.Original);
        f.Images.SealAndRequireCapacity(f.Original, 2 * nonImageOps, 2 * nonImageBytes);
        var priorOps = 4096 - nonImageOps - 4;
        Assert.True(priorOps > 0 && priorOps % 2 == 0);
        for (var operation = 0; operation < priorOps; operation += 2) _ = BoundedFiles.Read(worker.Path, f.Original);
        f.Images.RequireNodeCapacity(f.Original, worker, reviewer, complete, images); // node1 exact operation edge, both images pending
        f.Images.Verify(worker, f.Original); f.Images.Verify(reviewer, f.Original);
        var first = f.Images.Borrow(worker, f.Original, f.Original.Token); first.Close(false);
        f.Images.RequireNodeCapacity(f.Original, worker, reviewer, complete, images); // node2: both actual hashes remain owned, zero pending reads
        var second = f.Images.Borrow(worker, f.Original, f.Original.Token); second.Close(false);
        var actualBytes = (priorOps / 2) * (worker.Bytes + 1) + worker.Bytes + reviewer.Bytes + 2;
        BoundedFiles.RequireReadCapacity(f.Original, nonImageOps, Cap - actualBytes);
        Assert.Throws<ToolUsageException>(() => BoundedFiles.RequireReadCapacity(f.Original, nonImageOps, Cap - actualBytes + 1));
        _ = BoundedFiles.Read(worker.Path, f.Original);
        Assert.Throws<ToolUsageException>(() => f.Images.RequireNodeCapacity(f.Original, worker, reviewer, complete, images));
        f.Close();

        // Exact byte-cap plan is metadata admission only: never open these declared large extents.
        using var edge = new Frame(); var edgeWorker = edge.File("worker.data") with { Bytes = BoundedFiles.MaximumNativeImageBytes };
        var edgeReviewer = edge.File("reviewer.data") with { Bytes = Cap - 2 * nonImageBytes - edgeWorker.Bytes - 2 };
        var edgeData = Declared(edge, edgeWorker, edgeReviewer);
        var edgeComplete = InstalledHostAdmission.RuntimeTrust.ReadCapacity(edgeData.Host, edgeData.Root, edgeData.ConfigPin, edgeData.Config);
        var edgeImages = InstalledHostAdmission.RuntimeTrust.ImageReadCapacity(edgeData.Host, edgeData.Config);
        Assert.Equal(nonImageBytes, edgeComplete.Bytes - edgeImages.Bytes);
        edge.Images.Plan(edgeWorker, edge.Original); edge.Images.Plan(edgeReviewer, edge.Original);
        edge.Images.SealAndRequireCapacity(edge.Original, 2 * nonImageOps, 2 * nonImageBytes);
        edge.Images.RequireNodeCapacity(edge.Original, edgeWorker, edgeReviewer, edgeComplete, edgeImages);
        BoundedFiles.RequireReadCapacity(edge.Original, 4096, Cap); // admission did not consume/refund real credit
        using var over = new Frame(); var overWorker = over.File("worker.data") with { Bytes = edgeWorker.Bytes };
        var overReviewer = over.File("reviewer.data") with { Bytes = edgeReviewer.Bytes + 1 };
        var overData = Declared(over, overWorker, overReviewer);
        var overComplete = InstalledHostAdmission.RuntimeTrust.ReadCapacity(overData.Host, overData.Root, overData.ConfigPin, overData.Config);
        var overImages = InstalledHostAdmission.RuntimeTrust.ImageReadCapacity(overData.Host, overData.Config);
        over.Images.Plan(overWorker, over.Original); over.Images.Plan(overReviewer, over.Original);
        Assert.Throws<ToolUsageException>(() => over.Images.SealAndRequireCapacity(over.Original,
            2 * (overComplete.Operations - overImages.Operations), 2 * (overComplete.Bytes - overImages.Bytes)));
    }

    [Fact(Timeout = 30000)]
    public void Owner_release_failure_preserves_the_original_cleanup_admission_failure()
    {
        using var f = new Frame(); var pin = f.File("host.data");
        f.Images.Plan(pin, f.Original); f.Images.SealAndRequireCapacity(f.Original, 0, 0);
        var first = f.Images.Borrow(pin, f.Original, f.Original.Token);
        var second = f.Images.Borrow(pin, f.Original, f.Original.Token);
        first.RetainUnknown(); f.Original.BeginFailureCleanup();
        var failure = Assert.Throws<AggregateException>(() => second.Close(false));
        Assert.Collection(failure.InnerExceptions, error => Assert.IsType<TimeoutException>(error), error => Assert.IsType<IOException>(error));
        Assert.False(second.IsReleased); Assert.True(f.Original.HasRetainedUnknown);
        f.Close(); NoWriter(pin);
    }
}
