using System.Text.Json;
using DeliveryForge.Execution.Git;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

[Collection(LocalSingleFixtureCollection.Name)]
public sealed class WriterNamespaceCleanupRegressionTests
{
    [Theory(Timeout = 120_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Archive_and_destruction_require_the_current_namespace_to_match_the_held_writer(bool replace)
    {
        await using var artifacts = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        await using var runtime = await OwnedEphemeralRuntime.CreateAsync(artifacts, TestContext.Current.CancellationToken);
        var context = await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", TestContext.Current.CancellationToken);
        await runtime.Worktrees.PrepareAsync(runtime.Repository, runtime.Worktree, context.Commit, TestContext.Current.CancellationToken);
        var token = Guid.NewGuid().ToString("N");
        string lockPath;
        byte[] originalBytes;
        using (var writer = runtime.Worktrees.AcquireWriter(runtime.Worktree, runtime.RuntimeId, token))
        {
            runtime.RecordWriterLease(writer);
            lockPath = writer.DiagnosticLockPath;
            originalBytes = writer.GetDiagnosticBytes().ToArray();
        }
        var sentinel = Path.Combine(runtime.Worktree, "subject.txt");
        var sentinelBytes = await File.ReadAllBytesAsync(sentinel, TestContext.Current.CancellationToken);
        WriterLease? replacement = null;
        var entered = false;
        runtime.FixtureArchiveEntryBarrier = async cancellationToken =>
        {
            entered = true;
            if (replace)
            {
                File.Delete(lockPath);
                replacement = runtime.Worktrees.AcquireWriter(runtime.Worktree, runtime.RuntimeId, token);
                Assert.Equal(originalBytes, replacement.GetDiagnosticBytes().ToArray());
            }
            await artifacts.WriteNewJsonAsync("writer-namespace-replacement.json", new
            {
                fixture = true,
                productionCertified = false,
                entered,
                replace,
                lockPath,
                sameRunTokenAndBytes = replace,
                integratedBaseCommit = artifacts.IntegratedSource.Commit,
                implementationHead = artifacts.IntegratedSource.HeadCommit,
                limitation = "Actual live lease/pathname replacement; no arbitrary-filesystem atomicity or production claim."
            }, cancellationToken);
        };
        try
        {
            var removed = await runtime.TryCleanupAsync(TestContext.Current.CancellationToken);
            Assert.True(entered);
            if (!replace)
            {
                Assert.True(removed);
                Assert.False(Directory.Exists(runtime.Root));
            }
            else
            {
                Assert.False(removed);
                var relativeLock = Path.GetRelativePath(runtime.Root, lockPath);
                Assert.All(Directory.EnumerateDirectories(artifacts.Root, "runtime-archive-*"), archive =>
                    Assert.False(File.Exists(Path.Combine(archive, relativeLock)), "Old held bytes were archived under the replaced current pathname."));
                foreach (var commandPath in Directory.EnumerateFiles(artifacts.Root, "runtime-command-*.json"))
                {
                    using var command = JsonDocument.Parse(await File.ReadAllBytesAsync(commandPath, TestContext.Current.CancellationToken));
                    var arguments = command.RootElement.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
                    Assert.False(arguments.Length >= 2 && arguments[0] == "worktree" && arguments[1] == "remove", "Destructive Git removal was attempted after namespace replacement.");
                }
                Assert.True(Directory.Exists(runtime.Root));
                Assert.Equal(originalBytes, replacement!.GetDiagnosticBytes().ToArray());
                Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(sentinel, TestContext.Current.CancellationToken));
            }
            artifacts.MarkAssertionsPassed();
        }
        finally { runtime.FixtureArchiveEntryBarrier = null; replacement?.Dispose(); }
    }
}
