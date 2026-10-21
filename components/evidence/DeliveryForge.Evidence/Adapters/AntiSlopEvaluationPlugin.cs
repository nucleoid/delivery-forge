using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Evidence.Adapters;

public sealed class AntiSlopEvaluationPlugin : IDeterministicEvaluationPlugin
{
    public const string UpstreamCommit = "13909c9e07590f5a85d93c62712259fe14a883f7";
    public const string OxlintVersion = "1.78.0";
    private const string ReviewedPackageSha256 = "7bb09c43863e87b3721e61f5891e3798949c346212dcb6c07f30cd666652ca1f";
    private const string ReviewedLockSha256 = "556259ea811200192cd7dc75016408cf76a4718a813245cb9f3b820d74ed8567";
    private const string ReviewedConfigSha256 = "5226e689d45cb10f4a25df0e31f52092f52b958fdd179862a0312815df9c8e10";
    private const string ReviewedEffectConfigSha256 = "d4e372437c08bfc5d4db784bb78ae78b2e3950442379b526cede50b5f3c8e907";
    private const string ReviewedBatchRunnerSha256 = "cc9853de43920adb20a9ec459f77ed072606c41b5ddf37d378db4abb98c74d01";
    public const string ReviewedLinuxX64GnuRuntimeClosureSha256 =
        "sha256:f3c39acbe45af98d4ea005347660992ac47a75fb6511a9ba6c2563792a3be267";
    public const string ReviewedLinuxX64GnuEffectRuntimeClosureSha256 =
        "sha256:232567ab4655c415110837439dc85ef9d408ea86afedbac682aea0568d9a4fce";
    internal const int MaximumDirectArgumentBytes = 24_000;
    private static readonly HashSet<string> SupportedExtensions = new(
        [".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".mts", ".cts"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AllowedEnvironment = new(
        ["SystemRoot", "WINDIR", "TMP", "TEMP"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> BaseRuleIds = new(
    [
        "oxc(no-accumulating-spread)",
        "anti-slop(no-array-filter-map)",
        "anti-slop(no-reduce-accumulator-copy)",
        "anti-slop(no-chained-type-assertions)",
        "anti-slop(no-conditional-empty-object-spread)",
        "anti-slop(no-known-value-widening)",
        "anti-slop(no-module-mocking)",
        "anti-slop(no-object-parameters)",
        "anti-slop(no-reflect-apply)",
        "anti-slop(no-reflect-get)",
        "anti-slop(no-runtime-typeof)",
        "anti-slop(no-shape-in-symbol-names)",
        "anti-slop(no-unknown-parameters)",
        "anti-slop(no-unknown-returns)",
        "anti-slop(no-unknown-type-aliases)",
        "anti-slop(no-unsafe-dictionary-type)",
        "anti-slop(no-widen-then-assert)",
        "anti-slop(require-readable-spacing)",
        "anti-slop(require-safety-comment-for-type-assertion)"
    ], StringComparer.Ordinal);
    private static readonly HashSet<string> EffectRuleIds = new(
    [
        "anti-slop-effect(no-manual-effect-error-tag)",
        "anti-slop-effect(no-manual-tag-comparison)",
        "anti-slop-effect(no-manual-tagged-construction)",
        "anti-slop-effect(no-service-constructor-imports)",
        "anti-slop-effect(prefer-effect-match)"
    ], StringComparer.Ordinal);

    private readonly string? _trustedBundleRoot;
    private readonly string? _trustedNodePath;
    private readonly bool _enforceReviewedPins;

    private AntiSlopEvaluationPlugin(
        string? trustedBundleRoot = null,
        string? trustedNodePath = null,
        bool enforceReviewedPins = true)
    {
        _trustedBundleRoot = trustedBundleRoot is null ? null : Path.GetFullPath(trustedBundleRoot);
        _trustedNodePath = trustedNodePath is null ? null : Path.GetFullPath(trustedNodePath);
        _enforceReviewedPins = enforceReviewedPins;
    }

    public string Id => "dmmulroy.anti-slop";
    public string Version => $"0.1.2+{UpstreamCommit}";
    public string ExpectedTool => "oxlint";
    public EvidenceAdapterKind CompatibilityAdapter => EvidenceAdapterKind.External;

    public static AntiSlopEvaluationPlugin CreateBundled() => new();

    public static AntiSlopEvaluationPlugin CreateBundled(string bundleRoot, string nodePath)
    {
        if (string.IsNullOrWhiteSpace(bundleRoot) || string.IsNullOrWhiteSpace(nodePath) ||
            !Path.IsPathFullyQualified(bundleRoot) || !Path.IsPathFullyQualified(nodePath))
            throw new EvaluationPluginException(
                "Anti-slop registration requires absolute code-owned bundle and Node paths.");
        return new(bundleRoot, nodePath);
    }

    internal static AntiSlopEvaluationPlugin CreateBundledForFixture(string bundleRoot, string nodePath) =>
        new(bundleRoot, nodePath, enforceReviewedPins: false);

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
        var authorityFailure = ValidateAuthorityWithoutExecution(request);
        if (authorityFailure is not null)
            return authorityFailure;
        var inputs = DeterministicEvaluationPluginRegistry.NormalizeInputs(request.Plugin?.Inputs ?? []);
        return inputs.Count == 0
            ? NormalizedEvidence.Incomplete("Anti-slop selected zero JavaScript-family inputs.")
            : null;
    }

    public NormalizedEvidence? ValidateAuthorityWithoutExecution(GateRequest request)
    {
        if (!string.Equals(request.Capability.Version, OxlintVersion, StringComparison.Ordinal) ||
            !string.Equals(request.Capability.FormatVersion, "oxlint-json-v1", StringComparison.Ordinal))
            return NormalizedEvidence.Incomplete(
                "Anti-slop requires the exact pinned Oxlint 1.78.0 JSON capability.");
        var inputs = DeterministicEvaluationPluginRegistry.NormalizeInputs(request.Plugin?.Inputs ?? []);
        var unsupported = inputs.Where(input => !IsSupportedInput(input)).ToArray();
        if (unsupported.Length > 0)
            return NormalizedEvidence.Incomplete(
                $"Anti-slop input selection contains unsupported paths: {string.Join(", ", unsupported)}.");

        var arguments = request.Invocation.Arguments;
        if (arguments.Any(argument => argument.StartsWith("--fix", StringComparison.Ordinal)))
            return NormalizedEvidence.Incomplete("Anti-slop never permits Oxlint autofix options.");
        if (request.Invocation.InheritEnvironment ||
            (request.Invocation.Environment?.Keys.Any(key => !AllowedEnvironment.Contains(key)) ?? false))
            return NormalizedEvidence.Incomplete(
                "Anti-slop requires a cleared, allow-listed execution environment.");

        string? invocationError = null;
        string configPath = string.Empty;
        IReadOnlyList<string> invocationInputs = [];
        if (!Path.IsPathFullyQualified(request.Invocation.FileName) ||
            !TryReadInvocation(arguments, inputs, out configPath, out invocationInputs, out invocationError))
            return NormalizedEvidence.Incomplete(invocationError ??
                "Anti-slop requires the canonical direct or argv-safe batch-host invocation.");
        if (!inputs.SequenceEqual(invocationInputs, StringComparer.Ordinal))
            return NormalizedEvidence.Incomplete(
                "Anti-slop invocation inputs do not exactly match the frozen plugin input selection.");

        var expectedConfig = request.Plugin?.EnableEffectRules == true
            ? "oxlint.effect.config.ts"
            : "oxlint.config.ts";
        if (!Path.IsPathFullyQualified(configPath) ||
            !string.Equals(Path.GetFileName(configPath), expectedConfig, StringComparison.Ordinal))
            return NormalizedEvidence.Incomplete(
                $"Anti-slop invocation is not bound to an absolute {expectedConfig} configuration.");
        if (_trustedBundleRoot is null || _trustedNodePath is null)
            return NormalizedEvidence.Incomplete(
                "Anti-slop has no code-owned installed bundle/runtime registration.");
        try
        {
            var selectedConfigRoot = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (!string.Equals(selectedConfigRoot, _trustedBundleRoot, PathComparison) ||
                !string.Equals(Path.GetFullPath(request.Invocation.FileName), _trustedNodePath, PathComparison))
                return NormalizedEvidence.Incomplete(
                    "Anti-slop invocation does not use the code-owned registered bundle and Node runtime.");
            if (_enforceReviewedPins)
                ValidateReviewedPins(_trustedBundleRoot, _trustedNodePath,
                    request.Plugin?.EnableEffectRules == true);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or
                                           IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return NormalizedEvidence.Incomplete(
                $"Anti-slop bundle/runtime pin or path is invalid: {exception.Message}");
        }
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
                : NormalizedEvidence.Incomplete("Anti-slop configuration or runtime closure changed during evaluation.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return NormalizedEvidence.Incomplete(
                $"Anti-slop configuration or runtime closure became unavailable: {exception.Message}");
        }
    }

    public string GetRuntimeClosureIdentity(GateRequest request) =>
        !_enforceReviewedPins || _trustedBundleRoot is null || _trustedNodePath is null
            ? request.ConfigurationIdentity
            : ComputeRuntimeClosureIdentity(
                _trustedBundleRoot, _trustedNodePath, request.Plugin?.EnableEffectRules == true);

    public NormalizedEvidence Normalize(
        GateRequest request,
        CommandResult execution,
        EvaluationPluginSnapshot snapshot)
    {
        var state = snapshot.State as AntiSlopSnapshot;
        if (state is null)
            return NormalizedEvidence.Error("Anti-slop snapshot is missing or mismatched.");
        var normalized = NormalizeReport(
            System.Text.Encoding.UTF8.GetBytes(execution.StandardOutput),
            state.Inputs,
            execution.ExitCode ?? -1,
            request.Invocation.WorkingDirectory,
            request.Plugin?.EnableEffectRules == true);
        if (normalized.Outcome == GateOutcome.Pass && HasExactTrustedPolicyBinding(request))
            return normalized with { ProductionCapable = true, Limitations = [] };
        return normalized;
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
        int processExitCode,
        string? workingDirectory = null,
        bool effectRulesEnabled = false)
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
            var allowedRules = effectRulesEnabled
                ? BaseRuleIds.Concat(EffectRuleIds).ToHashSet(StringComparer.Ordinal)
                : BaseRuleIds;
            if (reportData.AnalyzedFileCount != analyzedInputs.Count || reportData.RuleCount != allowedRules.Count)
                return NormalizedEvidence.Incomplete(
                    "Oxlint report does not prove configured analysis of the complete frozen input set.");
            var findings = reportData.Findings;
            if (findings.Any(finding => finding.IsParserOrToolDiagnostic))
                return NormalizedEvidence.Incomplete("Oxlint reported a parser, configuration, or tool diagnostic.");
            if (findings.Any(finding => finding.File is not null &&
                !analyzedInputs.Contains(
                    NormalizeReportedPath(finding.File, workingDirectory), StringComparer.Ordinal)))
                return NormalizedEvidence.Incomplete("Oxlint report contains a finding outside the frozen input set.");
            if (findings.Any(finding => finding.Rule is null || !allowedRules.Contains(finding.Rule)))
                return NormalizedEvidence.Incomplete("Oxlint reported an unexpected diagnostic rule id outside the configured anti-slop policy.");
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
                ["Anti-slop is advisory unless protected policy binds this exact plugin identity, version, and tool."]);
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

