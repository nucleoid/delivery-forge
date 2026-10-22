using System.Security.Cryptography;
using System.Text;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence;

/// <summary>
/// Supported developer-facing composition for advisory anti-slop evaluation. It selects
/// JavaScript-family inputs, constructs the canonical request, runs the gate, and returns
/// receipt-bearing results. It never activates production authority.
/// </summary>
public static class AdvisoryAntiSlopComposition
{
    public static async Task<AdvisoryAntiSlopCompositionResult> RunAsync(
        AdvisoryAntiSlopOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var repositoryRoot = RequireAbsoluteDirectory(options.RepositoryRoot, nameof(options.RepositoryRoot));
        var bundleRoot = RequireAbsoluteDirectory(options.BundleRoot, nameof(options.BundleRoot));
        var nodePath = RequireAbsolutePath(options.NodePath, nameof(options.NodePath));
        if (!File.Exists(nodePath))
            throw new EvaluationPluginException("The advisory anti-slop Node executable does not exist.");
        var outputDirectory = RequireAbsolutePath(options.OutputDirectory, nameof(options.OutputDirectory));
        options = options with
        {
            RepositoryRoot = repositoryRoot,
            BundleRoot = bundleRoot,
            NodePath = nodePath,
            OutputDirectory = outputDirectory
        };

        var executor = new ProcessCommandExecutor();
        var reader = new GitRepositoryIdentityReader(executor, repositoryRoot, options.ProtectedBaseCommit);
        var registry = DeterministicEvaluationPluginRegistry.CreateBuiltIns(bundleRoot, nodePath);
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(nodePath)))}";
        var capability = ToolCapability.FromDetection(
            "oxlint",
            AntiSlopEvaluationPlugin.OxlintVersion,
            "oxlint-json-v1",
            fixture: false,
            ["Advisory composition is not production authority."],
            nodePath,
            executableIdentity,
            ["lint"],
            repositoryRoot);
        var runner = new GateRunner(
            executor, reader, repositoryRoot, options.ProtectedBaseCommit, registry);
        return await RunCoreAsync(options, reader, registry, runner, capability, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static Task<AdvisoryAntiSlopCompositionResult> RunFixtureAsync(
        AdvisoryAntiSlopOptions options,
        IRepositoryIdentityReader repository,
        TrustedEvaluationPluginRegistry registry,
        ICommandExecutor executor,
        ToolCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(capability);
        var runner = new GateRunner(
            executor,
            repository,
            options.RepositoryRoot,
            options.ProtectedBaseCommit,
            registry);
        return RunCoreAsync(options, repository, registry.Plugins, runner, capability, cancellationToken);
    }

    private static async Task<AdvisoryAntiSlopCompositionResult> RunCoreAsync(
        AdvisoryAntiSlopOptions options,
        IRepositoryIdentityReader repository,
        DeterministicEvaluationPluginRegistry registry,
        GateRunner runner,
        ToolCapability capability,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = Path.GetFullPath(options.RepositoryRoot);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        var selections = registry.SelectApplicable(
            [new EvaluationPluginConfiguration(
                AntiSlopEvaluationPlugin.CreateBundled().Id,
                options.Enabled,
                options.EnableEffectRules)],
            options.CandidateInputs,
            options.IgnoredInputs);
        var expectedRepository = await repository.ReadAsync(cancellationToken).ConfigureAwait(false);
        var policy = CreateAdvisoryPolicy(options, repositoryRoot);
        var executions = new List<AdvisoryAntiSlopExecution>();

        foreach (var selection in selections)
        {
            if (!selection.Enabled)
            {
                executions.Add(new(selection, null, "disabled; no tool execution or receipt was fabricated"));
                continue;
            }
            if (selection.Inputs is not { Count: > 0 })
            {
                executions.Add(new(selection, null, "not applicable; no JavaScript-family input was selected"));
                continue;
            }

            var request = AntiSlopRequestFactory.Create(
                options.GateId,
                policy,
                capability,
                expectedRepository,
                repositoryRoot,
                options.BundleRoot,
                options.NodePath,
                selection.Inputs,
                options.Scope,
                options.Timeout,
                outputDirectory,
                selection.EnableEffectRules);
            var result = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            executions.Add(new(selection, result, "executed advisory gate; inspect outcome and receipt"));
        }

        return new(selections, executions, ProductionAuthorityActivated: false);
    }

    private static ResolvedEvidencePolicy CreateAdvisoryPolicy(
        AdvisoryAntiSlopOptions options,
        string repositoryRoot)
    {
        var policyIdentity = Identity("delivery-forge-advisory-anti-slop-policy-v1");
        var contentIdentity = Identity(string.Join('\n',
            options.ProtectedBaseCommit,
            repositoryRoot,
            options.GateId,
            options.Enabled,
            options.EnableEffectRules));
        return EvidencePolicy.Resolve(
            new PolicyCandidate(
                policyIdentity,
                contentIdentity,
                options.ProtectedBaseCommit,
                Fixture: false,
                MaximumChangedCodeDebtIncrease: 0m,
                RequiredGates: [options.GateId]),
            new PolicyAuthority(
                PolicyAuthorityKind.ProtectedGitBase,
                options.ProtectedBaseCommit,
                contentIdentity));
    }

    private static string Identity(string value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    private static string RequireAbsoluteDirectory(string path, string parameterName)
    {
        var fullPath = RequireAbsolutePath(path, parameterName);
        if (!Directory.Exists(fullPath))
            throw new EvaluationPluginException($"{parameterName} must name an existing absolute directory.");
        return fullPath;
    }

    private static string RequireAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
            throw new EvaluationPluginException($"{parameterName} must be an absolute path.");
        return Path.GetFullPath(path);
    }
}

public sealed record AdvisoryAntiSlopOptions(
    string RepositoryRoot,
    string ProtectedBaseCommit,
    string BundleRoot,
    string NodePath,
    string OutputDirectory,
    IReadOnlyList<string> CandidateInputs,
    IReadOnlySet<string>? IgnoredInputs = null,
    bool Enabled = true,
    bool EnableEffectRules = false,
    string GateId = "anti-slop",
    string Scope = "advisory-javascript-family",
    TimeSpan? RequestedTimeout = null)
{
    public TimeSpan Timeout => RequestedTimeout ?? TimeSpan.FromMinutes(2);
}

public sealed record AdvisoryAntiSlopExecution(
    EvaluationPluginSelection Selection,
    GateRunResult? Result,
    string Disposition);

public sealed record AdvisoryAntiSlopCompositionResult(
    IReadOnlyList<EvaluationPluginSelection> Selections,
    IReadOnlyList<AdvisoryAntiSlopExecution> Executions,
    bool ProductionAuthorityActivated);
