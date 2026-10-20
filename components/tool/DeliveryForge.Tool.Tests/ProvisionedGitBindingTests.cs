using System.Text;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Tool.Tests;

// Actual owned Git/file conformance only. This does not issue worker completion,
// independent review, accepted baseline or publication authority.
public sealed class ProvisionedGitBindingTests
{
    public static bool Windows => OperatingSystem.IsWindows();

    [Theory(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    [InlineData("marker")]
    [InlineData("backpointer")]
    [InlineData("common")]
    public async Task Original_metadata_redirection_refuses_before_even_the_worker_config_probe(string role)
    {
        using var fixture = await Fixture.CreateAsync();
        using var writer = fixture.Manager.AcquireWriter(fixture.Worker, "owned", "retained-writer");
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        var marker = File.ReadAllBytes(Path.Combine(fixture.Worker, ".git"));
        var path = role switch
        {
            "marker" => Path.Combine(fixture.Worker, ".git"),
            "backpointer" => Path.Combine(binding.GitDirectory, "gitdir"),
            _ => Path.Combine(binding.GitDirectory, "commondir")
        };
        var before = fixture.Manager.ActualOwnedGitInvocations;
        // Git marks the linked-worktree marker Hidden on Windows. Open that
        // existing fixture file rather than FileMode.Create, preserving its
        // attributes and actually installing the selected negative metadata.
        var changed = Encoding.UTF8.GetBytes(role == "marker" ? "gitdir: " + fixture.Foreign + "\n" : fixture.Foreign + "\n");
        using (var metadata = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            metadata.SetLength(0);
            metadata.Write(changed);
        }
        Assert.Equal(changed, File.ReadAllBytes(path));
        if (role != "marker") Assert.Equal(marker, File.ReadAllBytes(Path.Combine(fixture.Worker, ".git")));
        Assert.Equal(WriterNamespaceIdentityOutcome.Match, writer.ObserveCurrentNamespaceIdentity().Outcome);
        await Assert.ThrowsAsync<WorktreeBoundaryException>(() => fixture.Manager.ObserveAsync(fixture.Worker, TestContext.Current.CancellationToken));
        Assert.Equal(before, fixture.Manager.ActualOwnedGitInvocations);
        Assert.False(fixture.Original.HasExplicitUnknown);
        TestContext.Current.TestOutputHelper!.WriteLine($"Original {role} association refused with zero later Git births; held writer remained the original.");
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Fresh_owner_cannot_adopt_existing_valid_worker_metadata_or_writer()
    {
        using var fixture = await Fixture.CreateAsync();
        var foreign = new WorktreeManager(fixture.GitImage, fixture.Executor, fixture.Original)
            .WithParentAdmission(fixture.Parent, "owned");
        var before = foreign.ActualOwnedGitInvocations;
        var originalWriterPath = fixture.Manager.RequireOriginalBinding(fixture.Worker).WriterPath;
        await Assert.ThrowsAsync<WorktreeBoundaryException>(() => foreign.ValidateAssignedWorktreeAsync(fixture.Repository, fixture.Worker, TestContext.Current.CancellationToken));
        Assert.Throws<WorktreeBoundaryException>(() => foreign.AcquireWriter(fixture.Worker, "owned", "adopted"));
        Assert.Equal(before, foreign.ActualOwnedGitInvocations);
        Assert.False(File.Exists(originalWriterPath));
        Assert.False(fixture.Original.HasExplicitUnknown);
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Initial_native_port_manager_uses_the_provisioned_parent_for_actual_birth()
    {
        using var fixture = await Fixture.CreateAsync();
        var before = fixture.InitialManager.ActualOwnedGitInvocations;
        var admittedBefore = fixture.Parent.NativeAdmissionAttempts;
        // The initial manager has no admission field, as in the real native
        // port. The shared binding must enroll its actual task through parent.
        var observed = await fixture.InitialManager.ObserveAsync(fixture.Worker, TestContext.Current.CancellationToken);
        Assert.True(observed.IsClean);
        var actualBirths = fixture.InitialManager.ActualOwnedGitInvocations - before;
        Assert.True(actualBirths > 0);
        Assert.Equal(actualBirths, (long)(fixture.Parent.NativeAdmissionAttempts - admittedBefore));
        Assert.False(fixture.Original.HasExplicitUnknown);
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Initial_readonly_manager_refuses_writer_birth_before_opening_any_lock()
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        var effects = fixture.Parent.Inner.EnteredCount;
        Assert.Throws<WorktreeBoundaryException>(() => fixture.InitialManager.AcquireWriter(fixture.Worker, "owned", "unadmitted"));
        Assert.False(File.Exists(binding.WriterPath));
        Assert.Equal(effects, fixture.Parent.Inner.EnteredCount);
        Assert.False(fixture.Original.HasExplicitUnknown);
        using var actual = fixture.Manager.AcquireWriter(fixture.Worker, "owned", "admitted");
        Assert.True(binding.OwnsWriter(actual));
        Assert.Equal(WriterNamespaceIdentityOutcome.Match, actual.ObserveCurrentNamespaceIdentity().Outcome);
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Provisioned_writer_postrefusal_closes_actual_handle_and_keeps_durable_failure_lock()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Parent.RefuseWriterReturn = true;
        Assert.Throws<IOException>(() => fixture.Manager.AcquireWriter(fixture.Worker, "owned", "postrefused-writer"));
        var actual = Assert.IsType<WriterLease>(fixture.Parent.ActualWriter);
        Assert.True(actual.FirstCloseReturned);
        Assert.Equal(WriterNamespaceIdentityOutcome.Unknown, actual.ObserveCurrentNamespaceIdentity().Outcome);
        var path = actual.DiagnosticLockPath;
        Assert.Equal("owned\npostrefused-writer\n", File.ReadAllText(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(fixture.Original.HasExplicitUnknown);
        Assert.True(fixture.Parent.Inner.AllKnownSettled);
    }

    sealed class PostBirthParent : ICoordinatedSynchronousEffectAdmission
    {
        internal readonly ParentEffectAdmission Inner;
        internal bool RefuseWriterReturn;
        internal bool Current = true;
        internal int NativeAdmissionAttempts;
        internal WriterLease? ActualWriter;
        internal PostBirthParent() => Inner = new(new object(), run => Current && run == "owned", _ => Current = false);
        public void CheckCurrent(string runId) => Inner.CheckCurrent(runId);
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke)
        {
            NativeAdmissionAttempts++;
            return Inner.EnterAsync(runId, invoke);
        }
        public T EnterSynchronous<T>(string runId, Func<T> invoke)
        {
            var value = Inner.EnterSynchronous(runId, invoke);
            if (RefuseWriterReturn && value is WriterLease writer)
            {
                ActualWriter = writer;
                throw new IOException("Actual parent postcheck refused the returned original writer.");
            }
            return value;
        }
        public void RetainUncertain(string runId, string reason) => Inner.RetainUncertain(runId, reason);
    }

    sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "forge-provisioned-" + Guid.NewGuid().ToString("N"));
        internal string Repository => Path.Combine(Root, "repository");
        internal string Worker => Path.Combine(Root, "worker");
        internal string Foreign => Path.Combine(Root, "foreign");
        internal readonly ToolLifetime Original = new(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        internal readonly ProcessCommandExecutor Executor = new();
        internal readonly PostBirthParent Parent = new();
        internal readonly string GitImage = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, "git.exe")).First(File.Exists);
        internal WorktreeManager Manager = null!, InitialManager = null!;
        internal static async Task<Fixture> CreateAsync()
        {
            var value = new Fixture();
            try
            {
                Directory.CreateDirectory(value.Repository); Directory.CreateDirectory(value.Foreign);
                await value.Git("init", "-q", "-b", "main");
                File.WriteAllText(Path.Combine(value.Repository, "subject.txt"), "original\n");
                await value.Git("add", "--", "subject.txt");
                await value.Git("-c", "user.name=BindingFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "original");
                var head = await value.Git("rev-parse", "HEAD");
                value.InitialManager = new WorktreeManager(value.GitImage, value.Executor, value.Original);
                value.Manager = value.InitialManager.WithParentAdmission(value.Parent, "owned");
                await value.Manager.PrepareAsync(value.Repository, value.Worker, head, TestContext.Current.CancellationToken);
                var observed = await value.Manager.ObserveAsync(value.Worker, TestContext.Current.CancellationToken);
                Assert.Equal(head, observed.HeadCommit); Assert.True(observed.IsClean);
                return value;
            }
            catch { value.Dispose(); throw; }
        }
        async Task<string> Git(params string[] arguments)
        {
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(row => !((string)row.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => (string)row.Key, row => row.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = "NUL";
            environment["GIT_CONFIG_SYSTEM"] = "NUL"; environment["GIT_TERMINAL_PROMPT"] = "0";
            var capture = await Parent.EnterAsync("owned", () => Executor.ExecuteBoundedCaptureAsync(new(GitImage,
                ["-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", "-c", "commit.gpgSign=false", .. arguments], Repository, environment)
            { EnvironmentIsComplete = true }, TimeSpan.FromSeconds(15), 1048576, Original.Token, new OwnedWorkPhaseBudget(Original)));
            Assert.Equal(0, capture.Command.ExitCode); Assert.True(capture.Command.OwnedProcessQuiescent);
            Assert.True(capture.RootExitObserved); Assert.True(capture.StandardOutputDrainCompleted); Assert.True(capture.StandardErrorDrainCompleted);
            Assert.True(capture.Command.LifecycleDiagnostics?.OwnedTree is { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 });
            return Encoding.UTF8.GetString(capture.StandardOutputBytes).Trim();
        }
        public void Dispose()
        {
            // Preserve actual provisioned metadata and every failure namespace;
            // physical closure does not authorize erasing diagnostic evidence.
            TestContext.Current.TestOutputHelper!.WriteLine("Retained actual provisioned Git fixture: " + Root);
            Original.Dispose();
        }
    }
}
