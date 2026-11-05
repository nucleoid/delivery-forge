using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Evidence;

// Private exact-compiler reconstruction and comparison. Equality alone is NOT an authored-body-origin proof,
// census completeness, producer receipt, execution authority or permission to load
// arbitrary compiler/analyzer code from a manifest.
internal static class CompilerExactReemit
{
    internal sealed record Input(string Path, byte[] Bytes);
    internal sealed record Reference(string Path, byte[] Bytes, string[] Aliases, bool EmbedInteropTypes);
    // Closed reconstruction DATA: the decoder checks input membership,
    // descriptor/scalar consistency and inert observation shape. It cannot
    // prove the original task or non-scalar argv bytes; Compare grants no
    // runtime-task authority.
    internal sealed record Request(string BaseDirectory, string[] ScalarArguments,
        Input[] Sources, Reference[] References, string[] EmbeddedSourcePaths);
    // Only the actual reconstruction factory can create this result. A caller's
    // JSON boolean cannot authorize residual compiler bodies. Source association
    // is useful only together with equality of the complete actual PE and PDB.
    internal sealed class Comparison
    {
        private readonly string actualPeHash, actualPdbHash;
        private readonly Dictionary<string, SourceIdentity> sourceCorpus;
        internal bool Equal { get; }
        internal string Reason { get; }
        internal byte[] EmittedPe { get; }
        internal byte[] EmittedPdb { get; }
        internal byte[] OriginInventory { get; }
        internal bool HasProvenSourceOrigin { get; }
        private Comparison(CoreResult result)
        {
            Equal = result.Equal; Reason = result.Reason;
            EmittedPe = result.EmittedPe; EmittedPdb = result.EmittedPdb; OriginInventory = result.OriginInventory;
            actualPeHash = result.ActualPeHash; actualPdbHash = result.ActualPdbHash;
            sourceCorpus = result.Sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
            using var inventory = ProducerJson.Read(result.OriginInventory);
            HasProvenSourceOrigin = Equal && inventory.RootElement.GetProperty("sourceCount").GetInt32() == sourceCorpus.Count &&
                inventory.RootElement.TryGetProperty("sourceExhaustion", out var closure) &&
                closure.GetProperty("complete").GetBoolean() && closure.GetProperty("pendingInitializerCount").GetInt32() == 0 &&
                closure.GetProperty("unresolved").GetArrayLength() == 0;
        }
        internal static Comparison Run(Request request, byte[] common, byte[] csharp,
            byte[] actualPe, byte[] actualPdb, InstalledToolFiles files)
            => new(CompareCore(request, common, csharp, actualPe, actualPdb, files));
        internal bool ProvesSourceOrigin(byte[] actualPe, byte[] actualPdb,
            IReadOnlyList<CompilerLineCensus.Document> documents, InstalledToolFiles files)
        {
            files.Check();
            if (!HasProvenSourceOrigin || documents.Count != sourceCorpus.Count) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var utf8 = new UTF8Encoding(false, true);
            foreach (var document in documents)
            {
                files.Check();
                if (document.PhysicalPath is not { } path || !seen.Add(path) ||
                    !sourceCorpus.TryGetValue(path, out var source) || document.Sha256 != source.Sha256)
                    return false;
                try
                {
                    if (document.Text.Length > source.Length) return false;
                    var length = utf8.GetByteCount(document.Text); files.Check();
                    if (length != source.Length) return false;
                    var digest = Convert.ToHexStringLower(SHA256.HashData(utf8.GetBytes(document.Text))); files.Check();
                    if (digest != source.Sha256) return false;
                }
                catch (EncoderFallbackException) { return false; }
            }
            var matches = Convert.ToHexStringLower(SHA256.HashData(actualPe)) == actualPeHash &&
                Convert.ToHexStringLower(SHA256.HashData(actualPdb)) == actualPdbHash;
            files.Check(); return matches;
        }
    }
    private sealed record SourceIdentity(string Path, int Length, string Sha256);
    private sealed record CoreResult(bool Equal, string Reason, byte[] EmittedPe, byte[] EmittedPdb,
        byte[] OriginInventory, string ActualPeHash, string ActualPdbHash, IReadOnlyList<SourceIdentity> Sources);
    private const int MaximumArtifactBytes = 128 * 1024 * 1024;
    private const int MaximumRows = 10000;
    // Exact already-selected SDK 10.0.401 compiler pair, not a caller-supplied hash.
    private const string CommonHash = "03b3ab9d73d477d259fbc0ae167dd8caf4775c3c8688f1fdc5e0d3603255c3f4";
    private const string CSharpHash = "12b0428203faae6555858d2ff856cef7450d2764cf00c57e7516587a04885870";

    internal static Comparison Compare(Request request, byte[] common, byte[] csharp,
        byte[] actualPe, byte[] actualPdb, InstalledToolFiles files)
        => Comparison.Run(request, common, csharp, actualPe, actualPdb, files);

