using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Evidence;

// Private DATA decoder only. A retained official recipe does not grant freshness,
// loaded-image attestation, authored origin, census, coverage or execution authority.
internal static class CompilerOfficialRecipe
{
    private const int MaximumRows = 10000;
    private const long MaximumBytes = 128L * 1024 * 1024;
    internal sealed record Decoded(CompilerExactReemit.Request Request, byte[] Common, byte[] CSharp,
        byte[] Assembly, byte[] Pdb, IReadOnlyList<CompilerLineCensus.Document> Documents);

    internal static CompilerExactReemit.Comparison Compare(JsonElement manifest, string contextId, string buildId,
        Func<string, byte[]> readArtifact, InstalledToolFiles files)
    {
        var value = Decode(manifest, contextId, buildId, readArtifact, files);
        return CompilerExactReemit.Compare(value.Request, value.Common, value.CSharp, value.Assembly, value.Pdb, files);
    }

    internal static Decoded Decode(JsonElement manifest, string contextId, string buildId,
        Func<string, byte[]> readArtifact, InstalledToolFiles files)
    {
        files.Check();
        Require(Text(manifest, "manifestSchemaVersion") == "1.0", "manifest schema");
        var contexts = Index(Rows(manifest, "contexts"), files);
        var builds = Index(Rows(manifest, "builds"), files);
        var artifacts = Index(Rows(manifest, "artifacts"), files);
        Require(contexts.TryGetValue(contextId, out var context) && builds.TryGetValue(buildId, out _), "selected context/build");
        var build = builds[buildId]; Require(Text(build, "contextId") == contextId, "foreign build context");
        var selected = artifacts.Values.Where(a => Text(a, "kind") == "compiler-invocation-recipe" &&
            Optional(a, "contextId") == contextId && Optional(a, "buildId") == buildId).Take(2).ToArray();
        Require(selected.Length == 1, "one official recipe artifact");
        byte[] Read(JsonElement row, int maximum = (int)MaximumBytes, bool rawHash = false)
        {
            files.Check(); var locator = Text(row, "locator"); InstalledToolProfile.Relative(locator);
            var length = Integer(row, "length"); Require(length is >= 0 && length <= maximum, "artifact extent");
            var expected = rawHash ? "sha256:" + RawHash(row, "sha256") : Identity(row, "sha256");
            var bytes = readArtifact(locator);
            Require(bytes.LongLength == length && InstalledToolFiles.Identity(bytes) == expected, "artifact bytes");
            files.Check(); return bytes;
        }
        using var parsed = ProducerJson.Read(Read(selected[0], ProducerJson.MaximumBytes, rawHash: true));
        var recipe = parsed.RootElement;
        Shape(recipe, "provider", "disposition", "contextId", "buildId", "baseDirectory", "scalarArguments", "sources", "references",
            "embeddedSourcePaths", "compilerCommon", "compilerCSharp", "assembly", "pdb", "referenceAssembly", "observedClosureArtifacts",
            "taskAssociationSha256", "taskBinding", "commandBinding", "images", "arguments", "argumentRoles", "taskOptions", "unsupportedAttestations");
        Require(Text(recipe, "provider") == "completed-csc-typed-input-recipe-v1" && Text(recipe, "disposition") == "typed-input-data-only" &&
            Text(recipe, "contextId") == contextId && Text(recipe, "buildId") == buildId, "recipe provider/disposition/tuple");
        _ = RawHash(recipe, "taskAssociationSha256");
        Require(Text(recipe, "taskBinding") == "selected-sdk-core-target" && Text(recipe, "commandBinding") is
            "selected-host-exec-csc-arguments-match" or "selected-sdk-csc-exe-arguments-match", "completed task association");
        Require(Strings(recipe, "unsupportedAttestations").SequenceEqual(new[]
        { "recipe.loadedCompilerAndTaskImageNotAttested", "recipe.runtimeLoadedImageNotAttested" }), "unsupported attestations");
        var baseDirectory = Absolute(recipe, "baseDirectory");
        var sources = Rows(recipe, "sources"); var references = Rows(recipe, "references");
        var closure = Rows(recipe, "observedClosureArtifacts"); var scalar = Strings(recipe, "scalarArguments");
        var embedded = Strings(recipe, "embeddedSourcePaths");
        Require(sources.Length > 0 && references.Length > 0, "nonempty compiler inputs");
        var inputs = Rows(context, "inputs");
        var sourceInputs = inputs.Where(i => Text(i, "role") is "source" or "compiler-source").ToArray();
        var referenceInputs = inputs.Where(i => Text(i, "role") == "reference").ToArray();
        Require(sourceInputs.Length == sources.Length && referenceInputs.Length == references.Length, "complete source/reference membership");
        Dictionary<string, JsonElement> InputIndex(JsonElement[] rows)
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var row in rows) { files.Check(); Require(result.TryAdd(Text(row, "logicalPath"), row), "ambiguous context input role"); }
            return result;
        }
        var sourceIndex = InputIndex(sourceInputs); var referenceIndex = InputIndex(referenceInputs);
        var mappings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var mapping in Rows(context.GetProperty("pathPolicy"), "reportRootMappings"))
        {
            files.Check(); Shape(mapping, "logicalRoot", "reportRoot");
            Require(mappings.TryAdd(Text(mapping, "logicalRoot"), mapping), "duplicate captured source path role");
        }
        long declared = 0;
        void Charge(JsonElement row)
        {
            files.Check(); var length = Integer(row, "length");
            Require(length is >= 0 && length <= MaximumBytes && declared <= MaximumBytes - length, "aggregate declared extent"); declared += length;
        }
        var matchedSources = new HashSet<string>(StringComparer.Ordinal); var matchedReferences = new HashSet<string>(StringComparer.Ordinal);
        var sourcePaths = new string[sources.Length]; var referencePaths = new string[references.Length];
        var aliases = new string[references.Length][]; var embedInterop = new bool[references.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            files.Check(); var row = sources[i];
            Shape(row, "path", "locator", "length", "sha256", "ordinal", "role", "logicalPath", "encoding", "generated");
            Require(Integer(row, "ordinal") == i && Text(row, "role") is "source" or "compiler-source" && Text(row, "encoding") == "utf-8", "source ordinal/role/encoding");
            sourcePaths[i] = Absolute(row, "path");
            var logical = Text(row, "logicalPath"); InstalledToolProfile.Relative(logical);
            Require(mappings.TryGetValue(logical, out var mapped) && Absolute(mapped, "reportRoot") == sourcePaths[i], "exact captured source path role");
            Require(sourceIndex.TryGetValue(logical, out var input) && matchedSources.Add(logical) && Text(input, "role") == Text(row, "role"), "unique source role");
            SameInput(row, input); Require(Boolean(row, "generated") == Boolean(input, "generated") &&
                Optional(input, "encoding") == "utf-8", "source attributes"); Charge(row);
        }
        Require(sourcePaths.Distinct(InstalledToolFiles.PathComparer).Count() == sourcePaths.Length &&
            embedded.Distinct(StringComparer.Ordinal).Count() == embedded.Length && embedded.All(path => sourcePaths.Contains(path, StringComparer.Ordinal)), "source/embed path membership");
        for (var i = 0; i < references.Length; i++)
        {
            files.Check(); var row = references[i]; Shape(row, "path", "locator", "length", "sha256", "aliases", "embedInteropTypes", "ordinal", "logicalPath");
            Require(Integer(row, "ordinal") == i, "reference ordinal"); referencePaths[i] = Absolute(row, "path");
            var logical = Text(row, "logicalPath"); InstalledToolProfile.Relative(logical);
            Require(referenceIndex.TryGetValue(logical, out var input) && matchedReferences.Add(logical), "unique reference role"); SameInput(row, input);
            aliases[i] = Strings(row, "aliases"); embedInterop[i] = Boolean(row, "embedInteropTypes");
            var expectedAliases = Optional(input, "referenceAliases");
            Require(aliases[i].Length == 0 ? expectedAliases is null : string.Join(',', aliases[i]) == expectedAliases, "reference aliases");
            Require(aliases[i].Distinct(StringComparer.Ordinal).Count() == aliases[i].Length &&
                aliases[i].SequenceEqual(aliases[i].Order(StringComparer.Ordinal)) && !(aliases[i].Length == 1 && aliases[i][0] == "global"), "canonical aliases");
            var expectedEmbed = input.TryGetProperty("embedInteropTypes", out var embed) && embed.ValueKind != JsonValueKind.Null && embed.GetBoolean();
            Require(!input.TryGetProperty("embedInteropTypes", out embed) || embed.ValueKind == JsonValueKind.Null || embed.ValueKind == JsonValueKind.True, "canonical reference embed metadata");
            Require(embedInterop[i] == expectedEmbed, "reference embed semantics");
            Require(logical == ReferenceLogical(RawHash(input, "sha256"), expectedAliases, expectedEmbed), "canonical reference property identity"); Charge(row);
        }
        JsonElement Artifact(JsonElement row, string kind)
        {
            files.Check(); var id = Text(row, "artifactId");
            Require(artifacts.TryGetValue(id, out _), "artifact id"); var found = artifacts[id];
            Require(Text(found, "kind") == kind && Optional(found, "contextId") == contextId && Optional(found, "buildId") == buildId,
                "artifact kind/context/build");
            Require(Text(row, "locator") == Text(found, "locator") && Integer(row, "length") == Integer(found, "length") &&
                Identity(row, "sha256") == "sha256:" + RawHash(found, "sha256"), "artifact descriptor");
            return found;
        }
        var common = recipe.GetProperty("compilerCommon"); var csharp = recipe.GetProperty("compilerCSharp");
        foreach (var (row, role) in new[] { (common, "compiler-common"), (csharp, "compiler-csharp") })
        {
            Shape(row, "locator", "length", "sha256", "artifactId", "role", "compilerPath");
            Require(Text(row, "role") == role, "compiler image role"); _ = Absolute(row, "compilerPath"); _ = Artifact(row, "compiler-image"); Charge(row);
        }
        var assembly = recipe.GetProperty("assembly"); var pdb = recipe.GetProperty("pdb");
        foreach (var (row, kind, hashField) in new[] { (assembly, "assembly", "assemblySha256"), (pdb, "pdb", "pdbSha256") })
        {
            Require(artifacts.Values.Count(a => Text(a, "kind") == kind && Optional(a, "contextId") == contextId &&
                Optional(a, "buildId") == buildId) == 1, "unique actual build output role");
            Shape(row, "locator", "length", "sha256", "artifactId", "compilerPath"); _ = Absolute(row, "compilerPath");
            _ = Artifact(row, kind); Require(Identity(row, "sha256") == "sha256:" + RawHash(build, hashField), "build output digest"); Charge(row);
        }
        var referenceAssembly = recipe.GetProperty("referenceAssembly");
        if (referenceAssembly.ValueKind != JsonValueKind.Null)
        {
            Shape(referenceAssembly, "compilerPath", "length", "sha256"); _ = Absolute(referenceAssembly, "compilerPath");
            Require(Integer(referenceAssembly, "length") is > 0 and <= MaximumBytes, "reference output extent"); _ = Identity(referenceAssembly, "sha256");
        }
        var closureIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in closure)
        {
            Shape(row, "locator", "length", "sha256", "artifactId", "role", "compilerPath");
            Require(Text(row, "role") is "analyzer" or "analyzerConfig" or "additionalFile" && closureIds.Add(Text(row, "artifactId")), "inert closure role");
            _ = Absolute(row, "compilerPath"); _ = Artifact(row, "compiler-observed-input"); Charge(row);
        }
        Require(artifacts.Values.Where(a => Text(a, "kind") == "compiler-observed-input" && Optional(a, "contextId") == contextId &&
            Optional(a, "buildId") == buildId).Select(a => Text(a, "id")).ToHashSet(StringComparer.Ordinal).SetEquals(closureIds), "complete inert artifact membership");
        Require(artifacts.Values.Count(a => Text(a, "kind") == "compiler-image" && Optional(a, "contextId") == contextId && Optional(a, "buildId") == buildId) == 2,
            "complete compiler image membership");
        Require(Text(common, "artifactId") != Text(csharp, "artifactId"), "distinct compiler image roles");
        ValidateObservations(recipe, scalar, sources.Length, references.Length, embedded.Length, embedInterop, closure, common, csharp, files);
        // All structural/extent/role checks above precede any input/compiler byte reads.
        var sourceBytes = sources.Select(row => Read(row)).ToArray(); var referenceBytes = references.Select(row => Read(row)).ToArray();
        foreach (var row in closure) _ = Read(row); // Inert bytes only; never loaded.
        var peBytes = Read(assembly); var pdbBytes = Read(pdb);
        var request = new CompilerExactReemit.Request(baseDirectory, scalar,
            sourcePaths.Select((path, i) => new CompilerExactReemit.Input(path, sourceBytes[i])).ToArray(),
            referencePaths.Select((path, i) => new CompilerExactReemit.Reference(path, referenceBytes[i], aliases[i], embedInterop[i])).ToArray(), embedded);
        var documents = sources.Select((row, index) =>
        {
            files.Check(); var logical = Text(row, "logicalPath"); var input = sourceIndex[logical];
            var repositoryPath = Optional(input, "repositoryPath");
            if (repositoryPath is not null) InstalledToolProfile.Relative(repositoryPath);
            return new CompilerLineCensus.Document(logical, repositoryPath, sourcePaths[index], RawHash(input, "sha256"),
                new UTF8Encoding(false, true).GetString(sourceBytes[index]), Boolean(input, "generated"));
        }).ToArray();
        return new(request, Read(common), Read(csharp), peBytes, pdbBytes, documents);
    }

    private static void ValidateObservations(JsonElement recipe, string[] scalars, int sources, int references, int embedded, bool[] embedInterop,
        JsonElement[] closure, JsonElement common, JsonElement csharp, InstalledToolFiles files)
    {
        var arguments = Rows(recipe, "arguments", 32768); var roles = Rows(recipe, "argumentRoles", 32768);
        Require(arguments.Length > 0 && arguments.Length == roles.Length, "argument membership");
        var limits = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["source"] = sources, ["reference"] = references, ["embedded-source"] = embedded, ["scalar"] = scalars.Length,
            ["analyzer"] = closure.Count(r => Text(r, "role") == "analyzer"),
            ["analyzerconfig"] = closure.Count(r => Text(r, "role") == "analyzerConfig"),
            ["additionalfile"] = closure.Count(r => Text(r, "role") == "additionalFile")
        };
        var seen = limits.ToDictionary(p => p.Key, _ => 0, StringComparer.Ordinal); var generatedOutput = 0; long bytes = 0;
        for (var i = 0; i < arguments.Length; i++)
        {
            files.Check(); var argument = arguments[i]; var role = roles[i];
            Shape(argument, "ordinal", "utf8Length", "sha256"); Shape(role, "ordinal", "role", "inputOrdinal", "name");
            Require(Integer(argument, "ordinal") == i && Integer(role, "ordinal") == i, "argument ordinal");
            var length = Integer(argument, "utf8Length"); Require(length is > 0 and <= 65536 && bytes <= 2 * 1024 * 1024 - length, "argument extent"); bytes += length;
            var hash = RawHash(argument, "sha256"); var name = Text(role, "role"); var option = Optional(role, "name");
            if (name == "generated-output")
            {
                Require(++generatedOutput == 1 && role.GetProperty("inputOrdinal").ValueKind == JsonValueKind.Null && option == "generatedfilesout", "generated output role"); continue;
            }
            Require(limits.ContainsKey(name) && Integer(role, "inputOrdinal") == seen[name] && seen[name] < limits[name], "typed argument role");
            var ordinal = seen[name]++;
            Require(name switch
            {
                "source" => option is null, "reference" => option is "reference" or "link", "embedded-source" => option == "embed",
                "scalar" => option is not null, _ => option == name
            }, "typed argument name");
            if (name == "reference") Require((option == "link") == embedInterop[ordinal], "reference argument embed semantics");
            if (name == "scalar")
            {
                var value = scalars[ordinal]; var raw = Encoding.UTF8.GetBytes(value);
                Require(raw.LongLength == length && Convert.ToHexStringLower(SHA256.HashData(raw)) == hash && value.StartsWith('/') &&
                    value[1..].Split(':', 2)[0] == option, "scalar argument digest/name");
            }
        }
        Require(seen.All(p => p.Value == limits[p.Key]), "complete typed argument coverage");
        var images = Rows(recipe, "images");
        var imageRoles = new[] { "dotnet-host", "csc", "compiler-common", "compiler-csharp", "compiler-deps", "compiler-runtimeconfig", "sdk-csc-target", "sdk-csc-task" };
        Require(images.Select(i => Text(i, "role")).SequenceEqual(imageRoles) ||
            images.Select(i => Text(i, "role")).SequenceEqual(imageRoles.Append("csc-launcher")), "observed image roles");
        foreach (var image in images)
        {
            Shape(image, "role", "length", "sha256"); Require(Integer(image, "length") is > 0 and <= MaximumBytes, "observed image extent"); _ = RawHash(image, "sha256");
        }
        foreach (var row in new[] { common, csharp })
        {
            var image = images.Single(i => Text(i, "role") == Text(row, "role"));
            Require(Integer(image, "length") == Integer(row, "length") && "sha256:" + RawHash(image, "sha256") == Identity(row, "sha256"), "selected compiler image descriptor");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in Rows(recipe, "taskOptions"))
        {
            files.Check(); Shape(option, "name", "type", "value"); Require(names.Add(Text(option, "name")), "duplicate task option");
            // Observational metadata is kept inert. Effective options are the
            // digest-bound scalars parsed only by the exact pinned target compiler.
            var name = Text(option, "name"); var type = Text(option, "type"); var value = Text(option, "value");
            var booleans = new[] { "AllowUnsafeBlocks", "CheckForOverflowUnderflow", "Optimize", "Deterministic", "EmitDebugInformation", "HighEntropyVA",
                "Prefer32Bit", "PublicSign", "DelaySign", "RefOnly", "NoWin32Manifest", "EmbedAllSources", "UseSharedCompilation",
                "UseHostCompilerIfAvailable", "SkipCompilerExecution", "ProvideCommandLineArgs", "NoConfig", "NoStandardLib", "NoLogo", "Utf8Output", "GenerateFullPaths" };
            if (booleans.Contains(name, StringComparer.Ordinal)) Require(type == "boolean" && value is "true" or "false", "boolean option observation");
            else if (name is "FileAlignment" or "WarningLevel" or "CodePage") Require(type == "integer" &&
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 65536 &&
                number.ToString(CultureInfo.InvariantCulture) == value, "integer option observation");
            else
            {
                string[] choices = name switch
                {
                    "DebugType" => ["none", "portable", "embedded", "full", "pdbonly"], "Nullable" => ["enable", "disable", "warnings", "annotations"],
                    "TargetType" => ["library", "exe", "winexe", "module", "appcontainerexe", "winmdobj"],
                    "Platform" => ["AnyCPU", "AnyCPU32BitPreferred", "x86", "x64", "ARM", "ARM64", "Itanium"],
                    "ChecksumAlgorithm" => ["SHA1", "SHA256"],
                    "LangVersion" => ["default", "latest", "latestmajor", "preview", "1", "1.0", "2", "2.0", "3", "3.0", "4", "4.0", "5", "5.0", "6", "6.0",
                        "7", "7.0", "7.1", "7.2", "7.3", "8", "8.0", "9", "9.0", "10", "10.0", "11", "11.0", "12", "12.0", "13", "13.0", "14", "14.0"], _ => []
                };
                Require(type == "enum" && choices.Contains(value, StringComparer.Ordinal), "enum option observation");
            }
        }
    }

    private static void SameInput(JsonElement row, JsonElement input) => Require(Text(row, "locator") == Text(input, "locator") &&
        Integer(row, "length") == Integer(input, "length") && Identity(row, "sha256") == "sha256:" + RawHash(input, "sha256"), "context input descriptor");
    private static string ReferenceLogical(string hash, string? aliases, bool embedded)
    {
        if (aliases is null && !embedded) return "references/" + hash + ".dll";
        // Exact existing producer reference-properties-v1 tuple; no code/path resolver.
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Field(string value)
        {
            var raw = Encoding.UTF8.GetBytes(value); Span<byte> length = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(length, raw.Length); digest.AppendData(length); digest.AppendData(raw);
        }
        Field("sha256-canonical-v1"); Field("reference-properties-v1");
        foreach (var field in new[] { hash, aliases ?? "", embedded ? "true" : "false" }) { digest.AppendData([1]); Field(field); }
        return "references/" + Convert.ToHexStringLower(digest.GetHashAndReset()) + ".dll";
    }
    private static Dictionary<string, JsonElement> Index(JsonElement[] rows, InstalledToolFiles files)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in rows) { files.Check(); Require(result.TryAdd(Text(row, "id"), row), "duplicate manifest id"); } return result;
    }
    private static JsonElement[] Rows(JsonElement value, string name, int maximum = MaximumRows)
    {
        var row = value.GetProperty(name); Require(row.ValueKind == JsonValueKind.Array && row.GetArrayLength() <= maximum, "array membership/extent"); return row.EnumerateArray().ToArray();
    }
    private static string[] Strings(JsonElement value, string name) => Rows(value, name, 32768).Select(item => String(item)).ToArray();
    private static string String(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String, "string type"); var text = value.GetString()!;
        Require(text.Length is > 0 and <= 16384 && !text.Any(char.IsControl), "string extent/content"); return text;
    }
    private static string Text(JsonElement value, string name) => String(value.GetProperty(name));
    private static string? Optional(JsonElement value, string name) => !value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null ? null : String(item);
    private static long Integer(JsonElement value, string name)
    {
        var item = value.GetProperty(name); Require(item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out _), "integer type"); return item.GetInt64();
    }
    private static bool Boolean(JsonElement value, string name)
    {
        var item = value.GetProperty(name); Require(item.ValueKind is JsonValueKind.True or JsonValueKind.False, "boolean type"); return item.GetBoolean();
    }
    private static string RawHash(JsonElement value, string name)
    {
        var text = Text(value, name); Require(text.Length == 64 && text.All(char.IsAsciiHexDigitLower), "raw SHA256"); return text;
    }
    private static string Identity(JsonElement value, string name)
    {
        var text = Text(value, name); Require(InstalledToolFiles.Hash(text), "SHA256 identity"); return text;
    }
    private static string Absolute(JsonElement value, string name)
    {
        var text = Text(value, name); Require(Path.IsPathFullyQualified(text) && Path.GetFullPath(text) == text, "canonical compiler path DATA"); return text;
    }
    private static void Shape(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object, "object type"); var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        Require(actual.Length == names.Length && actual.ToHashSet(StringComparer.Ordinal).SetEquals(names), "closed object shape");
    }
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new EvidenceWriteException("Official compiler recipe: " + reason + ".");
    }
}
