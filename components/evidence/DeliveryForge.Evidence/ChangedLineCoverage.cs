using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DeliveryForge.Evidence;

// DATA reduction only. Called privately only after the original installed check and every
// manifest artifact's actual byte/hash join. No caller document can create ProducerVerified.
internal static partial class ChangedLineCoverage
{
    private const int MaximumArtifactBytes = 128 * 1024 * 1024; // Existing operative complete artifact-file bound; JSON reports remain8MiB.
    internal sealed record Result(int ChangedExecutableLines, int CoveredChangedLines, decimal? Percent,
        GateOutcome Outcome, string Reason, string DiffSha256);

    internal static Result Read(byte[] diff, JsonElement manifest, string repository,
        Func<string, byte[]> readArtifact, InstalledToolFiles files)
    {
        files.Check();
        var changes = ReadHunks(diff);
        var comparer = InstalledToolFiles.PathComparer;
        var sources = new Dictionary<string, Dictionary<string, Source>>(StringComparer.Ordinal);
        var contexts = manifest.GetProperty("contexts").EnumerateArray().ToArray();
        if (contexts.Length is 0 or > 10000) throw Refuse("Context membership is absent or exceeds the finite profile.");
        foreach (var context in contexts)
        {
            files.Check();
            var id = context.GetProperty("id").GetString()!;
            var documents = new Dictionary<string, Source>(comparer);
            if (!sources.TryAdd(id, documents)) throw Refuse("Duplicate coverage context.");
            foreach (var input in context.GetProperty("inputs").EnumerateArray())
            {
                files.Check();
                if (input.GetProperty("role").GetString() != "source") continue;
                var logical = input.GetProperty("logicalPath").GetString()!;
                var locator = input.GetProperty("locator").GetString()!;
                InstalledToolProfile.Relative(locator);
                var raw = readArtifact(locator);
                if (raw.LongLength != input.GetProperty("length").GetInt64() ||
                    InstalledToolFiles.Identity(raw) != "sha256:" + input.GetProperty("sha256").GetString())
                    throw Refuse("Captured source bytes differ from the current manifest.");
                var repositoryPath = input.TryGetProperty("repositoryPath", out var rp) && rp.ValueKind == JsonValueKind.String ? rp.GetString() : null;
                var generated = input.GetProperty("generated").GetBoolean();
                string? path = null;
                if (!generated && repositoryPath is not null)
                {
                    InstalledToolProfile.Relative(repositoryPath);
                    path = Path.GetFullPath(Path.Combine(repository, repositoryPath));
                    if (!InstalledToolGateProvider.Inside(path, repository)) throw Refuse("Source escaped the original repository.");
                    // Complete physical source equality, not a path-only or report-only join.
                    var actual = files.Read(path, MaximumArtifactBytes);
                    if (!actual.AsSpan().SequenceEqual(raw)) throw Refuse("Current authored source differs from captured source.");
                }
                files.Check();
                var encoding = input.TryGetProperty("encoding", out var enc) ? enc.GetString() : null;
                if (encoding is not (null or "utf-8" or "utf8" or "UTF-8"))
                    throw Refuse("This line profile cannot determine captured source encoding.");
                var text = new UTF8Encoding(false, true).GetString(raw);
                var lines = text.Count(c => c == '\n') + 1;
                if (!documents.TryAdd(logical, new(path, repositoryPath, lines))) throw Refuse("Duplicate logical source path.");
            }
        }
        var executable = new Dictionary<string, Dictionary<string, bool>>(comparer);
        var coveredContexts = new HashSet<string>(StringComparer.Ordinal);
        var builds = manifest.GetProperty("builds").EnumerateArray().ToDictionary(b => b.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var executions = manifest.GetProperty("executions").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var reports = 0; var pointCount = 0;
        foreach (var artifact in manifest.GetProperty("artifacts").EnumerateArray())
        {
            files.Check();
            if (artifact.GetProperty("kind").GetString() != "coverage") continue;
            if (++reports > 10000 || artifact.GetProperty("format").GetString() != "opencover" ||
                artifact.GetProperty("coordinateKind").GetString() != "line") throw Refuse("Coverage is not the actual supported line projection.");
            var contextId = artifact.GetProperty("contextId").GetString()!;
            var buildId = artifact.GetProperty("buildId").GetString()!;
            var executionId = artifact.GetProperty("executionId").GetString()!;
            if (!sources.TryGetValue(contextId, out var sourceSet) || !builds.TryGetValue(buildId, out var build) ||
                build.GetProperty("contextId").GetString() != contextId || !executions.TryGetValue(executionId, out var execution) ||
                execution.GetProperty("contextId").GetString() != contextId || execution.GetProperty("buildId").GetString() != buildId ||
                !execution.GetProperty("completed").GetBoolean() || execution.GetProperty("exitCode").GetInt32() != 0)
                throw Refuse("Line report lacks the same completed context/build/test execution.");
            var bytes = readArtifact(artifact.GetProperty("locator").GetString()!);
            if (bytes.LongLength != artifact.GetProperty("length").GetInt64() ||
                InstalledToolFiles.Identity(bytes) != "sha256:" + artifact.GetProperty("sha256").GetString()) throw Refuse("Line artifact bytes changed.");
            files.Check();
            if (bytes.Length > MaximumArtifactBytes) throw Refuse("Complete line artifact exceeds the existing operative artifact bound.");
            using (var shapeStream = new MemoryStream(bytes, writable: false))
            using (var shape = XmlReader.Create(shapeStream, new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumArtifactBytes,
                MaxCharactersFromEntities = 0
            }))
            {
                var nodes = 0;
                while (shape.Read())
                {
                    files.Check();
                    if (shape.Depth > 64 || ++nodes > 1000000 || shape.AttributeCount > 32)
                        throw Refuse("Finite complete line XML structure bound exceeded.");
                }
            }
            using var stream = new MemoryStream(bytes, writable: false);
            using var xml = XmlReader.Create(stream, new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumArtifactBytes,
                MaxCharactersFromEntities = 0
            });
            var document = XDocument.Load(xml, LoadOptions.None);
            files.Check();
            var module = document.Root?.Element("Modules")?.Elements("Module").Single() ?? throw Refuse("Missing single line module.");
            if (document.Root!.Name != "CoverageSession" || document.Descendants("Module").Count() != 1 ||
                module.Element("ModuleName")?.Value != build.GetProperty("moduleIdentity").GetString()) throw Refuse("Line module differs from the actual build.");
            var byId = new Dictionary<string, Source>(StringComparer.Ordinal);
            foreach (var file in module.Element("Files")!.Elements("File"))
            {
                files.Check();
                var physical = file.Attribute("fullPath")?.Value ?? throw Refuse("Missing line document path.");
                if (!Path.IsPathFullyQualified(physical)) throw Refuse("Line document is not the captured physical path.");
                var matched = sourceSet.Values.Where(s => s.PhysicalPath is not null && comparer.Equals(s.PhysicalPath, Path.GetFullPath(physical))).ToArray();
                // Generated/nonrepository files are not silently accepted as authored changed
                // evidence: this profile remains incomplete until their exact path model exists.
                if (matched.Length != 1 || !byId.TryAdd(file.Attribute("uid")!.Value, matched[0])) throw Refuse("Ambiguous/unmapped line document.");
            }
            foreach (var method in module.Descendants("Method"))
            {
                files.Check();
                var fileId = method.Descendants("FileRef").Single().Attribute("uid")!.Value;
                if (!byId.TryGetValue(fileId, out var source)) throw Refuse("Method has a foreign line document.");
                foreach (var point in method.Descendants("SequencePoint"))
                {
                    files.Check();
                    if (++pointCount > 1000000 || point.Attribute("fileid")?.Value != fileId ||
                        point.Attribute("sc") is not null || point.Attribute("ec") is not null || point.Attribute("el") is not null || point.Attribute("offset") is not null ||
                        !int.TryParse(point.Attribute("sl")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line) ||
                        line <= 0 || line > source.Lines || line >= 0xFEEFEE ||
                        !int.TryParse(point.Attribute("vc")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var visits) || visits < 0)
                        throw Refuse("Malformed actual line counter.");
                    if (source.RepositoryPath is null || !changes.TryGetValue(source.RepositoryPath, out var added) || !added.Contains(line)) continue;
                    if (!executable.TryGetValue(source.RepositoryPath + ":" + line.ToString(CultureInfo.InvariantCulture), out var perContext))
                        executable.Add(source.RepositoryPath + ":" + line.ToString(CultureInfo.InvariantCulture), perContext = new(StringComparer.Ordinal));
                    // Repeated executions OR their line hits within the SAME context. Separate
                    // contexts are conjoined below; one target cannot conceal another's miss.
                    perContext[contextId] = perContext.GetValueOrDefault(contextId) || visits > 0;
                }
            }
            coveredContexts.Add(contextId);
        }
        foreach (var path in changes.Keys)
            if (!sources.Values.Any(set => set.Values.Any(s => comparer.Equals(s.RepositoryPath, path))))
                throw Refuse("Changed authored file is not in the complete captured compiler source set.");
        foreach (var context in contexts)
            if (context.GetProperty("inputs").EnumerateArray().Any(i => i.GetProperty("role").GetString() == "source" &&
                i.TryGetProperty("repositoryPath", out var rp) && rp.ValueKind == JsonValueKind.String && changes.ContainsKey(rp.GetString()!)) &&
                !coveredContexts.Contains(context.GetProperty("id").GetString()!))
                throw Refuse("Changed context has no complete actual line report.");
        files.Check();
        var count = executable.Count; var hit = executable.Values.Count(contextsForLine => contextsForLine.Values.All(v => v));
        decimal? percent = count == 0 ? null : 100m * hit / count;
        return new(count, hit, percent, count == 0 ? GateOutcome.Incomplete : percent >= 80m ? GateOutcome.Pass : GateOutcome.Fail,
            count == 0 ? "No measurable changed executable lines; no exemption or N/A is inferred." : "Actual changed executable LINE coverage (distinct lines; all observed contexts).", InstalledToolFiles.Identity(diff));
    }

    private sealed record Source(string? PhysicalPath, string? RepositoryPath, int Lines);
    internal static Dictionary<string, HashSet<int>> ReadHunks(byte[] diff)
    {
        if (diff.Length > ProducerJson.MaximumBytes) throw Refuse("Complete source diff exceeds capture profile.");
        var text = new UTF8Encoding(false, true).GetString(diff);
        var result = new Dictionary<string, HashSet<int>>(InstalledToolFiles.PathComparer);
        string? path = null; var oldRemaining = 0; var newRemaining = 0; var next = 0; var changed = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal)) continue;
            if (oldRemaining != 0 || newRemaining != 0)
            {
                if (line.StartsWith('+') && newRemaining-- > 0)
                {
                    if (path is null || ++changed > 1000000) throw Refuse("Changed-line membership invalid.");
                    if (!result.TryGetValue(path, out var set)) result.Add(path, set = []);
                    if (!set.Add(next++)) throw Refuse("Overlapping changed-line hunks.");
                }
                else if (line.StartsWith('-') && oldRemaining-- > 0) { }
                else throw Refuse("Nonzero-context or truncated diff is unsupported.");
                continue;
            }
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (line == "+++ /dev/null") { path = null; continue; }
                if (!line.StartsWith("+++ b/", StringComparison.Ordinal)) throw Refuse("Quoted or foreign Git path.");
                path = line[6..]; InstalledToolProfile.Relative(path);
                if (!path.EndsWith(".cs", StringComparison.Ordinal)) throw Refuse("Unexpected non-C# diff member.");
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var h = Hunk().Match(line); if (!h.Success) throw Refuse("Malformed hunk.");
                oldRemaining = Number(h.Groups[2].Success ? h.Groups[2].Value : "1");
                next = Number(h.Groups[3].Value); newRemaining = Number(h.Groups[4].Success ? h.Groups[4].Value : "1");
                if (newRemaining > 0 && (next == 0 || path is null)) throw Refuse("Invalid new-side hunk.");
            }
            else if (line.StartsWith('+') || line.StartsWith('-') && !line.StartsWith("--- ", StringComparison.Ordinal)) throw Refuse("Unexpected changed-line body outside a hunk.");
            else if (line.StartsWith("Binary files", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal)) throw Refuse("Binary source diff.");
        }
        if (oldRemaining != 0 || newRemaining != 0) throw Refuse("Incomplete hunk body.");
        return result;
    }
    private static int Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= 1000000 ? number : throw Refuse("Hunk extent exceeds profile.");
    private static EvidenceWriteException Refuse(string message) => new("Changed-LINE coverage is incomplete: " + message);
    [GeneratedRegex("^@@ -(\\d+)(?:,(\\d+))? \\+(\\d+)(?:,(\\d+))? @@(?:.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Hunk();
}
