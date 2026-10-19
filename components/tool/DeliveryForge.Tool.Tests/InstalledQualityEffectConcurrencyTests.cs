using System.Text;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Tool.Tests;

// Actual parent admission and file reads/writes with a controlled blocking
// synchronous callback. No live lease, tool outcome or baseline is fabricated.
public sealed class InstalledQualityEffectConcurrencyTests
{
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
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Test callback release did not arrive.");
            files.WriteNew(forbidden, "forbidden"u8.ToArray());
            return true;
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? revocation = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            revocation = Task.Factory.StartNew(() => { lock (gate) current = false; },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.Same(revocation, await Task.WhenAny(revocation, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)));
            Assert.False(admission.AllKnownSettled);
        }
        finally
        {
            release.Set();
            try { await effect.WaitAsync(TimeSpan.FromSeconds(5)); } catch (CoordinationUncertainException) { }
            if (revocation is not null) await revocation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => effect);
        Assert.False(File.Exists(forbidden));
        Assert.Equal("known complete input", File.ReadAllText(input));
    }
}
