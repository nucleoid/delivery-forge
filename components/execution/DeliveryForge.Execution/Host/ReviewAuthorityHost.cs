using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using DeliveryForge.Evidence;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Review;

namespace DeliveryForge.Execution.Host;

public sealed record ReviewAuthorityRootContext(
    string RunIdentity,
    string PlanIdentity,
    string EvidencePolicyIdentity,
    string ReviewPolicyContentIdentity,
    string ReviewPolicySourceRevision,
    ReviewPolicyAuthorityKind ReviewPolicyAuthorityKind,
    string SourceAuthorityIdentity,
    string SourceRoot,
    string ProtectedBaseCommit,
    string ProtectedObjectViewIdentity = "");

public sealed record ProductionReviewAuthorityConfiguration(
    string RootIdentity,
    ReviewAuthorityRootContext Context);

public sealed record ReviewAcceptanceContext(
    string RunIdentity,
    string RequestIdentity,
    string PlanIdentity,
    string EvidencePolicyIdentity,
    string ReviewPolicyContentIdentity,
    string ReviewPolicySourceRevision,
    ReviewPolicyAuthorityKind ReviewPolicyAuthorityKind,
    string SourceAuthorityIdentity,
    string SourceRoot,
    string ProtectedBaseCommit,
    string ProtectedObjectViewIdentity = "");

