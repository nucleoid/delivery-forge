using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;

namespace DeliveryForge.Planning.Tests;

[Collection(RichPlanImportFixtureCollection.Name)]
public sealed class RichPlanImportTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("ready-roundtrip")]
    [InlineData("coherent-gate-tamper")]
    [InlineData("same-tree-base-advance")]
    [InlineData("raw-index-flags")]
    [InlineData("same-objects-other-root")]
    [InlineData("required-context-unavailable")]
    [InlineData("contract-projection-only")]
    [InlineData("precancelled")]
    [InlineData("no-original-budget")]
    public async Task Rich_import_reconciles_real_freezer_and_current_git_without_trusting_saved_ready(string scenario)
    {
        await using var repository = new ImportRepositoryFixture(TestContext.Current.CancellationToken);
        try
        {
            await repository.InitializeAsync();
            var token = repository.WorkToken;
            var reader = new GitRepositoryContextReader(repository.Git);
            var context = await repository.Wait(reader.ReadAsync(repository.Root, "refs/heads/main", token));
            var file = await repository.Wait(reader.ReadFileAsync(context, "tracked.txt", token));
            var now = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
            EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, now, [], EvidenceRequirement.Required)];
            var request = new PlanningRequest("fixture/import", "#4", "implement", "Import complete portable planning material",
                ["tracked.txt"], ["production"], ["Current owner revalidates exact material and source"], "implement");
            var draft = new PlanDraft(request, context, evidence, [new("tracked.txt", "content", "update")],
                [new("base", [], "current source"), new("dependent", ["base"], "base accepted")],
                [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
                new("additive", "none", "none", "none", "none", "none", "receipt", "revert", ["inspect diagnostics"]),
                [], IntakePlanner.Assess(request, evidence));
            var original = PlanFreezer.Freeze(draft, "original-import", now);
            Assert.True(original.DownstreamReady);
            Assert.True(original.ReconcileBase(context).DownstreamReady);
            Assert.Equal(original.Identity, CanonicalJson.ComputeIdentity(original.CanonicalBytes));
            Assert.DoesNotContain(repository.Root, Encoding.UTF8.GetString(original.CanonicalBytes), StringComparison.Ordinal);
            var source = new PlanImportSource(repository.Root, "refs/heads/main", repository.Git);
            var expectations = new PlanImportExpectations(original.Identity, original.ContractIdentity, request.Repository,
                request.WorkItem, repository.Root, "refs/heads/main", request.RequestedCeiling);
            var bytes = original.CanonicalBytes;
            if (scenario == "coherent-gate-tamper")
            {
                var altered = PlanFreezer.Freeze(draft with
                { Gates = [new("test", new("dotnet", [PlanCommandArgument.Literal("test"), PlanCommandArgument.Literal("--no-build")]), "PASS")] },
                    "changed-import", now);
                Assert.True(altered.DownstreamReady);
                Assert.Equal(altered.Identity, CanonicalJson.ComputeIdentity(altered.CanonicalBytes));
                Assert.NotEqual(original.Identity, altered.Identity); Assert.NotEqual(original.ContentDigest, altered.ContentDigest);
                bytes = altered.CanonicalBytes; // Coherent owner-produced data, denied against ORIGINAL independent expectations.
            }
            if (scenario == "same-tree-base-advance")
            {
                await repository.Command(repository.Root, "commit", "--allow-empty", "-q", "-m", "same tree new base");
                var advanced = await repository.Wait(reader.ReadAsync(repository.Root, "refs/heads/main", token));
                Assert.NotEqual(context.Commit, advanced.Commit); Assert.Equal(context.Tree, advanced.Tree);
                Assert.False(original.ReconcileBase(advanced).DownstreamReady);
            }
            if (scenario == "raw-index-flags")
            {
                var index = Path.Combine(repository.Root, ".git", "index");
                var before = SHA256.HashData(await File.ReadAllBytesAsync(index, token));
                await repository.Command(repository.Root, "update-index", "--assume-unchanged", "tracked.txt");
                var after = SHA256.HashData(await File.ReadAllBytesAsync(index, token));
                Assert.False(before.SequenceEqual(after));
                Assert.StartsWith("h ", await repository.Command(repository.Root, "ls-files", "-v", "--", "tracked.txt"), StringComparison.Ordinal);
                var current = await repository.Wait(reader.ReadAsync(repository.Root, "refs/heads/main", token));
                Assert.Equal(context.Commit, current.Commit); Assert.Equal(context.Tree, current.Tree); Assert.False(current.Dirty);
                Assert.Equal(file.Bytes, (await repository.Wait(reader.ReadFileAsync(current, "tracked.txt", token))).Bytes);
            }
            if (scenario == "same-objects-other-root")
            {
                var second = await repository.Clone();
                var other = await repository.Wait(reader.ReadAsync(second, "refs/heads/main", token));
                Assert.Equal(context.Commit, other.Commit); Assert.Equal(context.Tree, other.Tree);
                Assert.False(original.ReconcileBase(other).DownstreamReady);
                source = source with { RepositoryRoot = second }; // Trusted expectations still independently select original root.
            }
            if (scenario == "required-context-unavailable")
            {
                const string privateSummary = "Private fixture caller relationship is not exported";
                var imported = new ImportedContextEnvelope("1.0.0",
                    [new("memory", "/private/import-fixture", privateSummary, null, now,
                    Requirement: EvidenceRequirement.Required, DistilledMeaning: ImportedMeaningCode.CallerRelationship)], [], []);
                var intake = IntakePlanner.Assess(request, evidence, imported, EvidenceRequirement.Required);
                Assert.True(intake.Ready); Assert.True(intake.ImportedContextAvailable);
                original = PlanFreezer.Freeze(draft with { Intake = intake }, "required-import", now);
                Assert.True(original.DownstreamReady);
                using var document = JsonDocument.Parse(original.CanonicalBytes);
                Assert.True(document.RootElement.GetProperty("plan").GetProperty("intake").GetProperty("importedContextAvailable").GetBoolean());
                Assert.DoesNotContain(privateSummary, Encoding.UTF8.GetString(original.CanonicalBytes), StringComparison.Ordinal);
                expectations = expectations with { BundleIdentity = original.Identity, ContractIdentity = original.ContractIdentity };
                bytes = original.CanonicalBytes; // Fresh required private context is deliberately NOT provided to importer.
            }
            if (scenario == "contract-projection-only") bytes = original.PlanContractBytes;
            var budget = repository.WorkRemaining();
            if (scenario == "precancelled")
            {
                using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token); cancelled.Cancel();
                source = source with { TrustedGitExecutable = Path.Combine(repository.Root, "never-start-nonexistent-git") };
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlanImporter.ImportAndReconcileAsync(bytes, source, expectations,
                    budget, cancellationToken: cancelled.Token));
                return;
            }
            if (scenario == "no-original-budget")
            {
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PlanImporter.ImportAndReconcileAsync(bytes, source, expectations,
                    TimeSpan.Zero, cancellationToken: token));
                return;
            }
            if (scenario is "coherent-gate-tamper" or "contract-projection-only")
            {
                var error = await Assert.ThrowsAsync<PlanningException>(() => PlanImporter.ImportAndReconcileAsync(bytes, source, expectations,
                    budget, cancellationToken: token));
                Assert.Contains(scenario == "contract-projection-only" ? "bundle" : "identity", error.Message, StringComparison.OrdinalIgnoreCase);
                return;
            }
            var result = await PlanImporter.ImportAndReconcileAsync(bytes, source, expectations, budget, cancellationToken: token);
            if (scenario != "ready-roundtrip")
            {
                Assert.False(result.DownstreamReady); Assert.Null(result.Plan); Assert.NotEmpty(result.Diagnostics);
                var requiredDiagnostic = scenario switch
                {
                    "same-tree-base-advance" => "base",
                    "raw-index-flags" => "index",
                    "same-objects-other-root" => "root",
                    "required-context-unavailable" => "required",
                    _ => throw new InvalidOperationException("Unknown import specification.")
                };
                Assert.Contains(result.Diagnostics, value => value.Contains(requiredDiagnostic, StringComparison.OrdinalIgnoreCase));
                return;
            }
            Assert.True(result.DownstreamReady); var actual = Assert.IsType<FrozenPlan>(result.Plan);
            Assert.Equal(original.CanonicalBytes, actual.CanonicalBytes); Assert.Equal(original.PlanContractBytes, actual.PlanContractBytes);
            Assert.Equal(original.Identity, actual.Identity); Assert.Equal(original.ContractIdentity, actual.ContractIdentity);
            Assert.Equal(original.ContentDigest, actual.ContentDigest); Assert.Equal(original.PlanRevision, actual.PlanRevision);
            Assert.True(actual.ReconcileBase(await repository.Wait(reader.ReadAsync(repository.Root, "refs/heads/main", token))).DownstreamReady);
        }
        catch (Exception failure) { repository.Primary = failure; throw; }
    }
}

