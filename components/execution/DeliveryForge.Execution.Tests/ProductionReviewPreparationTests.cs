using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Contracts.State;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Execution.Host;
using DeliveryForge.Review;

namespace DeliveryForge.Execution.Tests;

// These controls prove refusal before dispatch. Their inert image is deliberately
// not executable and is never a production/native conformance receipt.
public sealed class ProductionReviewPreparationTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "forge-review-admission-test-" + Guid.NewGuid().ToString("N"));
    readonly RefusingParent parent = new();
    bool quiet = true;
    string Image => Path.Combine(root, "inert-image.data");
    public ProductionReviewPreparationTests()
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Image, "inert owned source admission control");
    }
    ProductionReviewSourceOwner Owner() => new(new ProcessCommandExecutor(), new Original(), parent, "run-control", root,
        Path.Combine(root, ".git"), Path.Combine(root, ".git"), Image, new FileInfo(Image).Length,
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Image))), "git version control", new string('a', 40), new string('b', 40), () => quiet,
        "sha256:" + new string('a', 64), "sha256:" + new string('b', 64), ".forge/review-policy.json", 1024);

    [Fact]
    public void Saved_public_data_cannot_construct_the_opaque_owner()
    {
        Assert.Empty(typeof(ProductionReviewSourceOwner).GetConstructors());
        quiet = false;
        Assert.Throws<ReviewPreflightException>(() => Owner());
        Assert.Equal(0, parent.Entries);
    }

    [Fact]
    public async Task Original_native_quiescence_is_rechecked_before_any_Git_dispatch()
    {
        var owner = Owner(); quiet = false;
        await Assert.ThrowsAsync<ReviewPreflightException>(() => owner.GitAsync(Path.Combine(root, ".git"), ["--version"], CancellationToken.None));
        Assert.Equal(0, parent.Entries);
    }

    [Fact]
    public async Task Changed_trusted_image_refuses_before_parent_admission()
    {
        var owner = Owner(); File.WriteAllText(Image, "changed image");
        await Assert.ThrowsAsync<ReviewPreflightException>(() => owner.GitAsync(Path.Combine(root, ".git"), ["--version"], CancellationToken.None));
        Assert.Equal(0, parent.Entries);
    }

    [Fact]
    public async Task Metadata_redirect_refuses_even_when_native_worker_is_quiet()
    {
        var owner = Owner(); Directory.Delete(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere\n");
        await Assert.ThrowsAsync<ReviewPreflightException>(() => owner.GitAsync(Path.Combine(root, ".git"), ["--version"], CancellationToken.None));
        Assert.Equal(0, parent.Entries);
    }

    [Fact]
    public async Task Foreign_object_view_and_non_allowlisted_patch_input_never_dispatch()
    {
        var owner = Owner(); var controlled = Path.Combine(root, "controlled"); owner.Begin(controlled); owner.MaximumBytes = 1024;
        await Assert.ThrowsAsync<ReviewPreflightException>(() => owner.GitAsync(Path.Combine(root, "foreign"), ["--version"], CancellationToken.None));
        await Assert.ThrowsAsync<ReviewPreflightException>(() => owner.GitAsync(controlled, ["apply", "--cached", "--unsafe-paths", "-"], CancellationToken.None, [1, 2, 3]));
        Assert.False(Directory.Exists(controlled));
        Assert.Equal(0, parent.Entries);
    }

    [Fact]
    public void Original_source_preparation_cannot_replay_with_a_second_private_view()
    {
        var owner = Owner(); owner.Begin(Path.Combine(root, "first"));
        Assert.Throws<ReviewPreflightException>(() => owner.Begin(Path.Combine(root, "second")));
        Assert.Equal(0, parent.Entries);
    }

    [Fact]
    public async Task Parent_revocation_at_file_birth_refuses_read_and_never_creates_write_target()
    {
        var owner = Owner(); parent.AllowAsync = true; parent.DenySynchronous = true;
        var read = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReadFileAsync(Image, 1024, CancellationToken.None));
        Assert.Equal("File birth admission revoked.", read.Message);
        var target = Path.Combine(root, "must-not-exist");
        var write = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.WriteFileAsync(target, [1, 2, 3], CancellationToken.None));
        Assert.Equal("File birth admission revoked.", write.Message);
        Assert.False(File.Exists(target)); Assert.False(owner.Uncertain);
    }

    public static bool WindowsCreationOwnedProfile => OperatingSystem.IsWindows();

    [Fact(Timeout = 120_000, SkipUnless = nameof(WindowsCreationOwnedProfile), Skip = "Linux production source acceptance requires its selected creation-owned scope profile.")]
    public async Task Actual_Windows_Git_source_builder_uses_protected_base_and_owned_capture_without_reviewer_acceptance()
    {
        // Private test issuance exercises the builder and real native Git only.
        // It is not a completed real worker or independent reviewer observation.
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); bound.CancelAfter(TimeSpan.FromSeconds(90));
        var original = new Original(); var executor = new ProcessCommandExecutor();
        var fixture = await ReviewGitPreparationBoundaryTests.Fixture.CreateAsync(null, bound.Token);
        async Task<string> Git(params string[] arguments)
        {
            var nul = "NUL";
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(row => !((string)row.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => (string)row.Key, row => row.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = nul; environment["GIT_CONFIG_SYSTEM"] = nul;
            environment["GIT_NO_REPLACE_OBJECTS"] = "1"; environment["GIT_NO_LAZY_FETCH"] = "1"; environment["GIT_TERMINAL_PROMPT"] = "0";
            var capture = await executor.ExecuteBoundedCaptureAsync(new(fixture.Git,
                ["-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", .. arguments], fixture.Root, environment)
            { EnvironmentIsComplete = true },
                TimeSpan.FromSeconds(15), 1048576, bound.Token, new OwnedWorkPhaseBudget(original));
            Assert.Equal(0, capture.Command.ExitCode); Assert.True(capture.Command.OwnedProcessQuiescent);
            Assert.True(capture.RootExitObserved); Assert.True(capture.StandardOutputDrainCompleted); Assert.True(capture.StandardErrorDrainCompleted);
            Assert.True(capture.Command.LifecycleDiagnostics?.OwnedTree is { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 });
            return Encoding.UTF8.GetString(capture.StandardOutputBytes).Trim();
        }
        const string policy = """
            {"Fixture":false,"Advisory":false,"AllowedFamilies":[{"Author":"gpt","Reviewer":"claude"}],"AllowedScopes":["source"],"AllowExactReuse":false,"Budget":{"MaximumRounds":2,"MaximumDuration":"00:00:30","MaximumTokens":4096,"MaximumCost":1.0},"ExemptionApprovers":[]}
            """;
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "review.json"), policy, new UTF8Encoding(false), bound.Token);
        await Git("add", "--", "review.json");
        await Git("-c", "user.name=SourceBuilderConformance", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgSign=false", "commit", "-q", "-m", "protected policy");
        var baseCommit = await Git("rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "review.json"), "{}", bound.Token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "subject.txt"), "candidate source\n", bound.Token);
        await Git("add", "--", "review.json", "subject.txt");
        await Git("-c", "user.name=SourceBuilderConformance", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgSign=false", "commit", "-q", "-m", "candidate cannot replace protected policy");
        var headCommit = await Git("rev-parse", "HEAD");
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "components", "contracts", "DeliveryForge.Contracts.Tests", "Fixtures", "Valid", "plan.json"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var contracts = Path.Combine(repository.FullName, "components", "contracts", "DeliveryForge.Contracts.Tests", "Fixtures", "Valid");
        var plan = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(contracts, "plan.json"), bound.Token))!.AsObject();
        plan["baseCommit"] = baseCommit;
        plan["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(plan.ToJsonString()));
        var planBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(plan.ToJsonString()));
        var evidenceBytes = await File.ReadAllBytesAsync(Path.Combine(contracts, "evidence-policy.json"), bound.Token);
        var frozenPlan = ContractValidator.ParseAndValidate(planBytes); var evidence = ContractValidator.ParseAndValidate(evidenceBytes);
        var imageBytes = await File.ReadAllBytesAsync(fixture.Git, bound.Token);
        var admittedParent = new SourceBuilderParent();
        var owner = new ProductionReviewSourceOwner(executor, original, admittedParent, "run-source-builder-conformance", fixture.Root,
            fixture.GitDirectory, fixture.GitDirectory, fixture.Git, imageBytes.Length, Convert.ToHexStringLower(SHA256.HashData(imageBytes)), fixture.Version,
            baseCommit, headCommit, () => true, frozenPlan.Identity, evidence.Identity, "review.json", 1048576);
        var substitutedPlan = plan.DeepClone().AsObject(); substitutedPlan["planRevision"] = "caller-substitution";
        substitutedPlan["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(substitutedPlan.ToJsonString()));
        await Assert.ThrowsAsync<ReviewPreflightException>(() => ReviewAuthorityHost.CreateProductionFromOwnedSourceAsync(owner,
            CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(substitutedPlan.ToJsonString())), evidenceBytes, "review.json", 1048576, bound.Token));
        await Assert.ThrowsAsync<ReviewPreflightException>(() => ReviewAuthorityHost.CreateProductionFromOwnedSourceAsync(owner,
            planBytes, evidenceBytes, "caller-policy.json", 1048576, bound.Token));
        Assert.Equal(0, admittedParent.Entries);
        using var bootstrap = await ReviewAuthorityHost.CreateProductionFromOwnedSourceAsync(owner, planBytes, evidenceBytes, "review.json", 1048576, bound.Token);
        Assert.Equal(ReviewAuthorityRealm.Production, bootstrap.ObjectView.SourceRealm);
        Assert.Equal(baseCommit, bootstrap.Policy.SourceRevision);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(policy))), bootstrap.Policy.ContentIdentity);
        Assert.True(bootstrap.Policy.Trusted); Assert.False(bootstrap.Policy.Fixture);
        Assert.Equal(frozenPlan.Identity, bootstrap.RootContext.PlanIdentity); Assert.Equal(evidence.Identity, bootstrap.RootContext.EvidencePolicyIdentity);
        Assert.Equal(headCommit, bootstrap.ObjectView.FrozenHeadCommit);
        Assert.False(owner.Uncertain);
        // Empty directories yield no files; traversal must still stop at its depth bound.
        var emptyRoot = Path.Combine(fixture.Root, "empty-depth-control"); var deepest = emptyRoot;
        for (var depth = 1; depth <= 65; depth++) deepest = Path.Combine(deepest, "d");
        Directory.CreateDirectory(deepest);
        var depthFailure = await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(bootstrap.ObjectView, baseCommit, headCommit,
            frozenPlan.Identity, new Dictionary<string, string>(), bound.Token));
        Assert.Equal("Owned source traversal exceeds64 directory levels.", depthFailure.Message);
        // Preserve the refused deep tree. Index validation precedes traversal,
        // so the next control does not need to move this Windows directory tree.
        // Retain the accepted raw index before a real bounded-read refusal control.
        var indexPath = Path.Combine(fixture.GitDirectory, "index");
        await File.WriteAllBytesAsync(Path.Combine(fixture.GitDirectory, "source-builder-accepted-index"), await File.ReadAllBytesAsync(indexPath, bound.Token), bound.Token);
        using (var oversizedIndex = new FileStream(indexPath, FileMode.Open, FileAccess.Write, FileShare.None)) oversizedIndex.SetLength(16777217);
        var oversized = await Assert.ThrowsAsync<ReviewPreflightException>(() => GitPatchIdentity.ComputeAsync(bootstrap.ObjectView, baseCommit, headCommit,
            frozenPlan.Identity, new Dictionary<string, string>(), bound.Token));
        Assert.Equal("Owned source/index exceeds its byte bound.", oversized.Message);
        // No reviewer capability, execution, acceptance receipt or publication is issued.
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
    sealed class RefusingParent : ICoordinatedSynchronousEffectAdmission
    {
        internal int Entries;
        internal bool AllowAsync, DenySynchronous;
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) { Entries++; return AllowAsync ? invoke() : throw new InvalidOperationException("Negative control must not reach native admission."); }
        public void RetainUncertain(string runId, string reason) => throw new InvalidOperationException("No native owner was created in this control.");
        public T EnterSynchronous<T>(string runId, Func<T> invoke) => DenySynchronous ? throw new InvalidOperationException("File birth admission revoked.") : invoke();
        public void CheckCurrent(string runId) { }
    }
    sealed class SourceBuilderParent : ICoordinatedSynchronousEffectAdmission
    {
        internal int Entries;
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) { Entries++; return invoke(); }
        public void RetainUncertain(string runId, string reason) => throw new IOException("Source-builder native uncertainty: " + reason);
        public T EnterSynchronous<T>(string runId, Func<T> invoke) => invoke();
        public void CheckCurrent(string runId) { }
    }
    sealed class Original : IOwnedDuplexBudget
    {
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(1200);
        public TimeSpan WorkLimit => TimeSpan.FromSeconds(1100);
        public TimeSpan CleanupRemaining => TimeSpan.FromSeconds(60);
        public CancellationToken Token => CancellationToken.None;
        public void CheckWork() { if (Stopwatch.GetElapsedTime(OriginalStarted) >= WorkLimit) throw new TimeoutException(); }
    }
}
