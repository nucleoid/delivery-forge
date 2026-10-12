using System.Diagnostics;
using System.Text;
using RealDotNet.Subject;
using Xunit;

namespace RealDotNet.Subject.Tests;

public sealed class CalculatorTests
{
    [Fact]
    public async Task Adds_two_numbers()
    {
        var directory = Environment.GetEnvironmentVariable("DELIVERY_FORGE_FIXTURE_BARRIER_DIRECTORY");
        if (!string.IsNullOrEmpty(directory))
        {
            var nonce = Environment.GetEnvironmentVariable("DELIVERY_FORGE_FIXTURE_BARRIER_NONCE")
                ?? throw new InvalidOperationException("Enabled fixture barrier requires its fresh nonce.");
            if (!Path.IsPathFullyQualified(directory) || !Guid.TryParseExact(nonce, "N", out _))
                throw new InvalidOperationException("Fixture barrier binding is invalid.");
            for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Fixture barrier path is redirected.");
            var entered = Path.Combine(directory, "entered-" + nonce);
            await using (var stream = new FileStream(entered, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(Encoding.UTF8.GetBytes(nonce), TestContext.Current.CancellationToken);
            var release = Path.Combine(directory, "release-" + nonce);
            var started = Stopwatch.GetTimestamp();
            while (!File.Exists(release))
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(180))
                    throw new TimeoutException("Real compiled fixture workload exceeded its180-second self limit.");
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            if ((File.GetAttributes(release) & FileAttributes.ReparsePoint) != 0 ||
                await File.ReadAllTextAsync(release, TestContext.Current.CancellationToken) != nonce)
                throw new IOException("Fixture release does not match this compiled test's nonce.");
        }
        Assert.Equal(4, Calculator.Add(1, 3));
    }
}
