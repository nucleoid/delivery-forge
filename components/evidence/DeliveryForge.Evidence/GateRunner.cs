using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;

namespace DeliveryForge.Evidence;

public sealed class GateRunner(ICommandExecutor executor, IRepositoryIdentityReader repository)
{
    private static readonly JsonSerializerOptions ContractJson = CreateContractJson();

    public async Task<GateRunResult> RunAsync(GateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        Directory.CreateDirectory(request.OutputDirectory);
        var claimPath = Path.Combine(request.OutputDirectory, SafeSegment(request.GateId) + ".claim");
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
        var gateDirectory = Path.Combine(request.OutputDirectory, SafeSegment(request.GateId));

        var before = await repository.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!before.IsClean || before != request.ExpectedRepository)
        {
            return await PersistWithoutExecutionAsync(
                request, gateDirectory, before, before,
                NormalizedEvidence.Error("Repository identity is not the expected clean committed base/head/tree."),
                cancellationToken).ConfigureAwait(false);
        }

        Directory.CreateDirectory(gateDirectory);
        var execution = await executor.ExecuteAsync(request.Invocation, request.Timeout, cancellationToken)
            .ConfigureAwait(false);
        var artifactHashes = await WriteArtifactsAsync(gateDirectory, execution, CancellationToken.None).ConfigureAwait(false);
        var after = await repository.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        var sourceChanged = before != after || !after.IsClean;

        NormalizedEvidence normalized;
        try
        {
            normalized = request.Normalize(execution);
        }
        catch (OperationCanceledException)
        {
            normalized = NormalizedEvidence.Incomplete("Evidence normalization was cancelled.");
        }
        catch (Exception exception)
        {
            normalized = NormalizedEvidence.Error($"Evidence normalizer failed: {exception.Message}");
        }
        if (sourceChanged)
            normalized = NormalizedEvidence.Error("Repository source/index/status identity changed during evaluation.");
        else if (execution.TimedOut)
            normalized = NormalizedEvidence.Incomplete("Tool timed out before complete evidence was produced.");
        else if (execution.Cancelled)
            normalized = NormalizedEvidence.Incomplete("Tool execution was cancelled before complete evidence was produced.");
        else if (normalized.Outcome == GateOutcome.Pass && execution.ExitCode != 0)
            normalized = NormalizedEvidence.Error("PASS evidence requires an observed zero process exit.");
        else if (normalized.Outcome == GateOutcome.Pass &&
                 (!request.Policy.AuthorityVerified ||
                  !request.Policy.RequiredGates.Contains(request.GateId, StringComparer.Ordinal) ||
                  !normalized.ProductionCapable))
            normalized = NormalizedEvidence.Incomplete(
                "Tool PASS is advisory because protected policy/gate authority or certified producer evidence is absent.");

        return await PersistAsync(
            request, gateDirectory, before, after, execution, artifactHashes, normalized, sourceChanged, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<GateRunResult> PersistWithoutExecutionAsync(
        GateRequest request,
        string gateDirectory,
        RepositoryIdentity before,
        RepositoryIdentity after,
        NormalizedEvidence normalized,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(gateDirectory);
        var now = DateTimeOffset.UtcNow;
        var execution = new CommandResult(null, string.Empty, string.Empty, false, false, now, now);
        var hashes = await WriteArtifactsAsync(gateDirectory, execution, cancellationToken).ConfigureAwait(false);
        return await PersistAsync(
            request, gateDirectory, before, after, execution, hashes, normalized, true, cancellationToken)
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
        CancellationToken cancellationToken)
    {
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
                request.Scope,
                request.CapabilityIdentity,
                request.ConfigurationIdentity
            }, ContractJson),
            execution.StartedAt, execution.CompletedAt, execution.ExitCode,
            OutcomeText(normalized.Outcome), normalized.Reason, artifactHashes,
            sourceChanged, normalized.Limitations, normalized.NotApplicableRationale);

        var provisionalBytes = SerializeReceipt(provisional);
        var receipt = provisional with { Identity = CanonicalJson.ComputeIdentity(provisionalBytes) };
        var receiptBytes = SerializeReceipt(receipt);
        var receiptPath = await new AppendOnlyContractStore(Path.Combine(gateDirectory, "receipts"))
            .WriteImmutableAsync(receiptBytes, cancellationToken).ConfigureAwait(false);

        return new GateRunResult(
            normalized.Outcome, normalized.Reason, normalized.Fixture, normalized.ProductionCapable,
            normalized.Limitations, sourceChanged, request.Invocation, execution, before, after,
            request.Policy.PolicyIdentity, request.CapabilityIdentity, request.ConfigurationIdentity, request.Scope,
            artifactHashes, receiptPath, normalized.NotApplicableRationale);
    }

    private static async Task<IReadOnlyDictionary<string, string>> WriteArtifactsAsync(
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

    private static void ValidateRequest(GateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.GateId) ||
            string.IsNullOrWhiteSpace(request.Scope) ||
            request.Timeout <= TimeSpan.Zero ||
            request.Timeout > TimeSpan.FromHours(1))
            throw new ArgumentException("Gate id, scope, and a finite positive timeout up to one hour are required.");
        if (!Path.IsPathFullyQualified(request.OutputDirectory) ||
            !Path.IsPathFullyQualified(request.Invocation.WorkingDirectory))
            throw new ArgumentException("Gate output and command working directories must be absolute.");
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
