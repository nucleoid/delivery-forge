using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Evidence;

public sealed class GateRunner
{
    internal const int AntiSlopMaximumCapturedBytesPerStream = 1024 * 1024;
    internal const int ExistingGateMaximumCapturedBytesPerStream = 64 * 1024 * 1024;
    private readonly ICommandExecutor _executor;
    private readonly IRepositoryIdentityReader _repository;
    private readonly string _trustedWorkingDirectory;
    private readonly string _trustedProtectedBaseCommit;
    private readonly DeterministicEvaluationPluginRegistry _plugins;
    private readonly bool _emitAuthoritativeV2;
    private readonly HostOwnedReceiptStore? _authoritativeReceiptStore;

    public GateRunner(
        ICommandExecutor executor,
        IRepositoryIdentityReader repository,
        string trustedWorkingDirectory,
        string trustedProtectedBaseCommit,
        DeterministicEvaluationPluginRegistry? plugins = null,
        bool emitAuthoritativeV2 = false,
        TrustedGatePluginRegistry? authorityRegistry = null,
        HostOwnedReceiptStore? authoritativeReceiptStore = null)
    {
        _executor = executor;
        _repository = repository;
        _trustedWorkingDirectory = Path.GetFullPath(trustedWorkingDirectory);
        _trustedProtectedBaseCommit = trustedProtectedBaseCommit;
        _plugins = plugins ?? DeterministicEvaluationPluginRegistry.CreateBuiltIns();
        _plugins.AssertMatchesAuthorityRegistry(authorityRegistry ?? TrustedGatePluginRegistry.BuiltIns);
        _emitAuthoritativeV2 = emitAuthoritativeV2;
        _authoritativeReceiptStore = authoritativeReceiptStore;
        if (_emitAuthoritativeV2 && _authoritativeReceiptStore is null)
            throw new ArgumentException("Authoritative v2 emission requires a host-owned receipt store.");
    }
    private static readonly JsonSerializerOptions ContractJson = CreateContractJson();

