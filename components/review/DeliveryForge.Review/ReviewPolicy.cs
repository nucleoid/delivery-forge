using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeliveryForge.Contracts.State;
using DeliveryForge.Evidence;
using System.Diagnostics;

namespace DeliveryForge.Review;

public enum ReviewPolicyAuthorityKind
{
    ProtectedGitBase,
    ParentAdmin,
    WorkerBranch,
    Caller
}

public sealed record ReviewPolicyAuthority(
    ReviewPolicyAuthorityKind Kind,
    string SourceRevision,
    string ContentIdentity);

public sealed record FamilyPair(string Author, string Reviewer);

public sealed record ReviewPolicyCandidate(
    string PolicyIdentity,
    string ContentIdentity,
    string SourceRevision,
    bool Fixture,
    bool Advisory,
    IReadOnlyList<FamilyPair> AllowedFamilies,
    IReadOnlyList<string> AllowedScopes,
    bool AllowExactReuse,
    ReviewBudget Budget,
    IReadOnlyList<string> ExemptionApprovers);

public sealed partial class ReviewPolicy
{
    private ReviewPolicy(
        string policyIdentity,
        string contentIdentity,
        string sourceRevision,
        ReviewPolicyAuthorityKind authorityKind,
        bool trusted,
        bool fixture,
        bool advisory,
        IEnumerable<FamilyPair> allowedFamilies,
        IEnumerable<string> allowedScopes,
        bool allowExactReuse,
        ReviewBudget budget,
        IEnumerable<string> exemptionApprovers,
        object? sourceOwner = null,
        ReviewAuthorityRealm sourceRealm = ReviewAuthorityRealm.Test,
        string sourceAuthorityIdentity = "",
        string protectedObjectViewIdentity = "")
    {
        PolicyIdentity = policyIdentity;
        ContentIdentity = contentIdentity;
        SourceRevision = sourceRevision;
        AuthorityKind = authorityKind;
        Trusted = trusted;
        Fixture = fixture;
        Advisory = advisory;
        AllowedFamilies = Array.AsReadOnly(allowedFamilies.ToArray());
        AllowedScopes = Array.AsReadOnly(allowedScopes.ToArray());
        AllowExactReuse = allowExactReuse;
        Budget = budget;
        ExemptionApprovers = Array.AsReadOnly(exemptionApprovers.ToArray());
        SourceOwner = sourceOwner;
        SourceRealm = sourceRealm;
        SourceAuthorityIdentity = sourceAuthorityIdentity;
        ProtectedObjectViewIdentity = protectedObjectViewIdentity;
    }

    public static ReviewPolicy Missing { get; } = new(
        string.Empty, string.Empty, string.Empty, ReviewPolicyAuthorityKind.Caller,
        false, false, false, [], [], false, ReviewBudget.None, []);

    public string PolicyIdentity { get; }
    public string ContentIdentity { get; }
    public string SourceRevision { get; }
    public ReviewPolicyAuthorityKind AuthorityKind { get; }
    public bool Trusted { get; }
    public bool Fixture { get; }
    public bool Advisory { get; }
    public IReadOnlyList<FamilyPair> AllowedFamilies { get; }
    public IReadOnlyList<string> AllowedScopes { get; }
    public bool AllowExactReuse { get; }
    public ReviewBudget Budget { get; }
    public IReadOnlyList<string> ExemptionApprovers { get; }
    internal object? SourceOwner { get; }
    public ReviewAuthorityRealm SourceRealm { get; }
    public string SourceAuthorityIdentity { get; }
    public string ProtectedObjectViewIdentity { get; }
    internal bool HasSourceProvenance => SourceOwner is not null;

    public static ReviewPolicy Resolve(ReviewPolicyCandidate candidate, ReviewPolicyAuthority authority)
    {
        Validate(candidate);
        // A caller-created authority record is descriptive only. As in #7, verified authority is
        // issued by a resolver that reads the exact protected source, never by this raw factory.
        return Create(candidate, authority.Kind, trusted: false);
    }

    internal static ReviewPolicy TrustedFixture(
        string policyIdentity,
        IReadOnlyList<FamilyPair> allowedFamilies,
        IReadOnlyList<string> allowedScopes,
        bool allowExactReuse,
        ReviewBudget budget)
    {
        var candidate = new ReviewPolicyCandidate(
            policyIdentity, Identity('f'), new string('f', 40), true, false,
            allowedFamilies, allowedScopes, allowExactReuse, budget, []);
        Validate(candidate);
        return Create(candidate, ReviewPolicyAuthorityKind.Caller, trusted: true);
    }

