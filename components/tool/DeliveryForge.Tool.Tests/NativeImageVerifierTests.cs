using System.Security.Cryptography;
using System.IO.MemoryMappedFiles;

namespace DeliveryForge.Tool.Tests;

// Image-byte checks only. No file here is launched as a host or issues capability.
public sealed class NativeImageVerifierTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "forge-native-image-" + Guid.NewGuid().ToString("N"));
    string Image => Path.Combine(root, "inert-image.data");
    public NativeImageVerifierTests() { Directory.CreateDirectory(root); File.WriteAllText(Image, "inert image bytes\n"); }
    bool retainImage;
    AdmissionFile Pin()
    {
        // Fixture setup is also cancellable; this is not a production image pin issuer.
        var token = TestContext.Current.CancellationToken;
        token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(Image);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return new(Image, stream.Length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    [Fact(Timeout = 60000)]
    public void Complete_image_above_old_array_bound_is_streamed_and_last_byte_drift_is_refused()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        void Stage(string value) => TestContext.Current.TestOutputHelper!.WriteLine(
            $"Actual image stage={value}; elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(started)}; root={root}");
        Stage("started");
        try
        {
            using (var extent = new FileStream(Image, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                extent.SetLength(256L * 1024 * 1024 + 1);
                extent.Position = extent.Length - 1; extent.WriteByte(41);
            }
            Stage("extent-created");
            var pin = Pin();
            Stage("fixture-complete-hash-returned");
            using var life = new ToolLifetime(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            BoundedFiles.VerifyNativeImage(pin, life, TestContext.Current.CancellationToken);
            Stage("production-positive-returned");
            using (var changed = new FileStream(Image, FileMode.Open, FileAccess.Write, FileShare.None))
            { changed.Position = changed.Length - 1; changed.WriteByte(42); }
            Assert.Throws<IOException>(() => BoundedFiles.VerifyNativeImage(pin, life, TestContext.Current.CancellationToken));
            Stage("production-last-byte-refusal-returned");
            Assert.False(life.HasRetainedUnknown);
            // A failed complete hash must still close the actual original file handle.
            using var exclusive = new FileStream(Image, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Stage("exclusive-first-close-proof-returned");
        }
        catch
        {
            retainImage = true; Stage("failed-retained"); throw;
        }
    }

    [Theory]
    [InlineData("grow")]
    [InlineData("shrink")]
    [InlineData("same-length-change")]
    public void Genuine_positive_precedes_changed_extent_or_content_refusal(string change)
    {
        var pin = Pin();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        BoundedFiles.VerifyNativeImage(pin, life, TestContext.Current.CancellationToken);
        using (var altered = new FileStream(Image, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (change == "grow") altered.SetLength(altered.Length + 1);
            else if (change == "shrink") altered.SetLength(altered.Length - 1);
            else altered.WriteByte(0);
        }
        Assert.Throws<IOException>(() => BoundedFiles.VerifyNativeImage(pin, life, TestContext.Current.CancellationToken));
        Assert.False(life.HasRetainedUnknown);
        using var exclusive = new FileStream(Image, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Retained_Windows_image_keeps_exact_identity_and_denies_mutation_until_original_first_close()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows image-sharing semantics; this case does not certify Linux.");
        var pin = Pin();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var held = BoundedFiles.RetainVerifiedNativeImage(pin, life, TestContext.Current.CancellationToken);
        try
        {
            for (var observation = 0; observation < 6; observation++) held.Check(TestContext.Current.CancellationToken);
            Assert.Throws<IOException>(() => { using var writer = new FileStream(Image, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); writer.WriteByte(42); });
            Assert.Throws<IOException>(() => File.Delete(Image));
            Assert.Throws<IOException>(() => File.Move(Image, Image + ".moved"));
            Assert.Equal("inert image bytes\n", File.ReadAllText(Image));
            Assert.False(held.Unknown);
        }
        finally { held.Close(false); }
        Assert.Throws<IOException>(() => held.Check(TestContext.Current.CancellationToken));
        using var exclusive = new FileStream(Image, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Retained_Windows_image_refuses_preexisting_writer_and_writable_view_even_after_originating_handles_close()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows image-sharing semantics; this case does not certify Linux.");
        var pin = Pin();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var writer = new FileStream(Image, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Throws<IOException>(() => BoundedFiles.RetainVerifiedNativeImage(pin, life, TestContext.Current.CancellationToken));
        // That refused acquisition entered its real constructor and therefore
        // remains UNKNOWN; do not reuse its original as a fresh allowance.
        using var mapLife = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var mapping = MemoryMappedFile.CreateFromFile(writer, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        var fileHandle = writer.SafeFileHandle;
        var mapHandle = mapping.SafeMemoryMappedFileHandle;
        writer.Dispose(); mapping.Dispose();
        // Managed owner state only: released raw handle values are recyclable
        // and cannot be queried as independent original kernel-close evidence.
        Assert.True(fileHandle.IsClosed); Assert.True(mapHandle.IsClosed);
        Assert.False(view.SafeMemoryMappedViewHandle.IsClosed);
        Assert.Throws<IOException>(() => BoundedFiles.RetainVerifiedNativeImage(pin, mapLife, TestContext.Current.CancellationToken));
        // Prove that the surviving view really remains writable, rather than
        // accepting a disposed mapping as the negative control.
        view.Write(0, (byte)41); view.Flush();
        using var sharedReader = new FileStream(Image, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Equal(41, sharedReader.ReadByte());
    }

    [Fact]
    public void Cancelled_reviewer_phase_refuses_image_acquisition_without_closing_original_work()
    {
        var pin = Pin();
        using var exclusive = new FileStream(Image, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var phase = new CancellationTokenSource(); phase.Cancel();
        Assert.Throws<OperationCanceledException>(() => BoundedFiles.VerifyNativeImage(pin, life, phase.Token));
        life.Check(); Assert.False(life.HasRetainedUnknown);
    }

    [Fact]
    public void Overlimit_pin_refuses_before_opening_even_a_locked_image()
    {
        var pin = Pin() with { Bytes = BoundedFiles.MaximumNativeImageBytes + 1 };
        using var exclusive = new FileStream(Image, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Throws<ToolUsageException>(() => BoundedFiles.VerifyNativeImage(pin, life, TestContext.Current.CancellationToken));
        Assert.False(life.HasRetainedUnknown);
    }

    [Fact]
    public void Redirected_image_refuses_after_the_plain_target_positive()
    {
        var pin = Pin();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        BoundedFiles.VerifyNativeImage(pin, life, TestContext.Current.CancellationToken);
        var link = Path.Combine(root, "redirected-image.data");
        File.CreateSymbolicLink(link, Image);
        Assert.Throws<IOException>(() => BoundedFiles.VerifyNativeImage(pin with { Path = link }, life, TestContext.Current.CancellationToken));
        Assert.Equal("inert image bytes\n", File.ReadAllText(Image));
        Assert.False(life.HasRetainedUnknown);
    }

    public void Dispose() { if (!retainImage) Directory.Delete(root, recursive: true); }
}