    public async Task<GateRunResult> RunAsync(GateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var deadline = DateTimeOffset.UtcNow + request.Timeout;
        var plugin = _plugins.Resolve(request.Adapter, request.Plugin);
        var pluginBinding = DeterministicEvaluationPluginRegistry.Bind(
            request, plugin,
            request.Plugin?.Enabled == false ? EvaluationPluginStatus.Disabled : EvaluationPluginStatus.Selected);
        Directory.CreateDirectory(request.OutputDirectory);
        var claimPath = Path.Combine(request.OutputDirectory, SafeSegment(request.GateId) + ".claim");
        var gateDirectory = Path.Combine(request.OutputDirectory, SafeSegment(request.GateId));
        var gateDirectoryPreexisted = Directory.Exists(gateDirectory);
        GateClaimAcquisition claim;
        try
        {
            claim = GateClaimLease.Acquire(claimPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EvidenceWriteException)
        {
            claim = new(null, $"Gate claim could not be established: {exception.Message}", null);
        }
        if (claim.Lease is null)
            return await PersistWithoutExecutionAsync(
                request, gateDirectory + ".claim-incomplete-" + Guid.NewGuid().ToString("N"),
                request.ExpectedRepository, request.ExpectedRepository,
                NormalizedEvidence.Incomplete(claim.UnavailableReason ?? "Gate claim is unavailable."),
                pluginBinding, CancellationToken.None).ConfigureAwait(false);
        await using var claimLease = claim.Lease;
        if (claim.InterruptedClaimPath is not null)
            return await PersistWithoutExecutionAsync(
                request, gateDirectory + ".interrupted-" + Guid.NewGuid().ToString("N"),
                request.ExpectedRepository, request.ExpectedRepository,
                NormalizedEvidence.Incomplete(
                    "Recovered a stale gate claim from an interrupted owner; retry is permitted after this durable receipt."),
                pluginBinding, CancellationToken.None).ConfigureAwait(false);
        if (gateDirectoryPreexisted)
        {
            var priorReceipts = Path.Combine(gateDirectory, "receipts");
            var completedAttempt = Directory.Exists(priorReceipts) &&
                    Directory.EnumerateFiles(priorReceipts, "*.json").Any() ||
                File.Exists(Path.Combine(gateDirectory, "stdout.txt")) &&
                    File.Exists(Path.Combine(gateDirectory, "stderr.txt"));
            if (!completedAttempt)
            {
                var failureDirectory = gateDirectory + ".claim-incomplete-" + Guid.NewGuid().ToString("N");
                return await PersistWithoutExecutionAsync(
                    request, failureDirectory, request.ExpectedRepository, request.ExpectedRepository,
                    NormalizedEvidence.Incomplete("Gate artifact directory was left incomplete before the atomic claim."),
                    pluginBinding, CancellationToken.None).ConfigureAwait(false);
            }
            gateDirectory += ".retry-" + Guid.NewGuid().ToString("N");
        }

        if (plugin is not Adapters.AntiSlopEvaluationPlugin && pluginBinding.Enabled &&
            ValidateCapabilityBinding(request, plugin) is { } nonScopeCapabilityFailure)
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, request.ExpectedRepository, request.ExpectedRepository,
                nonScopeCapabilityFailure, pluginBinding, CancellationToken.None).ConfigureAwait(false);

        RepositoryIdentity before;
        try
        {
            before = await ReadRepositoryAsync(Remaining(deadline), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, request.ExpectedRepository, request.ExpectedRepository,
                NormalizedEvidence.Incomplete(
                    $"Repository identity read was cancelled or exceeded the gate deadline: {exception.Message}"),
                pluginBinding, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, request.ExpectedRepository, request.ExpectedRepository,
                NormalizedEvidence.Incomplete($"Repository identity could not be read: {exception.Message}"),
                pluginBinding,
                CancellationToken.None).ConfigureAwait(false);
        }
        if (!before.IsClean || before != request.ExpectedRepository)
        {
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before,
                NormalizedEvidence.Error("Repository identity is not the expected clean committed base/head/tree."),
                pluginBinding,
                CancellationToken.None).ConfigureAwait(false);
        }
        if (!pluginBinding.Enabled)
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before,
                NormalizedEvidence.Incomplete(
                    "The selected deterministic evaluation plugin is disabled; no tool outcome exists and the gate is unsatisfied."),
                pluginBinding,
                CancellationToken.None).ConfigureAwait(false);

        if (request.Policy.AuthorityVerified &&
            (!string.Equals(request.Policy.SourceRevision, before.BaseCommit, StringComparison.Ordinal) ||
             !string.Equals(request.Policy.SourceRevision, _trustedProtectedBaseCommit, StringComparison.Ordinal) ||
             !string.Equals(request.Policy.SourceRepositoryRoot, _trustedWorkingDirectory,
                 OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before,
                NormalizedEvidence.Error(
                    "Protected policy authority is not bound to this gate's frozen repository base and root."),
                pluginBinding,
                CancellationToken.None).ConfigureAwait(false);
        }

        var requiredGateBindingFailure = ValidateRequiredGateBinding(request, plugin);
        if (requiredGateBindingFailure is not null)
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before, requiredGateBindingFailure, pluginBinding,
                CancellationToken.None).ConfigureAwait(false);

        AutomaticScopeValidation scopeValidation;
        try
        {
            scopeValidation = await ValidateAutomaticScopeAsync(
                request, plugin, before, Remaining(deadline), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before,
                NormalizedEvidence.Incomplete(
                    $"Anti-slop scope derivation was cancelled or exceeded the gate deadline: {exception.Message}"),
                pluginBinding, CancellationToken.None).ConfigureAwait(false);
        }
        if (scopeValidation.InputSetIdentity is not null)
            pluginBinding = pluginBinding with { InputSetIdentity = scopeValidation.InputSetIdentity };
        if (scopeValidation.MaterializedRoot is not null)
            request = request with
            {
                Invocation = request.Invocation with { WorkingDirectory = scopeValidation.MaterializedRoot }
            };
        using var materializationLease = new FrozenMaterializationLease(scopeValidation.MaterializedRoot);
        if (scopeValidation.Failure is not null)
        {
            if (scopeValidation.Failure.Outcome == GateOutcome.NotApplicable)
            {
                pluginBinding = pluginBinding with { Status = EvaluationPluginStatus.NotApplicable };
                var authorityFailure = ValidateAuthoritativeNoExecution(request, plugin);
                if (authorityFailure is not null)
                    return await PersistWithoutExecutionAsync(
                        request, gateDirectory, before, before, authorityFailure, pluginBinding,
                        CancellationToken.None).ConfigureAwait(false);
            }
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before, scopeValidation.Failure, pluginBinding,
                CancellationToken.None).ConfigureAwait(false);
        }

        if (plugin is Adapters.AntiSlopEvaluationPlugin && pluginBinding.Enabled &&
            ValidateCapabilityBinding(request, plugin) is { } earlyCapabilityFailure)
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before, earlyCapabilityFailure,
                pluginBinding, CancellationToken.None).ConfigureAwait(false);

        if (pluginBinding.Enabled && !request.Capability.Fixture && request.Policy.AuthorityVerified &&
            request.Policy.RequiredGates.Contains(request.GateId, StringComparer.Ordinal) &&
            !HasTrustedProductionExecution())
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before,
                NormalizedEvidence.Incomplete(
                    "A required production gate requires code-owned bounded process and Git readers; caller-supplied execution is advisory only."),
                pluginBinding, CancellationToken.None).ConfigureAwait(false);

        NormalizedEvidence? pluginValidation;
        try
        {
            _ = Remaining(deadline);
            pluginValidation = plugin.ValidateBeforeExecution(request);
            _ = Remaining(deadline);
        }
        catch (OperationCanceledException exception)
        {
            pluginValidation = NormalizedEvidence.Incomplete(
                $"Plugin validation exceeded the finite gate deadline: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            pluginValidation = NormalizedEvidence.Incomplete(
                $"Plugin configuration or inputs could not be read: {exception.Message}");
        }
        if (pluginValidation is not null)
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before, pluginValidation, pluginBinding,
                CancellationToken.None).ConfigureAwait(false);

        Directory.CreateDirectory(gateDirectory);
        EvaluationPluginSnapshot evidenceSnapshot;
        try
        {
            _ = Remaining(deadline);
            evidenceSnapshot = plugin.CaptureBefore(request);
            _ = Remaining(deadline);
        }
        catch (OperationCanceledException exception)
        {
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before,
                NormalizedEvidence.Incomplete(
                    $"Evidence snapshot exceeded the finite gate deadline: {exception.Message}"),
                pluginBinding, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before,
                plugin is Adapters.AntiSlopEvaluationPlugin
                    ? NormalizedEvidence.Incomplete(
                        $"Anti-slop configuration or runtime closure became unavailable before execution: {exception.Message}")
                    : NormalizedEvidence.Error($"Evidence inputs could not be snapshotted: {exception.Message}"),
                pluginBinding,
                CancellationToken.None).ConfigureAwait(false);
        }

        CommandResult execution;
        var captureBoundExceeded = false;
        try
        {
            if (_executor is ProcessCommandExecutor { SupportsCreationOwnedBoundedCapture: true } ownedExecutor)
            {
                var captureLimit = plugin is Adapters.AntiSlopEvaluationPlugin
                    ? AntiSlopMaximumCapturedBytesPerStream
                    : ExistingGateMaximumCapturedBytesPerStream;
                var capture = await ownedExecutor.ExecuteBoundedCaptureAsync(
                    request.Invocation, Remaining(deadline), captureLimit, cancellationToken)
                    .ConfigureAwait(false);
                execution = capture.Command;
                captureBoundExceeded = capture.CaptureBoundExceeded;
            }
            else if (_executor is IBoundedGateFixtureExecutor { SupportsCreationOwnedBoundedCapture: true } fixtureExecutor)
            {
                var captureLimit = plugin is Adapters.AntiSlopEvaluationPlugin
                    ? AntiSlopMaximumCapturedBytesPerStream
                    : ExistingGateMaximumCapturedBytesPerStream;
                var capture = await fixtureExecutor.ExecuteBoundedCaptureAsync(
                    request.Invocation, Remaining(deadline), captureLimit, cancellationToken)
                    .ConfigureAwait(false);
                execution = capture.Command;
                captureBoundExceeded = capture.CaptureBoundExceeded;
            }
            else
            {
                // Compatibility for deterministic in-memory/fixture executors. Actual gate tools
                // use the creation-owned ProcessCommandExecutor path above.
                execution = await _executor.ExecuteAsync(request.Invocation, Remaining(deadline), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            var now = DateTimeOffset.UtcNow;
            execution = new CommandResult(
                null, string.Empty, "Command execution was cancelled.", false, true, now, now,
                OwnedProcessQuiescent: false);
        }
        catch (Exception exception)
        {
            var now = DateTimeOffset.UtcNow;
            execution = new CommandResult(
                null, string.Empty, $"Command execution failed: {exception.Message}", false, false, now, now,
                OwnedProcessQuiescent: false);
        }
        var artifactHashes = await WriteArtifactsAsync(gateDirectory, execution, CancellationToken.None).ConfigureAwait(false);
        string? artifactBindingError = null;
        try
        {
            plugin.AppendBoundArtifacts(
                request, evidenceSnapshot, gateDirectory, artifactHashes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            artifactBindingError = exception.Message;
        }
        RepositoryIdentity after;
        var identityReadFailed = false;
        try
        {
            using var finalReadDeadline = new CancellationTokenSource(Remaining(deadline));
            after = await _repository.ReadAsync(finalReadDeadline.Token).ConfigureAwait(false);
        }
        catch
        {
            after = before;
            identityReadFailed = true;
        }
        var sourceChanged = identityReadFailed || before != after || !after.IsClean;

        NormalizedEvidence normalized;
        try
        {
            _ = Remaining(deadline);
            normalized = artifactBindingError is null
                ? plugin.Normalize(request, execution, evidenceSnapshot)
                : NormalizedEvidence.Incomplete($"Bound evidence artifact could not be persisted: {artifactBindingError}");
            _ = Remaining(deadline);
        }
        catch (OperationCanceledException)
        {
            normalized = NormalizedEvidence.Incomplete("Evidence normalization was cancelled.");
        }
        catch (Exception exception)
        {
            normalized = NormalizedEvidence.Error($"Evidence normalizer failed: {exception.Message}");
        }
        var toolOutcome = normalized.Outcome;
        if (identityReadFailed)
            normalized = NormalizedEvidence.Incomplete("Repository identity could not be read after evaluation.");
        else if (sourceChanged)
            normalized = NormalizedEvidence.Error("Repository source/index/status identity changed during evaluation.");
        else if (ValidateCapabilityBinding(request, plugin) is not null)
            normalized = NormalizedEvidence.Error("The bound executable bytes changed during gate execution.");
        else if (plugin.ValidateAfterExecution(request, evidenceSnapshot) is { } postExecutionValidation)
            normalized = postExecutionValidation;
        else if (!execution.OwnedProcessQuiescent)
            normalized = NormalizedEvidence.Incomplete(
                plugin is Adapters.AntiSlopEvaluationPlugin
                    ? "Anti-slop process failed to start or settle; no complete tool evidence exists."
                    : "Owned tool process did not become quiescent after interruption.");
        else if (captureBoundExceeded)
            normalized = NormalizedEvidence.Incomplete(
                "Tool output exceeded its deterministic per-plugin per-stream capture limit; no truncated output can authorize PASS.");
        else if (execution.TimedOut)
            normalized = NormalizedEvidence.Incomplete("Tool timed out before complete evidence was produced.");
        else if (execution.Cancelled)
            normalized = NormalizedEvidence.Incomplete("Tool execution was cancelled before complete evidence was produced.");
        else if (execution.ExitCode is null)
            normalized = NormalizedEvidence.Incomplete("Tool process did not start or did not reach a valid exit.");
        else if (normalized.Outcome == GateOutcome.Pass && execution.ExitCode != 0)
            normalized = NormalizedEvidence.Error("PASS evidence requires an observed zero process exit.");
        else if (normalized.Outcome == GateOutcome.Pass &&
                 (!request.Policy.AuthorityVerified ||
                  !request.Policy.RequiredGates.Contains(request.GateId, StringComparer.Ordinal) ||
                  !normalized.ProductionCapable))
            normalized = NormalizedEvidence.Incomplete(
                "Tool PASS is advisory because protected policy/gate authority or certified producer evidence is absent.");
        else if (normalized.Outcome == GateOutcome.NotApplicable &&
                 (string.IsNullOrWhiteSpace(normalized.NotApplicableRationale) ||
                  !request.Policy.AllowedNotApplicable.Contains(request.GateId) ||
                  !request.Policy.AuthorityVerified ||
                  !request.Policy.RequiredGates.Contains(request.GateId, StringComparer.Ordinal) ||
                  request.Capability.Fixture ||
                  !normalized.ProductionCapable))
            normalized = NormalizedEvidence.Incomplete(
                "Tool NOT_APPLICABLE is advisory unless policy permits it and rationale, protected binding, non-fixture capability, and certified producer evidence are complete.");

        var executedPlugin = DeterministicEvaluationPluginRegistry.Bind(
            request, plugin,
            toolOutcome == GateOutcome.NotApplicable
                ? EvaluationPluginStatus.NotApplicable
                : EvaluationPluginStatus.Executed,
            OutcomeText(toolOutcome));
        if (scopeValidation.InputSetIdentity is not null)
            executedPlugin = executedPlugin with { InputSetIdentity = scopeValidation.InputSetIdentity };
        return await PersistAsync(
            request, gateDirectory, before, after, execution, artifactHashes, normalized, sourceChanged,
            executedPlugin, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<GateRunResult> PersistWithoutExecutionAsync(
        GateRequest request,
        string gateDirectory,
        RepositoryIdentity before,
        RepositoryIdentity after,
        NormalizedEvidence normalized,
        EvaluationPluginReceiptMetadata plugin,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(gateDirectory);
        var now = DateTimeOffset.UtcNow;
        var execution = new CommandResult(null, string.Empty, string.Empty, false, false, now, now);
        var hashes = await WriteArtifactsAsync(gateDirectory, execution, cancellationToken).ConfigureAwait(false);
        return await PersistAsync(
            request, gateDirectory, before, after, execution, hashes, normalized,
            before != after || !before.IsClean || !after.IsClean,
            plugin, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GateRunResult> PersistAsync(
        GateRequest request,
        string gateDirectory,
        RepositoryIdentity before,
        RepositoryIdentity after,
        CommandResult execution,
        IReadOnlyDictionary<string, string> artifactHashes,
        NormalizedEvidence normalized,
        bool sourceChanged,
        EvaluationPluginReceiptMetadata plugin,
        CancellationToken cancellationToken)
    {
        var limitations = normalized.Limitations
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).ToArray();
        var provisional = new AuthoritativeGateReceiptContract(
            "2.0.0", "gate-receipt", $"sha256:{new string('0', 64)}",
            request.GateId, request.Policy.PolicyIdentity,
            before.BaseCommit, before.HeadCommit, before.TreeId,
            JsonSerializer.Serialize(new
            {
                request.Invocation.FileName,
                request.Invocation.Arguments,
                WorkingDirectory = plugin.Id == "dmmulroy.anti-slop"
                    ? "<host-owned-frozen-inputs>"
                    : request.Invocation.WorkingDirectory,
                Environment = request.Invocation.Environment ?? new Dictionary<string, string>(),
                request.Invocation.InheritEnvironment,
                request.Scope,
                request.Capability.ExecutableIdentity,
                CapabilityTool = request.Capability.Tool,
                CapabilityVersion = request.Capability.Version,
                CapabilityFormat = request.Capability.FormatVersion,
                CapabilityExecutable = request.Capability.ExecutablePath,
                CapabilityFixture = request.Capability.Fixture,
                CapabilityProbeWorkingDirectory = request.Capability.ProbeWorkingDirectory,
                PolicySourceRevision = request.Policy.SourceRevision,
                PolicyContentIdentity = request.Policy.ContentIdentity,
                request.ConfigurationIdentity,
                Plugin = new
                {
                    plugin.Id,
                    plugin.Version,
                    plugin.Enabled,
                    Status = PluginStatusText(plugin.Status),
                    plugin.ConfigurationIdentity,
                    plugin.Inputs,
                    plugin.InputSetIdentity,
                    plugin.ToolOutcome,
                    EffectRulesEnabled = request.Plugin?.EnableEffectRules ?? false
                }
            }, ContractJson),
            execution.StartedAt, execution.CompletedAt, execution.ExitCode,
            OutcomeText(normalized.Outcome), normalized.Reason, artifactHashes,
            sourceChanged, limitations, normalized.NotApplicableRationale,
            new GateAuthorityBindingContract(
                request.GateId, request.Policy.PolicyIdentity, before.HeadCommit,
                plugin.Id, plugin.Version, request.Capability.Tool, request.Capability.Version,
                request.Capability.ExecutableIdentity ?? "sha256:" + new string('0', 64),
                request.ConfigurationIdentity, plugin.InputSetIdentity,
                plugin.Enabled, PluginStatusText(plugin.Status),
                normalized.ProductionCapable,
                normalized.ProductionCapable ? "production" : "advisory"));

        byte[] receiptBytes;
        if (_emitAuthoritativeV2)
        {
            var provisionalBytes = SerializeReceipt(provisional);
            var receipt = provisional with { Identity = CanonicalJson.ComputeIdentity(provisionalBytes) };
            receiptBytes = SerializeReceipt(receipt);
        }
        else
        {
            var legacy = new GateReceiptContract(
                "1.0.0", provisional.Kind, provisional.Identity, provisional.GateId,
                provisional.PolicyIdentity, provisional.BaseCommit, provisional.HeadCommit,
                provisional.TreeId, provisional.Command, provisional.StartedAt, provisional.CompletedAt,
                provisional.ExitCode, provisional.Outcome, provisional.Reason, provisional.ArtifactHashes,
                provisional.SourceChanged, provisional.Limitations, provisional.NotApplicableRationale);
            var provisionalBytes = SerializeReceipt(legacy);
            legacy = legacy with { Identity = CanonicalJson.ComputeIdentity(provisionalBytes) };
            receiptBytes = SerializeReceipt(legacy);
        }
        string receiptPath;
        if (_emitAuthoritativeV2)
        {
            var validated = ContractValidator.ParseAndValidate(receiptBytes);
            receiptPath = _authoritativeReceiptStore!.Append(validated);
        }
        else
        {
            receiptPath = await new AppendOnlyContractStore(Path.Combine(gateDirectory, "receipts"))
                .WriteImmutableAsync(receiptBytes, cancellationToken).ConfigureAwait(false);
        }

        return new GateRunResult(
            normalized.Outcome, normalized.Reason, normalized.Fixture, normalized.ProductionCapable,
            limitations, sourceChanged, request.Invocation, execution, before, after,
            request.Policy.PolicyIdentity, request.Capability.ExecutableIdentity ?? "unavailable", request.ConfigurationIdentity, request.Scope,
            artifactHashes, receiptPath, normalized.NotApplicableRationale)
        {
            Plugin = plugin,
            TrustedReceiptProvenance = _emitAuthoritativeV2
                ? _authoritativeReceiptStore!.Provenance
                : null
        };
    }

    private static async Task<Dictionary<string, string>> WriteArtifactsAsync(
        string directory,
        CommandResult result,
        CancellationToken cancellationToken)
    {
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, content) in new[] { ("stdout.txt", result.StandardOutput), ("stderr.txt", result.StandardError) })
        {
            var path = Path.Combine(directory, name);
            var bytes = Encoding.UTF8.GetBytes(content);
            await using var stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
            artifacts[name] = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        }

        return artifacts;
    }

    private void ValidateRequest(GateRequest request)
    {
        if (!string.Equals(Path.GetFullPath(request.Invocation.WorkingDirectory), _trustedWorkingDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The invocation working directory is not the parent-frozen host root.");

        if (string.IsNullOrWhiteSpace(request.GateId) ||
            string.IsNullOrWhiteSpace(request.Scope) ||
            request.Timeout <= TimeSpan.Zero ||
            request.Timeout > TimeSpan.FromHours(1))
            throw new ArgumentException("Gate id, scope, and a finite positive timeout up to one hour are required.");
        if (!Path.IsPathFullyQualified(request.OutputDirectory) ||
            !Path.IsPathFullyQualified(request.Invocation.WorkingDirectory))
            throw new ArgumentException("Gate output and command working directories must be absolute.");
        var output = Path.GetFullPath(request.OutputDirectory);
        var trustedPrefix = _trustedWorkingDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _trustedWorkingDirectory
            : _trustedWorkingDirectory + Path.DirectorySeparatorChar;
        if (string.Equals(output, _trustedWorkingDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            output.StartsWith(trustedPrefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Gate output directory must be outside the parent-frozen working root.");
        if (Directory.Exists(output) && File.GetAttributes(output).HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("Gate output directory cannot be a reparse point.");

        if (string.IsNullOrWhiteSpace(request.Invocation.FileName) ||
            request.Invocation.Arguments.Any(argument => argument is null) ||
            request.Invocation.StandardInput is not null)
            throw new ArgumentException("Executable and immutable argument list are required.");
    }

    private static string SafeSegment(string value)
    {
        if (value.Any(character => Path.GetInvalidFileNameChars().Contains(character)) ||
            value is "." or "..")
            throw new ArgumentException("Gate id is not a safe artifact path segment.");
        return value;
    }


    private NormalizedEvidence? ValidateCapabilityBinding(
        GateRequest request,
        IDeterministicEvaluationPlugin plugin)
    {
        var expectedTool = plugin.ExpectedTool;
        if (!string.Equals(expectedTool, request.Capability.Tool, StringComparison.Ordinal))
            return NormalizedEvidence.Error("The selected adapter does not match the detected tool capability.");
        if (!request.Capability.DetectionVerified)
            return NormalizedEvidence.Error("The selected capability was not issued by the capability detector.");

        if (!request.Capability.Supported)
            return NormalizedEvidence.Incomplete(
                "The selected adapter has no detected supported executable capability.",
                request.Capability.Fixture,
                request.Capability.Limitations?.ToArray() ?? []);
        if (string.IsNullOrWhiteSpace(request.Capability.ExecutablePath) ||
            !Path.IsPathFullyQualified(request.Capability.ExecutablePath) ||
            string.IsNullOrWhiteSpace(request.Capability.ExecutableIdentity))
            return NormalizedEvidence.Error("Detected capability lacks an absolute executable path or byte identity.");
        if (string.IsNullOrWhiteSpace(request.Capability.ProbeWorkingDirectory) ||
            !string.Equals(Path.GetFullPath(request.Capability.ProbeWorkingDirectory), _trustedWorkingDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return NormalizedEvidence.Error(
                "The capability probe root does not match the parent-frozen gate working root.");

        string expected;
        string actual;
        try
        {
            expected = Path.GetFullPath(request.Capability.ExecutablePath);
            actual = Path.GetFullPath(request.Invocation.FileName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return NormalizedEvidence.Error("The capability or invocation executable path is malformed.");
        }
        if (!string.Equals(expected, actual, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            return NormalizedEvidence.Error("The invocation executable is not the detected capability executable.");

        try
        {
            var identity = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(expected)))}";
            return string.Equals(identity, request.Capability.ExecutableIdentity, StringComparison.Ordinal)
                ? null
                : NormalizedEvidence.Error("The detected executable bytes do not match the bound capability identity.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return NormalizedEvidence.Error("The bound capability executable cannot be read.");
        }
    }

    private static NormalizedEvidence? ValidateRequiredGateBinding(
        GateRequest request,
        IDeterministicEvaluationPlugin plugin)
    {
        if (!request.Policy.AuthorityVerified ||
            !request.Policy.RequiredGates.Contains(request.GateId, StringComparer.Ordinal))
            return null;
        var binding = request.Policy.RequiredGatePluginBindings.SingleOrDefault(candidate =>
            string.Equals(candidate.GateId, request.GateId, StringComparison.Ordinal));
        if (binding is null)
            return NormalizedEvidence.Incomplete(
                "Protected required gate has no trusted code-owned plugin identity binding and is unsatisfied.");
        return string.Equals(binding.PluginId, plugin.Id, StringComparison.Ordinal) &&
               string.Equals(binding.PluginVersion, plugin.Version, StringComparison.Ordinal) &&
               string.Equals(binding.Tool, plugin.ExpectedTool, StringComparison.Ordinal) &&
               string.Equals(binding.Tool, request.Capability.Tool, StringComparison.Ordinal)
            ? null
            : NormalizedEvidence.Error(
                "Selected plugin identity/version/tool does not match the protected required-gate binding.");
    }

    private NormalizedEvidence? ValidateAuthoritativeNoExecution(
        GateRequest request,
        IDeterministicEvaluationPlugin plugin)
    {
        if (ValidateCapabilityBinding(request, plugin) is { } capabilityFailure)
            return capabilityFailure;
        NormalizedEvidence? pluginAuthorityFailure;
        try
        {
            pluginAuthorityFailure = plugin.ValidateAuthorityWithoutExecution(request);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            pluginAuthorityFailure = NormalizedEvidence.Incomplete(
                $"No-execution plugin authority could not be verified: {exception.Message}");
        }
        if (pluginAuthorityFailure is not null)
            return pluginAuthorityFailure;
        if (!request.Policy.AuthorityVerified ||
            !request.Policy.RequiredGates.Contains(request.GateId, StringComparer.Ordinal) ||
            request.Capability.Fixture || !HasTrustedProductionExecution() ||
            !_emitAuthoritativeV2 || _authoritativeReceiptStore is null)
            return NormalizedEvidence.Incomplete(
                "NOT_APPLICABLE is advisory unless the same trusted production capability, required-gate, code-owned execution, and durable v2 provenance bindings as PASS are present.");
        return null;
    }

    private async Task<AutomaticScopeValidation> ValidateAutomaticScopeAsync(
        GateRequest request,
        IDeterministicEvaluationPlugin plugin,
        RepositoryIdentity repository,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (plugin is not Adapters.AntiSlopEvaluationPlugin)
            return new(null, null, null);
        if (_repository is not IRepositoryEvaluationScopeReader scopeReader)
            return new(NormalizedEvidence.Incomplete(
                "Anti-slop requires trusted frozen-tree scope and blob evidence."), null, null);
        try
        {
            var scope = await scopeReader.ReadEvaluationScopeAsync(
                repository.HeadCommit, timeout, cancellationToken).ConfigureAwait(false);
            var expected = DeterministicEvaluationPluginRegistry.NormalizeInputs(
                plugin.DetectInputs(scope.TrackedFiles, scope.IgnoredFiles));
            var requested = DeterministicEvaluationPluginRegistry.NormalizeInputs(request.Plugin?.Inputs ?? []);
            if (scope.InvalidInputs is { Count: > 0 })
                return new(NormalizedEvidence.Incomplete(
                    $"Anti-slop frozen scope contains non-regular JavaScript-family paths: {string.Join(", ", scope.InvalidInputs)}."),
                    scope.InputSetIdentity, scope.MaterializedRoot);
            if (!expected.SequenceEqual(requested, StringComparer.Ordinal))
                return new(NormalizedEvidence.Incomplete(
                    "Anti-slop requested inputs do not exactly match protected-base scope over the frozen Git tree."),
                    scope.InputSetIdentity, scope.MaterializedRoot);
            if (scope.SuppressionViolations is { Count: > 0 })
                return new(NormalizedEvidence.Incomplete(
                    $"Anti-slop frozen inputs add or change inline suppression directives: {string.Join(", ", scope.SuppressionViolations)}."),
                    scope.InputSetIdentity, scope.MaterializedRoot);
            if (expected.Count == 0)
            {
                if (!request.Policy.AllowedNotApplicable.Contains(request.GateId))
                    return new(NormalizedEvidence.Incomplete(
                        "Anti-slop selected zero inputs and protected policy does not permit NOT_APPLICABLE."),
                        scope.InputSetIdentity, scope.MaterializedRoot);
                return new(new NormalizedEvidence(
                        GateOutcome.NotApplicable,
                        "Protected anti-slop scope contains no applicable JavaScript-family files.",
                        false,
                        true,
                        [],
                        "The frozen tracked tree contains no non-ignored regular JavaScript-family files."),
                    scope.InputSetIdentity, scope.MaterializedRoot);
            }
            return new(null, scope.InputSetIdentity, scope.MaterializedRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EvidenceRepositoryException or EvaluationPluginException)
        {
            return new(NormalizedEvidence.Incomplete(
                $"Anti-slop frozen-tree scope could not be derived: {exception.Message}"), null, null);
        }
    }

    private sealed record AutomaticScopeValidation(
        NormalizedEvidence? Failure,
        string? InputSetIdentity,
        string? MaterializedRoot);

    private Task<RepositoryIdentity> ReadRepositoryAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        _repository is IDeadlineRepositoryIdentityReader bounded
            ? bounded.ReadAsync(timeout, cancellationToken)
            : ReadFixtureRepositoryAsync(timeout, cancellationToken);

    private async Task<RepositoryIdentity> ReadFixtureRepositoryAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return await _repository.ReadAsync(deadline.Token).ConfigureAwait(false);
    }

    private sealed class FrozenMaterializationLease(string? root) : IDisposable
    {
        public void Dispose() => GitRepositoryIdentityReader.DeleteMaterialization(root);
    }

    private static string OutcomeText(GateOutcome outcome) => outcome switch
    {
        GateOutcome.Pass => "PASS",
        GateOutcome.Fail => "FAIL",
        GateOutcome.Incomplete => "INCOMPLETE",
        GateOutcome.Error => "ERROR",
        GateOutcome.NotApplicable => "NOT_APPLICABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static string PluginStatusText(EvaluationPluginStatus status) => status switch
    {
        EvaluationPluginStatus.Selected => "SELECTED",
        EvaluationPluginStatus.Disabled => "DISABLED",
        EvaluationPluginStatus.Executed => "EXECUTED",
        EvaluationPluginStatus.NotApplicable => "NOT_APPLICABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private bool HasTrustedProductionExecution()
    {
        var process = _executor is ProcessCommandExecutor { SupportsCreationOwnedBoundedCapture: true } ||
            _executor is IBoundedGateFixtureExecutor { SupportsCreationOwnedBoundedCapture: true };
        var repository = _repository is GitRepositoryIdentityReader { SupportsCreationOwnedBoundedRead: true } ||
            _repository is IBoundedRepositoryFixtureReader { SupportsCreationOwnedBoundedRead: true };
        return process && repository;
    }

    private static TimeSpan Remaining(DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new OperationCanceledException("The finite gate deadline expired.");
        return remaining;
    }

    private static JsonSerializerOptions CreateContractJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    private static byte[] SerializeReceipt(AuthoritativeGateReceiptContract receipt)
    {
        var node = JsonSerializer.SerializeToNode(receipt, ContractJson)?.AsObject()
            ?? throw new InvalidOperationException("Gate receipt serialization produced no object.");
        if (receipt.NotApplicableRationale is null)
            node.Remove("notApplicableRationale");
        return JsonSerializer.SerializeToUtf8Bytes(node, ContractJson);
    }

    private static byte[] SerializeReceipt(GateReceiptContract receipt)
    {
        var node = JsonSerializer.SerializeToNode(receipt, ContractJson)?.AsObject()
            ?? throw new InvalidOperationException("Gate receipt serialization produced no object.");
        if (receipt.NotApplicableRationale is null)
            node.Remove("notApplicableRationale");
        return JsonSerializer.SerializeToUtf8Bytes(node, ContractJson);
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (!DateTimeOffset.TryParse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
                throw new JsonException("Expected a UTC timestamp.");
            return parsed.ToUniversalTime();
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                System.Globalization.CultureInfo.InvariantCulture));
    }
}
