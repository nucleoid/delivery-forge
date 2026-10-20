using System.Text;
using DeliveryForge.Contracts.State;

namespace DeliveryForge.Review.Tests;

// Scripted Git DATA exercises the scratch owner protocol, not production Git
// provenance, a capability receipt, or independent review acceptance.
public sealed class PatchScratchRetentionTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("verification-failure")]
    [InlineData("cleanup-denied")]
    [InlineData("cleanup-failure")]
    public async Task Exact_controlled_paths_survive_failure_and_primary_is_preserved(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-patch-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var primary = new IOException("Scripted owned Git failure.");
        var cleanupFailure = new IOException("Scripted scratch retirement failure.");
        var calls = 0; var disposed = 0; var paths = new List<string>();
        Task<GitProcessResult> Git(string gitDirectory, IReadOnlyList<string> args, CancellationToken token,
            byte[]? input, IReadOnlyDictionary<string, string>? environment)
        {
            Assert.Equal(root, gitDirectory); token.ThrowIfCancellationRequested(); calls++;
            var path = environment!["GIT_INDEX_FILE"];
            Assert.Equal(root, Path.GetDirectoryName(path));
            if (args[0] == "read-tree")
            {
                paths.Add(path); File.WriteAllText(path, "scripted index DATA");
                if (scenario == "verification-failure") throw primary;
            }
            var text = args[0] == "write-tree" ? (calls == 2 ? "base-tree" : "head-tree") : "";
            return Task.FromResult(new GitProcessResult(0, Encoding.UTF8.GetBytes(text), []));
        }
        var context = new ControlledGitEvidenceContext(root, root, root, Path.Combine(root, "unused-git"), "DATA", 1024,
            "DATA", "DATA", "DATA", ReviewAuthorityRealm.Production, [], new object(), dispose: () => disposed++,
            ownedGit: Git, mayCleanOwnedFiles: () => scenario != "cleanup-denied",
            deleteOwnedScratch: path => { if (scenario == "cleanup-failure") throw cleanupFailure; if (File.Exists(path)) File.Delete(path); });
        var task = GitPatchIdentity.VerifyPatchReconstructsBothDirectionsAsync(context, [], "base", "head", "base-tree", "head-tree", TestContext.Current.CancellationToken);
        if (scenario == "success")
        {
            await task; Assert.Empty(context.RetainedScratchPaths); context.Dispose(); Assert.Equal(1, disposed);
            Assert.All(paths, path => Assert.False(File.Exists(path)));
        }
        else
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => task);
            if (scenario == "verification-failure") Assert.Same(primary, error);
            if (scenario == "cleanup-failure") Assert.Same(cleanupFailure, error);
            Assert.Equal(4, context.RetainedScratchPaths.Count);
            Assert.All(context.RetainedScratchPaths, path => Assert.Equal(root, Path.GetDirectoryName(path)));
            Assert.NotNull(error.Data["RetainedProductionReviewScratch"]);
            context.Dispose(); Assert.Equal(0, disposed); Assert.All(paths, path => Assert.True(File.Exists(path)));
        }
    }
}
