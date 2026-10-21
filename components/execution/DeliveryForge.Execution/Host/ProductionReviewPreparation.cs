using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Review;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Execution.Host;

// Only the actual Tool bridge can construct this handoff. Constructor arguments
// describe that bridge's retained owners; public configuration cannot mint one.
public sealed partial class ProductionReviewSourceOwner
{
    static readonly List<(ProductionReviewSourceOwner Owner, IDisposable Resource)> UnsettledResources = [];
    readonly ProcessCommandExecutor executor;
    readonly IOwnedDuplexBudget original;
    readonly ICoordinatedSynchronousEffectAdmission parent;
    readonly Func<bool> nativeCompletedAndQuiet;
    readonly long gitBytes;
    readonly string gitSha256;
    readonly WorktreeManager.OwnedGitBinding? provisionedBinding;
    string? controlled;
    bool entered, uncertain;
    int activeCaptures;
    internal ProductionReviewSourceOwner(ProcessCommandExecutor executor, IOwnedDuplexBudget original,
        ICoordinatedSynchronousEffectAdmission parent, string runId, string sourceRoot, string workerGitDirectory,
        string commonGitDirectory, string trustedGitExecutable, long trustedGitBytes, string trustedGitSha256,
        string expectedGitVersion, string baseCommit, string headCommit, Func<bool> actualNativeCompletedAndQuiet,
        string expectedPlanIdentity, string expectedEvidencePolicyIdentity, string expectedPolicyRelativePath, long maximumEvidenceBytes,
        WorktreeManager.OwnedGitBinding? actualProvisionedBinding = null)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.original = original ?? throw new ArgumentNullException(nameof(original));
        this.parent = parent ?? throw new ArgumentNullException(nameof(parent));
        nativeCompletedAndQuiet = actualNativeCompletedAndQuiet ?? throw new ArgumentNullException(nameof(actualNativeCompletedAndQuiet));
        foreach (var path in new[] { sourceRoot, workerGitDirectory, commonGitDirectory, trustedGitExecutable }) Plain(path);
        RunId = runId; SourceRoot = Path.GetFullPath(sourceRoot); WorkerGitDirectory = Path.GetFullPath(workerGitDirectory);
        CommonGitDirectory = Path.GetFullPath(commonGitDirectory); GitExecutable = Path.GetFullPath(trustedGitExecutable);
        gitBytes = trustedGitBytes; gitSha256 = trustedGitSha256; GitVersion = expectedGitVersion;
        BaseCommit = baseCommit; HeadCommit = headCommit;
        PlanIdentity = expectedPlanIdentity; EvidencePolicyIdentity = expectedEvidencePolicyIdentity;
        PolicyRelativePath = expectedPolicyRelativePath; SelectedMaximumBytes = maximumEvidenceBytes;
        provisionedBinding = actualProvisionedBinding;
        if (provisionedBinding is not null)
        {
            provisionedBinding.RequireOriginal(original, parent, runId);
            if (!SamePath(provisionedBinding.Worktree, sourceRoot) || !SamePath(provisionedBinding.GitDirectory, workerGitDirectory) || !SamePath(provisionedBinding.CommonDirectory, commonGitDirectory))
                throw new ReviewPreflightException("Review source roles differ from original parent provisioning.");
        }
        if (string.IsNullOrWhiteSpace(runId) || !ObjectId(baseCommit) || !ObjectId(headCommit) ||
            trustedGitBytes is < 1 or > 67108864 || trustedGitSha256.Length != 64 ||
            trustedGitSha256.Any(c => !char.IsAsciiHexDigitLower(c)) || !expectedGitVersion.StartsWith("git version ", StringComparison.Ordinal) ||
            !Identity(expectedPlanIdentity) || !Identity(expectedEvidencePolicyIdentity) || string.IsNullOrWhiteSpace(expectedPolicyRelativePath) || maximumEvidenceBytes is < 1 or > 16777216)
            throw new ArgumentException("Actual finite Git image and exact completed revision required.");
        VerifyImage();
    }
    static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal string RunId { get; }
    internal string SourceRoot { get; }
    internal string WorkerGitDirectory { get; }
    internal string CommonGitDirectory { get; }
    internal string GitExecutable { get; }
    internal string GitVersion { get; }
    internal string BaseCommit { get; }
    internal string HeadCommit { get; }
    internal string PlanIdentity { get; }
    internal string EvidencePolicyIdentity { get; }
    internal string PolicyRelativePath { get; }
    internal long SelectedMaximumBytes { get; }
    internal bool Uncertain => uncertain || provisionedBinding?.Uncertain == true;
    internal bool MayCleanOwnedFiles => !Uncertain && Volatile.Read(ref activeCaptures) == 0;
    internal CancellationToken OriginalToken => original.Token;
    internal void Check()
    {
        original.CheckWork();
        parent.CheckCurrent(RunId);
        if (Uncertain || !nativeCompletedAndQuiet()) throw new ReviewPreflightException("Original completed native worker is no longer quiet.", residualProcessUncertain: Uncertain);
    }
    void VerifyImage()
    {
        Check();
        CheckMetadataBinding();
        parent.EnterSynchronous(RunId, () =>
        {
            Plain(GitExecutable);
            using var image = new FileStream(GitExecutable, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (image.Length != gitBytes || Convert.ToHexStringLower(SHA256.HashData(image)) != gitSha256)
                throw new ReviewPreflightException("Trusted Git image changed after original selection.");
            original.CheckWork(); return true;
        });
    }
    internal Task<T> EnterAsync<T>(Func<Task<T>> operation)
    {
        Check();
        return parent.EnterAsync(RunId, operation);
    }
    internal T EnterSynchronous<T>(Func<T> operation)
    {
        Check(); return parent.EnterSynchronous(RunId, () => { original.CheckWork(); return operation(); });
    }
    internal object? EnterSynchronousObject(Func<object?> operation) => EnterSynchronous(operation);
    internal Exception? CloseResource(IDisposable resource, ref Exception? primary)
    {
        if (primary is null) { try { Check(); } catch (Exception error) { primary = error; } }
        var returned = false;
        try
        {
            if (primary is not null) _ = original.CleanupRemaining;
            resource.Dispose(); returned = true;
            if (primary is not null) _ = original.CleanupRemaining; else Check();
            return null;
        }
        catch (Exception failure)
        {
            if (!returned)
            {
                lock (UnsettledResources) UnsettledResources.Add((this, resource));
                uncertain = true;
                var failures = new List<Exception> { failure };
                try { if (original is IOwnedBudgetUnknownRetention retained) retained.PreserveUnknown(); }
                catch (Exception observation) { failures.Add(observation); }
                try { parent.RetainUncertain(RunId, "Actual review file/enumerator first disposition did not return."); }
                catch (Exception observation) { failures.Add(observation); }
                if (failures.Count > 1) return new AggregateException("Review first disposition and uncertainty observations failed.", failures);
            }
            return failure;
        }
    }
    internal static void ThrowFailures(Exception? primary, Exception? close)
    {
        if (primary is not null && close is not null) throw new AggregateException("Original review operation and first disposition failed.", primary, close);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (close is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(close).Throw();
    }
    internal async Task<byte[]> ReadFileAsync(string path, int maximum, CancellationToken token)
    {
        var result = await EnterAsync(async () =>
        {
            FileStream? file = null; byte[]? resultBytes = null; Exception? primary = null;
            try
            {
                EnterSynchronous(() => { Plain(path); return file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous); });
                var opened = file ?? throw new IOException("Owned file birth returned no stream.");
                if (opened.Length > maximum) throw new ReviewPreflightException("Owned source/index exceeds its byte bound.");
                using var bytes = new MemoryStream(); var buffer = new byte[65536];
                while (true)
                {
                    Check(); token.ThrowIfCancellationRequested();
                    var count = await opened.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (bytes.Length + count > maximum) throw new ReviewPreflightException("Owned source/index grew past its byte bound.");
                    bytes.Write(buffer, 0, count);
                }
                Check(); resultBytes = bytes.ToArray();
            }
            catch (Exception failure) { primary = failure; }
            var close = file is null ? null : CloseResource(file, ref primary);
            ThrowFailures(primary, close);
            return resultBytes ?? throw new IOException("Owned source read returned no bytes.");
        }).ConfigureAwait(false);
        Check(); return result;
    }
    internal async Task WriteFileAsync(string path, byte[] bytes, CancellationToken token)
    {
        await EnterAsync(async () =>
        {
            FileStream? file = null; Exception? primary = null;
            try
            {
                EnterSynchronous(() => { Plain(path); return file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous); });
                var opened = file ?? throw new IOException("Owned file birth returned no stream.");
                Check(); await opened.WriteAsync(bytes, token).ConfigureAwait(false);
                Check(); await opened.FlushAsync(token).ConfigureAwait(false);
                Check();
            }
            catch (Exception failure) { primary = failure; }
            var close = file is null ? null : CloseResource(file, ref primary);
            ThrowFailures(primary, close); return true;
        }).ConfigureAwait(false);
        Check();
    }
    internal void Begin(string directory)
    {
        Check(); CheckMetadataBinding(); if (entered) throw new ReviewPreflightException("Production source preparation cannot replay.");
        Plain(directory); entered = true; controlled = Path.GetFullPath(directory);
    }
    internal async Task<GitProcessResult> GitAsync(string gitDirectory, IReadOnlyList<string> arguments,
        CancellationToken token, byte[]? input = null, IReadOnlyDictionary<string, string>? extra = null)
    {
        VerifyImage(); token.ThrowIfCancellationRequested();
        Plain(gitDirectory); gitDirectory = Path.GetFullPath(gitDirectory);
        var workerRole = SamePath(gitDirectory, WorkerGitDirectory);
        var controlledRole = controlled is not null && SamePath(gitDirectory, controlled);
        if (!workerRole && !controlledRole)
            throw new ReviewPreflightException("Git evidence attempted a foreign object authority.");
        string? patchPath = null;
        var dispatchEntered = false;
        var commandSucceeded = false;
        var args = arguments.ToArray();
        if (input is not null)
        {
            if (input.Length > MaximumBytes || !controlledRole || args.Length < 2 || args[0] != "apply" || args[^1] != "-" ||
                !args.SequenceEqual(new[] { "apply", "--cached", "--binary", "--whitespace=nowarn", "-" }) &&
                !args.SequenceEqual(new[] { "apply", "--cached", "--reverse", "--binary", "--whitespace=nowarn", "-" }))
                throw new ReviewPreflightException("Only exact bounded patch reconstruction may carry Git input.");
            patchPath = Path.Combine(controlled!, "owned-patch-" + Guid.NewGuid().ToString("N"));
            await WriteFileAsync(patchPath, input, token).ConfigureAwait(false);
            args[^1] = patchPath;
        }
        try
        {
            var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(row => !((string)row.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => (string)row.Key, row => row.Value?.ToString() ?? "", OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var closed = new Dictionary<string, string>
            {
                ["GIT_DIR"] = gitDirectory,
                ["GIT_COMMON_DIR"] = workerRole ? CommonGitDirectory : gitDirectory,
                ["GIT_WORK_TREE"] = SourceRoot,
                ["GIT_OBJECT_DIRECTORY"] = Path.Combine(workerRole ? CommonGitDirectory : gitDirectory, "objects"),
                ["GIT_INDEX_FILE"] = Path.Combine(gitDirectory, "index"),
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "",
                ["GIT_CONFIG_COUNT"] = "0",
                ["GIT_CONFIG_PARAMETERS"] = "",
                ["GIT_CONFIG"] = Path.Combine(workerRole ? CommonGitDirectory : gitDirectory, "config"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = nullDevice,
                ["GIT_CONFIG_SYSTEM"] = nullDevice,
                ["GIT_NO_REPLACE_OBJECTS"] = "1",
                ["GIT_NO_LAZY_FETCH"] = "1",
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_GRAFT_FILE"] = nullDevice,
                ["GIT_OPTIONAL_LOCKS"] = "0",
                ["LC_ALL"] = "C",
                ["LANG"] = "C"
            };
            foreach (var pair in closed) environment[pair.Key] = pair.Value;
            if (extra is not null)
                foreach (var pair in extra)
                {
                    if (pair.Key != "GIT_INDEX_FILE" || !Path.IsPathFullyQualified(pair.Value)) throw new ReviewPreflightException("Unsupported owned Git environment override.");
                    environment[pair.Key] = pair.Value;
                }
            var prefix = new[] { "--no-replace-objects", $"--git-dir={gitDirectory}", $"--work-tree={SourceRoot}", "-c", "core.fsmonitor=false", "-c", $"core.hooksPath={nullDevice}" };
            var invocation = new CommandInvocation(GitExecutable, prefix.Concat(args).ToArray(), SourceRoot, environment) { EnvironmentIsComplete = true };
            BoundedCommandCaptureResult capture;
            try
            {
                capture = await EnterAsync(() =>
                {
                    CheckMetadataBinding();
                    var allowance = Remaining();
                    dispatchEntered = true;
                    Interlocked.Increment(ref activeCaptures);
                    return executor.ExecuteBoundedCaptureAsync(invocation, allowance, MaximumBytes, token, new OwnedWorkPhaseBudget(original));
                }).ConfigureAwait(false);
            }
            catch
            {
                if (dispatchEntered) { uncertain = true; parent.RetainUncertain(RunId, "Original review Git dispatch/capture did not return."); }
                throw;
            }
            var facts = capture.Command.LifecycleDiagnostics;
            var settled = capture.Command.OwnedProcessQuiescent && capture.RootExitObserved && capture.StandardOutputDrainCompleted && capture.StandardErrorDrainCompleted &&
                facts?.NativeFailure is null && facts?.OwnedTree is { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 } &&
                (!OperatingSystem.IsLinux() || capture.OwnershipKind == BoundedCaptureOwnershipKind.LinuxCreationOwnedScope);
            if (!settled) { uncertain = true; parent.RetainUncertain(RunId, "Original review Git native scope or streams remain uncertain."); }
            if (!settled || capture.Command.Cancelled || capture.Command.TimedOut || capture.CaptureBoundExceeded || capture.Command.ExitCode != 0)
            {
                var failure = new ReviewPreflightException("Owned review Git refused; actual capture retained.", residualProcessUncertain: !settled);
                failure.Data["ReviewGitPreparationCapture"] = ReviewGitPreparationCaptureData.FromCapture(capture);
                throw failure;
            }
            VerifyImage();
            commandSucceeded = true;
            return new(capture.Command.ExitCode!.Value, capture.StandardOutputBytes, capture.StandardErrorBytes);
        }
        finally
        {
            if (dispatchEntered) Interlocked.Decrement(ref activeCaptures);
            // An uncertain native scope retains its exact input and parent owner.
            if (patchPath is not null && commandSucceeded && !uncertain) DeleteScratch(patchPath);
        }
    }
    internal int MaximumBytes { get; set; }
    internal void DeleteScratch(string path)
    {
        try
        {
            Check();
            if (!MayCleanOwnedFiles) throw new ReviewPreflightException("Original native scope is active; scratch retained.", residualProcessUncertain: true);
            EnterSynchronous(() => { Plain(path); if (File.Exists(path)) File.Delete(path); return true; }); Check();
        }
        catch (Exception failure) { failure.Data["RetainedProductionReviewScratch"] = path; throw; }
    }
    internal void DisposeView(string directory, bool cleanup = true)
    {
        if (cleanup && !MayCleanOwnedFiles) return;
        void Endpoint() { if (cleanup) _ = original.CleanupRemaining; else Check(); if (!MayCleanOwnedFiles) throw new ReviewPreflightException("Original review scope remains active.", residualProcessUncertain: true); }
        void Delete(string path)
        {
            Endpoint(); Plain(path);
            var full = Path.GetFullPath(path); var root = Path.GetFullPath(directory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(full, root, comparison) && !full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new ReviewPreflightException("Review cleanup path escaped its original owned view.");
            if (Directory.Exists(path))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) Delete(child);
                Endpoint(); Directory.Delete(path); Endpoint();
            }
            else if (File.Exists(path)) { Endpoint(); File.Delete(path); Endpoint(); }
        }
        Exception? primary = null, post = null;
        try
        {
            if (cleanup) Delete(directory);
            else EnterSynchronous(() => { Delete(directory); return true; });
        }
        catch (Exception error) { primary = error; }
        try { Endpoint(); } catch (Exception error) { post = error; }
        if (primary is not null || post is not null)
        {
            var failure = primary is not null && post is not null ? new AggregateException("Original view cleanup and independent same-original post.", primary, post) : primary ?? post!;
            failure.Data["RetainedProductionReviewControlledDirectory"] = directory;
            throw failure;
        }
    }
    internal TimeSpan Remaining()
    {
        original.CheckWork(); var left = original.WorkLimit - Stopwatch.GetElapsedTime(original.OriginalStarted);
        if (left <= TimeSpan.Zero) throw new TimeoutException("Original review work allowance expired.");
        return left < TimeSpan.FromSeconds(30) ? left : TimeSpan.FromSeconds(30);
    }
    internal static bool ObjectId(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigitLower);
    static bool Identity(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(char.IsAsciiHexDigitLower);
    internal void CheckMetadataBinding()
    {
        Check();
        if (provisionedBinding is not null) { provisionedBinding.Check(); return; }
        parent.EnterSynchronous(RunId, () =>
        {
            static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            string Text(string path)
            {
                FileStream? file = null; string? text = null; Exception? primary = null;
                try
                {
                    Plain(path); file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (file.Length > 4096) throw new ReviewPreflightException("Owned Git metadata link exceeds4096 bytes.");
                    var bytes = new byte[4097]; var count = 0;
                    while (count < bytes.Length) { Check(); var next = file.Read(bytes, count, bytes.Length - count); if (next == 0) break; count += next; }
                    if (count > 4096) throw new ReviewPreflightException("Owned Git metadata link grew past4096 bytes.");
                    Check(); text = new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimEnd('\r', '\n');
                }
                catch (Exception failure) { primary = failure; }
                var close = file is null ? null : CloseResource(file, ref primary);
                ThrowFailures(primary, close); return text!;
            }
            var metadata = Path.Combine(SourceRoot, ".git"); Plain(metadata);
            if (Directory.Exists(metadata))
            {
                if (!Same(metadata, WorkerGitDirectory) || !Same(metadata, CommonGitDirectory)) throw new ReviewPreflightException("Standalone Git authority differs from owned paths.");
                return true;
            }
            var link = Text(metadata);
            if (!link.StartsWith("gitdir: ", StringComparison.Ordinal) || !Same(Path.GetFullPath(link[8..], SourceRoot), WorkerGitDirectory) ||
                !Same(Path.GetFullPath(Text(Path.Combine(WorkerGitDirectory, "gitdir")), WorkerGitDirectory), metadata) ||
                !Same(Path.GetFullPath(Text(Path.Combine(WorkerGitDirectory, "commondir")), WorkerGitDirectory), CommonGitDirectory))
                throw new ReviewPreflightException("Owned worktree metadata/common-directory association changed.");
            return true;
        });
    }
    internal static void Plain(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ReviewPreflightException("Absolute owned source path required.");
        for (FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path); item is not null;
            item = item is DirectoryInfo directory ? directory.Parent : ((FileInfo)item).Directory)
            if (item.LinkTarget is not null || item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ReviewPreflightException("Owned source path is redirected.");
    }
}

public sealed class ProductionReviewBootstrap : IDisposable
{
    internal ProductionReviewBootstrap(ReviewAuthorityHost host, ControlledGitEvidenceContext view, ProtectedReviewSourceContext source, ReviewPolicy policy, ReviewAuthorityRootContext root)
    { Host = host; ObjectView = view; ProtectedSource = source; Policy = policy; RootContext = root; }
    public ReviewAuthorityHost Host { get; }
    public ControlledGitEvidenceContext ObjectView { get; }
    public ProtectedReviewSourceContext ProtectedSource { get; }
    public ReviewPolicy Policy { get; }
    public ReviewAuthorityRootContext RootContext { get; }
    public void Dispose() => ObjectView.Dispose();
    public void DisposeDuringWork() => ObjectView.DisposeDuringWork();
}

public sealed partial class ReviewAuthorityHost
{
    public static async Task<ProductionReviewBootstrap> CreateProductionFromOwnedSourceAsync(ProductionReviewSourceOwner owner,
        byte[] frozenPlanContract, byte[] evidencePolicyContract, string relativePolicyPath, long maximumEvidenceBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.OriginalToken);
        cancellationToken = linked.Token;
        if (maximumEvidenceBytes is < 1 or > 16777216 || maximumEvidenceBytes > owner.SelectedMaximumBytes || frozenPlanContract.Length > 1048576 || evidencePolicyContract.Length > 1048576)
            throw new ArgumentException("Production review inputs exceed finite allowances.");
        owner.Check();
        var plan = ContractValidator.ParseAndValidate(frozenPlanContract);
        var evidencePolicy = ContractValidator.ParseAndValidate(evidencePolicyContract);
        if (plan.SchemaName != "plan" || evidencePolicy.SchemaName != "evidence-policy") throw new ArgumentException("Actual frozen plan/evidence policy contracts required.");
        if (plan.Identity != owner.PlanIdentity || evidencePolicy.Identity != owner.EvidencePolicyIdentity || relativePolicyPath != owner.PolicyRelativePath)
            throw new ReviewPreflightException("Public contract/policy inputs differ from the original owner-issued selection.");
        using var planJson = JsonDocument.Parse(plan.CanonicalBytes);
        if (planJson.RootElement.GetProperty("baseCommit").GetString() != owner.BaseCommit) throw new ReviewPreflightException("Plan base differs from the actual owned source.");
        var controlled = Path.Combine(Path.GetTempPath(), "delivery-forge-production-review", Guid.NewGuid().ToString("N"));
        owner.Begin(controlled); owner.MaximumBytes = checked((int)maximumEvidenceBytes);
        try
        {
            return await owner.EnterAsync(async () =>
            {
                // Return and retain THIS actual preparation Task before any
                // filesystem prefix. EnterAsync invokes under the parent gate;
                // the following admitted synchronous batch runs outside it.
                await Task.Yield();
                // Failure retains the private partial object view and all native diagnostics.
                foreach (var directory in new[] { owner.WorkerGitDirectory, owner.CommonGitDirectory })
                {
                    ProductionReviewSourceOwner.Plain(directory);
                    if (File.Exists(Path.Combine(directory, "shallow")) || File.Exists(Path.Combine(directory, "info", "grafts")) || Directory.Exists(Path.Combine(directory, "refs", "replace")))
                        throw new ReviewPreflightException("Source uses incomplete or replacement object authority.");
                }
                var workerObjects = Path.Combine(owner.CommonGitDirectory, "objects");
                if (File.Exists(Path.Combine(workerObjects, "info", "alternates"))) throw new ReviewPreflightException("External source object authority is unsupported.");
                var config = await owner.ReadFileAsync(Path.Combine(owner.CommonGitDirectory, "config"), 1048576, cancellationToken).ConfigureAwait(false);
                if (new[] { "include", "promisor", "partialclone", "alternates" }.Any(s => Encoding.UTF8.GetString(config).Contains(s, StringComparison.OrdinalIgnoreCase)))
                    throw new ReviewPreflightException("Source Git contains external authority.");
                if (Encoding.UTF8.GetString((await owner.GitAsync(owner.WorkerGitDirectory, ["--version"], cancellationToken).ConfigureAwait(false)).StandardOutput).Trim() != owner.GitVersion)
                    throw new ReviewPreflightException("Trusted actual Git version changed.");
                var index = await owner.ReadFileAsync(Path.Combine(owner.WorkerGitDirectory, "index"), 16777216, cancellationToken).ConfigureAwait(false);
                owner.EnterSynchronous(() => { ProductionReviewSourceOwner.Plain(controlled); Directory.CreateDirectory(Path.Combine(controlled, "objects")); Directory.CreateDirectory(Path.Combine(controlled, "refs")); Directory.CreateDirectory(Path.Combine(controlled, "info")); return true; });
                await owner.WriteFileAsync(Path.Combine(controlled, "HEAD"), Encoding.UTF8.GetBytes("ref: refs/heads/controlled\n"), cancellationToken).ConfigureAwait(false);
                await owner.WriteFileAsync(Path.Combine(controlled, "config"), Encoding.UTF8.GetBytes("[core]\n\trepositoryformatversion = 0\n\tbare = true\n\tfilemode = true\n"), cancellationToken).ConfigureAwait(false);
                await owner.WriteFileAsync(Path.Combine(controlled, "info", "attributes"), Encoding.UTF8.GetBytes(ProductionReviewSourceOwner.ArchiveAttributeText), cancellationToken).ConfigureAwait(false);
                var copied = new Dictionary<string, string>(StringComparer.Ordinal); long copiedBytes = 0; int entries = 0;
                // One finite, self-contained synchronous object copy/verify
                // operation. Parent admission retains its actual completion but
                // releases its gate before the filesystem work. Nothing here
                // issues native dispatch or flows authority through an await.
                owner.EnterSynchronous(() =>
                {
                    byte[] ReadObject(string path)
                    {
                        FileStream? file = null; byte[]? bytes = null; Exception? primary = null;
                        try
                        {
                            owner.Check(); cancellationToken.ThrowIfCancellationRequested(); ProductionReviewSourceOwner.Plain(path);
                            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            owner.Check(); var length = file.Length;
                            if (length > 16777216) throw new ReviewPreflightException("Owned source/index exceeds its byte bound.");
                            if (length > 134217728 - copiedBytes) throw new ReviewPreflightException("Owned object inventory exceeds128MiB.");
                            bytes = new byte[checked((int)length)]; var offset = 0;
                            while (offset < bytes.Length)
                            {
                                owner.Check(); cancellationToken.ThrowIfCancellationRequested();
                                var count = file.Read(bytes, offset, Math.Min(65536, bytes.Length - offset)); owner.Check();
                                if (count == 0) throw new ReviewPreflightException("Owned object ended before its captured extent.");
                                offset += count;
                            }
                            owner.Check(); var eof = file.ReadByte(); owner.Check();
                            if (eof != -1 || file.Length != length) throw new ReviewPreflightException("Owned object extent changed while read.");
                        }
                        catch (Exception failure) { primary = failure; }
                        var close = file is null ? null : owner.CloseResource(file, ref primary);
                        ProductionReviewSourceOwner.ThrowFailures(primary, close);
                        return bytes ?? throw new IOException("Owned object read returned no bytes.");
                    }
                    void WriteObject(string path, byte[] bytes)
                    {
                        FileStream? file = null; Exception? primary = null;
                        try
                        {
                            owner.Check(); cancellationToken.ThrowIfCancellationRequested(); ProductionReviewSourceOwner.Plain(path);
                            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            owner.Check();
                            for (var offset = 0; offset < bytes.Length; offset += Math.Min(65536, bytes.Length - offset))
                            {
                                owner.Check(); cancellationToken.ThrowIfCancellationRequested();
                                file.Write(bytes, offset, Math.Min(65536, bytes.Length - offset)); owner.Check();
                            }
                            owner.Check(); file.Flush(); owner.Check();
                        }
                        catch (Exception failure) { primary = failure; }
                        var close = file is null ? null : owner.CloseResource(file, ref primary);
                        ProductionReviewSourceOwner.ThrowFailures(primary, close);
                    }
                    void Inventory(string directory, bool copy, int depth = 0)
                    {
                        owner.Check(); cancellationToken.ThrowIfCancellationRequested(); ProductionReviewSourceOwner.Plain(directory);
                        if (depth > 64) throw new ReviewPreflightException("Owned object inventory exceeds64 directory levels.");
                        IEnumerator<string>? iterator = null; Exception? primary = null;
                        try
                        {
                            owner.Check(); iterator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator(); owner.Check();
                            while (true)
                            {
                                owner.Check(); cancellationToken.ThrowIfCancellationRequested(); var next = iterator.MoveNext(); owner.Check();
                                if (!next) break;
                                var path = iterator.Current;
                                owner.Check(); cancellationToken.ThrowIfCancellationRequested(); ProductionReviewSourceOwner.Plain(path);
                                if (++entries > 4096) throw new ReviewPreflightException("Owned object inventory exceeds4096 entries.");
                                var relative = Path.GetRelativePath(workerObjects, path); var destination = Path.Combine(controlled, "objects", relative);
                                owner.Check(); var directoryEntry = Directory.Exists(path); owner.Check();
                                if (directoryEntry)
                                {
                                    if (copy) { owner.Check(); Directory.CreateDirectory(destination); owner.Check(); }
                                    Inventory(path, copy, depth + 1); continue;
                                }
                                var bytes = ReadObject(path);
                                copiedBytes += bytes.Length; if (copiedBytes > 134217728) throw new ReviewPreflightException("Owned object inventory exceeds128MiB.");
                                owner.Check(); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); owner.Check();
                                if (copy) { copied.Add(relative, hash); WriteObject(destination, bytes); }
                                else if (!copied.Remove(relative, out var expected) || hash != expected) throw new ReviewPreflightException("Source objects changed during owned copy.");
                            }
                        }
                        catch (Exception failure) { primary = failure; }
                        var close = iterator is null ? null : owner.CloseResource(iterator, ref primary);
                        ProductionReviewSourceOwner.ThrowFailures(primary, close);
                    }
                    Inventory(workerObjects, true); entries = 0; copiedBytes = 0;
                    Inventory(workerObjects, false);
                    if (copied.Count != 0) throw new ReviewPreflightException("Source objects disappeared during owned copy.");
                    return true;
                });
                var freshIndex = await owner.ReadFileAsync(Path.Combine(owner.WorkerGitDirectory, "index"), 16777216, cancellationToken).ConfigureAwait(false);
                if (!index.AsSpan().SequenceEqual(freshIndex)) throw new ReviewPreflightException("Original index changed during owned copy.");
                await owner.GitAsync(controlled, ["fsck", "--full", "--strict", "--no-reflogs", "--no-dangling", owner.BaseCommit, owner.HeadCommit], cancellationToken).ConfigureAwait(false);
                var rootId = "production-source:" + Guid.NewGuid().ToString("N"); var issuer = new object();
                var view = new ControlledGitEvidenceContext(owner.SourceRoot, owner.WorkerGitDirectory, controlled, owner.GitExecutable, owner.GitVersion,
                    maximumEvidenceBytes, rootId, owner.HeadCommit, "production-object-view:" + Guid.NewGuid().ToString("N"), ReviewAuthorityRealm.Production,
                    SHA256.HashData(index), issuer, () => owner.DisposeView(controlled), ownedGit: owner.GitAsync,
                    mayCleanOwnedFiles: () => owner.MayCleanOwnedFiles, checkOwnedWork: owner.Check, deleteOwnedScratch: owner.DeleteScratch,
                    ownedSynchronous: owner.EnterSynchronousObject, readOwnedFile: owner.ReadFileAsync, ownedSourceSnapshot: owner.VerifySourceSnapshotAsync, disposeOwnedWork: () => owner.DisposeView(controlled, cleanup: false));
                _ = await GitPatchIdentity.ComputeAsync(view, owner.BaseCommit, owner.HeadCommit, plan.Identity, new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
                var source = new ProtectedReviewSourceContext(view, owner.BaseCommit, rootId);
                var policy = await new ProtectedReviewPolicyResolver(source).ResolveBoundedAsync(relativePolicyPath, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                if (!policy.Trusted || policy.Fixture || policy.Advisory || policy.SourceRealm != ReviewAuthorityRealm.Production)
                    throw new ReviewPolicyException("Actual protected production review policy is required.");
                var root = new ReviewAuthorityRootContext(owner.RunId, plan.Identity, evidencePolicy.Identity, policy.ContentIdentity, policy.SourceRevision,
                    policy.AuthorityKind, rootId, owner.SourceRoot, owner.BaseCommit, view.ProtectedObjectViewIdentity);
                var host = new ReviewAuthorityHost(ReviewAuthorityRealm.Production, rootId, null, new(rootId, root), root, issuer);
                owner.CheckMetadataBinding(); owner.Check(); return new ProductionReviewBootstrap(host, view, source, policy, root);
            }).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failure.Data["ProductionReviewControlledDirectory"] = controlled;
            throw;
        }
    }
}