    private static CoreResult CompareCore(Request request, byte[] common, byte[] csharp,
        byte[] actualPe, byte[] actualPdb, InstalledToolFiles files)
    {
        files.Check();
        Validate(request, common, csharp, actualPe, actualPdb, files);
        // Freeze all mutable caller arrays, then revalidate the exact snapshot
        // before any assembly load. No caller can swap code after its hash check.
        request = new(request.BaseDirectory, request.ScalarArguments.ToArray(),
            request.Sources.Select(input => new Input(input.Path, input.Bytes.ToArray())).ToArray(),
            request.References.Select(input => new Reference(input.Path, input.Bytes.ToArray(), input.Aliases.ToArray(), input.EmbedInteropTypes)).ToArray(),
            request.EmbeddedSourcePaths.ToArray());
        common = common.ToArray(); csharp = csharp.ToArray();
        actualPe = actualPe.ToArray(); actualPdb = actualPdb.ToArray();
        Validate(request, common, csharp, actualPe, actualPdb, files);
        // Read only this actual running Evidence image, never a recipe-provided
        // assembly path. MVID checks that the retained bytes name this module.
        var own = typeof(CompilerExactReemit).Assembly;
        if (string.IsNullOrEmpty(own.Location))
            throw new CompilerRecipeUnsupportedException("The current Evidence host has no ordinary image for exact reconstruction.");
        var image = files.Read(own.Location, MaximumArtifactBytes);
        using (var stream = new MemoryStream(image, writable: false))
        using (var pe = new PEReader(stream))
        {
            var metadata = pe.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != own.ManifestModule.ModuleVersionId)
                throw new IOException("Current Evidence image differs from the loaded re-emission implementation.");
        }
        files.Check();
        var island = new CompilerIsland(common, csharp);
        try
        {
            using var stream = new MemoryStream(image, writable: false);
            var assembly = island.LoadFromStream(stream);
            var entry = assembly.GetType("DeliveryForge.Evidence.CompilerExactReemitEntry", throwOnError: true)!
                .GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new IOException("Exact compiler comparison entry is unavailable.");
            object? result;
            try
            {
                result = entry.Invoke(null, [request.BaseDirectory, request.ScalarArguments,
                    request.Sources.Select(input => input.Path).ToArray(), request.Sources.Select(input => input.Bytes).ToArray(),
                    request.References.Select(input => input.Path).ToArray(), request.References.Select(input => input.Bytes).ToArray(),
                    request.References.Select(input => input.Aliases).ToArray(), request.References.Select(input => input.EmbedInteropTypes).ToArray(),
                    request.EmbeddedSourcePaths, (Action)files.Check, files.Token]);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                // The island loaded this exact MVID-bound Evidence image. Only
                // its actual closed Type denotes unsupported reconstruction;
                // a name, message, generic IOException or caller DATA does not.
                var unsupportedType = assembly.GetType(typeof(CompilerRecipeUnsupportedException).FullName!, throwOnError: true)!;
                if (error.InnerException.GetType() == unsupportedType)
                {
                    files.Check();
                    throw new CompilerRecipeUnsupportedException(error.InnerException.Message);
                }
                ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw;
            }
            files.Check();
            if (result is not byte[][] { Length: 3 } emitted || emitted[2].Length > MaximumArtifactBytes)
                throw new IOException("Exact compiler comparison returned an unsupported result shape.");
            var equal = emitted[0].AsSpan().SequenceEqual(actualPe) && emitted[1].AsSpan().SequenceEqual(actualPdb);
            files.Check();
            string Identity(byte[] bytes)
            {
                files.Check(); var digest = Convert.ToHexStringLower(SHA256.HashData(bytes)); files.Check();
                return $"{bytes.LongLength} bytes sha256:{digest}";
            }
            var details = $" Actual PE: {Identity(actualPe)}; emitted PE: {Identity(emitted[0])}; actual PDB: {Identity(actualPdb)}; emitted PDB: {Identity(emitted[1])}.";
            return new(equal, (equal ? "Complete PE and portable PDB byte equality; authored body-origin remains unproven by equality alone."
                : "Exact target-compiler PE/PDB bytes differ; general source origin remains unsupported.") + details,
                emitted[0], emitted[1], emitted[2], Convert.ToHexStringLower(SHA256.HashData(actualPe)),
                Convert.ToHexStringLower(SHA256.HashData(actualPdb)), request.Sources.Select(source =>
                {
                    files.Check();
                    return new SourceIdentity(source.Path, source.Bytes.Length, Convert.ToHexStringLower(SHA256.HashData(source.Bytes)));
                }).ToArray());
        }
        finally { island.Unload(); } // Unload requests collection; no immediate-unload claim.
    }

    // Pure bounded profile preflight; unsupported captured compiler profiles
    // keep the legacy census incomplete without loading any captured code.
    internal static bool Supports(Request request, byte[] common, byte[] csharp,
        byte[] pe, byte[] pdb, InstalledToolFiles files)
        => UnsupportedReason(request, common, csharp, pe, pdb, files) is null;

    private static void Validate(Request request, byte[] common, byte[] csharp,
        byte[] pe, byte[] pdb, InstalledToolFiles files)
    {
        if (UnsupportedReason(request, common, csharp, pe, pdb, files) is { } reason)
            throw new IOException(reason);
    }

    private static string? UnsupportedReason(Request request, byte[] common, byte[] csharp, byte[] pe, byte[] pdb, InstalledToolFiles files)
    {
        if (common.Length != 8292136 || csharp.Length != 19191592 ||
            Convert.ToHexStringLower(SHA256.HashData(common)) != CommonHash ||
            Convert.ToHexStringLower(SHA256.HashData(csharp)) != CSharpHash)
            return "Only the selected exact SDK 10.0.401 Roslyn 5.9 compiler images are supported.";
        if (!Path.IsPathFullyQualified(request.BaseDirectory) ||
            request.BaseDirectory != Path.GetFullPath(request.BaseDirectory) || request.BaseDirectory.Any(char.IsControl) ||
            request.Sources.Length is 0 or > MaximumRows || request.References.Length is 0 or > MaximumRows ||
            request.ScalarArguments.Length is 0 or > 32768 || pe.Length is 0 or > MaximumArtifactBytes || pdb.Length is 0 or > MaximumArtifactBytes)
            return "Exact compiler recipe exceeds the supported finite profile.";
        long bytes = 0;
        foreach (var item in request.Sources.Select(input => (input.Path, input.Bytes))
            .Concat(request.References.Select(input => (input.Path, input.Bytes))))
        {
            files.Check();
            if (!Path.IsPathFullyQualified(item.Path) || item.Path != Path.GetFullPath(item.Path) ||
                item.Path.Length > 16384 || item.Path.Any(char.IsControl) || item.Bytes.Length > MaximumArtifactBytes ||
                (bytes = checked(bytes + item.Bytes.Length)) > MaximumArtifactBytes)
                return "Exact compiler input membership/extent is unsupported.";
        }
        long aliasCharacters = 0; var aliasCount = 0;
        foreach (var reference in request.References)
        {
            files.Check();
            foreach (var alias in reference.Aliases)
            {
                files.Check();
                if (++aliasCount > MaximumRows || alias.Length > 16384 || alias.Any(char.IsControl) ||
                    (aliasCharacters += alias.Length) > 2 * 1024 * 1024)
                    return "Exact compiler reference alias membership exceeds the finite profile.";
            }
        }
        if (request.Sources.Select(input => input.Path).Distinct(StringComparer.Ordinal).Count() != request.Sources.Length ||
            request.EmbeddedSourcePaths.Length > request.Sources.Length ||
            request.EmbeddedSourcePaths.Distinct(StringComparer.Ordinal).Count() != request.EmbeddedSourcePaths.Length ||
            request.EmbeddedSourcePaths.Any(path => !request.Sources.Any(input => input.Path == path)))
            return "Exact compiler role membership is ambiguous.";
        // Parser can read response/ruleset inputs: prohibit every unsupported
        // switch BEFORE loading ANY captured code, not after Parse has run.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        { "noconfig", "unsafe-", "checked-", "nowarn", "fullpaths", "nostdlib+", "errorreport", "warn", "define", "highentropyva+",
          "nullable", "features", "debug-", "debug", "filealign", "optimize+", "pathmap", "out", "refout", "target", "warnaserror+",
          "utf8output", "deterministic+", "langversion", "checksumalgorithm", "pdb" };
        long argumentCharacters = 0;
        foreach (var argument in request.ScalarArguments)
        {
            files.Check();
            if (argument.Length is 0 or > 65536 || argument[0] != '/' || argument.Any(char.IsControl) ||
                (argumentCharacters += argument.Length) > 2 * 1024 * 1024 ||
                !allowed.Contains(argument[1..].Split(':', 2)[0]))
                return "Exact compiler scalar argument profile is unsupported.";
        }
        if (!request.ScalarArguments.Contains("/noconfig", StringComparer.Ordinal) ||
            !request.ScalarArguments.Contains("/nostdlib+", StringComparer.Ordinal))
            return "Exact compiler profile requires explicit no-config/no-standard-library inputs.";
        files.Check(); return null;
    }

    private sealed class CompilerIsland(byte[] common, byte[] csharp) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "Microsoft.CodeAnalysis" or "Microsoft.CodeAnalysis.CSharp")
            {
                if (name.Version is not { Major: 5 } || name.Version > new Version(5, 9, 0, 0))
                    throw new IOException("Unexpected compiler assembly binding.");
                using var image = new MemoryStream(name.Name == "Microsoft.CodeAnalysis" ? common : csharp, writable: false);
                return LoadFromStream(image);
            }
            // Only BCL contracts cross the island. Never probe the recipe/source
            // directory or fall back to host Roslyn 5.0/analyzers/dependencies.
            if (name.Name is "System.Private.CoreLib" or "netstandard" or "Microsoft.CSharp" ||
                name.Name?.StartsWith("System.", StringComparison.Ordinal) == true)
                return Default.LoadFromAssemblyName(name);
            throw new IOException("Exact compiler isolation dependency is unsupported.");
        }
    }
}
