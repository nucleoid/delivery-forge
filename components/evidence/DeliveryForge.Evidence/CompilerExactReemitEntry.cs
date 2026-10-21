using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace DeliveryForge.Evidence;

// Loaded ONLY in CompilerExactReemit's exact pinned 5.9 island. No reference to
// InstalledToolFiles/Contracts/Coordination or host Roslyn types in the BCL ABI.
internal static class CompilerExactReemitEntry
{
    internal static byte[][] Run(string baseDirectory, string[] scalarArguments,
        string[] sourcePaths, byte[][] sources, string[] referencePaths, byte[][] references,
        string[][] aliases, bool[] embedInterop, string[] embeddedPaths, Action check, CancellationToken token)
    {
        check(); token.ThrowIfCancellationRequested();
        if (typeof(CSharpCompilation).Assembly.GetName().Version != new Version(5, 9, 0, 0) ||
            typeof(Compilation).Assembly.GetName().Version != new Version(5, 9, 0, 0))
            throw Unsupported("The isolated target compiler is not exact Roslyn 5.9.");
        // Host already rejected all file-reading switches. Parse receives no
        // response/ruleset/source/reference/analyzer paths to resolve or execute.
        var parsed = CSharpCommandLineParser.Default.Parse(scalarArguments, baseDirectory, sdkDirectory: null, additionalReferenceDirectories: null);
        if (parsed.Errors.Any(error => error.Severity == DiagnosticSeverity.Error))
            throw DiagnosticFailure("Exact compiler scalar parsing refused.", parsed.Errors, check, token);
        if (parsed.SourceFiles.Length != 0 || parsed.MetadataReferences.Length != 0 || parsed.AnalyzerReferences.Length != 0 ||
            parsed.AnalyzerConfigPaths.Length != 0 || parsed.ManifestResources.Any() || parsed.RuleSetPath is not null ||
            parsed.SourceLink is not null || !parsed.EmitPdbFile || parsed.EmitOptions.DebugInformationFormat != DebugInformationFormat.PortablePdb ||
            parsed.CompilationOptions.OutputKind != OutputKind.DynamicallyLinkedLibrary || !parsed.CompilationOptions.Deterministic ||
            parsed.OutputFileName is null || parsed.ParseOptions.Kind != SourceCodeKind.Regular)
            throw Unsupported("The effective compiler recipe is outside the exact library/portable/deterministic profile.");
        var trees = new List<SyntaxTree>();
        var embedded = new List<EmbeddedText>();
        for (var i = 0; i < sources.Length; i++)
        {
            check(); token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(sources[i], writable: false);
            var text = SourceText.From(stream, parsed.Encoding, parsed.ChecksumAlgorithm, throwIfBinaryDetected: true, canBeEmbedded: true);
            var tree = CSharpSyntaxTree.ParseText(text, parsed.ParseOptions, sourcePaths[i], token);
            trees.Add(tree);
            if (embeddedPaths.Contains(sourcePaths[i], StringComparer.Ordinal)) embedded.Add(EmbeddedText.FromSource(sourcePaths[i], text));
            check();
        }
        var owned = new List<AssemblyMetadata>();
        try
        {
            var metadata = new List<MetadataReference>();
            for (var i = 0; i < references.Length; i++)
            {
                check(); token.ThrowIfCancellationRequested();
                var image = AssemblyMetadata.CreateFromImage(ImmutableArray.Create(references[i]));
                owned.Add(image);
                metadata.Add(image.GetReference(filePath: referencePaths[i], aliases: aliases[i].ToImmutableArray(), embedInteropTypes: embedInterop[i]));
                check();
            }
            // Resolver performs lexical PathMap only; ordinary regular C# has no
            // #load resolution. No MetadataReferenceResolver is installed.
            var resolver = new SourceFileResolver(parsed.SourcePaths, baseDirectory, parsed.PathMap);
            var options = parsed.CompilationOptions.WithSourceReferenceResolver(resolver).WithMetadataReferenceResolver(null)
                .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);
            var name = parsed.CompilationName ?? Path.GetFileNameWithoutExtension(parsed.OutputFileName);
            var compilation = CSharpCompilation.Create(name, trees, metadata, options);
            var mappedPdbPath = resolver.NormalizePath(parsed.GetPdbFilePath(parsed.OutputFileName), baseFilePath: null)
                ?? throw Unsupported("The exact compiler PDB path is unsupported.");
            var emit = parsed.EmitOptions.WithPdbFilePath(mappedPdbPath)
                .WithOutputNameOverride(parsed.OutputFileName);
            using var pe = new BoundedOutput(check);
            using var pdb = new BoundedOutput(check);
            using var refAssembly = new BoundedOutput(check);
            // Csc's default library VERSION resource is recreated by the public
            // compiler API. Full byte equality is required; this is NOT a waiver
            // if target Csc's policy/options differ from this candidate recipe.
            using var resources = compilation.CreateDefaultWin32Resources(versionResource: true, noManifest: true,
                manifestContents: null, iconInIcoFormat: null);
            check();
            var result = compilation.Emit(pe, pdb, win32Resources: resources, options: emit,
                embeddedTexts: embedded, metadataPEStream: parsed.OutputRefFilePath is null ? null : refAssembly, cancellationToken: token);
            check(); token.ThrowIfCancellationRequested();
            if (!result.Success) throw DiagnosticFailure("The exact input compilation did not emit successfully.", result.Diagnostics, check, token);
            return [pe.ToArray(), pdb.ToArray()];
        }
        finally { foreach (var image in owned) image.Dispose(); }
    }

    private static IOException DiagnosticFailure(string message, ImmutableArray<Diagnostic> diagnostics, Action check, CancellationToken token)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics.Take(64))
        {
            check(); token.ThrowIfCancellationRequested();
            var id = diagnostic.Id;
            ids.Add(id.Length is > 0 and <= 32 && id.All(char.IsAsciiLetterOrDigit) ? id : "UNSUPPORTED-ID");
        }
        check(); token.ThrowIfCancellationRequested();
        return Unsupported($"{message} Diagnostic count={diagnostics.Length}; first64 distinct IDs=[{string.Join(',', ids.Order(StringComparer.Ordinal))}]. No diagnostic messages or paths retained.");
    }

    private static IOException Unsupported(string message) => new(message);
    private sealed class BoundedOutput(Action check) : MemoryStream
    {
        private const int MaximumBytes = 128 * 1024 * 1024;
        private void Before(int count)
        {
            check();
            if (count < 0 || Position > MaximumBytes - (long)count)
                throw Unsupported("Complete re-emitted compiler output exceeds the existing artifact bound.");
        }
        public override void Write(byte[] buffer, int offset, int count)
        { Before(count); base.Write(buffer, offset, count); check(); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Before(buffer.Length); base.Write(buffer); check(); }
        public override void WriteByte(byte value) { Before(1); base.WriteByte(value); check(); }
        public override void SetLength(long value)
        {
            check();
            if (value is < 0 or > MaximumBytes) throw Unsupported("Complete re-emitted compiler output exceeds the existing artifact bound.");
            base.SetLength(value); check();
        }
    }
}
