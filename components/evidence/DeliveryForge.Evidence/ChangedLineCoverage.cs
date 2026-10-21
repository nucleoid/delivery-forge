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
        GateOutcome Outcome, string Reason, string DiffSha256, int UnmeasuredChangedLines = 0, bool CompilerCensusComplete = false);

    internal static Result Read(byte[] diff, JsonElement manifest, string repository,
        Func<string, byte[]> readArtifact, InstalledToolFiles files) => ReadCore(diff, manifest, repository, readArtifact, files, false);

    internal static Result ReadCompilerBound(byte[] diff, JsonElement manifest, string repository,
        Func<string, byte[]> readArtifact, InstalledToolFiles files) => ReadCore(diff, manifest, repository, readArtifact, files, true);

    private static Result ReadCore(byte[] diff, JsonElement manifest, string repository,
        Func<string, byte[]> readArtifact, InstalledToolFiles files, bool requireCompilerCensus)
    {
        files.Check();
        var changes = ReadHunks(diff);
        var comparer = InstalledToolFiles.PathComparer;
        var sources = new Dictionary<string, Dictionary<string, Source>>(StringComparer.Ordinal);
        var contexts = Array(manifest, "contexts").ToArray();
        if (contexts.Length is 0 or > 10000) throw Refuse("Context membership is absent or exceeds the finite profile.");
        foreach (var context in contexts)
        {
            files.Check();
            var id = Text(context, "id");
            var documents = new Dictionary<string, Source>(comparer);
            if (!sources.TryAdd(id, documents)) throw Refuse("Duplicate coverage context.");
            foreach (var input in Array(context, "inputs"))
            {
                files.Check();
                if (Text(input, "role") is not ("source" or "compiler-source")) continue;
                var logical = Text(input, "logicalPath"); InstalledToolProfile.Relative(logical);
                var locator = Text(input, "locator");
                InstalledToolProfile.Relative(locator);
                var raw = readArtifact(locator);
                if (raw.LongLength != Integer(input, "length") ||
                    InstalledToolFiles.Identity(raw) != "sha256:" + Text(input, "sha256"))
                    throw Refuse("Captured source bytes differ from the current manifest.");
                var repositoryPath = OptionalText(input, "repositoryPath");
                var generated = Boolean(input, "generated");
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
                var encoding = OptionalText(input, "encoding");
                if (encoding is not (null or "utf-8" or "utf8" or "UTF-8"))
                    throw Refuse("This line profile cannot determine captured source encoding.");
                var text = Decode(raw);
                var lines = PhysicalLines(text, files);
                if (!documents.TryAdd(logical, new(path, repositoryPath, lines, logical, Text(input, "sha256"), text, generated)
                    { CompilerPhysicalRole = path })) throw Refuse("Duplicate logical source path.");
            }
            if (requireCompilerCensus) BindCompilerPhysicalRoles(context, repository, documents, files);
        }
        var executable = new Dictionary<string, Dictionary<string, bool>>(comparer);
        var coveredContexts = new HashSet<string>(StringComparer.Ordinal);
        var reportedDocuments = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var builds = Index(Array(manifest, "builds"), files);
        var executions = Index(Array(manifest, "executions"), files);
        var artifacts = Array(manifest, "artifacts").ToArray();
        if (artifacts.Length > 1_000_000) throw Refuse("Complete artifact membership exceeds the existing point ceiling.");
        var required = new Dictionary<string, HashSet<string>>(comparer);
        var providerLines = new Dictionary<string, HashSet<string>>(comparer);
        var completeContexts = new HashSet<string>(StringComparer.Ordinal);
        var membershipSteps = 0;
        void MembershipStep()
        {
            files.Check();
            if (++membershipSteps > 1_000_000) throw Refuse("Complete compiler line/context membership exceeds the existing finite profile.");
        }
        var censusComplete = requireCompilerCensus;
        string? censusReason = null;
        if (requireCompilerCensus)
        {
            foreach (var context in sources)
            {
                files.Check();
                if (!context.Value.Values.Any(source => !source.Generated && source.RepositoryPath is { } path && changes.ContainsKey(path))) continue;
                var selected = builds.Values.Where(build => Text(build, "contextId") == context.Key).Take(2).ToArray();
                if (selected.Length != 1) throw Refuse("Changed compiler context lacks one exact actual build.");
                var build = selected[0]; var buildId = Text(build, "id");
                byte[] ReadBuildArtifact(string role, string hashField)
                {
                    files.Check();
                    var rows = artifacts.Where(row => Text(row, "kind") == role &&
                        OptionalText(row, "contextId") == context.Key && OptionalText(row, "buildId") == buildId).Take(2).ToArray();
                    if (rows.Length != 1) throw Refuse("Compiler context lacks unique actual assembly/PDB artifact roles.");
                    var row = rows[0]; var locator = Text(row, "locator"); InstalledToolProfile.Relative(locator);
                    var bytes = readArtifact(locator);
                    if (bytes.Length > MaximumArtifactBytes || bytes.LongLength != Integer(row, "length") ||
                        Text(row, "sha256") != Text(build, hashField) || InstalledToolFiles.Identity(bytes) != "sha256:" + Text(row, "sha256"))
                        throw Refuse("Actual compiler artifact length/hash differs from the exact manifest build.");
                    files.Check(); return bytes;
                }
                var inventory = CompilerLineCensus.Inspect(ReadBuildArtifact("assembly", "assemblySha256"),
                    ReadBuildArtifact("pdb", "pdbSha256"), new(Text(build, "moduleIdentity"), Text(build, "mvid"), Text(build, "debugIdentity")),
                    context.Value.Values.Select(source => new CompilerLineCensus.Document(source.LogicalPath, source.RepositoryPath,
                        source.CompilerPhysicalRole, source.Sha256, source.Text, source.Generated)).ToArray(), files);
                if (!inventory.Complete) { censusComplete = false; censusReason ??= inventory.Reason; }
                else completeContexts.Add(context.Key);
                foreach (var row in inventory.ProviderLines)
                {
                    MembershipStep();
                    var source = context.Value[row.LogicalPath];
                    if (source.Generated || source.RepositoryPath is not { } path || !changes.TryGetValue(path, out var changed) || !changed.Contains(row.Number)) continue;
                    var key = path + ":" + row.Number.ToString(CultureInfo.InvariantCulture);
                    if (!providerLines.TryGetValue(key, out var membership)) providerLines.Add(key, membership = new(StringComparer.Ordinal));
                    membership.Add(context.Key);
                }
                foreach (var row in inventory.PolicyLines)
                {
                    MembershipStep();
                    var source = context.Value[row.LogicalPath];
                    if (source.Generated || source.RepositoryPath is not { } path || !changes.TryGetValue(path, out var changed) || !changed.Contains(row.Number)) continue;
                    var key = path + ":" + row.Number.ToString(CultureInfo.InvariantCulture);
                    if (!required.TryGetValue(key, out var membership)) required.Add(key, membership = new(StringComparer.Ordinal));
                    membership.Add(context.Key);
                }
            }
        }
        var reports = 0; var pointCount = 0;
        foreach (var artifact in artifacts)
        {
            files.Check();
            if (Text(artifact, "kind") != "coverage") continue;
            if (++reports > 10000 || Text(artifact, "format") != "opencover" ||
                Text(artifact, "coordinateKind") != "line") throw Refuse("Coverage is not the actual supported line projection.");
            var contextId = Text(artifact, "contextId");
            var buildId = Text(artifact, "buildId");
            var executionId = Text(artifact, "executionId");
            if (!sources.TryGetValue(contextId, out var sourceSet) || !builds.TryGetValue(buildId, out var build) ||
                Text(build, "contextId") != contextId || !executions.TryGetValue(executionId, out var execution) ||
                Text(execution, "contextId") != contextId || Text(execution, "buildId") != buildId ||
                !Boolean(execution, "completed") || Integer(execution, "exitCode") != 0)
                throw Refuse("Line report lacks the same completed context/build/test execution.");
            var locator = Text(artifact, "locator"); InstalledToolProfile.Relative(locator);
            var bytes = readArtifact(locator);
            if (bytes.LongLength != Integer(artifact, "length") ||
                InstalledToolFiles.Identity(bytes) != "sha256:" + Text(artifact, "sha256")) throw Refuse("Line artifact bytes changed.");
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
                try
                {
                    while (shape.Read())
                    {
                        files.Check();
                        if (shape.Depth > 64 || ++nodes > 1000000 || shape.AttributeCount > 32)
                            throw Refuse("Finite complete line XML structure bound exceeded.");
                    }
                }
                catch (XmlException error) { throw Refuse("Malformed line XML.", error); }
            }
            using var stream = new MemoryStream(bytes, writable: false);
            using var xml = XmlReader.Create(stream, new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumArtifactBytes,
                MaxCharactersFromEntities = 0
            });
            XDocument document;
            try { document = XDocument.Load(xml, LoadOptions.None); }
            catch (XmlException error) { throw Refuse("Malformed line XML.", error); }
            files.Check();
            if (document.Root?.Name != "CoverageSession") throw Refuse("Missing line coverage root.");
            var module = One(One(document.Root.Elements("Modules"), "Modules").Elements("Module"), "Module");
            if (document.Descendants("Module").Count() != 1 ||
                One(module.Elements("ModuleName"), "ModuleName").Value != Text(build, "moduleIdentity")) throw Refuse("Line module differs from the actual build.");
            var byId = new Dictionary<string, Source>(StringComparer.Ordinal);
            if (!reportedDocuments.TryGetValue(contextId, out var observed))
                reportedDocuments.Add(contextId, observed = new(comparer));
            foreach (var file in One(module.Elements("Files"), "Files").Elements("File"))
            {
                files.Check();
                var physical = Attribute(file, "fullPath");
                var fullPath = PhysicalPath(physical);
                var matched = sourceSet.Values.Where(s => s.PhysicalPath is not null && comparer.Equals(s.PhysicalPath, fullPath)).ToArray();
                // Generated/nonrepository files are not silently accepted as authored changed
                // evidence: this profile remains incomplete until their exact path model exists.
                if (matched.Length != 1 || !byId.TryAdd(Attribute(file, "uid"), matched[0])) throw Refuse("Ambiguous/unmapped line document.");
                if (matched[0].RepositoryPath is { } reported) observed.Add(reported);
            }
            foreach (var method in module.Descendants("Method"))
            {
                files.Check();
                var fileId = Attribute(One(method.Descendants("FileRef"), "FileRef"), "uid");
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
                    var key = source.RepositoryPath + ":" + line.ToString(CultureInfo.InvariantCulture);
                    if (requireCompilerCensus && completeContexts.Contains(contextId))
                    {
                        if (!providerLines.TryGetValue(key, out var providers) || !providers.Contains(contextId))
                            throw Refuse("A reported changed line is outside the independently captured compiler provider inventory.");
                        // The exact provider envelope may contain comment/blank
                        // or exclusive-endpoint lines. Validated H-minus-E rows
                        // never create denominator membership or measured hits.
                        if (!required.TryGetValue(key, out var members) || !members.Contains(contextId)) continue;
                    }
                    if (!executable.TryGetValue(key, out var perContext))
                        executable.Add(key, perContext = new(StringComparer.Ordinal));
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
        var unmeasured = new HashSet<string>(comparer);
        // Raw report DATA has no independent executable-line inventory. In compiler
        // mode only required line/context membership can establish missing measurement.
        if (!requireCompilerCensus)
        {
            foreach (var context in sources)
            {
                files.Check();
                var changed = context.Value.Values.Where(s => s.RepositoryPath is not null && changes.ContainsKey(s.RepositoryPath)).ToArray();
                if (changed.Length != 0 && !coveredContexts.Contains(context.Key))
                    foreach (var source in changed) unmeasured.Add(source.RepositoryPath!);
                else
                    foreach (var source in changed)
                        if (!reportedDocuments.TryGetValue(context.Key, out var observed) || !observed.Contains(source.RepositoryPath!))
                            unmeasured.Add(source.RepositoryPath!);
            }
        }
        files.Check();
        var missing = new HashSet<string>(comparer);
        foreach (var path in unmeasured)
            foreach (var line in changes[path]) missing.Add(path + ":" + line.ToString(CultureInfo.InvariantCulture));
        if (requireCompilerCensus)
        {
            foreach (var row in executable)
                foreach (var context in row.Value.Keys)
                    if (completeContexts.Contains(context) && (!providerLines.TryGetValue(row.Key, out var membership) || !membership.Contains(context)))
                        throw Refuse("A reported changed line is outside the independently captured compiler provider inventory.");
            foreach (var row in required)
            {
                files.Check();
                foreach (var context in row.Value)
                    if (!executable.TryGetValue(row.Key, out var observations) || !observations.ContainsKey(context)) missing.Add(row.Key);
            }
        }
        var count = requireCompilerCensus ? required.Count : executable.Count;
        var hit = requireCompilerCensus
            ? required.Count(row => executable.TryGetValue(row.Key, out var observations) &&
                row.Value.All(context => observations.TryGetValue(context, out var covered) && covered))
            : executable.Values.Count(contextsForLine => contextsForLine.Values.All(v => v));
        decimal? percent = count == 0 ? null : 100m * hit / count;
        if (missing.Count != 0 || requireCompilerCensus && !censusComplete)
            return new(count, hit, percent, GateOutcome.Incomplete,
                censusReason ?? "Changed compiler lines or authored documents are absent from an actual context's line reports; measurements remain unmeasured, not excluded or presumed nonexecutable.",
                InstalledToolFiles.Identity(diff), missing.Count, censusComplete);
        return new(count, hit, percent, count == 0 ? GateOutcome.Incomplete : percent >= 80m ? GateOutcome.Pass : GateOutcome.Fail,
            count == 0 ? "No measurable changed executable lines; no exemption or N/A is inferred." :
                requireCompilerCensus ? "Actual changed LINE coverage with complete compiler/PDB provider spans, a half-open source-token policy inventory, and measurements in every required context." :
                "Observed reported-line ratio only; no complete compiler census is claimed.", InstalledToolFiles.Identity(diff), 0, censusComplete);
    }

    private sealed record Source(string? PhysicalPath, string? RepositoryPath, int Lines, string LogicalPath,
        string Sha256, string Text, bool Generated)
    {
        // Compiler document identity only. Never used for current-file reads or
        // report File matching; those retain the authored PhysicalPath profile.
        internal string? CompilerPhysicalRole { get; init; }
    }
    internal static Dictionary<string, HashSet<int>> ReadHunks(byte[] diff)
    {
        if (diff.Length > ProducerJson.MaximumBytes) throw Refuse("Complete source diff exceeds capture profile.");
        var text = Decode(diff);
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
    private static JsonElement Property(JsonElement value, string name, JsonValueKind kind)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == kind
            ? item : throw Refuse("Missing or malformed line manifest field: " + name);
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) => Property(value, name, JsonValueKind.Array).EnumerateArray();
    private static string Text(JsonElement value, string name)
        => Property(value, name, JsonValueKind.String).GetString() is { Length: > 0 } text
            ? text : throw Refuse("Empty line manifest field: " + name);
    private static string? OptionalText(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Refuse("Malformed line manifest object.");
        if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.String) throw Refuse("Malformed optional line manifest field: " + name);
        return item.GetString();
    }
    private static long Integer(JsonElement value, string name)
        => Property(value, name, JsonValueKind.Number).TryGetInt64(out var number)
            ? number : throw Refuse("Nonintegral/out-of-range line manifest field: " + name);
    private static bool Boolean(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var item) ||
            item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Refuse("Malformed line manifest boolean: " + name);
        return item.GetBoolean();
    }
    private static Dictionary<string, JsonElement> Index(IEnumerable<JsonElement> rows, InstalledToolFiles files)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            files.Check();
            if (!result.TryAdd(Text(row, "id"), row)) throw Refuse("Duplicate line manifest identity.");
        }
        return result;
    }
    private static XElement One(IEnumerable<XElement> elements, string name)
    {
        var found = elements.Take(2).ToArray();
        return found.Length == 1 ? found[0] : throw Refuse("Missing/duplicate line XML element: " + name);
    }
    private static string Attribute(XElement element, string name)
        => element.Attribute(name)?.Value is { Length: > 0 } text ? text : throw Refuse("Missing line XML attribute: " + name);
    private static string PhysicalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw Refuse("Line document is not the captured physical path.");
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw Refuse("Malformed line document path.", error); }
    }
    private static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException error) { throw Refuse("Malformed UTF-8 line input.", error); }
    }
    private static int PhysicalLines(string text, InstalledToolFiles files)
    {
        // C# 6.3.2/6.3.3: CRLF is one terminator; CR/LF/NEL/LS/PS
        // also terminate lines. A final terminator creates no extra record.
        var length = text.Length;
        if (length != 0 && text[length - 1] == '\u001a') length--;
        var count = 0; var start = 0;
        for (var i = 0; i < length; i++)
        {
            if ((i & 4095) == 0) files.Check();
            if (text[i] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029')) continue;
            if (text[i] == '\r' && i + 1 < length && text[i + 1] == '\n') i++;
            count++; start = i + 1;
        }
        return count + (start < length ? 1 : 0);
    }
    private static EvidenceWriteException Refuse(string message, Exception? parser = null)
    {
        var failure = new EvidenceWriteException("Changed-LINE coverage is incomplete: " + message);
        if (parser is not null) failure.Data["ChangedLineParserFailure"] = parser;
        return failure;
    }
    [GeneratedRegex("^@@ -(\\d+)(?:,(\\d+))? \\+(\\d+)(?:,(\\d+))? @@(?:.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Hunk();
}