    internal static ReviewPolicy FromVerifiedProtectedSource(
        ReviewPolicyCandidate candidate,
        ProtectedReviewSourceContext source)
    {
        Validate(candidate);
        if (candidate.Fixture || candidate.Advisory)
            throw new ReviewPolicyException("A protected production policy cannot be fixture or advisory.");
        return Create(
            candidate,
            ReviewPolicyAuthorityKind.ProtectedGitBase,
            trusted: true,
            source.ObjectView.QuiescenceAuthority,
            source.ObjectView.SourceRealm,
            source.SourceAuthorityIdentity,
            source.ProtectedObjectViewIdentity);
    }

    private static ReviewPolicy Create(
        ReviewPolicyCandidate candidate,
        ReviewPolicyAuthorityKind authorityKind,
        bool trusted,
        object? sourceOwner = null,
        ReviewAuthorityRealm sourceRealm = ReviewAuthorityRealm.Test,
        string sourceAuthorityIdentity = "",
        string protectedObjectViewIdentity = "") =>
        new(candidate.PolicyIdentity, candidate.ContentIdentity, candidate.SourceRevision, authorityKind,
            trusted, candidate.Fixture, candidate.Advisory, candidate.AllowedFamilies,
            candidate.AllowedScopes, candidate.AllowExactReuse, candidate.Budget,
            candidate.ExemptionApprovers, sourceOwner, sourceRealm, sourceAuthorityIdentity,
            protectedObjectViewIdentity);

    internal bool SourceMatches(
        object issuer,
        ReviewAuthorityRealm realm,
        string sourceAuthorityIdentity,
        string protectedObjectViewIdentity) =>
        SourceOwner is null
            ? realm == ReviewAuthorityRealm.Test
            : ReferenceEquals(SourceOwner, issuer) &&
              SourceRealm == realm &&
              string.Equals(SourceAuthorityIdentity, sourceAuthorityIdentity, StringComparison.Ordinal) &&
              string.Equals(ProtectedObjectViewIdentity, protectedObjectViewIdentity, StringComparison.Ordinal);

    private static void Validate(ReviewPolicyCandidate candidate)
    {
        if (candidate is null ||
            candidate.AllowedFamilies is null ||
            candidate.AllowedScopes is null ||
            candidate.Budget is null ||
            candidate.ExemptionApprovers is null)
            throw new ReviewPolicyException("Review policy is incomplete.");
        if (!IdentityPattern().IsMatch(candidate.PolicyIdentity) ||
            !IdentityPattern().IsMatch(candidate.ContentIdentity) ||
            !GitPattern().IsMatch(candidate.SourceRevision))
            throw new ReviewPolicyException("Review policy identities are malformed.");
        if (candidate.AllowedFamilies.Count == 0 ||
            candidate.AllowedFamilies.Any(pair => pair is null || Unknown(pair.Author) || Unknown(pair.Reviewer) ||
                                                  Same(pair.Author, pair.Reviewer)) ||
            candidate.AllowedFamilies.Distinct().Count() != candidate.AllowedFamilies.Count)
            throw new ReviewPolicyException("Review family policy is empty, duplicated, unknown, or self-reviewing.");
        if (candidate.AllowedScopes.Count == 0 ||
            candidate.AllowedScopes.Any(string.IsNullOrWhiteSpace) ||
            candidate.AllowedScopes.Distinct(StringComparer.Ordinal).Count() != candidate.AllowedScopes.Count)
            throw new ReviewPolicyException("Review scopes are empty, malformed, or duplicated.");
        if (!candidate.Budget.IsFinite)
            throw new ReviewPolicyException("Review policy requires an explicit finite budget.");
        if (candidate.ExemptionApprovers.Any(string.IsNullOrWhiteSpace))
            throw new ReviewPolicyException("Exemption approvers are malformed.");
    }

    internal static bool Unknown(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase);

