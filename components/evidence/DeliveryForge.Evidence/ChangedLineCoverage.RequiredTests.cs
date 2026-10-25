using System.Globalization;
using System.Text.Json;

namespace DeliveryForge.Evidence;

internal static partial class ChangedLineCoverage
{
    // Classification DATA after the complete actual producer join. Required tests
    // stay unscored; this does not change production line/context membership.
    private static HashSet<string> ReadRequiredTestChangedSources(JsonElement[] artifacts,
        IReadOnlyDictionary<string, JsonElement> builds, IEnumerable<JsonElement> executions,
        string repository, IEnumerable<string> changes, IReadOnlyList<string>? approvedTestProjects,
        Func<string, byte[]> readArtifact, InstalledToolFiles files)
    {
        var recognized = new HashSet<string>(InstalledToolFiles.PathComparer);
        if (approvedTestProjects is null || approvedTestProjects.Count == 0) return recognized;
        if (approvedTestProjects.Count > 1024) throw Refuse("Required-test project membership exceeds its finite profile.");
        var approved = new HashSet<string>(InstalledToolFiles.PathComparer);
        foreach (var project in approvedTestProjects)
        {
            files.Check(); InstalledToolProfile.Relative(project);
            if (!project.EndsWith(".csproj", StringComparison.Ordinal) || !approved.Add(project))
                throw Refuse("Required-test project selection is malformed or duplicated.");
        }
        var changed = new HashSet<string>(changes, InstalledToolFiles.PathComparer);
        var indexed = Index(artifacts, files);
        byte[] Read(string id, string kind, int maximum = MaximumArtifactBytes)
        {
            files.Check();
            if (!indexed.TryGetValue(id, out var row) || Text(row, "kind") != kind ||
                Integer(row, "length") < 0 || Integer(row, "length") > maximum || !RequiredTestHash(Text(row, "sha256")))
                throw Refuse("Required-test artifact identity, role or complete extent differs.");
            var locator = Text(row, "locator"); InstalledToolProfile.Relative(locator);
            var bytes = readArtifact(locator); files.Check();
            if (bytes.LongLength != Integer(row, "length") || InstalledToolFiles.Identity(bytes) != "sha256:" + Text(row, "sha256"))
                throw Refuse("Required-test artifact bytes differ from the actual manifest.");
            return bytes;
        }
        var rows = artifacts.Where(row => Text(row, "kind") == "required-test-inventory").Take(2).ToArray();
        if (rows.Length != 1) throw Refuse("One complete required-test inventory is required.");
        using var inventory = ProducerJson.Read(Read(Text(rows[0], "id"), "required-test-inventory", 262144));
        var root = inventory.RootElement; RequiredTestFields(root, "provider", "tests", "reusableRecipe");
        if (Text(root, "provider") != "finite-sdk-required-test-inventory-v1" || Boolean(root, "reusableRecipe"))
            throw Refuse("Unsupported required-test inventory.");
        var tests = Array(root, "tests").ToArray();
        if (tests.Length is 0 or > 1024) throw Refuse("Required-test membership is absent or excessive.");
        var graphIds = new HashSet<string>(StringComparer.Ordinal);
        var observedProjects = new HashSet<string>(InstalledToolFiles.PathComparer);
        var actualExecutions = executions.ToArray();
        foreach (var test in tests)
        {
            files.Check();
            var graphId = Text(test, "graphId"); var project = Text(test, "project"); InstalledToolProfile.Relative(project);
            if (!RequiredTestHash(graphId) || !graphIds.Add(graphId) || !approved.Contains(project) || Integer(test, "rawExitCode") != 0)
                throw Refuse("Required-test inventory differs from the approved exact projects.");
            observedProjects.Add(project);
            var prefix = "required-test-" + graphId;
            if (Text(test, "assembly") != prefix + "-pe" || Text(test, "pdb") != prefix + "-pdb")
                throw Refuse("Required-test compiler artifact associations differ.");
            var assembly = Read(prefix + "-pe", "required-test-assembly"); var pdb = Read(prefix + "-pdb", "required-test-pdb");
            var linked = actualExecutions.Where(row => OptionalText(row, "testProject") is { } path &&
                InstalledToolFiles.PathComparer.Equals(path, project)).ToArray();
            if (linked.Length == 0) throw Refuse("Required test lacks an actual linked completed execution.");
            var linkedModules = new HashSet<string>(StringComparer.Ordinal);
            foreach (var execution in linked)
            {
                files.Check(); var buildId = Text(execution, "buildId");
                if (!buildId.StartsWith("build-", StringComparison.Ordinal) || !RequiredTestHash(buildId[6..]) ||
                    !builds.TryGetValue(buildId, out var build) || !RequiredTestHash(Text(execution, "contextId")) ||
                    Text(execution, "contextId") != Text(build, "contextId") ||
                    Text(execution, "id") != "test-" + buildId[6..] + "-" + graphId || !linkedModules.Add(buildId[6..]) ||
                    !Boolean(execution, "completed") || Integer(execution, "exitCode") != 0 || Integer(test, "passed") < 1 ||
                    Integer(execution, "totalTests") != Integer(test, "passed") || Integer(execution, "passedTests") != Integer(test, "passed") ||
                    Integer(execution, "failedTests") != 0 || Integer(execution, "skippedTests") != 0 ||
                    Text(execution, "testModuleIdentity") != Text(test, "moduleIdentity") || Text(execution, "testMvid") != Text(test, "mvid") ||
                    Text(execution, "testDebugIdentity") != Text(test, "debugIdentity") ||
                    "sha256:" + Text(execution, "testAssemblySha256") != InstalledToolFiles.Identity(assembly) ||
                    "sha256:" + Text(execution, "testPdbSha256") != InstalledToolFiles.Identity(pdb))
                    throw Refuse("Required-test graph/execution and exact compiler artifacts disagree.");
            }
            var modules = Array(test, "modules").ToArray();
            if (modules.Length is 0 or > 1024) throw Refuse("Required-test production links are absent or excessive.");
            var moduleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in modules)
            {
                files.Check(); var id = Text(module, "graphId");
                if (!RequiredTestHash(id) || !moduleIds.Add(id) || Text(module, "artifact") != prefix + "-coverage-" + id)
                    throw Refuse("Required-test production-module links differ.");
            }
            if (!moduleIds.SetEquals(linkedModules)) throw Refuse("Required-test module and execution membership disagree.");
            using var closure = ProducerJson.Read(Read("test-closure-" + graphId, "test-compiler-input-closure", ProducerJson.MaximumBytes));
            var body = closure.RootElement; RequiredTestFields(body, "provider", "project", "inputs", "reusableRecipe");
            if (Text(body, "provider") != "validated-csc-inputs-v1" || Text(body, "project") != project || Boolean(body, "reusableRecipe"))
                throw Refuse("Required-test compiler closure differs from its selected project.");
            var inputs = Array(body, "inputs").ToArray();
            if (inputs.Length is 0 or > 1000000) throw Refuse("Required-test compiler inputs exceed the finite profile.");
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(repository, project)))!;
            var documents = new List<CompilerLineCensus.Document>(); var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
            var logicalPaths = new HashSet<string>(StringComparer.Ordinal); var physicalPaths = new HashSet<string>(InstalledToolFiles.PathComparer);
            var sourceIndex = 0;
            foreach (var input in inputs)
            {
                files.Check(); if (Text(input, "role") != "compiler-source") continue;
                RequiredTestFields(input, "role", "logicalPath", "locator", "sha256", "length", "generated");
                var logical = Text(input, "logicalPath"); InstalledToolProfile.Relative(logical);
                var physical = Path.GetFullPath(Path.Combine(projectDirectory, logical));
                if (!logicalPaths.Add(logical) || !physicalPaths.Add(physical) || !InstalledToolGateProvider.Inside(physical, repository))
                    throw Refuse("Required-test source roles are ambiguous or escape the original repository.");
                var sourceId = "test-source-" + graphId + "-" + (sourceIndex++).ToString(CultureInfo.InvariantCulture);
                var raw = Read(sourceId, "test-compiler-source"); var artifact = indexed[sourceId];
                if (Text(input, "locator") != Text(artifact, "locator") || Integer(input, "length") != raw.LongLength ||
                    Text(input, "sha256") != Text(artifact, "sha256")) throw Refuse("Required-test captured source and closure disagree.");
                var relative = Path.GetRelativePath(repository, physical).Replace('\\', '/'); var generated = Boolean(input, "generated");
                string? repositoryPath = null;
                if (!generated && changed.Contains(relative))
                {
                    InstalledToolProfile.Relative(relative);
                    if (!files.Read(physical, MaximumArtifactBytes).AsSpan().SequenceEqual(raw))
                        throw Refuse("Current required-test source differs from its captured compiler bytes.");
                    repositoryPath = relative; candidates.Add(logical, relative);
                }
                documents.Add(new(logical, repositoryPath, physical, Text(input, "sha256"), Decode(raw), generated));
            }
            if (documents.Count == 0) throw Refuse("Required-test compiler source membership is absent.");
            var census = CompilerLineCensus.Inspect(assembly, pdb,
                new(Text(test, "moduleIdentity"), Text(test, "mvid"), Text(test, "debugIdentity")), documents, files);
            // Whole unscored test-census completeness is not required. Only exact
            // nonhidden points recognize a current non-generated changed source.
            // #line/checksum remapping returns no points and grants no exception.
            foreach (var point in census.Points)
                if (candidates.TryGetValue(point.LogicalPath, out var relative)) recognized.Add(relative);
        }
        if (!observedProjects.SetEquals(approved)) throw Refuse("Required-test inventory omits an approved project.");
        files.Check(); return recognized;
    }
    private static bool RequiredTestHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void RequiredTestFields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Refuse("Malformed required-test object.");
        var fields = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Remove(property.Name)) throw Refuse("Unknown or duplicate required-test field.");
        if (fields.Count != 0) throw Refuse("Incomplete required-test object.");
    }
}
