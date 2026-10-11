using System.Collections;
using System.Security.Cryptography;
using DeliveryForge.Publication.GitHost;

namespace DeliveryForge.Publication.Tests;

public sealed class NativeCancellationTests
{
    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_native_start_has_zero_owned_marker_side_effects(bool afterArgumentPreparation)
    {
        var owner = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "delivery-forge-publication-cancel", Guid.NewGuid().ToString("N")));
        var marker = Path.Combine(owner, "started");
        Directory.CreateDirectory(owner);
        using var cancellation = new CancellationTokenSource();
        var executable = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec")! : "/bin/sh";
        var configuration = new GitHubReadConfiguration(executable,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))), owner, "2.80.0",
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        IReadOnlyList<string> arguments = OperatingSystem.IsWindows()
            ? new[] { "/d", "/c", "echo started > \"" + marker + "\"" }
            : new[] { "-c", "printf started > \"$1\"", "publication-owned-marker", marker };
        if (afterArgumentPreparation) arguments = new CancelAfterArguments(arguments, cancellation);
        else cancellation.Cancel();
        try
        {
            var observed = await Record.ExceptionAsync(() => new ProcessGitHubCliExecutor(configuration)
                .ExecuteAsync(arguments, cancellation.Token));
            Assert.False(File.Exists(marker), "A cancelled invocation started its owned marker process before rejection.");
            Assert.IsAssignableFrom<OperationCanceledException>(observed);
        }
        finally
        {
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "delivery-forge-publication-cancel"));
            var relative = Path.GetRelativePath(parent, owner);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
                throw new InvalidOperationException("Owned fixture cleanup escaped its generated root.");
            if (Directory.Exists(owner)) Directory.Delete(owner, recursive: true);
        }
    }

    private sealed class CancelAfterArguments(IReadOnlyList<string> values, CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        public int Count => values.Count;
        public string this[int index] => values[index];
        public IEnumerator<string> GetEnumerator()
        {
            foreach (var value in values) yield return value;
            cancellation.Cancel();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
