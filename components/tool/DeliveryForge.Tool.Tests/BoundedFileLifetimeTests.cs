using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;
// Genuine filesystem effects only; no fake native/group knownness. All new
// definitions remain unexecuted until the real containing resource admission.
public sealed class BoundedFileLifetimeTests
{
    [Fact]
    public void Genuine_complete_write_read_then_existing_output_refuses_before_construction()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-files-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "actual.bin"); using var life = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var bytes = new byte[] { 0, 1, 2, 255 }; BoundedFiles.WriteNew(path, bytes, life); Assert.Equal(bytes, BoundedFiles.Read(path, life));
        var refusal = Assert.Throws<IOException>(() => BoundedFiles.WriteNew(path, new byte[] { 9 }, life)); Assert.Contains("no construction entered", refusal.Message, StringComparison.Ordinal); life.CheckWork(); Assert.Equal(bytes, BoundedFiles.Read(path, life)); File.Delete(path); Directory.Delete(directory);
    }
    [Fact]
    public void Genuine_source_read_then_quota_refusal_closes_file_under_same_original_without_refund()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-file-quota-" + Guid.NewGuid().ToString("N")); File.WriteAllBytes(path, [1, 2, 3, 4]); using var life = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken); var original = life.OriginalStarted;
        try
        {
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, BoundedFiles.Read(path, life));
            var refusal = Assert.Throws<IOException>(() => BoundedFiles.Read(path, life, 3)); Assert.Contains("bounded file profile", refusal.Message, StringComparison.Ordinal); Assert.Equal(original, life.OriginalStarted); Assert.Throws<TimeoutException>(life.CheckWork);
            // Exclusive open succeeds only after the actual first FileStream
            // disposition returned; this does not issue a process/group proof.
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.Equal(4, exclusive.Length);
        }
        finally { File.Delete(path); }
    }
}
