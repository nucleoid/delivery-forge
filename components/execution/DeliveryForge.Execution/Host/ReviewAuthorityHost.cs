using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
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
    string ProtectedBaseCommit);

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
    string ProtectedBaseCommit);

public sealed class ReviewAuthorityHost
{
    private readonly ReviewAuthorityRealm _realm;
    private readonly string _rootIdentity;
    private readonly DateTimeOffset? _testNow;
    private readonly ProductionReviewAuthorityConfiguration? _production;
    private readonly ReviewAuthorityRootContext? _rootContext;
    private readonly ConcurrentDictionary<string, byte> _revokedCapabilities = new(StringComparer.Ordinal);
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
        return new ControlledGitEvidenceContext(
            sourceRoot, workerGitDirectory, controlledGitDirectory, trustedGitExecutable,
            expectedGitVersion, maximumEvidenceBytes, sourceAuthorityIdentity);
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
        var controlled = Path.Combine(Path.GetTempPath(), "delivery-forge-controlled-git", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(controlled, "objects"));
        Directory.CreateDirectory(Path.Combine(controlled, "refs"));
        File.WriteAllText(Path.Combine(controlled, "HEAD"), "ref: refs/heads/controlled\n");
        File.WriteAllText(Path.Combine(controlled, "config"), "[core]\n\trepositoryformatversion = 0\n\tbare = true\n\tfilemode = true\n");
        CopyDirectory(Path.Combine(workerGitDirectory, "objects"), Path.Combine(controlled, "objects"));
        var version = ReadGitVersion(trustedGitExecutable, sourceRoot);
        return new ControlledGitEvidenceContext(
            sourceRoot, workerGitDirectory, controlled, trustedGitExecutable, version,
            maximumEvidenceBytes, _rootIdentity,
            () => { if (Directory.Exists(controlled)) Directory.Delete(controlled, recursive: true); },
            beforeFinalRecheck);
    }

    public ProtectedReviewSourceContext FreezeProtectedSource(
        string sourceRoot,
        string protectedBaseCommit,
        string sourceAuthorityIdentity,
        string trustedGitExecutable)
    {
        if (!Path.IsPathFullyQualified(sourceRoot) || !Directory.Exists(sourceRoot) ||
            string.IsNullOrWhiteSpace(protectedBaseCommit) || string.IsNullOrWhiteSpace(sourceAuthorityIdentity) ||
            !Path.IsPathFullyQualified(trustedGitExecutable) || !File.Exists(trustedGitExecutable))
            throw new ArgumentException("Protected review source context requires root-issued absolute source and Git identities.");
        if (_production is not null &&
            (!SamePath(_production.Context.SourceRoot, sourceRoot) ||
             !string.Equals(_production.Context.ProtectedBaseCommit, protectedBaseCommit, StringComparison.Ordinal) ||
             !string.Equals(_production.Context.SourceAuthorityIdentity, sourceAuthorityIdentity, StringComparison.Ordinal)))
            throw new InvalidOperationException("Protected review source does not match production root configuration.");
        return new ProtectedReviewSourceContext(
            sourceRoot, protectedBaseCommit, sourceAuthorityIdentity, trustedGitExecutable);
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
        return new IssuedReviewerCapability(
            _rootIdentity,
            proofIdentity,
            capability,
            _realm,
            validFrom,
            validUntil,
            () => _revokedCapabilities.ContainsKey(proofIdentity));
    }

    public void RevokeCapability(string proofIdentity) =>
        _revokedCapabilities.TryAdd(proofIdentity, 0);

    public AcceptedReviewExecution RecordExecution(
        string requestIdentity,
        string executionIdentity,
        bool accepted,
        bool completed,
        string rawArtifactHash,
        string rawVerdict,
        DateTimeOffset completedAt)
    {
        if (string.IsNullOrWhiteSpace(requestIdentity) || string.IsNullOrWhiteSpace(executionIdentity) ||
            string.IsNullOrWhiteSpace(rawArtifactHash) || string.IsNullOrWhiteSpace(rawVerdict))
            throw new ArgumentException("Accepted reviewer execution evidence is incomplete.");
        return new AcceptedReviewExecution(
            _rootIdentity,
            requestIdentity,
            executionIdentity,
            accepted,
            completed,
            rawArtifactHash,
            rawVerdict,
            completedAt,
            _realm);
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
        if (receipt.SchemaName != "review-receipt" || !Enum.IsDefined(role) ||
            review.Outcome != ReviewOutcome.Pass || !review.AuthorityAccepted ||
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

        if (_rootContext is null || !MatchesRootContext(_rootContext, context))
            throw new InvalidOperationException("Review acceptance does not match the root-frozen run, policy, or source authority.");
        if (_production is not null && !review.ProductionAccepted)
            throw new InvalidOperationException("Production review acceptance was not issued from live production evidence.");

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
            review.CapabilityProofIdentity,
            review.CapabilityValidUntil,
            context);
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
        var requirement = new ReviewAcceptanceRequirement(
            receipt.Identity,
            role,
            _realm,
            currentRequest.Identity.BaseCommit,
            currentRequest.Identity.HeadCommit,
            currentRequest.Identity.HeadTree);
        return new IssuedReviewReuseContext(
            _rootIdentity,
            currentRequest,
            capability.ProofIdentity,
            _realm,
            () => Accepts(requirement),
            () => string.Equals(capability.RootIdentity, _rootIdentity, StringComparison.Ordinal) &&
                  capability.Matches(
                      currentRequest.Capability,
                      currentRequest.Scope,
                      _testNow ?? DateTimeOffset.UtcNow));
    }

    public void IssueTestAcceptance(string receiptIdentity, ReviewEvidenceRole role)
    {
        if (_realm != ReviewAuthorityRealm.Test)
            throw new InvalidOperationException("Test acceptance can only be issued by an isolated test authority.");
        if (string.IsNullOrWhiteSpace(receiptIdentity) || !Enum.IsDefined(role))
            throw new ArgumentException("A valid receipt identity and review role are required.");
        _acceptances.TryAdd(
            (receiptIdentity, role),
            new AcceptanceEntry(receiptIdentity, role, _realm, "*", "*", "*", string.Empty,
                DateTimeOffset.MaxValue, null));
    }

    private bool Accepts(ReviewAcceptanceRequirement requirement)
    {
        if (requirement.RequiredRealm != _realm ||
            !_acceptances.TryGetValue((requirement.ReceiptIdentity, requirement.Role), out var entry) ||
            entry.Realm != requirement.RequiredRealm ||
            (!string.IsNullOrEmpty(entry.CapabilityProofIdentity) &&
             _revokedCapabilities.ContainsKey(entry.CapabilityProofIdentity)))
            return false;

        var now = _testNow ?? DateTimeOffset.UtcNow;
        return entry.CapabilityValidUntil >= now &&
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
        string.Equals(expected.ProtectedBaseCommit, actual.ProtectedBaseCommit, StringComparison.Ordinal);

    private static string ReadGitVersion(string executable, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "--version")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Trusted Git executable could not be started.");
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
        string CapabilityProofIdentity,
        DateTimeOffset CapabilityValidUntil,
        ReviewAcceptanceContext? Context);
}