    internal static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Identity(char value) => $"sha256:{new string(value, 64)}";

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    [GeneratedRegex("^[0-9a-f]{40}(?:[0-9a-f]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitPattern();
}

public sealed class ProtectedReviewSourceContext
{
    internal ProtectedReviewSourceContext(
        ControlledGitEvidenceContext objectView,
        string protectedBaseCommit,
        string sourceAuthorityIdentity)
    {
        ObjectView = objectView;
        ProtectedBaseCommit = protectedBaseCommit;
        SourceAuthorityIdentity = sourceAuthorityIdentity;
    }

    internal ControlledGitEvidenceContext ObjectView { get; }
    public string ProtectedBaseCommit { get; }
    public string SourceAuthorityIdentity { get; }
    public string ProtectedObjectViewIdentity => ObjectView.ProtectedObjectViewIdentity;
}

public sealed class ProtectedReviewPolicyResolver(ProtectedReviewSourceContext sourceContext)
{
    private readonly ProtectedReviewSourceContext _source = sourceContext ??
        throw new ArgumentNullException(nameof(sourceContext));

    // Additive entire-lifetime path. The legacy resolver is not a bounded capture fallback.
    public async Task<ReviewPolicy> ResolveBoundedAsync(string repositoryRelativePath, TimeSpan entireBudget,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (entireBudget <= TimeSpan.Zero || entireBudget > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(entireBudget));
        TimeSpan Remaining()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = entireBudget - Stopwatch.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) throw new TimeoutException("Original protected-policy allowance expired.");
            return left;
        }
        BoundedCommandCaptureResult? lastCapture = null;
        try
        {
            _ = Remaining();
            if (string.IsNullOrWhiteSpace(repositoryRelativePath) || Path.IsPathFullyQualified(repositoryRelativePath) ||
                repositoryRelativePath.Split(['/', '\\']).Any(segment => segment is "" or "." or ".."))
                throw new ReviewPolicyException("A safe repository-relative policy path is required.");
            var view = _source.ObjectView;
            if (view.MaximumEvidenceBytes <= 0 || view.MaximumEvidenceBytes > 16 * 1024 * 1024)
                throw new ReviewPolicyException("Protected resolver byte allowance is unsupported.");
            var git = view.ControlledGitDirectory;
            var objects = Path.Combine(git, "objects");
            RequirePlain(git); RequirePlain(objects);
            if (!Directory.Exists(objects) || File.Exists(Path.Combine(objects, "info", "alternates")) ||
                File.Exists(Path.Combine(git, "info", "grafts")) || File.Exists(Path.Combine(git, "shallow")) || Directory.Exists(Path.Combine(git, "refs", "replace")))
                throw new ReviewPolicyException("Protected object view contains unsupported authority.");
            var config = Path.Combine(git, "config");
            RequirePlain(config);
            using (var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                bound.CancelAfter(Remaining());
                try
                {
                    await using var file = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                    if (file.Length > 1024 * 1024) throw new ReviewPolicyException("Protected configuration exceeds1MiB.");
                    using var captured = new MemoryStream(); var buffer = new byte[4096];
                    while (true)
                    {
                        _ = Remaining(); var count = await file.ReadAsync(buffer.AsMemory(), bound.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        if (captured.Length + count > 1024 * 1024) throw new ReviewPolicyException("Protected configuration grew past1MiB.");
                        captured.Write(buffer, 0, count);
                    }
                    var text = StrictBoundedText(captured.ToArray());
                    if (new[] { "include", "promisor", "partialclone", "alternates", "worktree" }.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase)))
                        throw new ReviewPolicyException("Protected view contains external configuration.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new TimeoutException("Original protected-policy configuration allowance expired."); }
            }
            _ = Remaining(); RequirePlain(config);
            var executor = new ProcessCommandExecutor();
            async Task<byte[]> Git(IReadOnlyList<string> args)
            {
                var prefix = new[] { "--no-replace-objects", $"--git-dir={git}", $"--work-tree={view.SourceRoot}",
                    "-c", "core.fsmonitor=false", "-c", $"core.hooksPath={(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")}" };
                var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
                var environment = new Dictionary<string, string>
                {
                    ["GIT_DIR"] = git,
                    ["GIT_COMMON_DIR"] = git,
                    ["GIT_WORK_TREE"] = view.SourceRoot,
                    ["GIT_OBJECT_DIRECTORY"] = objects,
                    ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "",
                    ["GIT_INDEX_FILE"] = Path.Combine(git, "resolver-unused-index"),
                    ["GIT_CONFIG"] = config,
                    ["GIT_CONFIG_COUNT"] = "0",
                    ["GIT_CONFIG_PARAMETERS"] = "",
                    ["GIT_CONFIG_NOSYSTEM"] = "1",
                    ["GIT_CONFIG_GLOBAL"] = nullDevice,
                    ["GIT_CONFIG_SYSTEM"] = nullDevice,
                    ["GIT_GRAFT_FILE"] = nullDevice,
                    ["GIT_NO_REPLACE_OBJECTS"] = "1",
                    ["GIT_NO_LAZY_FETCH"] = "1",
                    ["GIT_TERMINAL_PROMPT"] = "0"
                };
                var invocation = new CommandInvocation(view.TrustedGitExecutable, prefix.Concat(args).ToArray(), view.SourceRoot, environment);
                var allowance = Remaining();
                // An unexpected new dispatch exception must not borrow a previous command's settled observation.
                lastCapture = null;
                var capture = await executor.ExecuteBoundedCaptureAsync(invocation,
                    allowance, checked((int)view.MaximumEvidenceBytes), cancellationToken).ConfigureAwait(false);
                lastCapture = capture; // Preserve actual physical uncertainty BEFORE choosing cancellation/timeout semantics.
                if (cancellationToken.IsCancellationRequested || capture.Command.Cancelled || capture.Command.TimedOut ||
                    capture.CaptureBoundExceeded || capture.Command.ExitCode != 0 || !capture.Command.OwnedProcessQuiescent ||
                    !capture.RootExitObserved || !capture.StandardOutputDrainCompleted || !capture.StandardErrorDrainCompleted)
                    throw ProtectedPolicyReadFailure.Create(capture, cancellationToken,
                        "Protected-policy command refused; actual capture/settlement observations retained.");
                _ = Remaining(); RequirePlain(git); RequirePlain(objects); RequirePlain(config);
                return capture.StandardOutputBytes;
            }
            var exactBase = StrictBoundedText(await Git(["rev-parse", "--verify", $"{_source.ProtectedBaseCommit}^{{commit}}"])).Trim();
            if (exactBase != _source.ProtectedBaseCommit) throw new ReviewPolicyException("Protected base did not resolve exactly.");
            var objectId = StrictBoundedText(await Git(["rev-parse", $"{_source.ProtectedBaseCommit}:{repositoryRelativePath.Replace('\\', '/')}"])).Trim();
            var type = StrictBoundedText(await Git(["cat-file", "-t", objectId])).Trim();
            var sizeText = StrictBoundedText(await Git(["cat-file", "-s", objectId])).Trim();
            if (type != "blob" || !long.TryParse(sizeText, out var size) || size < 0 || size > view.MaximumEvidenceBytes)
                throw new ReviewPolicyException("Protected policy is not a bounded blob.");
            var bytes = await Git(["cat-file", "blob", objectId]);
            if (bytes.LongLength != size) throw new ReviewPolicyException("Protected policy blob size changed.");
            ReviewPolicyFile filePolicy;
            try
            {
                filePolicy = JsonSerializer.Deserialize<ReviewPolicyFile>(StrictBoundedText(bytes), new JsonSerializerOptions
                { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false })
                    ?? throw new ReviewPolicyException("Protected review policy is empty.");
            }
            catch (JsonException exception) { throw new ReviewPolicyException("Protected review policy is malformed.", exception); }
            _ = Remaining();
            var identity = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            var result = ReviewPolicy.FromVerifiedProtectedSource(new(identity, identity, _source.ProtectedBaseCommit, filePolicy.Fixture,
                filePolicy.Advisory, filePolicy.AllowedFamilies, filePolicy.AllowedScopes, filePolicy.AllowExactReuse, filePolicy.Budget, filePolicy.ExemptionApprovers), _source);
            _ = Remaining(); return result;
        }
        catch (Exception failure) when (lastCapture is not null &&
            failure is (OperationCanceledException or TimeoutException or ReviewPolicyException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException))
        {
            if (failure.InnerException is ProtectedPolicyCaptureDiagnosticException) throw;
            throw ProtectedPolicyReadFailure.Attach(failure, lastCapture!, cancellationToken);
        }
    }

