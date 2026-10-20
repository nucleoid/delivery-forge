using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;

namespace DeliveryForge.Evidence;

public sealed class GateRunner
{
    internal const int MaximumCapturedBytesPerStream = 1024 * 1024;
    private readonly ICommandExecutor _executor;
    private readonly IRepositoryIdentityReader _repository;
    private readonly string _trustedWorkingDirectory;
    private readonly string _trustedProtectedBaseCommit;
    private readonly DeterministicEvaluationPluginRegistry _plugins;

    public GateRunner(
        ICommandExecutor executor,
        IRepositoryIdentityReader repository,
        string trustedWorkingDirectory,
        string trustedProtectedBaseCommit,
        DeterministicEvaluationPluginRegistry? plugins = null)
    {
        _executor = executor;
        _repository = repository;
        _trustedWorkingDirectory = Path.GetFullPath(trustedWorkingDirectory);
        _trustedProtectedBaseCommit = trustedProtectedBaseCommit;
        _plugins = plugins ?? DeterministicEvaluationPluginRegistry.CreateBuiltIns();
    }
    private static readonly JsonSerializerOptions ContractJson = CreateContractJson();

    public async Task<GateRunResult> RunAsync(GateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var plugin = _plugins.Resolve(request.Adapter, request.Plugin);
        var pluginBinding = DeterministicEvaluationPluginRegistry.Bind(
            request, plugin,
            request.Plugin?.Enabled == false ? EvaluationPluginStatus.Disabled : EvaluationPluginStatus.Selected);
        Directory.CreateDirectory(request.OutputDirectory);
        var claimPath = Path.Combine(request.OutputDirectory, SafeSegment(request.GateId) + ".claim");
        var gateDirectory = Path.Combine(request.OutputDirectory, SafeSegment(request.GateId));
        var gateDirectoryPreexisted = Directory.Exists(gateDirectory);
        try
        {
            await using var claim = new FileStream(
                claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            claim.Flush(true);
        }
        catch (IOException exception)
        {
            throw new EvidenceWriteException($"Gate claim already exists or cannot be created: {exception.Message}");
        }
        if (gateDirectoryPreexisted)
        {
            var failureDirectory = gateDirectory + ".claim-error-" + Guid.NewGuid().ToString("N");
            return await PersistWithoutExecutionAsync(
                request, failureDirectory, request.ExpectedRepository, request.ExpectedRepository,
                NormalizedEvidence.Error("Gate artifact directory existed before the atomic claim."),
                pluginBinding,
                CancellationToken.None).ConfigureAwait(false);
        }

        if (pluginBinding.Enabled && ValidateCapabilityBinding(request, plugin) is { } earlyCapabilityFailure)
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, request.ExpectedRepository, request.ExpectedRepository,
                earlyCapabilityFailure,
                pluginBinding,
                CancellationToken.None).ConfigureAwait(false);

        RepositoryIdentity before;
        try
        {
            before = await _repository.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, request.ExpectedRepository, request.ExpectedRepository,
                NormalizedEvidence.Error($"Repository identity could not be read: {exception.Message}"),
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

        var scopeFailure = await ValidateAutomaticScopeAsync(
            request, plugin, before, CancellationToken.None).ConfigureAwait(false);
        if (scopeFailure is not null)
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before, scopeFailure, pluginBinding,
                CancellationToken.None).ConfigureAwait(false);

        NormalizedEvidence? pluginValidation;
        try
        {
            pluginValidation = plugin.ValidateBeforeExecution(request);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            pluginValidation = NormalizedEvidence.Error(
                $"Plugin configuration or inputs could not be read: {exception.Message}");
        }
        if (pluginValidation is not null)
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before, pluginValidation, pluginBinding,
                CancellationToken.None).ConfigureAwait(false);