    private static string NormalizeReportedPath(string path, string? workingDirectory)
    {
        if (Path.IsPathFullyQualified(path) && !string.IsNullOrWhiteSpace(workingDirectory))
            path = Path.GetRelativePath(workingDirectory, path);
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private bool HasExactTrustedPolicyBinding(GateRequest request) =>
        request.Policy.AuthorityVerified &&
        request.Policy.RequiredGates.Contains(request.GateId, StringComparer.Ordinal) &&
        request.Policy.RequiredGatePluginBindings.Any(binding =>
            string.Equals(binding.GateId, request.GateId, StringComparison.Ordinal) &&
            string.Equals(binding.PluginId, Id, StringComparison.Ordinal) &&
            string.Equals(binding.PluginVersion, Version, StringComparison.Ordinal) &&
            string.Equals(binding.Tool, ExpectedTool, StringComparison.Ordinal));

    private static void ValidateReviewedPins(string root, string nodePath, bool effectRulesEnabled)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["package.json"] = ReviewedPackageSha256,
            ["package-lock.json"] = ReviewedLockSha256,
            ["oxlint.config.ts"] = ReviewedConfigSha256,
            ["run-oxlint-batches.mjs"] = ReviewedBatchRunnerSha256
        };
        if (effectRulesEnabled)
            expected["oxlint.effect.config.ts"] = ReviewedEffectConfigSha256;
        foreach (var (relative, digest) in expected)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, relative));
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), digest, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Anti-slop runtime file '{relative}' does not match its reviewed immutable pin.");
        }
        using var package = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(root, "node_modules", "oxlint", "package.json")));
        if (package.RootElement.GetProperty("version").GetString() != OxlintVersion)
            throw new InvalidOperationException("Installed Oxlint package version differs from the reviewed runtime pin.");
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64 ||
            !Directory.Exists(Path.Combine(root, "node_modules", "@oxlint", "binding-linux-x64-gnu")))
            throw new InvalidOperationException(
                "The complete reviewed anti-slop runtime closure is not pinned for this platform binding.");
        var actualClosure = ComputeRuntimeClosureIdentity(root, nodePath, effectRulesEnabled);
        var expectedClosure = effectRulesEnabled
            ? ReviewedLinuxX64GnuEffectRuntimeClosureSha256
            : ReviewedLinuxX64GnuRuntimeClosureSha256;
        if (!string.Equals(actualClosure, expectedClosure, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The complete anti-slop executable runtime closure differs from the reviewed pin: {actualClosure}.");
    }

    internal static string ComputeRuntimeClosureIdentity(string root, string nodePath, bool effectRulesEnabled)
    {
        root = Path.GetFullPath(root);
        nodePath = Path.GetFullPath(nodePath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendClosureFile(hash, root, Path.Combine(root, "package.json"));
        AppendClosureFile(hash, root, Path.Combine(root, "package-lock.json"));
        AppendClosureFile(hash, root, Path.Combine(root, "oxlint.config.ts"));
        AppendClosureFile(hash, root, Path.Combine(root, "run-oxlint-batches.mjs"));
        if (effectRulesEnabled)
            AppendClosureFile(hash, root, Path.Combine(root, "oxlint.effect.config.ts"));
        AppendClosureDirectory(hash, root, Path.Combine(root, "vendor", "anti-slop"));
        AppendClosureDirectory(hash, root, Path.Combine(root, "node_modules", "@oxlint", "plugins"));
        AppendClosureDirectory(hash, root, Path.Combine(root, "node_modules", "oxlint"));
        var bindings = Directory.EnumerateDirectories(
                Path.Combine(root, "node_modules", "@oxlint"), "binding-*", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal).ToArray();
        if (bindings.Length != 1)
            throw new InvalidOperationException("Exactly one reviewed Oxlint platform binding must be installed.");
        AppendClosureDirectory(hash, root, bindings[0]);
        AppendClosureFile(hash, Path.GetDirectoryName(nodePath)!, nodePath, "runtime/node-bytes");
        return $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    internal static string ComputeConfigurationIdentity(
        CommandInvocation invocation,
        bool effectRulesEnabled)
    {
        var batched = invocation.Arguments.Count == 7 && invocation.Arguments[1] == "--oxlint";
        var configIndex = batched ? 4 : 2;
        if (invocation.Arguments.Count <= configIndex ||
            invocation.Arguments[batched ? 3 : 1] != "--config")
            throw new InvalidOperationException("The canonical Oxlint configuration argument is missing.");

        var configPath = Path.GetFullPath(invocation.Arguments[configIndex]);
        var root = Path.GetDirectoryName(configPath) ??
            throw new InvalidOperationException("The Oxlint configuration root is unavailable.");
        var expectedConfig = effectRulesEnabled ? "oxlint.effect.config.ts" : "oxlint.config.ts";
        if (!string.Equals(Path.GetFileName(configPath), expectedConfig, StringComparison.Ordinal))
            throw new InvalidOperationException($"The selected configuration is not {expectedConfig}.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendClosureFile(hash, root, Path.Combine(root, "package.json"));
        AppendClosureFile(hash, root, Path.Combine(root, "package-lock.json"));
        AppendClosureFile(hash, root, Path.Combine(root, "oxlint.config.ts"));
        AppendClosureFile(hash, root, Path.Combine(root, "run-oxlint-batches.mjs"));
        if (effectRulesEnabled)
            AppendClosureFile(hash, root, Path.Combine(root, "oxlint.effect.config.ts"));
        AppendClosureDirectory(hash, root, Path.Combine(root, "vendor", "anti-slop"));
        AppendClosureDirectory(hash, root, Path.Combine(root, "node_modules", "oxlint"));
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

        var nodePath = invocation.FileName;
        if (!Path.IsPathFullyQualified(nodePath))
            throw new InvalidOperationException("The Node.js runtime path is not absolute.");
        var expectedEntryPoint = Path.GetFullPath(Path.Combine(root, "node_modules", "oxlint", "bin", "oxlint"));
        var actualEntryPoint = batched ? invocation.Arguments[2] : invocation.Arguments[0];
        if (!string.Equals(Path.GetFullPath(actualEntryPoint), expectedEntryPoint,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The Oxlint entrypoint is not the pinned package-local launcher.");
        if (batched)
        {
            var expectedRunner = Path.GetFullPath(Path.Combine(root, "run-oxlint-batches.mjs"));
            if (!string.Equals(Path.GetFullPath(invocation.Arguments[0]), expectedRunner, PathComparison))
                throw new InvalidOperationException("The anti-slop batch runner is not the code-owned launcher.");
            AppendClosureFile(hash, Path.GetDirectoryName(invocation.Arguments[6])!, invocation.Arguments[6],
                "inputs/manifest.json");
        }
        AppendValue(hash, "runtime/node-path", Path.GetFullPath(nodePath));
        AppendClosureFile(hash, Path.GetDirectoryName(nodePath)!, nodePath, "runtime/node-bytes");
        AppendValue(hash, "environment/inherit", invocation.InheritEnvironment.ToString());
        foreach (var pair in (invocation.Environment ?? new Dictionary<string, string>())
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            AppendValue(hash, $"environment/{pair.Key}", pair.Value);

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
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        hash.AppendData([0]);
    }

    private static void AppendValue(IncrementalHash hash, string name, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(name));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static bool TryReadInvocation(
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> inputs,
        out string configPath,
        out IReadOnlyList<string> invocationInputs,
        out string? error)
    {
        configPath = string.Empty;
        invocationInputs = [];
        error = null;
        var direct = arguments.Count == inputs.Count + 7 &&
            arguments.Count >= 7 && Path.IsPathFullyQualified(arguments[0]) &&
            arguments[1] == "--config" && arguments[3] == "--format" && arguments[4] == "json" &&
            arguments[5] == "--no-ignore" && arguments[6] == "--disable-nested-config";
        if (direct)
        {
            if (arguments.Sum(argument => Encoding.UTF8.GetByteCount(argument) + 1) > MaximumDirectArgumentBytes)
            {
                error = "Anti-slop direct argv exceeds the deterministic safety budget; use the code-owned batch host.";
                return false;
            }
            configPath = arguments[2];
            invocationInputs = arguments.Skip(7).Select(input => input.Replace('\\', '/'))
                .Order(StringComparer.Ordinal).ToArray();
            return true;
        }

        var batched = arguments.Count == 7 && Path.IsPathFullyQualified(arguments[0]) &&
            arguments[1] == "--oxlint" && Path.IsPathFullyQualified(arguments[2]) &&
            arguments[3] == "--config" && arguments[5] == "--inputs" &&
            Path.IsPathFullyQualified(arguments[6]);
        if (!batched)
        {
            error = "Anti-slop invocation is neither canonical direct execution nor the argv-safe code-owned batch host.";
            return false;
        }
        try
        {
            var manifest = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(arguments[6]));
            if (manifest is null)
            {
                error = "Anti-slop input manifest is malformed.";
                return false;
            }
            configPath = arguments[4];
            invocationInputs = DeterministicEvaluationPluginRegistry.NormalizeInputs(manifest);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"Anti-slop input manifest is unavailable or malformed: {exception.Message}";
            return false;
        }
    }

    private sealed record AntiSlopSnapshot(IReadOnlyList<string> Inputs, string ConfigurationIdentity);
    private sealed record AntiSlopFinding(string? File, string? Rule, bool IsParserOrToolDiagnostic);
    private sealed record AntiSlopReport(
        IReadOnlyList<AntiSlopFinding> Findings,
        int AnalyzedFileCount,
        int RuleCount);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
