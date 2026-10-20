using System.Security.Cryptography;

namespace DeliveryForge.Tool.Tests;

// Image-byte checks only. No file here is launched as a host or issues capability.
public sealed class NativeImageVerifierTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "forge-native-image-" + Guid.NewGuid().ToString("N"));
    string Image => Path.Combine(root, "inert-image.data");
    public NativeImageVerifierTests() { Directory.CreateDirectory(root); File.WriteAllText(Image, "inert image bytes\n"); }
    AdmissionFile Pin()
    {
        using var stream = File.OpenRead(Image);
        return new(Image, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    [Fact(Timeout = 60000)]
    public void Complete_image_above_old_array_bound_is_streamed_and_last_byte_drift_is_refused()
    {
        using (var extent = new FileStream(Image, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            extent.SetLength(256L * 1024 * 1024 + 1);
            extent.Position = extent.Length - 1; extent.WriteByte(41);
        }
        var pin = Pin();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        BoundedFiles.VerifyNativeImage(pin, life);
        using (var changed = new FileStream(Image, FileMode.Open, FileAccess.Write, FileShare.None))
        { changed.Position = changed.Length - 1; changed.WriteByte(42); }
        Assert.Throws<IOException>(() => BoundedFiles.VerifyNativeImage(pin, life));
        Assert.False(life.HasRetainedUnknown);
        // A failed complete hash must still close the actual original file handle.
        using var exclusive = new FileStream(Image, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("grow")]
    [InlineData("shrink")]
    [InlineData("same-length-change")]
    public void Genuine_positive_precedes_changed_extent_or_content_refusal(string change)
    {
        var pin = Pin();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        BoundedFiles.VerifyNativeImage(pin, life);
        using (var altered = new FileStream(Image, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (change == "grow") altered.SetLength(altered.Length + 1);
            else if (change == "shrink") altered.SetLength(altered.Length - 1);
            else altered.WriteByte(0);
        }
        Assert.Throws<IOException>(() => BoundedFiles.VerifyNativeImage(pin, life));
        Assert.False(life.HasRetainedUnknown);
        using var exclusive = new FileStream(Image, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
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
        Assert.Throws<ToolUsageException>(() => BoundedFiles.VerifyNativeImage(pin, life));
        Assert.False(life.HasRetainedUnknown);
    }

    [Fact]
    public void Redirected_image_refuses_after_the_plain_target_positive()
    {
        var pin = Pin();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        BoundedFiles.VerifyNativeImage(pin, life);
        var link = Path.Combine(root, "redirected-image.data");
        File.CreateSymbolicLink(link, Image);
        Assert.Throws<IOException>(() => BoundedFiles.VerifyNativeImage(pin with { Path = link }, life));
        Assert.Equal("inert image bytes\n", File.ReadAllText(Image));
        Assert.False(life.HasRetainedUnknown);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