        if (plugin is Adapters.AntiSlopEvaluationPlugin &&
            _executor is not ProcessCommandExecutor { SupportsCreationOwnedBoundedCapture: true } &&
            _executor is not IBoundedGateFixtureExecutor { SupportsCreationOwnedBoundedCapture: true })
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before,
                NormalizedEvidence.Error(
                    "Anti-slop requires the code-owned bounded process executor; unbounded or caller-supplied capture cannot produce gate evidence."),
                pluginBinding, CancellationToken.None).ConfigureAwait(false);

        Directory.CreateDirectory(gateDirectory);
        EvaluationPluginSnapshot evidenceSnapshot;
        try
        {
            evidenceSnapshot = plugin.CaptureBefore(request);
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
                var capture = await ownedExecutor.ExecuteBoundedCaptureAsync(
                    request.Invocation, request.Timeout, MaximumCapturedBytesPerStream, cancellationToken)
                    .ConfigureAwait(false);
                execution = capture.Command;
                captureBoundExceeded = capture.CaptureBoundExceeded;
            }
            else if (_executor is IBoundedGateFixtureExecutor { SupportsCreationOwnedBoundedCapture: true } fixtureExecutor)
            {
                var capture = await fixtureExecutor.ExecuteBoundedCaptureAsync(
                    request.Invocation, request.Timeout, MaximumCapturedBytesPerStream, cancellationToken)
                    .ConfigureAwait(false);
                execution = capture.Command;
                captureBoundExceeded = capture.CaptureBoundExceeded;
            }
            else
            {
                // Compatibility for deterministic in-memory/fixture executors. Actual gate tools
                // use the creation-owned ProcessCommandExecutor path above.
                execution = await _executor.ExecuteAsync(request.Invocation, request.Timeout, cancellationToken)
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
            after = await _repository.ReadAsync(CancellationToken.None).ConfigureAwait(false);
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
            normalized = artifactBindingError is null
                ? plugin.Normalize(request, execution, evidenceSnapshot)
                : NormalizedEvidence.Error($"Bound evidence artifact could not be persisted: {artifactBindingError}");
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
            normalized = NormalizedEvidence.Error("Repository identity could not be read after evaluation.");
        else if (sourceChanged)
            normalized = NormalizedEvidence.Error("Repository source/index/status identity changed during evaluation.");
        else if (ValidateCapabilityBinding(request, plugin) is not null)
            normalized = NormalizedEvidence.Error("The bound executable bytes changed during gate execution.");
        else if (plugin.ValidateAfterExecution(request, evidenceSnapshot) is { } postExecutionValidation)
            normalized = postExecutionValidation;
        else if (!execution.OwnedProcessQuiescent)
            normalized = NormalizedEvidence.Error("Owned tool process did not become quiescent after interruption.");
        else if (captureBoundExceeded)
            normalized = NormalizedEvidence.Incomplete(
                $"Tool output exceeded the deterministic {MaximumCapturedBytesPerStream}-byte per-stream capture limit.");
        else if (execution.TimedOut)
            normalized = NormalizedEvidence.Incomplete("Tool timed out before complete evidence was produced.");
        else if (execution.Cancelled)
            normalized = NormalizedEvidence.Incomplete("Tool execution was cancelled before complete evidence was produced.");
        else if (execution.ExitCode is null)
            normalized = NormalizedEvidence.Error("Tool process did not start or did not reach a valid exit.");
        else if (normalized.Outcome == GateOutcome.Pass && execution.ExitCode != 0)
            normalized = NormalizedEvidence.Error("PASS evidence requires an observed zero process exit.");
        else if (normalized.Outcome == GateOutcome.Pass &&
                 (!request.Policy.AuthorityVerified ||
                  !request.Policy.RequiredGates.Contains(request.GateId, StringComparer.Ordinal) ||
                  !normalized.ProductionCapable))
            normalized = NormalizedEvidence.Incomplete(
                "Tool PASS is advisory because protected policy/gate authority or certified producer evidence is absent.");

        var executedPlugin = DeterministicEvaluationPluginRegistry.Bind(
            request, plugin, EvaluationPluginStatus.Executed, OutcomeText(toolOutcome));
        return await PersistAsync(
            request, gateDirectory, before, after, execution, artifactHashes, normalized, sourceChanged,
            executedPlugin, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<GateRunResult> PersistWithoutExecutionAsync(
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

    private static async Task<GateRunResult> PersistAsync(
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
        var provisional = new GateReceiptContract(
            "1.0.0", "gate-receipt", $"sha256:{new string('0', 64)}",
            request.GateId, request.Policy.PolicyIdentity,
            before.BaseCommit, before.HeadCommit, before.TreeId,
            JsonSerializer.Serialize(new
            {
                request.Invocation.FileName,
                request.Invocation.Arguments,
                request.Invocation.WorkingDirectory,
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
                    Status = plugin.Status.ToString().ToUpperInvariant(),
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
                plugin.Enabled, plugin.Status.ToString().ToUpperInvariant(),
                normalized.ProductionCapable,
                normalized.ProductionCapable ? "production" : "advisory"));

        var provisionalBytes = SerializeReceipt(provisional);
        var receipt = provisional with { Identity = CanonicalJson.ComputeIdentity(provisionalBytes) };
        var receiptBytes = SerializeReceipt(receipt);
        var receiptPath = await new AppendOnlyContractStore(Path.Combine(gateDirectory, "receipts"))
            .WriteImmutableAsync(receiptBytes, cancellationToken).ConfigureAwait(false);

        return new GateRunResult(
            normalized.Outcome, normalized.Reason, normalized.Fixture, normalized.ProductionCapable,
            limitations, sourceChanged, request.Invocation, execution, before, after,
            request.Policy.PolicyIdentity, request.Capability.ExecutableIdentity ?? "unavailable", request.ConfigurationIdentity, request.Scope,
            artifactHashes, receiptPath, normalized.NotApplicableRationale)
        {
            Plugin = plugin
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
            request.Invocation.Arguments.Any(argument => argument is null))
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
            return NormalizedEvidence.Error(
                "Protected required gate has no trusted code-owned plugin identity binding.");
        return string.Equals(binding.PluginId, plugin.Id, StringComparison.Ordinal) &&
               string.Equals(binding.PluginVersion, plugin.Version, StringComparison.Ordinal) &&
               string.Equals(binding.Tool, plugin.ExpectedTool, StringComparison.Ordinal) &&
               string.Equals(binding.Tool, request.Capability.Tool, StringComparison.Ordinal)
            ? null
            : NormalizedEvidence.Error(
                "Selected plugin identity/version/tool does not match the protected required-gate binding.");
    }

    private async Task<NormalizedEvidence?> ValidateAutomaticScopeAsync(
        GateRequest request,
        IDeterministicEvaluationPlugin plugin,
        RepositoryIdentity repository,
        CancellationToken cancellationToken)
    {
        if (plugin is not Adapters.AntiSlopEvaluationPlugin)
            return null;
        if (_repository is not IRepositoryEvaluationScopeReader scopeReader)
            return NormalizedEvidence.Error(
                "Anti-slop requires trusted frozen-tree scope and effective-ignore evidence.");
        try
        {
            var scope = await scopeReader.ReadEvaluationScopeAsync(
                repository.HeadCommit, cancellationToken).ConfigureAwait(false);
            var expected = DeterministicEvaluationPluginRegistry.NormalizeInputs(
                plugin.DetectInputs(scope.TrackedFiles, scope.IgnoredFiles));
            var requested = DeterministicEvaluationPluginRegistry.NormalizeInputs(request.Plugin?.Inputs ?? []);
            return expected.SequenceEqual(requested, StringComparer.Ordinal)
                ? null
                : NormalizedEvidence.Incomplete(
                    "Anti-slop requested inputs do not exactly match the complete frozen Git-tree scope after effective ignores.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EvidenceRepositoryException or EvaluationPluginException)
        {
            return NormalizedEvidence.Error(
                $"Anti-slop frozen-tree scope could not be derived: {exception.Message}");
        }
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

    private static JsonSerializerOptions CreateContractJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
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
