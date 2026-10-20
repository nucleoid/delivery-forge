using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Evidence.Adapters;

public sealed class AntiSlopEvaluationPlugin : IDeterministicEvaluationPlugin
{
    public const string UpstreamCommit = "13909c9e07590f5a85d93c62712259fe14a883f7";
    private static readonly HashSet<string> SupportedExtensions = new(
        [".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".mts", ".cts"],
        StringComparer.OrdinalIgnoreCase);

    private AntiSlopEvaluationPlugin()
    {
    }

    public string Id => "dmmulroy.anti-slop";
    public string Version => $"0.1.2+{UpstreamCommit}";
    public string ExpectedTool => "oxlint";
    public EvidenceAdapterKind CompatibilityAdapter => EvidenceAdapterKind.External;

    public static AntiSlopEvaluationPlugin CreateBundled() => new();

    public static bool IsSupportedInput(string path) =>
        !string.IsNullOrWhiteSpace(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    public IReadOnlyList<string> DetectInputs(
        IReadOnlyList<string> candidateInputs,
        IReadOnlySet<string> ignoredInputs) =>
        DetectSupportedInputs(candidateInputs, ignoredInputs);

    public static IReadOnlyList<string> DetectSupportedInputs(
        IReadOnlyList<string> candidateInputs,
        IReadOnlySet<string>? ignoredInputs = null)
    {
        var candidates = DeterministicEvaluationPluginRegistry.NormalizeInputs(candidateInputs);
        var ignored = ignoredInputs is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : DeterministicEvaluationPluginRegistry.NormalizeInputs(ignoredInputs.ToArray())
                .ToHashSet(StringComparer.Ordinal);
        return candidates
            .Where(IsSupportedInput)
            .Where(input => !ignored.Contains(input))
            .ToArray();
    }

    public EvaluationPluginSnapshot CaptureBefore(GateRequest request)
    {
        var inputs = DeterministicEvaluationPluginRegistry.NormalizeInputs(request.Plugin?.Inputs ?? []);
        return new EvaluationPluginSnapshot(new AntiSlopSnapshot(
            inputs,
            ComputeConfigurationIdentity(request.Invocation, request.Plugin?.EnableEffectRules == true)));
    }

    public NormalizedEvidence? ValidateBeforeExecution(GateRequest request)
    {
        var inputs = DeterministicEvaluationPluginRegistry.NormalizeInputs(request.Plugin?.Inputs ?? []);
        if (inputs.Count == 0)
            return NormalizedEvidence.Incomplete("Anti-slop selected zero JavaScript-family inputs.");
        var unsupported = inputs.Where(input => !IsSupportedInput(input)).ToArray();
        if (unsupported.Length > 0)
            return NormalizedEvidence.Incomplete(
                $"Anti-slop input selection contains unsupported paths: {string.Join(", ", unsupported)}.");

        var arguments = request.Invocation.Arguments;
        if (arguments.Any(argument => argument.StartsWith("--fix", StringComparison.Ordinal)))
            return NormalizedEvidence.Incomplete("Anti-slop never permits Oxlint autofix options.");
        if (!string.IsNullOrWhiteSpace(EffectiveEnvironment(request.Invocation, "NODE_OPTIONS")) ||
            !string.IsNullOrWhiteSpace(EffectiveEnvironment(request.Invocation, "NODE_PATH")))
            return NormalizedEvidence.Incomplete(
                "Anti-slop does not permit NODE_OPTIONS or NODE_PATH in the execution environment.");

        if (arguments.Count != inputs.Count + 4 ||
            arguments[0] != "--config" ||
            arguments[2] != "--format" ||
            arguments[3] != "json")
            return NormalizedEvidence.Incomplete(
                "Anti-slop requires the canonical read-only invocation: --config <absolute bundled config> --format json <frozen inputs>.");

        var invocationInputs = arguments
            .Skip(4)
            .Select(input => input.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!inputs.SequenceEqual(invocationInputs, StringComparer.Ordinal))
            return NormalizedEvidence.Incomplete(
                "Anti-slop invocation inputs do not exactly match the frozen plugin input selection.");

        var expectedConfig = request.Plugin?.EnableEffectRules == true
            ? "oxlint.effect.config.ts"
            : "oxlint.config.ts";
        var configPath = arguments[1];
        if (!Path.IsPathFullyQualified(configPath) ||
            !string.Equals(Path.GetFileName(configPath), expectedConfig, StringComparison.Ordinal))
            return NormalizedEvidence.Incomplete(
                $"Anti-slop invocation is not bound to an absolute {expectedConfig} configuration.");
        string configIdentity;
        try
        {
            configIdentity = ComputeConfigurationIdentity(
                request.Invocation,
                request.Plugin?.EnableEffectRules == true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return NormalizedEvidence.Incomplete(
                $"Anti-slop configuration closure is unavailable: {exception.Message}");
        }
        if (!string.Equals(configIdentity, request.ConfigurationIdentity, StringComparison.Ordinal))
            return NormalizedEvidence.Incomplete(
                "Anti-slop configuration closure does not match the frozen configuration identity.");

        return null;
    }

    public NormalizedEvidence? ValidateAfterExecution(
        GateRequest request,
        EvaluationPluginSnapshot snapshot)
    {
        if (snapshot.State is not AntiSlopSnapshot state)
            return NormalizedEvidence.Error("Anti-slop snapshot is missing or mismatched.");
        try
        {
            var after = ComputeConfigurationIdentity(
                request.Invocation,
                request.Plugin?.EnableEffectRules == true);
            return string.Equals(after, state.ConfigurationIdentity, StringComparison.Ordinal)
                ? null
                : NormalizedEvidence.Error("Anti-slop configuration or runtime closure changed during evaluation.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return NormalizedEvidence.Error(
                $"Anti-slop configuration or runtime closure became unavailable: {exception.Message}");
        }
    }

    public NormalizedEvidence Normalize(
        GateRequest request,
        CommandResult execution,
        EvaluationPluginSnapshot snapshot)
    {
        var state = snapshot.State as AntiSlopSnapshot;
        if (state is null)
            return NormalizedEvidence.Error("Anti-slop snapshot is missing or mismatched.");
        return NormalizeReport(
            System.Text.Encoding.UTF8.GetBytes(execution.StandardOutput),
            state.Inputs,
            execution.ExitCode ?? -1);
    }

    public void AppendBoundArtifacts(
        GateRequest request,
        EvaluationPluginSnapshot snapshot,
        string gateDirectory,
        IDictionary<string, string> artifactHashes)
    {
    }

    public NormalizedEvidence NormalizeReport(
        ReadOnlySpan<byte> report,
        IReadOnlyList<string> analyzedInputs,
        int processExitCode)
    {
        if (analyzedInputs.Count == 0)
            return NormalizedEvidence.Incomplete("Anti-slop analyzed zero JavaScript-family inputs.");
        if (analyzedInputs.Any(input => !IsSupportedInput(input)))
            return NormalizedEvidence.Incomplete("Anti-slop analysis included an unsupported input type.");

        try
        {
            using var document = JsonDocument.Parse(report.ToArray());
            var reportData = ReadReport(document.RootElement);
            if (reportData is null)
                return NormalizedEvidence.Incomplete("Oxlint JSON report shape is unsupported or incomplete.");
            if (reportData.AnalyzedFileCount != analyzedInputs.Count || reportData.RuleCount <= 0)
                return NormalizedEvidence.Incomplete(
                    "Oxlint report does not prove configured analysis of the complete frozen input set.");
            var findings = reportData.Findings;
            if (findings.Any(finding => finding.IsParserOrToolDiagnostic))
                return NormalizedEvidence.Incomplete("Oxlint reported a parser, configuration, or tool diagnostic.");
            if (findings.Any(finding => finding.File is not null &&
                !analyzedInputs.Contains(NormalizeReportedPath(finding.File), StringComparer.Ordinal)))
                return NormalizedEvidence.Incomplete("Oxlint report contains a finding outside the frozen input set.");
            if (findings.Count > 0)
            {
                return new NormalizedEvidence(
                    GateOutcome.Fail,
                    $"Anti-slop reported {findings.Count} deterministic finding(s).",
                    false,
                    true,
                    []);
            }
            if (processExitCode != 0)
                return NormalizedEvidence.Incomplete(
                    "Oxlint exited non-zero without complete policy findings; parser or tool failure is possible.");
            return new NormalizedEvidence(
                GateOutcome.Pass,
                $"Anti-slop completed cleanly for {analyzedInputs.Count} JavaScript-family input(s).",
                false,
                false,
                ["Anti-slop is opinionated structural lint evidence, not protected correctness authority."]);
        }
        catch (JsonException exception)
        {
            return NormalizedEvidence.Incomplete($"Oxlint JSON report is malformed: {exception.Message}");
        }
    }

    private static AntiSlopReport? ReadReport(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.ValueKind == JsonValueKind.Array &&
            root.TryGetProperty("number_of_files", out var fileCount) && fileCount.TryGetInt32(out var analyzedFileCount) &&
            root.TryGetProperty("number_of_rules", out var ruleCount) && ruleCount.TryGetInt32(out var analyzedRuleCount))
            return new AntiSlopReport(
                diagnostics.EnumerateArray().Select(ReadDiagnostic).ToList(),
                analyzedFileCount,
                analyzedRuleCount);
        return null;
    }

    private static AntiSlopFinding ReadDiagnostic(JsonElement diagnostic)
    {
        var code = TryText(diagnostic, "code") ?? TryText(diagnostic, "ruleId");
        var file = TryText(diagnostic, "filename") ?? TryText(diagnostic, "filePath");
        var category = TryText(diagnostic, "category") ?? TryText(diagnostic, "severity");
        var toolDiagnostic = string.IsNullOrWhiteSpace(file) ||
            string.IsNullOrWhiteSpace(code) ||
            code.Contains("parse", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("syntax", StringComparison.OrdinalIgnoreCase) ||
            category is "fatal";
        return new AntiSlopFinding(file, code, toolDiagnostic);
    }

    private static string? TryText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeReportedPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    internal static string ComputeConfigurationIdentity(
        CommandInvocation invocation,
        bool effectRulesEnabled)
    {
        if (invocation.Arguments.Count < 2 || invocation.Arguments[0] != "--config")
            throw new InvalidOperationException("The canonical Oxlint configuration argument is missing.");

        var configPath = Path.GetFullPath(invocation.Arguments[1]);
        var root = Path.GetDirectoryName(configPath) ??
            throw new InvalidOperationException("The Oxlint configuration root is unavailable.");
        var expectedConfig = effectRulesEnabled ? "oxlint.effect.config.ts" : "oxlint.config.ts";
        if (!string.Equals(Path.GetFileName(configPath), expectedConfig, StringComparison.Ordinal))
            throw new InvalidOperationException($"The selected configuration is not {expectedConfig}.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendClosureFile(hash, root, Path.Combine(root, "package.json"));
        AppendClosureFile(hash, root, Path.Combine(root, "package-lock.json"));
        AppendClosureFile(hash, root, Path.Combine(root, "oxlint.config.ts"));
        if (effectRulesEnabled)
            AppendClosureFile(hash, root, Path.Combine(root, "oxlint.effect.config.ts"));
        AppendClosureDirectory(hash, root, Path.Combine(root, "vendor", "anti-slop"));
        AppendClosureDirectory(hash, root, Path.Combine(root, "node_modules", "oxlint", "bin"));
        AppendClosureDirectory(hash, root, Path.Combine(root, "node_modules", "oxlint", "dist"));
        AppendClosureDirectory(hash, root, Path.Combine(root, "node_modules", "@oxlint", "plugins"));

        var bindingsRoot = Path.Combine(root, "node_modules", "@oxlint");
        var bindings = Directory.Exists(bindingsRoot)
            ? Directory.EnumerateDirectories(bindingsRoot, "binding-*", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        if (bindings.Length == 0)
            throw new InvalidOperationException("The pinned Oxlint native binding is unavailable.");
        foreach (var binding in bindings)
            AppendClosureDirectory(hash, root, binding);

        var nodePath = ResolveNodeExecutable(invocation);
        AppendValue(hash, "runtime/node-path", Path.GetFullPath(nodePath));
        AppendClosureFile(hash, Path.GetDirectoryName(nodePath)!, nodePath, "runtime/node-bytes");
        AppendValue(hash, "environment/NODE_OPTIONS", EffectiveEnvironment(invocation, "NODE_OPTIONS") ?? string.Empty);
        AppendValue(hash, "environment/NODE_PATH", EffectiveEnvironment(invocation, "NODE_PATH") ?? string.Empty);

        return $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    private static void AppendClosureDirectory(IncrementalHash hash, string root, string directory)
    {
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"Required anti-slop closure directory is unavailable: {directory}");
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
            AppendClosureFile(hash, root, file);
    }

    private static void AppendClosureFile(
        IncrementalHash hash,
        string root,
        string path,
        string? logicalPath = null)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Required anti-slop closure file is unavailable: {path}");
        AppendValue(hash, logicalPath ?? Path.GetRelativePath(root, path).Replace('\\', '/'), string.Empty);
        hash.AppendData(File.ReadAllBytes(path));
        hash.AppendData([0]);
    }

    private static void AppendValue(IncrementalHash hash, string name, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(name));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static string ResolveNodeExecutable(CommandInvocation invocation)
    {
        var path = EffectiveEnvironment(invocation, "PATH") ?? string.Empty;
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe", "node.cmd", "node" } : ["node"];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        throw new InvalidOperationException("The Node.js executable cannot be resolved from the effective PATH.");
    }

    private static string? EffectiveEnvironment(CommandInvocation invocation, string name) =>
        invocation.Environment is not null && invocation.Environment.TryGetValue(name, out var value)
            ? value
            : Environment.GetEnvironmentVariable(name);

    private sealed record AntiSlopSnapshot(IReadOnlyList<string> Inputs, string ConfigurationIdentity);
    private sealed record AntiSlopFinding(string? File, string? Rule, bool IsParserOrToolDiagnostic);
    private sealed record AntiSlopReport(
        IReadOnlyList<AntiSlopFinding> Findings,
        int AnalyzedFileCount,
        int RuleCount);
}