    private static void RequirePlain(string path)
    {
        for (FileSystemInfo? current = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path); current is not null;
            current = current is DirectoryInfo directory ? directory.Parent : ((FileInfo)current).Directory)
            if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new ReviewPolicyException("Protected resolver path is redirected.");
    }
    private static string StrictBoundedText(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new ReviewPolicyException("Protected policy capture is not strict UTF8.", exception); }
    }

    public async Task<ReviewPolicy> ResolveAsync(
        string repositoryRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (Path.IsPathFullyQualified(repositoryRelativePath) ||
            repositoryRelativePath.Split(['/', '\\']).Any(segment => segment is "" or "." or ".."))
            throw new ReviewPolicyException("A safe repository-relative policy path is required.");

        ValidateObjectView();
        var exactBase = await GitTextAsync(["rev-parse", "--verify", $"{_source.ProtectedBaseCommit}^{{commit}}"], cancellationToken);
        if (!string.Equals(exactBase.Trim(), _source.ProtectedBaseCommit, StringComparison.Ordinal))
            throw new ReviewPolicyException("Git did not resolve the exact protected review-policy base.");

        var objectId = (await GitTextAsync(
            ["rev-parse", $"{_source.ProtectedBaseCommit}:{repositoryRelativePath.Replace('\\', '/')}"], cancellationToken)).Trim();
        var objectType = (await GitTextAsync(["cat-file", "-t", objectId], cancellationToken)).Trim();
        var objectSizeText = (await GitTextAsync(["cat-file", "-s", objectId], cancellationToken)).Trim();
        if (objectType != "blob" || !long.TryParse(objectSizeText, out var objectSize) ||
            objectSize < 0 || objectSize > _source.ObjectView.MaximumEvidenceBytes)
            throw new ReviewPolicyException("Protected review policy object is not a bounded blob.");
        var bytes = await GitBytesAsync(["cat-file", "blob", objectId], cancellationToken);
        ReviewPolicyFile candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<ReviewPolicyFile>(
                bytes,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = false,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                    AllowDuplicateProperties = false
                }) ?? throw new ReviewPolicyException("Protected review policy is empty.");
        }
        catch (JsonException error)
        {
            throw new ReviewPolicyException("Protected review policy is malformed.", error);
        }

        var contentIdentity = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        var resolved = new ReviewPolicyCandidate(
            contentIdentity, contentIdentity, _source.ProtectedBaseCommit, candidate.Fixture,
            candidate.Advisory, candidate.AllowedFamilies, candidate.AllowedScopes,
            candidate.AllowExactReuse, candidate.Budget, candidate.ExemptionApprovers);
        return ReviewPolicy.FromVerifiedProtectedSource(resolved, _source);
    }

    private async Task<string> GitTextAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        Encoding.UTF8.GetString(await GitBytesAsync(args, cancellationToken));

    private async Task<byte[]> GitBytesAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var prefix = new[]
        {
            "--no-replace-objects",
            $"--git-dir={_source.ObjectView.ControlledGitDirectory}",
            $"--work-tree={_source.ObjectView.SourceRoot}",
            "-c", "core.fsmonitor=false",
            "-c", $"core.hooksPath={(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")}"
        };
        var result = await GitProcess.RunAsync(
            _source.ObjectView.SourceRoot,
            prefix.Concat(args).ToArray(),
            cancellationToken,
            _source.ObjectView.TrustedGitExecutable,
            _source.ObjectView.MaximumEvidenceBytes);
        if (result.ExitCode != 0)
            throw new ReviewPolicyException($"Protected review policy Git read failed: git {string.Join(" ", args)}.");
        return result.StandardOutput;
    }

    private void ValidateObjectView()
    {
        var git = _source.ObjectView.ControlledGitDirectory;
        var objects = Path.Combine(git, "objects");
        if (!Directory.Exists(objects) ||
            File.Exists(Path.Combine(objects, "info", "alternates")) ||
            File.Exists(Path.Combine(git, "info", "grafts")) ||
            File.Exists(Path.Combine(git, "shallow")) ||
            Directory.Exists(Path.Combine(git, "refs", "replace")))
            throw new ReviewPolicyException("Protected object view contains replacement, graft, alternate, or incomplete authority.");
        var config = Path.Combine(git, "config");
        var text = File.Exists(config) ? File.ReadAllText(config) : string.Empty;
        if (new[] { "include", "promisor", "partialclone", "alternates", "worktree" }
            .Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase)))
            throw new ReviewPolicyException("Protected object view contains external configuration authority.");
    }

    private sealed class ReviewPolicyFile
    {
        public required bool Fixture { get; init; }
        public required bool Advisory { get; init; }
        public required IReadOnlyList<FamilyPair> AllowedFamilies { get; init; }
        public required IReadOnlyList<string> AllowedScopes { get; init; }
        public required bool AllowExactReuse { get; init; }
        public required ReviewBudget Budget { get; init; }
        public required IReadOnlyList<string> ExemptionApprovers { get; init; }
    }
}

public sealed class ReviewPolicyException(string message, Exception? inner = null) : Exception(message, inner);