public sealed class ReviewAuthorityHost
{
    private readonly ReviewAuthorityRealm _realm;
    private readonly string _rootIdentity;
    private readonly DateTimeOffset? _testNow;
    private readonly ProductionReviewAuthorityConfiguration? _production;
    private ReviewAuthorityRootContext? _rootContext; // Assigned once by construction or the private unpublished bootstrap.
    private readonly object _issuer = new();
    private readonly ConcurrentDictionary<string, byte> _revokedCapabilities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IssuedReviewerCapability> _capabilities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RegisteredReviewRequest> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string ReceiptIdentity, ReviewEvidenceRole Role), AcceptanceEntry> _acceptances = new();

    private ReviewAuthorityHost(
        ReviewAuthorityRealm realm,
        string rootIdentity,
        DateTimeOffset? testNow,
        ProductionReviewAuthorityConfiguration? production,
        ReviewAuthorityRootContext? rootContext)
    {
        _realm = realm;
        _rootIdentity = rootIdentity;
        _testNow = testNow;
        _production = production;
        _rootContext = rootContext;
        Authority = new ReviewAcceptanceAuthority(Accepts);
    }

    public ReviewAcceptanceAuthority Authority { get; }

    // Test-only same-issuer bootstrap. No caller root/source/view opaque identities and no live members
    // escape until real protected policy provenance and the immutable root have been joined.
    public static async Task<ProtectedReviewFixtureBootstrap> CreateProtectedGitContractFixtureAsync(
        ProtectedReviewFixtureConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        ArgumentNullException.ThrowIfNull(configuration);
        var total = configuration.EntireBudget;
        if (total <= TimeSpan.Zero || total > TimeSpan.FromSeconds(15) || string.IsNullOrWhiteSpace(configuration.RunIdentity) ||
            configuration.RunIdentity.Length > 256 || configuration.MaximumEvidenceBytes <= 0 || configuration.MaximumEvidenceBytes > 16 * 1024 * 1024)
            throw new ArgumentException("Protected fixture bootstrap requires one finite original allowance and bounded inputs.");
        TimeSpan Remaining()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = total - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) throw new TimeoutException("Original protected fixture bootstrap allowance expired.");
            return left;
        }
        _ = Remaining();
        var plan = ContractValidator.ParseAndValidate(configuration.FrozenPlanContract);
        var evidencePolicy = ContractValidator.ParseAndValidate(configuration.EvidencePolicyContract);
        if (plan.SchemaName != "plan" || evidencePolicy.SchemaName != "evidence-policy")
            throw new ArgumentException("Fixture bootstrap requires schema-valid immutable plan/evidence policy inputs.");
        using var planJson = JsonDocument.Parse(plan.CanonicalBytes);
        var protectedBase = planJson.RootElement.GetProperty("baseCommit").GetString()!;
        // This host remains unpublished until its own source/view/policy and immutable root are joined.
        var host = CreateIsolatedTest();
        var preparation = await host.PrepareIsolatedTestGitEvidenceAsync(configuration.SourceRoot, configuration.WorkerGitDirectory,
            configuration.TrustedGitExecutable, configuration.MaximumEvidenceBytes, Remaining(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (preparation.Observation.Outcome == ReviewGitPreparationOutcome.TimedOut)
            throw new TimeoutException("Original protected fixture preparation allowance expired.");
        if (preparation.Observation.Outcome != ReviewGitPreparationOutcome.Prepared || preparation.Context is null ||
            !preparation.Observation.OwnedProcessQuiescent || !preparation.Observation.RootExitObserved ||
            !preparation.Observation.StandardOutputDrainCompleted || !preparation.Observation.StandardErrorDrainCompleted)
            throw new ReviewPreflightException("Protected fixture bootstrap preparation is incomplete: " + preparation.Observation.Reason,
                residualProcessUncertain: !preparation.Observation.OwnedProcessQuiescent);
        var view = preparation.Context;
        // On any refusal retain the owned view for diagnostics; deleting it is not process closure proof.
        _ = Remaining();
        var source = host.FreezeProtectedSource(view, protectedBase, view.SourceAuthorityIdentity);
        // Typed resolver refusals propagate verbatim, including actual owner capture diagnostics.
        // No catch may convert residual uncertainty into a plain timeout/cancellation or closure proof.
        var policy = await new ProtectedReviewPolicyResolver(source).ResolveBoundedAsync(
            configuration.RelativePolicyPath, Remaining(), cancellationToken).ConfigureAwait(false);
        _ = Remaining();
        if (!policy.Trusted || policy.Fixture || policy.Advisory || policy.SourceRealm != ReviewAuthorityRealm.Test ||
            policy.SourceRevision != protectedBase || policy.SourceAuthorityIdentity != view.SourceAuthorityIdentity ||
            policy.ProtectedObjectViewIdentity != view.ProtectedObjectViewIdentity || policy.AuthorityKind != ReviewPolicyAuthorityKind.ProtectedGitBase)
            throw new ReviewPolicyException("Same-issuer protected policy provenance did not join the original Test object view.");
        var root = new ReviewAuthorityRootContext(configuration.RunIdentity, plan.Identity, evidencePolicy.Identity,
            policy.ContentIdentity, policy.SourceRevision, policy.AuthorityKind, view.SourceAuthorityIdentity, view.SourceRoot,
            protectedBase, view.ProtectedObjectViewIdentity);
        host.FreezeUnpublishedFixtureRoot(root);
        _ = Remaining();
        return new(host, view, source, policy, root);
    }

    private void FreezeUnpublishedFixtureRoot(ReviewAuthorityRootContext root)
    {
        if (_realm != ReviewAuthorityRealm.Test || _production is not null || _rootContext is not null ||
            !_requests.IsEmpty || !_capabilities.IsEmpty || !_acceptances.IsEmpty || !_revokedCapabilities.IsEmpty ||
            root.SourceAuthorityIdentity != _rootIdentity)
            throw new InvalidOperationException("Only an unpublished original Test issuer may freeze its root once, before members.");
        _rootContext = root;
    }

    // Bounded Test-only preparation; observations are data and cannot issue/rebind opaque ownership.
    public async Task<ReviewGitPreparationResult> PrepareIsolatedTestGitEvidenceAsync(
        string sourceRoot, string workerGitDirectory, string trustedGitExecutable, long maximumEvidenceBytes,
        TimeSpan deadline, CancellationToken cancellationToken = default, Action? beforeFinalRecheck = null)
    {
        if (_realm != ReviewAuthorityRealm.Test)
            throw new InvalidOperationException("Only an isolated test authority may prepare fixture Git metadata.");
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(15) || maximumEvidenceBytes <= 0 || maximumEvidenceBytes > 16 * 1024 * 1024 ||
            !Path.IsPathFullyQualified(sourceRoot) || !Path.IsPathFullyQualified(workerGitDirectory) || !Path.IsPathFullyQualified(trustedGitExecutable))
            throw new ArgumentException("Fixture preparation requires absolute paths, a finite entire deadline up to15 seconds and bounded bytes.");
        var started = Stopwatch.GetTimestamp();
        BoundedCommandCaptureResult? last = null;
        TimeSpan Remaining()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Original entire Git preparation deadline expired.");
            return remaining;
        }
        ReviewGitPreparationResult Refusal(ReviewGitPreparationOutcome outcome, string reason) => new(null, new(outcome,
            last?.Command.OwnedProcessQuiescent == true, last?.RootExitObserved == true,
            last?.StandardOutputDrainCompleted == true, last?.StandardErrorDrainCompleted == true,
            last?.Command.StandardOutput ?? "", last?.Command.StandardError ?? "", reason.Length > 1024 ? reason[..1024] : reason));
        try
        {
            _ = Remaining();
            RequirePlainPreparationPath(sourceRoot); RequirePlainPreparationPath(workerGitDirectory); RequirePlainPreparationPath(trustedGitExecutable);
            if (!Directory.Exists(sourceRoot) || !Directory.Exists(workerGitDirectory) || !File.Exists(trustedGitExecutable) ||
                !SamePath(Path.Combine(sourceRoot, ".git"), workerGitDirectory))
                throw new ReviewPreflightException("Only this plain standalone fixture repository is supported.");
            var workerObjects = Path.Combine(workerGitDirectory, "objects");
            RequirePlainPreparationPath(workerObjects);
            var config = Path.Combine(workerGitDirectory, "config");
            RequirePlainPreparationPath(config);
            if (!Directory.Exists(workerObjects) || File.Exists(Path.Combine(workerObjects, "info", "alternates")) ||
                File.Exists(Path.Combine(workerGitDirectory, "info", "grafts")) || Directory.Exists(Path.Combine(workerGitDirectory, "refs", "replace")))
                throw new ReviewPreflightException("Fixture object authority is external or incomplete.");
            var configBytes = await ReadPreparationBytesAsync(config, 1024 * 1024, Remaining, cancellationToken).ConfigureAwait(false);
            var configText = new UTF8Encoding(false, true).GetString(configBytes);
            if (new[] { "include", "promisor", "partialclone", "alternates", "worktree" }.Any(value => configText.Contains(value, StringComparison.OrdinalIgnoreCase)))
                throw new ReviewPreflightException("Fixture Git configuration contains unsupported external authority.");
            var executor = new ProcessCommandExecutor();
            var environment = new Dictionary<string, string>
            {
                ["GIT_DIR"] = workerGitDirectory,
                ["GIT_WORK_TREE"] = sourceRoot,
                ["GIT_COMMON_DIR"] = workerGitDirectory,
                ["GIT_OBJECT_DIRECTORY"] = workerObjects,
                ["GIT_INDEX_FILE"] = Path.Combine(workerGitDirectory, "index"),
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "",
                ["GIT_CONFIG_COUNT"] = "0",
                ["GIT_CONFIG_PARAMETERS"] = "",
                ["GIT_CONFIG"] = config,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_CONFIG_SYSTEM"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_NO_REPLACE_OBJECTS"] = "1",
                ["GIT_NO_LAZY_FETCH"] = "1",
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_GRAFT_FILE"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null"
            };
            async Task<string?> Git(IReadOnlyList<string> args)
            {
                last = await executor.ExecuteBoundedCaptureAsync(new(trustedGitExecutable, args, sourceRoot, environment),
                    Remaining(), (int)maximumEvidenceBytes, cancellationToken).ConfigureAwait(false);
                if (last.Command.Cancelled || last.Command.TimedOut || last.CaptureBoundExceeded || last.Command.ExitCode != 0 ||
                    !last.Command.OwnedProcessQuiescent || !last.RootExitObserved || !last.StandardOutputDrainCompleted || !last.StandardErrorDrainCompleted)
                    return null;
                _ = Remaining();
                return new UTF8Encoding(false, true).GetString(last.StandardOutputBytes).Trim();
            }
            var version = await Git(["--version"]).ConfigureAwait(false);
            if (version is null) return Refusal(CaptureOutcome(last!), "Version command did not establish complete owned capture.");
            if (!version.StartsWith("git version ", StringComparison.Ordinal)) throw new ReviewPreflightException("Trusted executable did not return a Git version.");
            var head = await Git(["--no-replace-objects", $"--git-dir={workerGitDirectory}", "rev-parse", "--verify", "HEAD^{commit}"]).ConfigureAwait(false);
            if (head is null) return Refusal(CaptureOutcome(last!), "Head command did not establish complete owned capture.");
            if (head.Length is not (40 or 64) || head.Any(character => !char.IsAsciiHexDigit(character)))
                throw new ReviewPreflightException("Actual Git head is not a complete object identity.");
            var index = await ReadPreparationBytesAsync(Path.Combine(workerGitDirectory, "index"), 16 * 1024 * 1024, Remaining, cancellationToken).ConfigureAwait(false);
            var controlled = Path.Combine(Path.GetTempPath(), "delivery-forge-controlled-git", Guid.NewGuid().ToString("N"));
            RequirePlainPreparationPath(controlled);
            if (Directory.Exists(controlled) || File.Exists(controlled)) throw new ReviewPreflightException("Controlled fixture leaf already exists.");
            Directory.CreateDirectory(Path.Combine(controlled, "objects")); Directory.CreateDirectory(Path.Combine(controlled, "refs"));
            await WritePreparationBytesAsync(Path.Combine(controlled, "HEAD"), Encoding.UTF8.GetBytes("ref: refs/heads/controlled\n"), Remaining, cancellationToken).ConfigureAwait(false);
            await WritePreparationBytesAsync(Path.Combine(controlled, "config"), Encoding.UTF8.GetBytes("[core]\n\trepositoryformatversion = 0\n\tbare = true\n\tfilemode = true\n"), Remaining, cancellationToken).ConfigureAwait(false);
            var count = 0; long copied = 0;
            async Task Copy(string source, string target)
            {
                _ = Remaining(); RequirePlainPreparationPath(source); RequirePlainPreparationPath(target);
                foreach (var entry in Directory.EnumerateFileSystemEntries(source))
                {
                    _ = Remaining(); if (++count > 4096) throw new ReviewPreflightException("Fixture object inventory exceeds4096 entries.");
                    RequirePlainPreparationPath(entry);
                    var destination = Path.Combine(target, Path.GetFileName(entry));
                    if (Directory.Exists(entry)) { Directory.CreateDirectory(destination); await Copy(entry, destination).ConfigureAwait(false); }
                    else
                    {
                        var bytes = await ReadPreparationBytesAsync(entry, 16 * 1024 * 1024, Remaining, cancellationToken).ConfigureAwait(false);
                        copied += bytes.Length; if (copied > 128 * 1024 * 1024) throw new ReviewPreflightException("Fixture object bytes exceed128MiB.");
                        await WritePreparationBytesAsync(destination, bytes, Remaining, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            await Copy(workerObjects, Path.Combine(controlled, "objects")).ConfigureAwait(false);
            _ = Remaining();
            var freshIndex = await ReadPreparationBytesAsync(Path.Combine(workerGitDirectory, "index"), 16 * 1024 * 1024, Remaining, cancellationToken).ConfigureAwait(false);
            if (!index.AsSpan().SequenceEqual(freshIndex)) throw new ReviewPreflightException("Raw fixture index changed during preparation.");
            var freshHead = await Git(["--no-replace-objects", $"--git-dir={workerGitDirectory}", "rev-parse", "--verify", "HEAD^{commit}"]).ConfigureAwait(false);
            if (freshHead is null) return Refusal(CaptureOutcome(last!), "Final head observation failed.");
            if (!string.Equals(head, freshHead, StringComparison.Ordinal)) throw new ReviewPreflightException("Fixture HEAD changed during preparation.");
            _ = Remaining();
            var context = new ControlledGitEvidenceContext(sourceRoot, workerGitDirectory, controlled, trustedGitExecutable, version,
                maximumEvidenceBytes, _rootIdentity, head, $"test-object-view:{Guid.NewGuid():N}", _realm, SHA256.HashData(index), _issuer,
                () => FixtureDirectoryCleanup.Delete(controlled), beforeFinalRecheck);
            return new(context, new(ReviewGitPreparationOutcome.Prepared, true, true, true, true,
                last!.Command.StandardOutput, last.Command.StandardError, "Bounded fixture preparation; no production authority."));
        }
        catch (OperationCanceledException)
        { return Refusal(cancellationToken.IsCancellationRequested ? ReviewGitPreparationOutcome.Cancelled : ReviewGitPreparationOutcome.TimedOut, "Original preparation cancellation/deadline observed; partial fixture files retained."); }
        catch (TimeoutException exception) { return Refusal(ReviewGitPreparationOutcome.TimedOut, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or ReviewPreflightException or NotSupportedException)
        { return Refusal(ReviewGitPreparationOutcome.Error, exception.GetType().Name + ": " + exception.Message); }
    }

    private static ReviewGitPreparationOutcome CaptureOutcome(BoundedCommandCaptureResult capture) =>
        capture.Command.Cancelled ? ReviewGitPreparationOutcome.Cancelled : capture.Command.TimedOut ? ReviewGitPreparationOutcome.TimedOut :
        capture.CaptureBoundExceeded ? ReviewGitPreparationOutcome.CaptureBoundExceeded : ReviewGitPreparationOutcome.Error;

    private static void RequirePlainPreparationPath(string path)
    {
        for (FileSystemInfo? current = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path); current is not null;
            current = current is DirectoryInfo directory ? directory.Parent : ((FileInfo)current).Directory)
            if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new ReviewPreflightException("Fixture preparation path is redirected.");
    }
    private static async Task<byte[]> ReadPreparationBytesAsync(string path, int maximum, Func<TimeSpan> remaining, CancellationToken caller)
    {
        RequirePlainPreparationPath(path);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(caller); bound.CancelAfter(remaining());
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > maximum) throw new ReviewPreflightException("Fixture file exceeds bounded bytes.");
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            _ = remaining(); var count = await stream.ReadAsync(buffer.AsMemory(), bound.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > maximum) throw new ReviewPreflightException("Fixture file grew past bounded bytes.");
            bytes.Write(buffer, 0, count);
        }
        _ = remaining(); RequirePlainPreparationPath(path); return bytes.ToArray();
    }
    private static async Task WritePreparationBytesAsync(string path, byte[] bytes, Func<TimeSpan> remaining, CancellationToken caller)
    {
        RequirePlainPreparationPath(path);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(caller); bound.CancelAfter(remaining());
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, bound.Token).ConfigureAwait(false); await stream.FlushAsync(bound.Token).ConfigureAwait(false);
        _ = remaining(); RequirePlainPreparationPath(path);
    }

    public static ReviewAuthorityHost CreateIsolatedTest(DateTimeOffset? now = null) =>
        new(ReviewAuthorityRealm.Test, $"test-root:{Guid.NewGuid():N}", now, null, null);

    public static ReviewAuthorityHost CreateIsolatedTest(
        ReviewAuthorityRootContext context,
        DateTimeOffset? now = null) =>
        new(ReviewAuthorityRealm.Test, $"test-root:{Guid.NewGuid():N}", now, null,
            context ?? throw new ArgumentNullException(nameof(context)));

    public static ReviewAuthorityHost CreateProduction(ProductionReviewAuthorityConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.RootIdentity) ||
            configuration.Context is null ||
            string.IsNullOrWhiteSpace(configuration.Context.SourceAuthorityIdentity) ||
            string.IsNullOrWhiteSpace(configuration.Context.ProtectedObjectViewIdentity) ||
            !Path.IsPathFullyQualified(configuration.Context.SourceRoot) ||
            string.IsNullOrWhiteSpace(configuration.Context.ProtectedBaseCommit))
            throw new ArgumentException("Production review authority requires frozen root and protected-source configuration.", nameof(configuration));
        return new ReviewAuthorityHost(
            ReviewAuthorityRealm.Production, configuration.RootIdentity, null, configuration, configuration.Context);
    }

    public ControlledGitEvidenceContext UseControlledGitEvidence(
        string sourceRoot,
        string workerGitDirectory,
        string controlledGitDirectory,
        string trustedGitExecutable,
        string expectedGitVersion,
        long maximumEvidenceBytes,
        string sourceAuthorityIdentity)
    {
        if (!Path.IsPathFullyQualified(sourceRoot) || !Path.IsPathFullyQualified(workerGitDirectory) ||
            !Path.IsPathFullyQualified(controlledGitDirectory) || !Path.IsPathFullyQualified(trustedGitExecutable) ||
            !Directory.Exists(sourceRoot) || !Directory.Exists(workerGitDirectory) ||
            !Directory.Exists(controlledGitDirectory) || !File.Exists(trustedGitExecutable) ||
            maximumEvidenceBytes <= 0 || string.IsNullOrWhiteSpace(expectedGitVersion) ||
            string.IsNullOrWhiteSpace(sourceAuthorityIdentity))
            throw new ArgumentException("Controlled Git evidence requires frozen absolute host metadata and bounds.");
        if (_production is not null &&
            (!SamePath(_production.Context.SourceRoot, sourceRoot) ||
             !string.Equals(_production.Context.SourceAuthorityIdentity, sourceAuthorityIdentity, StringComparison.Ordinal)))
            throw new InvalidOperationException("Controlled Git source does not match production root configuration.");
        throw new ReviewPreflightException(
            "Production immutable object snapshot and owned quiescence preparation is not configured; review evidence is INCOMPLETE.");
    }

    public ControlledGitEvidenceContext PrepareIsolatedTestGitEvidence(
        string sourceRoot,
        string workerGitDirectory,
        string trustedGitExecutable,
        long maximumEvidenceBytes,
        Action? beforeFinalRecheck = null)
    {
        if (_realm != ReviewAuthorityRealm.Test)
            throw new InvalidOperationException("Only an isolated test authority may prepare fixture Git metadata.");
        var workerObjects = Path.Combine(workerGitDirectory, "objects");
        var workerConfig = Path.Combine(workerGitDirectory, "config");
        var workerConfigText = File.Exists(workerConfig) ? File.ReadAllText(workerConfig) : string.Empty;
        if (!Directory.Exists(workerObjects) ||
            new DirectoryInfo(workerObjects).LinkTarget is not null ||
            File.Exists(Path.Combine(workerObjects, "info", "alternates")) ||
            File.Exists(Path.Combine(workerGitDirectory, "info", "grafts")) ||
            Directory.Exists(Path.Combine(workerGitDirectory, "refs", "replace")) ||
            new[] { "include", "promisor", "partialclone", "alternates", "worktree" }
                .Any(value => workerConfigText.Contains(value, StringComparison.OrdinalIgnoreCase)))
            throw new ReviewPreflightException(
                "Fixture Git preparation rejected external, replace, graft, promisor, include, or shared-worktree authority.");
        var controlled = Path.Combine(Path.GetTempPath(), "delivery-forge-controlled-git", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(controlled, "objects"));
        Directory.CreateDirectory(Path.Combine(controlled, "refs"));
        File.WriteAllText(Path.Combine(controlled, "HEAD"), "ref: refs/heads/controlled\n");
        File.WriteAllText(Path.Combine(controlled, "config"), "[core]\n\trepositoryformatversion = 0\n\tbare = true\n\tfilemode = true\n");
        CopyDirectory(workerObjects, Path.Combine(controlled, "objects"));
        var version = ReadGitVersion(trustedGitExecutable, sourceRoot);
        var frozenHead = ReadGitOutput(
            trustedGitExecutable, sourceRoot,
            ["--no-replace-objects", $"--git-dir={workerGitDirectory}", "rev-parse", "--verify", "HEAD^{commit}"]);
        var workerIndex = Path.Combine(workerGitDirectory, "index");
        if (!File.Exists(workerIndex))
            throw new ReviewPreflightException("Fixture Git preparation requires a parent-readable raw index snapshot.");
        var workerIndexHash = SHA256.HashData(File.ReadAllBytes(workerIndex));
        var objectViewIdentity = $"test-object-view:{Guid.NewGuid():N}";
        return new ControlledGitEvidenceContext(
            sourceRoot, workerGitDirectory, controlled, trustedGitExecutable, version,
            maximumEvidenceBytes, _rootIdentity, frozenHead, objectViewIdentity, _realm,
            workerIndexHash, _issuer,
            () => FixtureDirectoryCleanup.Delete(controlled),
            beforeFinalRecheck);
    }

    public ProtectedReviewSourceContext FreezeProtectedSource(
        ControlledGitEvidenceContext objectView,
        string protectedBaseCommit,
        string sourceAuthorityIdentity)
    {
        ArgumentNullException.ThrowIfNull(objectView);
        if (string.IsNullOrWhiteSpace(protectedBaseCommit) || string.IsNullOrWhiteSpace(sourceAuthorityIdentity) ||
            !ReferenceEquals(objectView.QuiescenceAuthority, _issuer) ||
            !string.Equals(objectView.SourceAuthorityIdentity, _rootIdentity, StringComparison.Ordinal))
            throw new ArgumentException("Protected review source context requires root-issued absolute source and Git identities.");
        if (_production is not null &&
            (!SamePath(_production.Context.SourceRoot, objectView.SourceRoot) ||
             !string.Equals(_production.Context.ProtectedBaseCommit, protectedBaseCommit, StringComparison.Ordinal) ||
             !string.Equals(_production.Context.SourceAuthorityIdentity, sourceAuthorityIdentity, StringComparison.Ordinal) ||
             !string.Equals(_production.Context.ProtectedObjectViewIdentity, objectView.ProtectedObjectViewIdentity, StringComparison.Ordinal)))
            throw new InvalidOperationException("Protected review source does not match production root configuration.");
        return new ProtectedReviewSourceContext(
            objectView, protectedBaseCommit, sourceAuthorityIdentity);
    }

    public IssuedReviewerCapability IssueCapability(
        ReviewerCapability capability,
        string proofIdentity,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (string.IsNullOrWhiteSpace(proofIdentity) || validUntil <= validFrom ||
            capability.Scopes is null || capability.Scopes.Count == 0 ||
            !capability.Supported || capability.Advisory ||
            (_realm == ReviewAuthorityRealm.Production && capability.Fixture))
            throw new InvalidOperationException("Reviewer capability evidence is incomplete or incompatible with the authority realm.");
        var issued = new IssuedReviewerCapability(
            _issuer,
            _rootIdentity,
            proofIdentity,
            capability,
            _realm,
            validFrom,
            validUntil,
            () => _revokedCapabilities.ContainsKey(proofIdentity));
        if (!_capabilities.TryAdd(proofIdentity, issued))
            throw new InvalidOperationException("Reviewer capability proof identity was already issued and cannot be rebound.");
        return issued;
    }

    public void RevokeCapability(string proofIdentity) =>
        _revokedCapabilities.TryAdd(proofIdentity, 0);

    public RegisteredReviewRequest RegisterRequest(
        string requestIdentity,
        ReviewRequest request,
        ReviewAcceptanceContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(requestIdentity) || _rootContext is null ||
            !MatchesRootContext(_rootContext, context) ||
            !string.Equals(request.Identity.PlanIdentity, context.PlanIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException("Review request does not match the root-frozen authority context.");
        var role = string.Equals(request.Capability.Family, "github-hosted", StringComparison.OrdinalIgnoreCase)
            ? ReviewEvidenceRole.Hosted
            : ReviewEvidenceRole.Independent;
        var registration = new RegisteredReviewRequest(
            _issuer, _rootIdentity, requestIdentity, request, context.RunIdentity,
            context.EvidencePolicyIdentity, context.ReviewPolicyContentIdentity,
            context.SourceAuthorityIdentity, context.ProtectedObjectViewIdentity, role);
        if (!_requests.TryAdd(requestIdentity, registration))
            throw new InvalidOperationException("Review request identity was already registered and cannot be rebound.");
        return registration;
    }

    public AcceptedReviewExecution RecordExecution(
        RegisteredReviewRequest registration,
        IssuedReviewerCapability capability,
        string executionIdentity,
        bool accepted,
        bool completed,
        string rawArtifactHash,
        string rawVerdict,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(capability);
        if (!ReferenceEquals(registration.Issuer, _issuer) ||
            !ReferenceEquals(capability.Issuer, _issuer) ||
            !_capabilities.TryGetValue(capability.ProofIdentity, out var registeredCapability) ||
            !ReferenceEquals(registeredCapability, capability) ||
            !capability.IsCurrent(completedAt) ||
            !_requests.TryGetValue(registration.RequestIdentity, out var current) ||
            !ReferenceEquals(current, registration) ||
            string.IsNullOrWhiteSpace(executionIdentity) ||
            string.IsNullOrWhiteSpace(rawArtifactHash) || string.IsNullOrWhiteSpace(rawVerdict))
            throw new ArgumentException("Accepted reviewer execution evidence is incomplete.");
        return new AcceptedReviewExecution(
            registration,
            capability,
            executionIdentity,
            accepted,
            completed,
            rawArtifactHash,
            rawVerdict,
            completedAt,
            _realm);
    }

    public ReviewConsumerContext CreateConsumerContext(ReviewAcceptanceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_rootContext is null || !MatchesRootContext(_rootContext, context))
            throw new InvalidOperationException("Consumer context does not match the root-frozen authority context.");
        return new ReviewConsumerContext(
            _issuer, context.RunIdentity, context.RequestIdentity, context.PlanIdentity,
            context.EvidencePolicyIdentity, context.ReviewPolicyContentIdentity,
            context.SourceAuthorityIdentity, context.ProtectedObjectViewIdentity);
    }

    public ReviewTransitionReceiver CreateTransitionReceiver(
        ReviewAcceptanceContext independentContext,
        ReviewAcceptanceContext? hostedContext = null)
    {
        ArgumentNullException.ThrowIfNull(independentContext);
        if (_rootContext is null || !MatchesRootContext(_rootContext, independentContext) ||
            (hostedContext is not null && !MatchesRootContext(_rootContext, hostedContext)) ||
            string.IsNullOrWhiteSpace(independentContext.RequestIdentity) ||
            (hostedContext is not null &&
             (string.IsNullOrWhiteSpace(hostedContext.RequestIdentity) ||
              string.Equals(independentContext.RequestIdentity, hostedContext.RequestIdentity, StringComparison.Ordinal))))
            throw new InvalidOperationException("Transition receiver requires distinct root-frozen request bindings.");
        return new ReviewTransitionReceiver(
            Authority,
            _realm,
            CreateConsumerContext(independentContext),
            hostedContext is null ? null : CreateConsumerContext(hostedContext));
    }

    // Explicit fixture-only bridge; production acceptance is never lowered to Test.
    public void EnsureContractFixtureTransition(
        DeliveryForge.Contracts.Models.WorkflowState from,
        DeliveryForge.Contracts.Models.WorkflowState to,
        TransitionEvidence evidence,
        ReviewTransitionReceiver receiver)
    {
        if (_realm != ReviewAuthorityRealm.Test || !ReferenceEquals(receiver.Authority, Authority))
            throw new InvalidOperationException("Fixture transition requires this exact isolated test issuer's pinned receiver.");
        WorkflowTransition.EnsureAllowedForIsolatedTest(from, to, evidence, receiver);
    }

    public void EnsureContractFixtureRootBinding(
        string runIdentity, string planIdentity, string evidencePolicyIdentity, ReviewTransitionReceiver receiver)
    {
        if (_realm != ReviewAuthorityRealm.Test || _rootContext is null ||
            !ReferenceEquals(receiver.Authority, Authority) ||
            runIdentity != _rootContext.RunIdentity || planIdentity != _rootContext.PlanIdentity ||
            evidencePolicyIdentity != _rootContext.EvidencePolicyIdentity)
            throw new InvalidOperationException("Publication fixture binding does not match the immutable review issuer root.");
    }

    public void EnsureContractFixturePredecessorScope(ReviewAuthorityHost predecessor)
    {
        if (predecessor is null || ReferenceEquals(predecessor, this) ||
            _realm != ReviewAuthorityRealm.Test || predecessor._realm != ReviewAuthorityRealm.Test ||
            _rootContext is null || predecessor._rootContext is null ||
            string.IsNullOrWhiteSpace(_rootContext.SourceAuthorityIdentity) ||
            string.IsNullOrWhiteSpace(predecessor._rootContext.SourceAuthorityIdentity) ||
            !Path.IsPathFullyQualified(_rootContext.SourceRoot) || !Path.IsPathFullyQualified(predecessor._rootContext.SourceRoot) ||
            !SamePath(_rootContext.SourceRoot, predecessor._rootContext.SourceRoot) ||
            _rootContext.SourceAuthorityIdentity != predecessor._rootContext.SourceAuthorityIdentity)
            throw new InvalidOperationException("New fixture root requires the predecessor's same owned source boundary and Test realm.");
    }

    // Fixture setup support, not a live policy resolver or production authority path.
    public ReviewPolicy CreateContractFixturePolicy(
        IReadOnlyList<FamilyPair> families,
        IReadOnlyList<string> scopes,
        bool allowExactReuse,
        ReviewBudget budget)
    {
        if (_realm != ReviewAuthorityRealm.Test || _rootContext is null)
            throw new InvalidOperationException("Contract fixture policy requires an isolated root-configured test host.");
        var policy = ReviewPolicy.TrustedFixture(
            _rootContext.ReviewPolicyContentIdentity, families, scopes, allowExactReuse, budget);
        if (policy.ContentIdentity != _rootContext.ReviewPolicyContentIdentity ||
            policy.SourceRevision != _rootContext.ReviewPolicySourceRevision ||
            policy.AuthorityKind != _rootContext.ReviewPolicyAuthorityKind)
            throw new InvalidOperationException("Root policy provenance does not match the pure contract fixture policy.");
        return policy;
    }

    public void IssueAcceptance(
        ValidatedContract receipt,
        NormalizedReview review,
        ReviewAcceptanceContext context,
        ReviewEvidenceRole role)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(context);
        var provenance = review.Provenance;
        if (receipt.SchemaName != "review-receipt" || !Enum.IsDefined(role) ||
            review.Outcome != ReviewOutcome.Pass || !review.AuthorityAccepted ||
            provenance is null || !ReferenceEquals(provenance.Issuer, _issuer) ||
            !ReferenceEquals(provenance.Registration.Issuer, _issuer) ||
            provenance.Registration.Role != role ||
            !ReferenceEquals(provenance.Execution.Registration, provenance.Registration) ||
            !ReferenceEquals(provenance.Execution.Capability, provenance.Capability) ||
            !ReferenceEquals(provenance.Capability.Issuer, _issuer) ||
            !_capabilities.TryGetValue(provenance.Capability.ProofIdentity, out var registeredCapability) ||
            !ReferenceEquals(registeredCapability, provenance.Capability) ||
            !provenance.Capability.IsCurrent(_testNow ?? DateTimeOffset.UtcNow) ||
            review.AuthorityRealm != _realm ||
            string.IsNullOrWhiteSpace(review.CapabilityProofIdentity) ||
            string.IsNullOrWhiteSpace(review.ExecutionIdentity) ||
            string.IsNullOrWhiteSpace(review.ExecutionRequestIdentity) ||
            !string.Equals(review.ExecutionRequestIdentity, context.RequestIdentity, StringComparison.Ordinal) ||
            !string.Equals(review.Identity.PlanIdentity, context.PlanIdentity, StringComparison.Ordinal) ||
            !string.Equals(review.PolicyContentIdentity, context.ReviewPolicyContentIdentity, StringComparison.Ordinal) ||
            !string.Equals(review.PolicySourceRevision, context.ReviewPolicySourceRevision, StringComparison.Ordinal) ||
            review.PolicyAuthorityKind != context.ReviewPolicyAuthorityKind ||
            string.IsNullOrWhiteSpace(context.RunIdentity) ||
            string.IsNullOrWhiteSpace(context.EvidencePolicyIdentity) ||
            string.IsNullOrWhiteSpace(context.SourceAuthorityIdentity) ||
            !Path.IsPathFullyQualified(context.SourceRoot) ||
            !string.Equals(review.Identity.BaseCommit, context.ProtectedBaseCommit, StringComparison.Ordinal))
            throw new InvalidOperationException("Review acceptance does not bind complete issued provenance.");

        if (!review.PolicySourceMatches(
                _issuer, _realm, context.SourceAuthorityIdentity, context.ProtectedObjectViewIdentity))
            throw new InvalidOperationException("Review acceptance policy source does not match the root-frozen realm and protected object view.");

        if (_rootContext is null || !MatchesRootContext(_rootContext, context))
            throw new InvalidOperationException("Review acceptance does not match the root-frozen run, policy, or source authority.");
        if (_production is not null && !review.ProductionAccepted)
            throw new InvalidOperationException("Production review acceptance was not issued from live production evidence.");

        var expectedReceipt = ReviewReceiptSerializer.Create(review);
        var expectedBytes = ReviewReceiptSerializer.Serialize(expectedReceipt);
        var expectedValidated = ContractValidator.ParseAndValidate(expectedBytes);
        if (!string.Equals(receipt.Identity, expectedValidated.Identity, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical receipt is not the exact safe-factory receipt for the issued review.");

        using var document = JsonDocument.Parse(receipt.CanonicalBytes);
        var root = document.RootElement;
        if (root.GetProperty("outcome").GetString() != "PASS" ||
            !string.Equals(root.GetProperty("baseCommit").GetString(), review.Identity.BaseCommit, StringComparison.Ordinal) ||
            !string.Equals(root.GetProperty("headCommit").GetString(), review.Identity.HeadCommit, StringComparison.Ordinal) ||
            !string.Equals(root.GetProperty("treeId").GetString(), review.Identity.HeadTree, StringComparison.Ordinal) ||
            !string.Equals(root.GetProperty("patchSha256").GetString(), review.Identity.PatchSha256, StringComparison.Ordinal) ||
            !string.Equals(root.GetProperty("artifactHash").GetString(), review.RawArtifactHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical receipt does not match the issued review evidence.");

        var entry = new AcceptanceEntry(
            receipt.Identity,
            role,
            _realm,
            review.Identity.BaseCommit,
            review.Identity.HeadCommit,
            review.Identity.HeadTree,
            provenance.Capability,
            context,
            provenance.Execution.ExecutionIdentity);
        if (!_acceptances.TryAdd((receipt.Identity, role), entry))
            throw new InvalidOperationException("Review acceptance identity and role were already issued.");
    }

    public IssuedReviewReuseContext CreateReuseContext(
        ValidatedContract receipt,
        ReviewEvidenceRole role,
        ReviewRequest currentRequest,
        IssuedReviewerCapability capability)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(currentRequest);
        ArgumentNullException.ThrowIfNull(capability);
        if (!_acceptances.TryGetValue((receipt.Identity, role), out var accepted) || accepted.Context is null)
            throw new InvalidOperationException("Review reuse requires a current root acceptance.");
        var requirement = new ReviewAcceptanceRequirement(
            receipt.Identity,
            role,
            _realm,
            currentRequest.Identity.BaseCommit,
            currentRequest.Identity.HeadCommit,
            currentRequest.Identity.HeadTree,
            accepted.Context.EvidencePolicyIdentity,
            CreateConsumerContext(accepted.Context));
        return new IssuedReviewReuseContext(
            _rootIdentity,
            currentRequest,
            capability.ProofIdentity,
            _realm,
            () => Accepts(requirement),
            () => ReferenceEquals(capability.Issuer, _issuer) &&
                  _capabilities.TryGetValue(capability.ProofIdentity, out var registered) &&
                  ReferenceEquals(registered, capability) &&
                  capability.Matches(
                      currentRequest.Capability,
                      currentRequest.Scope,
                      _testNow ?? DateTimeOffset.UtcNow));
    }

    public void IssueTestAcceptance(
        string receiptIdentity,
        ReviewEvidenceRole role,
        ReviewAcceptanceContext context,
        string executionIdentity)
    {
        if (_realm != ReviewAuthorityRealm.Test)
            throw new InvalidOperationException("Test acceptance can only be issued by an isolated test authority.");
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(receiptIdentity) || !Enum.IsDefined(role) ||
            string.IsNullOrWhiteSpace(executionIdentity) || _rootContext is null ||
            !MatchesRootContext(_rootContext, context))
            throw new ArgumentException("A valid receipt identity and review role are required.");
        _acceptances.TryAdd(
            (receiptIdentity, role),
            new AcceptanceEntry(receiptIdentity, role, _realm, "*", "*", "*",
                null, context, executionIdentity));
    }

    private bool Accepts(ReviewAcceptanceRequirement requirement)
    {
        if (requirement.RequiredRealm != _realm ||
            !_acceptances.TryGetValue((requirement.ReceiptIdentity, requirement.Role), out var entry) ||
            entry.Realm != requirement.RequiredRealm ||
            requirement.ConsumerContext is null ||
            !ReferenceEquals(requirement.ConsumerContext.Issuer, _issuer) ||
            entry.Context is null ||
            !string.Equals(requirement.EvidencePolicyIdentity, entry.Context.EvidencePolicyIdentity, StringComparison.Ordinal) ||
            !MatchesConsumer(entry.Context, requirement.ConsumerContext) ||
            (entry.Capability is not null &&
             (!ReferenceEquals(entry.Capability.Issuer, _issuer) ||
              !_capabilities.TryGetValue(entry.Capability.ProofIdentity, out var registered) ||
              !ReferenceEquals(registered, entry.Capability))))
            return false;

        var now = _testNow ?? DateTimeOffset.UtcNow;
        if (requirement.Role == ReviewEvidenceRole.Hosted)
        {
            if (string.IsNullOrWhiteSpace(requirement.OtherReceiptIdentity) ||
                !_acceptances.TryGetValue((requirement.OtherReceiptIdentity, ReviewEvidenceRole.Independent), out var independent) ||
                string.Equals(independent.ExecutionIdentity, entry.ExecutionIdentity, StringComparison.Ordinal))
                return false;
        }
        return (entry.Capability is null || entry.Capability.IsCurrent(now)) &&
               (entry.BaseCommit == "*" || string.Equals(entry.BaseCommit, requirement.BaseCommit, StringComparison.Ordinal)) &&
               (entry.HeadCommit == "*" || string.Equals(entry.HeadCommit, requirement.HeadCommit, StringComparison.Ordinal)) &&
               (entry.TreeId == "*" || string.Equals(entry.TreeId, requirement.TreeId, StringComparison.Ordinal));
    }

    private static bool MatchesRootContext(
        ReviewAuthorityRootContext expected,
        ReviewAcceptanceContext actual) =>
        string.Equals(expected.RunIdentity, actual.RunIdentity, StringComparison.Ordinal) &&
        string.Equals(expected.PlanIdentity, actual.PlanIdentity, StringComparison.Ordinal) &&
        string.Equals(expected.EvidencePolicyIdentity, actual.EvidencePolicyIdentity, StringComparison.Ordinal) &&
        string.Equals(expected.ReviewPolicyContentIdentity, actual.ReviewPolicyContentIdentity, StringComparison.Ordinal) &&
        string.Equals(expected.ReviewPolicySourceRevision, actual.ReviewPolicySourceRevision, StringComparison.Ordinal) &&
        expected.ReviewPolicyAuthorityKind == actual.ReviewPolicyAuthorityKind &&
        string.Equals(expected.SourceAuthorityIdentity, actual.SourceAuthorityIdentity, StringComparison.Ordinal) &&
        SamePath(expected.SourceRoot, actual.SourceRoot) &&
        string.Equals(expected.ProtectedBaseCommit, actual.ProtectedBaseCommit, StringComparison.Ordinal) &&
        string.Equals(expected.ProtectedObjectViewIdentity, actual.ProtectedObjectViewIdentity, StringComparison.Ordinal);

    private static bool MatchesConsumer(ReviewAcceptanceContext expected, ReviewConsumerContext actual) =>
        string.Equals(expected.RunIdentity, actual.RunIdentity, StringComparison.Ordinal) &&
        string.Equals(expected.RequestIdentity, actual.RequestIdentity, StringComparison.Ordinal) &&
        string.Equals(expected.PlanIdentity, actual.PlanIdentity, StringComparison.Ordinal) &&
        string.Equals(expected.EvidencePolicyIdentity, actual.EvidencePolicyIdentity, StringComparison.Ordinal) &&
        string.Equals(expected.ReviewPolicyContentIdentity, actual.ReviewPolicyIdentity, StringComparison.Ordinal) &&
        string.Equals(expected.SourceAuthorityIdentity, actual.ProtectedSourceIdentity, StringComparison.Ordinal) &&
        string.Equals(expected.ProtectedObjectViewIdentity, actual.ProtectedObjectViewIdentity, StringComparison.Ordinal);

    private static string ReadGitVersion(string executable, string workingDirectory)
        => ReadGitOutput(executable, workingDirectory, ["--version"]);

    private static string ReadGitOutput(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Trusted Git executable could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Trusted Git version could not be observed.");
        return output.Trim();
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new InvalidOperationException("Source object directory is unavailable.");
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record AcceptanceEntry(
        string ReceiptIdentity,
        ReviewEvidenceRole Role,
        ReviewAuthorityRealm Realm,
        string BaseCommit,
        string HeadCommit,
        string TreeId,
        IssuedReviewerCapability? Capability,
        ReviewAcceptanceContext? Context,
        string ExecutionIdentity);
}
