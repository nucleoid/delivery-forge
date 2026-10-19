using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence;

// Private actual owner composition. Original report bytes stay retained through append and
// normalization; a caller cannot replace them with a DTO, prior report or supported flag.
internal sealed class InstalledToolInvocation(
    InstalledToolProfile profile, InstalledToolGateRequest request, InstalledToolFiles files,
    InstalledToolGateProvider.ActualExecutor owner, CommandInvocation invocation, string? reportPath,
    Dictionary<string, string> original, Dictionary<string, string> gitBefore,
    Dictionary<string, string> installBefore, string environmentBefore, string sdkBefore, InstalledToolResourceAdmission.Data resourcesBefore, byte[]? changedDiff,
    Func<Dictionary<string, string>> captureGit, Func<Dictionary<string, string>> captureInstall)
{
    private bool _entered;
    private bool _returned;
    private CommandResult? _actual;
    private byte[]? _report;
    private byte[]? _proof;
    private Exception? _failure;
    private readonly List<byte[]> _artifacts = [];
    private readonly Dictionary<string, string> _bundle = new(StringComparer.Ordinal);
    private string? _bundleRoot;
    private ChangedLineCoverage.Result? _lines;

    internal Task CompleteAsync(CommandResult actual)
    {
        files.Effect(() => Complete(actual));
        return Task.CompletedTask;
    }
    private void Complete(CommandResult actual)
    {
        if (_entered) throw new EvidenceWriteException("An original installed invocation cannot be repeated.");
        _entered = true; _actual = actual;
        try
        {
            files.Check();
            if (!owner.Known(actual)) throw new EvidenceWriteException("Actual owned process/output/disposition is unknown or truncated.");
            _report = reportPath is null ? new UTF8Encoding(false, true).GetBytes(actual.StandardOutput) : files.Read(reportPath, ProducerJson.MaximumBytes);
            using var document = ProducerJson.Read(_report);
            if (profile.Tool == "mutate4csharp") JoinMutation(document.RootElement);
            else JoinCrap(document.RootElement, actual);
            // No forward SDK process is dispatched after the actual main owner enters cleanup.
            // The original parent retains the synchronous consume for quiescence;
            // its lease-state monitor remains available for renewal/revocation.
            InstalledToolFiles.Same(installBefore, captureInstall(), false, files, InstalledToolFiles.ComparisonPhase.InstallationCurrent);
            InstalledToolFiles.Same(gitBefore, captureGit(), false, files, InstalledToolFiles.ComparisonPhase.GitCurrent);
            var current = files.Inventory(invocation.WorkingDirectory, 100000, 1024L * 1024 * 1024, true);
            // No-state mutation must leave EVERY original and new ordinary file unchanged.
            // Crap's own completed current proof admits fresh build outputs, but cannot change
            // any pre-existing captured file. New-file inventory remains complete receipt DATA.
            InstalledToolFiles.Same(original, current, profile.Tool == "crap4csharp", files, InstalledToolFiles.ComparisonPhase.ConsumerCurrent);
            var resourcesAfter = InstalledToolResourceAdmission.Capture(files);
            if (resourcesAfter.AffinityMask != resourcesBefore.AffinityMask) throw new EvidenceWriteException("Actual resource admission changed during the invocation.");
            var environmentAfter = InstalledToolGateProvider.EnvironmentIdentity();
            if (environmentAfter != environmentBefore) throw new EvidenceWriteException("Parent environment changed while the tool ran.");
            files.Check();
            _proof = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "forge-current-installed-tool-v1",
                profile = profile.Identity,
                toolSourceCommit = profile.SourceCommit,
                profile.Version,
                profile.Data.PackageVersion,
                profile.Fixture,
                package = profile.Data.PackageSha256,
                installedPayload = InstalledToolFiles.InventoryIdentity(installBefore),
                repository = request.ExpectedRepository,
                request.Scope,
                request.ProjectRelativePath,
                configuration = profile.Data.ConfigurationSha256,
                environmentBefore,
                environmentAfter,
                sdkBefore,
                PostSdkProbe = false,
                Timing = files.TimingData,
                resourcesBefore,
                resourcesAfter,
                sourceBefore = original,
                sourceAfter = current,
                gitBefore,
                gitAfter = captureGit(),
                invocation.FileName,
                invocation.Arguments,
                actual.StartedAt,
                actual.CompletedAt,
                actual.ExitCode,
                actual.OwnedProcessQuiescent,
                report = InstalledToolFiles.Identity(_report),
                reportBytes = _report.Length,
                artifactHashes = _artifacts.Select(b => InstalledToolFiles.Identity(b)).ToArray(),
                bundleRoot = _bundleRoot,
                bundleFiles = _bundle,
                NativeOrPublicationAuthority = false
            });
            if (_proof.Length > ProducerJson.MaximumBytes) throw new EvidenceWriteException("Complete current-binding projection exceeds the named receipt bound.");
            files.Check(); _returned = true;
        }
        catch (Exception error) { _failure = error; }
    }

    private void JoinMutation(JsonElement root)
    {
        var data = Mutate4CSharpReportV2.ReadData(_report!);
        if (data.ClaimedExitCode != _actual!.ExitCode || root.GetProperty("mode").GetString() != "check" ||
            root.GetProperty("selection").GetProperty("kind").GetString() != "base" ||
            root.GetProperty("selection").GetProperty("baseRevision").GetString() != request.ExpectedRepository.BaseCommit ||
            root.GetProperty("scopePlan").GetProperty("baseCommit").GetString() != request.ExpectedRepository.BaseCommit ||
            root.GetProperty("scopePlan").GetProperty("root").GetString() != ".")
            throw new EvidenceWriteException("Mutation report's complete selection/revision/root/exit does not match the actual invocation.");
        var timestamp = root.GetProperty("generatedAtUtc").GetDateTimeOffset();
        if (timestamp < _actual.StartedAt.AddSeconds(-2) || timestamp > _actual.CompletedAt.AddSeconds(2))
            throw new EvidenceWriteException("Mutation report was not generated within this actual invocation.");
        using var configuration = ProducerJson.Read(files.Read(Path.Combine(invocation.WorkingDirectory, profile.Data.ConfigurationRelativePath), 1024 * 1024));
        var actualPolicy = root.GetProperty("policy");
        if (!JsonElement.DeepEquals(configuration.RootElement.GetProperty("policy"), actualPolicy))
            throw new EvidenceWriteException("Mutation execution policy differs from captured approved configuration.");
        if (data.ClaimedOutcome != GateOutcome.Incomplete)
        {
            var expected = configuration.RootElement.GetProperty("testSuites").EnumerateArray().Select(SuiteKey).ToHashSet(StringComparer.Ordinal);
            var observed = root.GetProperty("suites").EnumerateArray().Select(s => SuiteKey(s.GetProperty("accounting").GetProperty("configuration"))).ToHashSet(StringComparer.Ordinal);
            if (!expected.SetEquals(observed)) throw new EvidenceWriteException("Current mutation evidence omitted or substituted a configured suite.");
            var sdk = root.GetProperty("evidence").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "TOOL_INTERNAL_SDK")
                .SelectMany(e => e.GetProperty("diagnostics").EnumerateArray()).Select(e => e.GetString()!).ToArray();
            if (sdk.Length != 1 || !sdk[0].StartsWith("dotnet-sdk=" + sdkBefore + ";dependencies=", StringComparison.Ordinal))
                throw new EvidenceWriteException("Actual captured consumer SDK evidence differs from original selection.");
        }
        // All source/config/SDK/coverage/ledger identities remain in the complete original report.
        // Conclusive suite membership and fresh coverage joins were checked by ReadData.
    }

    private static string SuiteKey(JsonElement suite)
    {
        var path = suite.GetProperty("path").GetString()!.Replace('\\', '/').ToUpperInvariant();
        var members = suite.GetProperty("expectedMembers").EnumerateArray().Select(v => v.GetString()!.ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(new
        {
            path,
            runner = suite.GetProperty("runner").GetString(),
            framework = suite.GetProperty("framework").GetString(),
            configuration = suite.GetProperty("configuration").GetString(),
            members
        });
    }
    private void JoinCrap(JsonElement root, CommandResult actual)
    {
        var evaluation = root.GetProperty("evaluation"); var run = root.GetProperty("run");
        if (root.GetProperty("toolVersion").GetString() != profile.Version || run.GetProperty("exitCode").GetInt32() != actual.ExitCode ||
            evaluation.GetProperty("invocationMode").GetString() != "check")
            throw new EvidenceWriteException("Crap original version/operation/exit differs from the real invocation.");
        if (run.GetProperty("startedAt").GetDateTimeOffset() < actual.StartedAt.AddSeconds(-2) ||
            run.GetProperty("finishedAt").GetDateTimeOffset() > actual.CompletedAt.AddSeconds(2))
            throw new EvidenceWriteException("Crap invocation timestamps are not current.");
        if (!evaluation.TryGetProperty("policyTrust", out var trust) || trust.GetProperty("trust").GetString() != "base-trusted" ||
            trust.GetProperty("revision").GetString() != request.ExpectedRepository.BaseCommit ||
            !InstalledToolFiles.Hash("sha256:" + trust.GetProperty("policyHash").GetString()))
            throw new EvidenceWriteException("Crap result is not bound to the exact reviewed policy/base.");
        var manifests = run.GetProperty("artifacts").EnumerateArray().Where(a => a.GetProperty("kind").GetString() == "manifest").ToArray();
        if (actual.ExitCode is 0 or 2)
        {
            if (manifests.Length != 1 || !manifests[0].GetProperty("complete").GetBoolean())
                throw new EvidenceWriteException("Conclusive current Crap result lacks one complete manifest.");
            var manifestPath = Path.GetFullPath(manifests[0].GetProperty("path").GetString()!);
            var bundleArgument = invocation.Arguments.ToList().IndexOf("--artifact-output");
            _bundleRoot = Path.GetFullPath(invocation.Arguments[bundleArgument + 1]);
            if (!InstalledToolGateProvider.Inside(manifestPath, _bundleRoot)) throw new EvidenceWriteException("Manifest escapes the actual owned bundle.");
            var raw = files.Read(manifestPath, ProducerJson.MaximumBytes); _artifacts.Add(raw);
            using var manifest = ProducerJson.Read(raw);
            var body = manifest.RootElement;
            if (body.GetProperty("manifestSchemaVersion").GetString() != "1.0" ||
                body.GetProperty("producer").GetProperty("toolVersion").GetString() != profile.Version ||
                !body.GetProperty("capture").GetProperty("captureConsistent").GetBoolean() ||
                !body.GetProperty("capture").GetProperty("actualBindingComplete").GetBoolean() ||
                body.GetProperty("revision").GetProperty("head").GetString() != request.ExpectedRepository.HeadCommit ||
                body.GetProperty("revision").GetProperty("base").GetString() != request.ExpectedRepository.BaseCommit)
                throw new EvidenceWriteException("Current complete bundle producer/revision/capture mismatch.");
            foreach (var row in body.GetProperty("artifacts").EnumerateArray())
            {
                var relative = row.GetProperty("locator").GetString()!; InstalledToolProfile.Relative(relative);
                var bytes = files.Read(Path.Combine(_bundleRoot, relative), 128 * 1024 * 1024);
                if (bytes.LongLength != row.GetProperty("length").GetInt64() ||
                    InstalledToolFiles.Identity(bytes) != "sha256:" + row.GetProperty("sha256").GetString())
                    throw new EvidenceWriteException("Complete manifest artifact bytes differ from the actual bundle.");
            }
            foreach (var pair in files.Inventory(_bundleRoot, 20000, 1024L * 1024 * 1024, false)) _bundle.Add(pair.Key, pair.Value);
            var policies = body.GetProperty("artifacts").EnumerateArray().Where(a => a.GetProperty("kind").GetString() == "trusted-policy").ToArray();
            if (policies.Length != 1 || policies[0].GetProperty("sha256").GetString() != trust.GetProperty("policyHash").GetString())
                throw new EvidenceWriteException("Actual canonical policy identity is not the same trusted current policy.");
            var policyLocator = policies[0].GetProperty("locator").GetString()!; InstalledToolProfile.Relative(policyLocator);
            var actualPolicy = files.Read(Path.Combine(_bundleRoot, policyLocator), 1024 * 1024);
            if (actualPolicy.LongLength != policies[0].GetProperty("length").GetInt64() || InstalledToolFiles.Identity(actualPolicy) != "sha256:" + policies[0].GetProperty("sha256").GetString())
                throw new EvidenceWriteException("Actual canonical policy bytes changed during the current join.");
            var approvedRaw = files.Read(Path.Combine(invocation.WorkingDirectory, profile.Data.ConfigurationRelativePath), 1024 * 1024);
            if (InstalledToolFiles.Identity(approvedRaw) != profile.Data.ConfigurationSha256) throw new EvidenceWriteException("Approved raw policy changed.");
            CrapPolicyBinding.RequireSame(approvedRaw, actualPolicy);
            if (changedDiff is null) throw new EvidenceWriteException("The original current diff was not retained.");
            _lines = ChangedLineCoverage.Read(changedDiff, body, invocation.WorkingDirectory,
                locator => { InstalledToolProfile.Relative(locator); return files.Read(Path.Combine(_bundleRoot, locator), 128 * 1024 * 1024); }, files);
        }
    }
    internal NormalizedEvidence CompareApprovedBaseline(GateRunResult actual, ProtectedBaselineRegistry registry, ResolvedEvidencePolicy policy) => files.Effect(() =>
    {
        registry.RecheckApproval(files);
        if (profile.Fixture || profile.Tool != "crap4csharp" || actual.Fixture || actual.SourceChanged || actual.Outcome != GateOutcome.Pass ||
            !_entered || !_returned || _failure is not null || _report is null || _proof is null || !ReferenceEquals(actual.Execution, _actual) ||
            actual.PolicyIdentity != policy.PolicyIdentity || request.Policy != policy || actual.After != request.ExpectedRepository)
            return NormalizedEvidence.Incomplete("No genuine complete current nonfixture callable report for protected comparison.");
        if (registry.CallableProducer != CallableProducerIdentity.Current(profile, request))
            return NormalizedEvidence.Incomplete("Approved baseline producer, package, configuration or scope differs from the current callable gate; explicit compatible baseline approval is required.");
        var metrics = ProtectedBaselineRegistry.CallableMetrics(_report);
        return BaselineComparer.Compare(metrics, registry.Baseline, policy);
    });
    internal T Consume<T>(Func<T> consume) => files.Effect(consume);
    internal void CheckEndpoint() => files.Effect(() => files.Check());
    internal byte[] ReadCapability(string path) => files.Read(path, 128 * 1024 * 1024);
    internal void EnsureDirectory(string directory)
    {
        files.EnsureDirectory(directory);
    }
    internal void Claim(string path) => files.WriteNew(path, []);
    internal Dictionary<string, string> WriteExecutionArtifacts(string directory, CommandResult execution) => files.Effect(() => WriteExecutionArtifactsCore(directory, execution));
    private Dictionary<string, string> WriteExecutionArtifactsCore(string directory, CommandResult execution)
    {
        if (!ReferenceEquals(_actual, execution)) throw new EvidenceWriteException("Only the retained actual main result can publish installed execution artifacts.");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (leaf, text) in new[] { ("stdout.txt", execution.StandardOutput), ("stderr.txt", execution.StandardError) })
        {
            files.Check(); var bytes = new UTF8Encoding(false, true).GetBytes(text); files.Check();
            files.WriteNew(Path.Combine(directory, leaf), bytes); hashes.Add(leaf, InstalledToolFiles.Identity(bytes));
        }
        return hashes;
    }
    internal string WriteReceipt(string directory, byte[] receiptBytes) => files.Effect(() => WriteReceiptCore(directory, receiptBytes));
    private string WriteReceiptCore(string directory, byte[] receiptBytes)
    {
        files.Check();
        if (receiptBytes.Length > ProducerJson.MaximumBytes) throw new EvidenceWriteException("Complete receipt exceeds finite profile.");
        var validated = DeliveryForge.Contracts.Validation.ContractValidator.ParseAndValidate(receiptBytes);
        files.Check();
        var receiptRoot = Path.Combine(directory, "receipts"); EnsureDirectory(receiptRoot);
        var path = Path.Combine(receiptRoot, "sha256-" + validated.Identity[7..] + ".json");
        // CreateNew and independent first-close: a partial content-addressed target is
        // retained on failure and never adopted/retried as a complete receipt.
        files.WriteNew(path, validated.CanonicalBytes.ToArray()); files.Check(); return path;
    }
    internal void Append(string directory, IDictionary<string, string> hashes) => files.Effect(() => AppendCore(directory, hashes));
    private void AppendCore(string directory, IDictionary<string, string> hashes)
    {
        if (_report is not null) Save("producer-report.json", _report);
        if (changedDiff is not null) Save("producer-changed-lines.diff", changedDiff);
        if (_lines is not null) Save("producer-changed-line-coverage.json", JsonSerializer.SerializeToUtf8Bytes(_lines));
        for (var index = 0; index < owner.Calls.Count; index++)
        {
            var call = owner.Calls[index];
            Save($"process-{index:D2}.stdout.bin", call.Capture.StandardOutputBytes);
            Save($"process-{index:D2}.stderr.bin", call.Capture.StandardErrorBytes);
            Save($"process-{index:D2}.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                call.Invocation.FileName,
                call.Invocation.Arguments,
                call.Invocation.WorkingDirectory,
                Environment = InstalledToolFiles.InventoryIdentity(call.Invocation.Environment ?? new Dictionary<string, string>()),
                call.Result.StartedAt,
                call.Result.CompletedAt,
                call.Result.ExitCode,
                call.Result.TimedOut,
                call.Result.Cancelled,
                call.Result.OwnedProcessQuiescent,
                call.Capture.Timing,
                call.Capture.CaptureBoundExceeded,
                call.Capture.RootExitObserved,
                call.Capture.StandardOutputDrainCompleted,
                call.Capture.StandardErrorDrainCompleted,
                OriginalKnown = owner.Known(call.Result)
            }));
        }
        if (_bundle.Count > 0)
        {
            if (_bundleRoot is null) throw new EvidenceWriteException("Actual complete bundle root is absent.");
            var exported = Path.Combine(directory, "producer-bundle"); EnsureDirectory(exported);
            foreach (var pair in _bundle)
            {
                files.Check(); InstalledToolProfile.Relative(pair.Key);
                var bytes = files.Read(Path.Combine(_bundleRoot, pair.Key), 128 * 1024 * 1024);
                var hash = InstalledToolFiles.Identity(bytes);
                if (bytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + hash != pair.Value)
                    throw new EvidenceWriteException("Complete current bundle changed before immutable evidence export.");
                var target = Path.Combine(exported, pair.Key); EnsureDirectory(Path.GetDirectoryName(target)!);
                files.WriteNew(target, bytes); hashes.Add("producer-bundle/" + pair.Key, hash);
            }
        }
        if (_proof is not null) Save("producer-current-binding.json", _proof);
        for (var index = 0; index < _artifacts.Count; index++) Save($"producer-artifact-{index:D2}.bin", _artifacts[index]);
        if (_failure is not null)
            Save("producer-binding-failure.json", FailureArtifact(_failure, _entered, _returned, _actual?.ExitCode));
        void Save(string leaf, byte[] bytes)
        {
            files.WriteNew(Path.Combine(directory, leaf), bytes);
            hashes[leaf] = InstalledToolFiles.Identity(bytes);
        }
    }

    // The exact projection saved by the actual failure-artifact path. Never serialize
    // arbitrary Exception.Data or independent diagnostic exception messages.
    internal static byte[] FailureArtifact(Exception failure, bool entered, bool returned, int? originalExit) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            Type = failure.GetType().FullName,
            Message = failure.Message.Length > 2048 ? failure.Message[..2048] : failure.Message,
            Entered = entered,
            Returned = returned,
            OriginalExit = originalExit,
            RuntimeAccepted = false,
            InventoryMismatch = InstalledToolFiles.Mismatch(failure),
            InventoryMismatchUnavailable = InstalledToolFiles.MismatchDiagnosticFailure(failure) is not null
        });

    internal NormalizedEvidence Normalize(CommandResult execution, ToolCapability capability) => files.Effect(() => NormalizeCore(execution, capability));
    private NormalizedEvidence NormalizeCore(CommandResult execution, ToolCapability capability)
    {
        if (!_entered || !_returned || _failure is not null || !ReferenceEquals(_actual, execution) || _report is null)
            return NormalizedEvidence.Error("Current installed-tool binding did not complete: " + (_failure?.Message ?? "original observation missing"), profile.Fixture);
        var result = profile.Tool == "mutate4csharp" ? Mutate4CSharpAdapter.NormalizeCurrent(_report, capability, execution.ExitCode!.Value) :
            Crap4CSharpAdapter.NormalizeCurrent(_report, capability, execution.ExitCode!.Value);
        if (profile.Tool == "crap4csharp" && result.Outcome is GateOutcome.Pass or GateOutcome.NotApplicable)
        {
            if (_lines is null) result = NormalizedEvidence.Incomplete("Complete true changed-LINE evidence is absent.", profile.Fixture);
            else if (_lines.Outcome != GateOutcome.Pass)
                result = new(_lines.Outcome, _lines.Reason + " Covered=" + _lines.CoveredChangedLines + "/" + _lines.ChangedExecutableLines,
                    profile.Fixture, false, [.. result.Limitations, "No baseline/exemption or publication authority is inferred."]);
        }
        return result with { ProducerVerified = !profile.Fixture && result.Outcome != GateOutcome.Error };
    }
}
