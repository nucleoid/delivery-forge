using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace DeliveryForge.Evidence;

// Unwired private DATA comparison. Equality is NOT an authored-body-origin proof,
// census completeness, producer receipt, execution authority or permission to load
// arbitrary compiler/analyzer code from a manifest.
internal static class CompilerExactReemit
{
    internal sealed record Input(string Path, byte[] Bytes);
    internal sealed record Reference(string Path, byte[] Bytes, string[] Aliases, bool EmbedInteropTypes);
    // ScalarArguments is the closed projection of the SAME completed Csc argv;
    // role ordinals, input bytes, configs and output association must be bound by
    // the producer/consumer recipe decoder BEFORE calling this comparison slice.
    internal sealed record Request(string BaseDirectory, string[] ScalarArguments,
        Input[] Sources, Reference[] References, string[] EmbeddedSourcePaths);
    internal sealed record Comparison(bool Equal, string Reason);
    private const int MaximumArtifactBytes = 128 * 1024 * 1024;
    private const int MaximumRows = 10000;
    // Exact already-selected SDK 10.0.401 compiler pair, not a caller-supplied hash.
    private const string CommonHash = "03b3ab9d73d477d259fbc0ae167dd8caf4775c3c8688f1fdc5e0d3603255c3f4";
    private const string CSharpHash = "12b0428203faae6555858d2ff856cef7450d2764cf00c57e7516587a04885870";

    internal static Comparison Compare(Request request, byte[] common, byte[] csharp,
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
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            files.Check();
            if (result is not byte[][] { Length: 2 } emitted)
                throw new IOException("Exact compiler comparison returned an unsupported result shape.");
            var equal = emitted[0].AsSpan().SequenceEqual(actualPe) && emitted[1].AsSpan().SequenceEqual(actualPdb);
            files.Check();
            string Identity(byte[] bytes)
            {
                files.Check(); var digest = Convert.ToHexStringLower(SHA256.HashData(bytes)); files.Check();
                return $"{bytes.LongLength} bytes sha256:{digest}";
            }
            var details = $" Actual PE: {Identity(actualPe)}; emitted PE: {Identity(emitted[0])}; actual PDB: {Identity(actualPdb)}; emitted PDB: {Identity(emitted[1])}.";
            return new(equal, (equal ? "Complete PE and portable PDB byte equality only; authored body-origin remains unproven."
                : "Exact target-compiler PE/PDB bytes differ; general source origin remains unsupported.") + details);
        }
        finally { island.Unload(); } // Unload requests collection; no immediate-unload claim.
    }

    private static void Validate(Request request, byte[] common, byte[] csharp, byte[] pe, byte[] pdb, InstalledToolFiles files)
    {
        if (common.Length != 8292136 || csharp.Length != 19191592 ||
            Convert.ToHexStringLower(SHA256.HashData(common)) != CommonHash ||
            Convert.ToHexStringLower(SHA256.HashData(csharp)) != CSharpHash)
            throw new IOException("Only the selected exact SDK 10.0.401 Roslyn 5.9 compiler images are supported.");
        if (!Path.IsPathFullyQualified(request.BaseDirectory) ||
            request.BaseDirectory != Path.GetFullPath(request.BaseDirectory) || request.BaseDirectory.Any(char.IsControl) ||
            request.Sources.Length is 0 or > MaximumRows || request.References.Length is 0 or > MaximumRows ||
            request.ScalarArguments.Length is 0 or > 32768 || pe.Length is 0 or > MaximumArtifactBytes || pdb.Length is 0 or > MaximumArtifactBytes)
            throw new IOException("Exact compiler recipe exceeds the supported finite profile.");
        long bytes = 0;
        foreach (var item in request.Sources.Select(input => (input.Path, input.Bytes))
            .Concat(request.References.Select(input => (input.Path, input.Bytes))))
        {
            files.Check();
            if (!Path.IsPathFullyQualified(item.Path) || item.Path != Path.GetFullPath(item.Path) ||
                item.Path.Length > 16384 || item.Path.Any(char.IsControl) || item.Bytes.Length > MaximumArtifactBytes ||
                (bytes = checked(bytes + item.Bytes.Length)) > MaximumArtifactBytes)
                throw new IOException("Exact compiler input membership/extent is unsupported.");
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
                    throw new IOException("Exact compiler reference alias membership exceeds the finite profile.");
            }
        }
        if (request.Sources.Select(input => input.Path).Distinct(StringComparer.Ordinal).Count() != request.Sources.Length ||
            request.EmbeddedSourcePaths.Length > request.Sources.Length ||
            request.EmbeddedSourcePaths.Distinct(StringComparer.Ordinal).Count() != request.EmbeddedSourcePaths.Length ||
            request.EmbeddedSourcePaths.Any(path => !request.Sources.Any(input => input.Path == path)))
            throw new IOException("Exact compiler role membership is ambiguous.");
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
                throw new IOException("Exact compiler scalar argument profile is unsupported.");
        }
        if (!request.ScalarArguments.Contains("/noconfig", StringComparer.Ordinal) ||
            !request.ScalarArguments.Contains("/nostdlib+", StringComparer.Ordinal))
            throw new IOException("Exact compiler profile requires explicit no-config/no-standard-library inputs.");
        files.Check();
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
