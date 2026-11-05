using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DeliveryForge.Evidence;

// Private policy DATA from already-bound source and actual PDB intervals. H is
// the provider's inclusive line envelope; E is a separate half-open source-token
// projection. Neither a report nor the current diff chooses either membership.
internal sealed class CompilerLineProjection
{
    private const int MaximumSteps = 1_000_000;
    private readonly InstalledToolFiles files;
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);
    private readonly HashSet<CompilerLineCensus.Line> provider = [];
    private readonly HashSet<CompilerLineCensus.Line> policy = [];
    private int parseSteps, projectionSteps;
    internal string? Reason { get; private set; }
    private sealed record PhysicalLine(int Start, int Length, int End);
    private sealed record Source(PhysicalLine[] Lines, TextSpan[] Tokens, int Length);

    internal CompilerLineProjection(IReadOnlyList<CompilerLineCensus.Document> documents, InstalledToolFiles files)
    {
        this.files = files;
        long extent = 0;
        foreach (var document in documents)
        {
            files.Check();
            if (document.Text.Length > 8 * 1024 * 1024 - extent)
            { Unsupported("Complete source-token parser exceeds its restrictive8Mi UTF-16-unit text profile."); return; }
            extent += document.Text.Length;
        }
        foreach (var document in documents)
        {
            files.Check();
            var tree = CSharpSyntaxTree.ParseText(document.Text,
                new CSharpParseOptions(LanguageVersion.CSharp14, DocumentationMode.None),
                cancellationToken: files.Token);
            files.Check();
            if (tree.GetDiagnostics(files.Token).Any(row => row.Severity == DiagnosticSeverity.Error))
            { Unsupported("Captured source has unsupported syntax for the fixed source-token projection."); return; }
            files.Check();
            var root = tree.GetRoot(files.Token);
            foreach (var node in root.DescendantNodes(descendIntoTrivia: true))
            {
                if (!ParseStep()) return;
                // Effective conditional symbols/source kind are not inferred.
                if (node is DirectiveTriviaSyntax directive && directive is not
                    (NullableDirectiveTriviaSyntax or PragmaWarningDirectiveTriviaSyntax or RegionDirectiveTriviaSyntax or EndRegionDirectiveTriviaSyntax))
                { Unsupported("Source-token projection does not support choosing/remapping directives."); return; }
            }
            var tokens = new List<TextSpan>();
            foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
            {
                if (!ParseStep()) return;
                if (token.IsMissing || token.Span.Length == 0 || token.IsKind(SyntaxKind.EndOfFileToken)) continue;
                if (tokens.Count != 0 && tokens[^1].End > token.SpanStart)
                { Unsupported("Source-token spans are not an ordered disjoint physical projection."); return; }
                tokens.Add(token.Span);
            }
            var lines = Lines(document.Text);
            if (lines is null || !sources.TryAdd(document.LogicalPath, new(lines, tokens.ToArray(), EffectiveLength(document.Text))))
            { Unsupported("Source-token physical membership is unsupported or ambiguous."); return; }
        }
        files.Check();
    }

    private void Unsupported(string reason) => Reason ??= reason;
    private bool ParseStep()
    {
        files.Check();
        if (++parseSteps <= MaximumSteps) return true;
        Unsupported("Complete source-token traversal exceeds the existing finite membership profile."); return false;
    }
    private bool ProjectionStep()
    {
        files.Check();
        if (++projectionSteps <= MaximumSteps) return true;
        Unsupported("Compiler span/token/line projection exceeds the existing finite membership profile."); return false;
    }
    private static int EffectiveLength(string text) => text.Length > 0 && text[^1] == '\u001a' ? text.Length - 1 : text.Length;
    private PhysicalLine[]? Lines(string text)
    {
        var result = new List<PhysicalLine>(); var length = EffectiveLength(text);
        var start = length > 0 && text[0] == '\ufeff' ? 1 : 0;
        for (var index = start; index < length; index++)
        {
            if ((index & 4095) == 0) files.Check();
            if (text[index] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029')) continue;
            var end = index + 1;
            if (text[index] == '\r' && end < length && text[end] == '\n') end++;
            if (!ParseStep()) return null;
            result.Add(new(start, index - start, end)); start = end; index = end - 1;
        }
        if (start < length)
        {
            if (!ParseStep()) return null;
            result.Add(new(start, length - start, length));
        }
        return result.ToArray();
    }

    internal bool Add(CompilerLineCensus.Point point)
    {
        files.Check();
        if (Reason is not null) return false;
        if (!sources.TryGetValue(point.LogicalPath, out var source) || !Interval(source, point, out var start, out var end))
        { Unsupported("Compiler point has unsupported ordered half-open physical source coordinates."); return false; }
        var last = Math.Min(point.EndLine, source.Lines.Length);
        for (var line = point.StartLine; line <= last; line++)
        {
            if (!ProjectionStep()) return false;
            provider.Add(new(point.LogicalPath, line));
        }
        var low = 0; var high = source.Tokens.Length;
        while (low < high)
        {
            if (!ProjectionStep()) return false;
            var middle = low + (high - low) / 2;
            if (source.Tokens[middle].End <= start) low = middle + 1; else high = middle;
        }
        var tokenCharacterSeen = false;
        for (var index = low; index < source.Tokens.Length && source.Tokens[index].Start < end; index++)
        {
            if (!ProjectionStep()) return false;
            var token = source.Tokens[index]; var begin = Math.Max(start, token.Start); var finish = Math.Min(end, token.End);
            if (finish <= begin) continue;
            // Binary search the first physical line whose content/end can meet
            // this token. No point*token*all-lines Cartesian traversal.
            var first = point.StartLine - 1; var upper = last;
            while (first < upper)
            {
                if (!ProjectionStep()) return false;
                var middle = first + (upper - first) / 2;
                if (source.Lines[middle].End <= begin) first = middle + 1; else upper = middle;
            }
            for (var line = first; line < last && source.Lines[line].Start < finish; line++)
            {
                if (!ProjectionStep()) return false;
                var physical = source.Lines[line];
                if (Math.Max(begin, physical.Start) < Math.Min(finish, physical.Start + physical.Length))
                {
                    tokenCharacterSeen = true;
                    policy.Add(new(point.LogicalPath, line + 1));
                }
            }
        }
        if (!tokenCharacterSeen)
        { Unsupported("A nonhidden compiler span has no usable source-token character; completeness is unproven."); return false; }
        files.Check(); return true;
    }

    private static bool Interval(Source source, CompilerLineCensus.Point point, out int start, out int end)
    {
        start = end = 0;
        if (point.StartLine <= 0 || point.StartLine > source.Lines.Length || point.StartColumn <= 0 ||
            point.EndLine < point.StartLine || point.EndColumn <= 0) return false;
        var first = source.Lines[point.StartLine - 1];
        if (point.StartColumn > first.Length + 1) return false;
        start = first.Start + point.StartColumn - 1;
        if (point.EndLine <= source.Lines.Length)
        {
            var last = source.Lines[point.EndLine - 1];
            if (point.EndColumn > last.Length + 1) return false;
            end = last.Start + point.EndColumn - 1;
        }
        else if (point.EndLine == source.Lines.Length + 1 && point.EndColumn == 1 &&
                 source.Lines[^1].End > source.Lines[^1].Start + source.Lines[^1].Length)
            end = source.Length; // Actual trailing newline, not a phantom physical line.
        else return false;
        return end > start;
    }

    internal IReadOnlyList<CompilerLineCensus.Line> ProviderLines
    {
        get { files.Check(); var result = Array.AsReadOnly(provider.ToArray()); files.Check(); return result; }
    }
    internal IReadOnlyList<CompilerLineCensus.Line> PolicyLines
    {
        get { files.Check(); var result = Array.AsReadOnly(policy.ToArray()); files.Check(); return result; }
    }
}
