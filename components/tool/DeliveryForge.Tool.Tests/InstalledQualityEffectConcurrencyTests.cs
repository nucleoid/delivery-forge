using System.Text;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Tool.Tests;

// Actual parent admission and file reads/writes with a controlled blocking
// synchronous callback. No live lease, tool outcome or baseline is fabricated.
public sealed class InstalledQualityEffectConcurrencyTests
{
    [Fact]
    public void Cancellation_after_read_before_first_close_is_not_reported_as_success()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-close-cancel-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "completed bytes");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), cancelled.Token);
        var files = new InstalledToolFiles(life, TimeSpan.FromSeconds(20), new InstalledToolHostScope.Credits());
        var frame = new InstalledToolFiles.FileFrame(path, false)
        {
            AcquireEntered = true, AcquireReturned = true,
            Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        };
        Assert.NotEqual(-1, frame.Stream.ReadByte());
        cancelled.Cancel();
        files.FirstClose(frame);
        Assert.True(frame.CloseReturned);
        Assert.ThrowsAny<Exception>(() => files.Require(frame));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Fact]
    public void Revocation_after_acquisition_still_closes_the_original_input_handle()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Observes actual Windows file-sharing ownership; Linux conformance remains separate.");
        var path = Path.Combine(Path.GetTempPath(), "forge-acquired-input-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "same original input");
        var current = true; var observedOpenHandle = false;
        var admission = new ParentEffectAdmission(new object(), _ =>
        {
            if (!current) return false;
            try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException)
            {
                // The actual reader's FileShare.Read denies this write-open.
                // Revoke only after acquisition has positively happened.
                observedOpenHandle = true; current = false;
            }
            return current;
        }, _ => current = false);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var files = new InstalledToolFiles(life, TimeSpan.FromSeconds(20), new InstalledToolHostScope.Credits(), admission, "owned");
        Assert.ThrowsAny<Exception>(() => files.Read(path, 128));
        Assert.True(observedOpenHandle);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.False(life.HasRetainedUnknown);
    }

    [Fact(Timeout = 15000)]
    public async Task Revocation_does_not_wait_for_file_finalization_and_prevents_its_next_write()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-file-effect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "known.txt");
        var forbidden = Path.Combine(root, "must-not-be-written.txt");
        File.WriteAllText(input, "known complete input");
        var gate = new object(); var current = true;
        var admission = new ParentEffectAdmission(gate, run => current && run == "owned", _ => current = false);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var files = new InstalledToolFiles(life, TimeSpan.FromSeconds(20), new InstalledToolHostScope.Credits(), admission, "owned");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var effect = Task.Factory.StartNew(() => files.Effect(() =>
        {
            Assert.Equal("known complete input", Encoding.UTF8.GetString(files.Read(input, 128)));
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken)) throw new TimeoutException("Test callback release did not arrive.");
            files.WriteNew(forbidden, "forbidden"u8.ToArray());
            return true;
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? revocation = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            revocation = Task.Factory.StartNew(() => { lock (gate) current = false; },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.Same(revocation, await Task.WhenAny(revocation, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)));
            Assert.False(admission.AllKnownSettled);
        }
        finally
        {
            release.Set();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await effect.WaitAsync(cleanup.Token); } catch (CoordinationUncertainException) { }
            if (revocation is not null) await revocation.WaitAsync(cleanup.Token);
        }
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => effect);
        Assert.False(File.Exists(forbidden));
        Assert.Equal("known complete input", File.ReadAllText(input));
    }
}
