using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Evidence;
using DeliveryForge.Execution.Host;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Tool.Tests;

// Test-only private issuance: actual finite parent/file/Git conformance, not a
// completed worker, independent reviewer, baseline or publication acceptance.
public sealed class ProductionReviewFiniteAdmissionTests
{
    public static bool Windows => OperatingSystem.IsWindows();

    [Fact(Timeout = 180000, SkipUnless = nameof(Windows), Skip = "Linux native source preparation requires its separately selected owned scope.")]
    public async Task Actual_linked_worktree_prepares_with_the_real_finite_parent_without_multiplicative_metadata_effects()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-linked-review-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository"); var worker = Path.Combine(root, "worker");
        Directory.CreateDirectory(repository);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(180), TestContext.Current.CancellationToken);
        var executor = new ProcessCommandExecutor(); var gate = new object(); var current = true;
        var parent = new ParentEffectAdmission(gate, run => current && run == "owned", _ => current = false);
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, "git.exe")).First(File.Exists);
        async Task<string> Git(string directory, params string[] args)
        {
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(row => !((string)row.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => (string)row.Key, row => row.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = "NUL";
            environment["GIT_CONFIG_SYSTEM"] = "NUL"; environment["GIT_TERMINAL_PROMPT"] = "0";
            var capture = await executor.ExecuteBoundedCaptureAsync(new(git,
                ["-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", "-c", "commit.gpgSign=false", .. args], directory, environment)
            { EnvironmentIsComplete = true }, TimeSpan.FromSeconds(15), 1048576,
                TestContext.Current.CancellationToken, new OwnedWorkPhaseBudget(original));
            Assert.Equal(0, capture.Command.ExitCode); Assert.True(capture.Command.OwnedProcessQuiescent);
            Assert.True(capture.RootExitObserved); Assert.True(capture.StandardOutputDrainCompleted); Assert.True(capture.StandardErrorDrainCompleted);
            Assert.True(capture.Command.LifecycleDiagnostics?.OwnedTree is { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 });
            return Encoding.UTF8.GetString(capture.StandardOutputBytes).Trim();
        }
        var passed = false;
        try
        {
            await Git(repository, "init", "-q", "-b", "main");
            const string policy = """
                {"Fixture":false,"Advisory":false,"AllowedFamilies":[{"Author":"gpt","Reviewer":"claude"}],"AllowedScopes":["source"],"AllowExactReuse":false,"Budget":{"MaximumRounds":2,"MaximumDuration":"00:00:30","MaximumTokens":4096,"MaximumCost":1.0},"ExemptionApprovers":[]}
                """;
            await File.WriteAllTextAsync(Path.Combine(repository, "review.json"), policy, TestContext.Current.CancellationToken);
            // One existing commit adds many ACTUAL distinct loose object bytes;
            // the former ~10 admitted effects/object exhausts the real1024 cap.
            for (var index = 0; index < 256; index++)
                await File.WriteAllTextAsync(Path.Combine(repository, $"inventory-{index}.txt"), $"distinct-owned-object-{index}\n", TestContext.Current.CancellationToken);
            for (var index = 0; index < 12; index++)
            {
                // Retain these genuine historical Git objects. The
                // current source is separately extended below to507 tracked
                // entries, exercising BOTH exact object and source groups.
                if (index == 1)
                    for (var file = 0; file < 256; file++) File.Delete(Path.Combine(repository, $"inventory-{file}.txt"));
                if (index == 2)
                {
                    for (var file = 0; file < 504; file++)
                        await File.WriteAllTextAsync(Path.Combine(repository, $"census-{file}.txt"),
                            file == 1 ? "$Format:%H$\n" : $"complete-source-{file}\n", TestContext.Current.CancellationToken);
                    await File.WriteAllTextAsync(Path.Combine(repository, ".gitattributes"),
                        "census-0.txt export-ignore\ncensus-1.txt export-subst\n", TestContext.Current.CancellationToken);
                }
                await File.WriteAllTextAsync(Path.Combine(repository, "subject.txt"), $"history-{index}\n", TestContext.Current.CancellationToken);
                if (index is 0 or 1 or 2) await Git(repository, "add", "--", ".");
                else await Git(repository, "add", "--", "review.json", "subject.txt");
                await Git(repository, "-c", "user.name=SourceBuilder", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", $"history-{index}");
            }
            var baseCommit = await Git(repository, "rev-parse", "HEAD");
            var manager = new WorktreeManager(git, executor, original).WithParentAdmission(parent, "owned");
            await manager.PrepareAsync(repository, worker, baseCommit, TestContext.Current.CancellationToken);
            using var writer = manager.AcquireWriter(worker, "owned", "actual-source-builder-writer");
            var provisioned = manager.RequireOriginalBinding(worker);
            await File.WriteAllTextAsync(Path.Combine(worker, "subject.txt"), "candidate\n", TestContext.Current.CancellationToken);
            await Git(worker, "add", "--", "subject.txt");
            await Git(worker, "-c", "user.name=SourceBuilder", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "candidate");
            var head = await Git(worker, "rev-parse", "HEAD");
            var workerGit = await Git(worker, "rev-parse", "--absolute-git-dir");
            var commonGit = await Git(worker, "rev-parse", "--path-format=absolute", "--git-common-dir");
            Assert.True(File.Exists(Path.Combine(worker, ".git"))); Assert.NotEqual(workerGit, commonGit);
            var objectFiles = Directory.EnumerateFiles(Path.Combine(commonGit, "objects"), "*", SearchOption.AllDirectories).Count();
            Assert.True(objectFiles >= 507);
            Assert.Equal(507, Directory.EnumerateFiles(worker).Count(path => Path.GetFileName(path) != ".git"));
            Assert.Equal("complete-source-0\n", await File.ReadAllTextAsync(Path.Combine(worker, "census-0.txt"), TestContext.Current.CancellationToken));
            Assert.Equal("$Format:%H$\n", await File.ReadAllTextAsync(Path.Combine(worker, "census-1.txt"), TestContext.Current.CancellationToken));
            DirectoryInfo? source = new(Environment.CurrentDirectory);
            while (source is not null && !File.Exists(Path.Combine(source.FullName, "DeliveryForge.slnx"))) source = source.Parent;
            Assert.NotNull(source);
            var contracts = Path.Combine(source.FullName, "components", "contracts", "DeliveryForge.Contracts.Tests", "Fixtures", "Valid");
            var plan = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(contracts, "plan.json"), TestContext.Current.CancellationToken))!.AsObject();
            plan["baseCommit"] = baseCommit;
            plan["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(plan.ToJsonString()));
            var planBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(plan.ToJsonString()));
            var evidenceBytes = await File.ReadAllBytesAsync(Path.Combine(contracts, "evidence-policy.json"), TestContext.Current.CancellationToken);
            var planIdentity = ContractValidator.ParseAndValidate(planBytes).Identity;
            var evidenceIdentity = ContractValidator.ParseAndValidate(evidenceBytes).Identity;
            var image = await File.ReadAllBytesAsync(git, TestContext.Current.CancellationToken);
            var owner = new ProductionReviewSourceOwner(executor, original, parent, "owned", worker, workerGit, commonGit,
                git, image.Length, Convert.ToHexStringLower(SHA256.HashData(image)), await Git(worker, "--version"),
                baseCommit, head, () => true, planIdentity, evidenceIdentity, "review.json", 1048576, provisioned);
            using var bootstrap = await ReviewAuthorityHost.CreateProductionFromOwnedSourceAsync(owner, planBytes, evidenceBytes,
                "review.json", 1048576, TestContext.Current.CancellationToken);
            Assert.Equal(baseCommit, bootstrap.Policy.SourceRevision); Assert.Equal(head, bootstrap.ObjectView.FrozenHeadCommit);
            Assert.True(current); Assert.False(owner.Uncertain); Assert.False(original.HasExplicitUnknown);
            Assert.InRange(parent.EnteredCount, 1, 1023); Assert.True(parent.AllKnownSettled);
            TestContext.Current.TestOutputHelper!.WriteLine($"Actual linked object files={objectFiles}; finite parent effects={parent.EnteredCount}/1024.");
            var controlledView = bootstrap.ObjectView.ControlledGitDirectory;
            bootstrap.DisposeDuringWork(); bootstrap.DisposeDuringWork(); bootstrap.Dispose();
            original.Check(); Assert.False(Directory.Exists(controlledView));
            Assert.True(parent.AllKnownSettled); Assert.False(original.HasExplicitUnknown);
            passed = true;
        }
        finally
        {
            if (passed && !original.HasExplicitUnknown)
            {
                _ = original.CleanupRemaining;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                { _ = original.CleanupRemaining; File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly); }
                _ = original.CleanupRemaining; Directory.Delete(root, recursive: true); _ = original.CleanupRemaining;
            }
            else TestContext.Current.TestOutputHelper!.WriteLine("Retained actual source-preparation failure namespace: " + root);
        }
    }

    [Fact]
    public async Task Exhausted_existing_parent_refuses_before_file_birth_without_false_unknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-finite-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git")); var image = Path.Combine(root, "inert-image.data");
        File.WriteAllText(image, "inert exact owned image; never launched");
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var gate = new object(); var current = true;
        var parent = new ParentEffectAdmission(gate, run => current && run == "owned", _ => current = false);
        try
        {
            var bytes = File.ReadAllBytes(image);
            var owner = new ProductionReviewSourceOwner(new ProcessCommandExecutor(), original, parent, "owned", root,
                Path.Combine(root, ".git"), Path.Combine(root, ".git"), image, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                "git version inert", new('a', 40), new('b', 40), () => true, "sha256:" + new string('a', 64),
                "sha256:" + new string('b', 64), "review.json", 1024);
            while (parent.EnteredCount < 1024) parent.EnterSynchronous("owned", () => true);
            var target = Path.Combine(root, "must-not-be-born");
            await Assert.ThrowsAsync<IOException>(() => owner.WriteFileAsync(target, [1], TestContext.Current.CancellationToken));
            Assert.False(File.Exists(target)); Assert.False(owner.Uncertain); Assert.False(original.HasExplicitUnknown);
            Assert.False(current); Assert.Equal(1024, parent.EnteredCount); Assert.True(parent.AllKnownSettled);
        }
        finally { _ = original.CleanupRemaining; Directory.Delete(root, recursive: true); }
    }
}
