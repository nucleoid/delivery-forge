using System.Diagnostics;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Execution.Tests.Repositories;

public sealed class OriginalWriterBirthTests
{
    [Fact]
    public void Missing_original_provisioning_refuses_before_writer_birth()
    {
        using var directory = new TemporaryDirectory(); Directory.CreateDirectory(Path.Combine(directory.Path, ".git"));
        // Synthetic current metadata must not manufacture original provisioning
        // authority or a writer; the actual post-birth control uses PrepareAsync.
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "git.exe" : "git")).First(File.Exists);
        var original = new Original(); var parent = new Parent();
        var manager = new WorktreeManager(git, new ProcessCommandExecutor(), original).WithParentAdmission(parent, "owned");
        Assert.Throws<WorktreeBoundaryException>(() => manager.AcquireWriter(directory.Path, "owned", "must-not-be-born"));
        Assert.Null(parent.ActualWriter);
        Assert.False(File.Exists(Path.Combine(directory.Path, ".git", "delivery-forge-writer.lock")));
        Assert.Equal(0, original.CleanupChecks); Assert.Equal(0, manager.ActualOwnedGitInvocations);
    }
    sealed class Original : IOwnedDuplexBudget
    {
        internal int CleanupChecks;
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(1200);
        public TimeSpan WorkLimit => TimeSpan.FromSeconds(1100);
        public TimeSpan CleanupRemaining { get { CleanupChecks++; return TimeSpan.FromSeconds(60); } }
        public CancellationToken Token => CancellationToken.None;
        public void CheckWork() { }
    }
    sealed class Parent : ICoordinatedSynchronousEffectAdmission
    {
        internal WriterLease? ActualWriter;
        public void CheckCurrent(string runId) { }
        public T EnterSynchronous<T>(string runId, Func<T> invoke) { var value = invoke(); ActualWriter = Assert.IsType<WriterLease>(value); throw new IOException("Admission postcheck refused after actual delegate return."); }
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) => throw new InvalidOperationException("This file-owner control must not invoke native Git.");
        public void RetainUncertain(string runId, string reason) => throw new IOException(reason);
    }
}
