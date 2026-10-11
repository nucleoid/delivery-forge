using DeliveryForge.Evidence;
using DeliveryForge.Planning;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

public sealed class IntegratedBaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delivery-forge-ancestry-" + Guid.NewGuid().ToString("N"));
    private readonly string _git = FindGit();
    private bool _cleanupSafe = true;

    [Fact]
    public async Task Open_branch_is_not_integrated_until_exact_commit_is_ancestor_of_base()
    {
        await Initialize();
        var reader = new GitRepositoryContextReader(_git);
        var before = await reader.ReadAsync(_root, "refs/heads/main", TestContext.Current.CancellationToken);
        await Git("checkout", "-q", "-b", "topic");
        var candidate = await Commit("candidate");
        await Git("checkout", "-q", "main");
        Assert.False(await reader.IsAncestorAsync(before, candidate, TestContext.Current.CancellationToken));
        await Git("merge", "--ff-only", "topic");
        var after = await reader.ReadAsync(_root, "refs/heads/main", TestContext.Current.CancellationToken);
        Assert.True(await reader.IsAncestorAsync(after, candidate, TestContext.Current.CancellationToken));
        Assert.False(await reader.IsAncestorAsync(before, candidate, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_refs_and_legacy_grafts_cannot_manufacture_integration(bool legacyGraft)
    {
        await Initialize();
        var original = await Git("rev-parse", "HEAD");
        await Git("checkout", "-q", "-b", "topic");
        var candidate = await Commit("candidate");
        await Git("checkout", "-q", "main");
        if (legacyGraft) await File.WriteAllTextAsync(Path.Combine(_root, ".git", "info", "grafts"), $"{original} {candidate}\n", TestContext.Current.CancellationToken);
        else await Git("replace", "--graft", original, candidate);
        var reader = new GitRepositoryContextReader(_git);
        var context = await reader.ReadAsync(_root, "refs/heads/main", TestContext.Current.CancellationToken);
        Assert.False(await reader.IsAncestorAsync(context, candidate, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("topic")]
    [InlineData("--all")]
    [InlineData("aaaaaaaa")]
    public async Task Only_exact_commit_object_ids_are_accepted(string candidate)
    {
        await Initialize();
        var reader = new GitRepositoryContextReader(_git);
        var context = await reader.ReadAsync(_root, "refs/heads/main", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PlanningException>(() => reader.IsAncestorAsync(context, candidate, TestContext.Current.CancellationToken));
    }

    private async Task Initialize()
    {
        Directory.CreateDirectory(_root);
        await Git("init", "-q", "-b", "main");
        await Commit("base");
    }

    private async Task<string> Commit(string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked.txt"), content, TestContext.Current.CancellationToken);
        await Git("add", "tracked.txt");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", content);
        return await Git("rev-parse", "HEAD");
    }

    private async Task<string> Git(params string[] arguments)
    {
        var result = await new ProcessCommandExecutor().ExecuteAsync(new(_git, arguments, _root), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (!result.OwnedProcessQuiescent) _cleanupSafe = false;
        Assert.True(result.OwnedProcessQuiescent && !result.TimedOut && !result.Cancelled && result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private static string FindGit()
    {
        var filename = OperatingSystem.IsWindows() ? "git.exe" : "git";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory, filename)).First(File.Exists);
    }

    public void Dispose()
    {
        if (!_cleanupSafe || !Directory.Exists(_root)) return;
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(_root, "*", enumeration))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(_root, recursive: true);
    }
}