// Real public Git prerequisites only. Original tasks/processes remain rooted on unknown cleanup.
internal sealed class ImportRepositoryFixture : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<ImportRepositoryFixture, byte> Retained = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource _work;
    private readonly List<Task> _tasks = [];
    private readonly List<CommandOwner> _commands = [];
    private TimeSpan? _cleanupStarted;
    internal ImportRepositoryFixture(CancellationToken token)
    {
        _work = CancellationTokenSource.CreateLinkedTokenSource(token);
        _work.CancelAfter(WorkRemaining());
        Root = Path.Combine(Path.GetTempPath(), "delivery-forge-import-" + Guid.NewGuid().ToString("N"));
        Git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "git.exe" : "git")).First(File.Exists);
        Retained.TryAdd(this, 0);
    }
    internal string Root { get; }
    internal string Git { get; }
    internal CancellationToken WorkToken => _work.Token;
    internal Exception? Primary { get; set; }
    internal TimeSpan WorkRemaining()
    {
        WorkToken.ThrowIfCancellationRequested();
        var left = TimeSpan.FromSeconds(110) - Stopwatch.GetElapsedTime(_started);
        if (left <= TimeSpan.Zero) throw new TimeoutException("Original import fixture work allowance expired.");
        return left;
    }
    internal TimeSpan CleanupRemaining()
    {
        var elapsed = Stopwatch.GetElapsedTime(_started);
        var end = TimeSpan.FromSeconds(120);
        if (_cleanupStarted is { } begun && begun + TimeSpan.FromSeconds(10) < end) end = begun + TimeSpan.FromSeconds(10);
        var left = end - elapsed;
        if (left <= TimeSpan.Zero) throw new TimeoutException("Original import fixture cleanup allowance expired.");
        return left;
    }
    internal async Task InitializeAsync()
    {
        _ = WorkRemaining();
        if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Fresh fixture root collision.");
        Directory.CreateDirectory(Root);
        await Wait(File.WriteAllTextAsync(Path.Combine(Root, "tracked.txt"), "authoritative committed input\n", WorkToken));
        await Command(Root, "init", "-q", "-b", "main");
        await Command(Root, "add", "--", "tracked.txt");
        await Command(Root, "commit", "-q", "-m", "actual import baseline");
    }
    internal async Task<string> Clone()
    {
        var second = Root + "-clone";
        _ = WorkRemaining();
        if (Directory.Exists(second) || File.Exists(second)) throw new IOException("Fresh clone collision.");
        await Command(Root, "clone", "--local", "--no-hardlinks", "-q", Root, second);
        return second;
    }
    private void Track(Task task)
    {
        _tasks.Add(task);
        _ = task.ContinueWith(fault => _ = fault.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    internal async Task<T> Wait<T>(Task<T> task)
    {
        Track(task);
        var value = await task.WaitAsync(WorkRemaining(), WorkToken);
        _ = WorkRemaining(); return value;
    }
    private async Task Wait(Task task)
    {
        Track(task); await task.WaitAsync(WorkRemaining(), WorkToken); _ = WorkRemaining();
    }
    internal async Task<T> ObserveCleanup<T>(Func<Task<T>> operation)
    {
        _cleanupStarted ??= Stopwatch.GetElapsedTime(_started);
        _ = CleanupRemaining(); var actual = operation(); Track(actual);
        var value = await actual.WaitAsync(CleanupRemaining()); _ = CleanupRemaining(); return value;
    }
    internal async Task<string> Command(string cwd, params string[] arguments)
    {
        _ = WorkRemaining();
        var info = new ProcessStartInfo(Git)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        info.Environment["GIT_CONFIG_SYSTEM"] = info.Environment["GIT_CONFIG_GLOBAL"];
        info.Environment["GIT_GRAFT_FILE"] = info.Environment["GIT_CONFIG_GLOBAL"];
        info.Environment["GIT_NO_REPLACE_OBJECTS"] = "1"; info.Environment["GIT_NO_LAZY_FETCH"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in new[] { "--no-replace-objects", "-c", "core.hooksPath=" + info.Environment["GIT_CONFIG_GLOBAL"],
            "-c", "core.fsmonitor=false", "-c", "user.name=ImportFixture", "-c", "user.email=fixture@example.invalid" }.Concat(arguments))
            info.ArgumentList.Add(argument);
        var owner = new CommandOwner(new Process { StartInfo = info });
        _commands.Add(owner); // Register original object BEFORE actual Start/await.
        _ = WorkRemaining();
        if (!owner.Process.Start()) throw new IOException("Original fixture Git did not start.");
        owner.Started = true; owner.Process.StandardInput.Close();
        owner.Output = owner.Process.StandardOutput.ReadToEndAsync(); Track(owner.Output);
        owner.Error = owner.Process.StandardError.ReadToEndAsync(); Track(owner.Error);
        owner.Exit = owner.Process.WaitForExitAsync(CancellationToken.None); Track(owner.Exit);
        await owner.Exit.WaitAsync(WorkRemaining(), WorkToken);
        await Task.WhenAll(owner.Output, owner.Error).WaitAsync(WorkRemaining(), WorkToken);
        _ = WorkRemaining();
        var output = await owner.Output; var error = await owner.Error;
        if (owner.Process.ExitCode != 0) throw new IOException("Actual fixture Git failed: " + error);
        if (output.Length > 256 * 1024 || error.Length > 256 * 1024) throw new IOException("Fixture Git output exceeds selected small-source profile.");
        owner.Closed = true; owner.Process.Dispose();
        return output.TrimEnd('\r', '\n');
    }
    private sealed class CommandOwner(Process process)
    {
        internal Process Process { get; } = process;
        internal bool Started, Closed;
        internal Task? Exit;
        internal Task<string>? Output, Error;
    }
    public async ValueTask DisposeAsync()
    {
        _cleanupStarted ??= Stopwatch.GetElapsedTime(_started);
        var failures = new List<Exception>();
        foreach (var owner in _commands.Where(command => !command.Closed))
        {
            if (!owner.Started)
            {
                await Attempt(() => { _ = CleanupRemaining(); owner.Process.Dispose(); owner.Closed = true; return Task.CompletedTask; }, failures);
                continue;
            }
            await Attempt(() =>
            {
                _ = CleanupRemaining(); if (!owner.Process.HasExited) owner.Process.Kill(entireProcessTree: false);
                return Task.CompletedTask;
            }, failures); // ORIGINAL created Git object, never imported PID.
            await Attempt(async () =>
            {
                _ = CleanupRemaining();
                if (owner.Exit is null) { owner.Exit = owner.Process.WaitForExitAsync(CancellationToken.None); Track(owner.Exit); }
                await owner.Exit.WaitAsync(CleanupRemaining()); _ = CleanupRemaining();
            }, failures);
            if (owner.Output is not null) await Attempt(async () => { await owner.Output.WaitAsync(CleanupRemaining()); _ = CleanupRemaining(); }, failures);
            if (owner.Error is not null) await Attempt(async () => { await owner.Error.WaitAsync(CleanupRemaining()); _ = CleanupRemaining(); }, failures);
            await Attempt(() =>
            {
                _ = CleanupRemaining();
                if (!owner.Process.HasExited || owner.Exit?.IsCompletedSuccessfully != true ||
                    owner.Output is { IsCompletedSuccessfully: false } || owner.Error is { IsCompletedSuccessfully: false })
                    throw new IOException("Original Git/pipe settlement remains unknown.");
                owner.Closed = true; owner.Process.Dispose(); return Task.CompletedTask;
            }, failures);
        }
        foreach (var task in _tasks.Where(task => !task.IsCompleted))
        {
            try
            {
                await Task.WhenAny(task, Task.Delay(CleanupRemaining(), CancellationToken.None)); _ = CleanupRemaining();
                if (!task.IsCompleted) throw new TimeoutException("Actual source/reader task remains unknown.");
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        // Files are retained, not recursively deleted. No timeout/task completion is native authority.
        if (failures.Count == 0 && _commands.All(command => command.Closed) && _tasks.All(task => task.IsCompleted))
            Retained.TryRemove(this, out _);
        _work.Dispose();
        if (failures.Count > 0) throw new AggregateException("Original import fixture failure and independent cleanup retained.",
            Primary is null ? failures : new[] { Primary }.Concat(failures));
    }
    private static async Task Attempt(Func<Task> stage, List<Exception> failures)
    { try { await stage(); } catch (Exception failure) { failures.Add(failure); } }
}
