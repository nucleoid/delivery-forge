using System.Text;

namespace DeliveryForge.Evidence;

/// <summary>
/// A bounded JavaScript-family lexical scanner for Oxlint/ESLint directive comments.
/// It recognizes comment tokens only while scanning code, so directive-shaped bytes in
/// strings, templates, and regular-expression literals are never treated as comments.
/// </summary>
internal static class JavaScriptSuppressionScanner
{
    internal const int MaximumInputBytes = 4 * 1024 * 1024;
    internal const int MaximumDirectives = 1024;
    internal const int MaximumDirectiveBodyBytes = 16 * 1024;
    internal const int MaximumRulesPerDirective = 256;
    internal const int MaximumRuleBytes = 256;
    internal const int MaximumNestingDepth = 256;
    private const int CheckIntervalBytes = 16 * 1024;

    private static readonly DirectiveName[] DirectiveNames =
    [
        new("eslint-disable-next-line", SuppressionScope.Local, Disables: true, TargetLineOffset: 1),
        new("eslint-disable-line", SuppressionScope.Local, Disables: true, TargetLineOffset: 0),
        new("eslint-disable", SuppressionScope.File, Disables: true, TargetLineOffset: 0),
        new("eslint-enable", SuppressionScope.File, Disables: false, TargetLineOffset: 0),
        new("oxlint-disable-next-line", SuppressionScope.Local, Disables: true, TargetLineOffset: 1),
        new("oxlint-disable-line", SuppressionScope.Local, Disables: true, TargetLineOffset: 0),
        new("oxlint-disable", SuppressionScope.File, Disables: true, TargetLineOffset: 0),
        new("oxlint-enable", SuppressionScope.File, Disables: false, TargetLineOffset: 0)
    ];

    internal static SuppressionProfile Scan(
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken = default,
        DateTimeOffset? deadline = null)
    {
        if (input.Length > MaximumInputBytes)
            throw new EvidenceRepositoryException(
                $"Changed JavaScript-family blob exceeds the {MaximumInputBytes}-byte suppression scan ceiling.");

        var bytes = input.Span;
        var budget = new ScanBudget(cancellationToken, deadline);
        var profile = new SuppressionProfile(input.Span);
        var index = 0;
        ScanCode(bytes, ref index, profile, budget, stopAtTemplateExpressionEnd: false);
        budget.Check(bytes.Length, force: true);
        profile.FinalizeRanges();
        return profile;
    }

    internal static bool IntroducesDangerousCoverage(SuppressionProfile head, SuppressionProfile? @base)
    {
        foreach (var candidate in head.Coverages.Where(coverage => coverage.IsDangerous))
        {
            if (@base is null || !head.HasInheritedCoverage(candidate, @base))
                return true;
        }
        return false;
    }

    private static void ScanCode(
        ReadOnlySpan<byte> content,
        ref int index,
        SuppressionProfile profile,
        ScanBudget budget,
        bool stopAtTemplateExpressionEnd)
    {
        var braceDepth = 0;
        var canStartRegularExpression = true;
        var expectControlParenthesis = false;
        var controlParentheses = new Stack<bool>();

        while (index < content.Length)
        {
            budget.Check(index);
            var current = content[index];
            if (IsWhitespace(current))
            {
                index++;
                continue;
            }
            if (current is (byte)'\'' or (byte)'"')
            {
                if (IsLikelyJsxText(content, index))
                    throw Ambiguous("JSX text cannot be distinguished from TypeScript syntax without parser evidence");
                if (current == (byte)'\'' && !canStartRegularExpression && index > 0 && index + 1 < content.Length &&
                    IsAsciiAlphaNumeric(content[index - 1]) && IsAsciiAlphaNumeric(content[index + 1]))
                {
                    // JSX text contractions are not JavaScript string delimiters.
                    index++;
                    continue;
                }
                if (!SkipQuoted(content, ref index, current, budget))
                    throw Ambiguous("unterminated string literal");
                canStartRegularExpression = false;
                expectControlParenthesis = false;
                continue;
            }
            if (current == (byte)'`')
            {
                if (IsLikelyJsxText(content, index))
                    throw Ambiguous("JSX text cannot be distinguished from TypeScript syntax without parser evidence");
                if (!ScanTemplate(content, ref index, profile, budget))
                    throw Ambiguous("unterminated template literal");
                canStartRegularExpression = false;
                expectControlParenthesis = false;
                continue;
            }
            if (current == (byte)'/' && index + 1 < content.Length && content[index + 1] == (byte)'/')
            {
                var start = index += 2;
                while (index < content.Length && content[index] is not ((byte)'\r') and not ((byte)'\n') &&
                       !IsUnicodeLineTerminator(content, index))
                {
                    budget.Check(index);
                    index++;
                }
                ParseComment(content, start, index, profile);
                continue;
            }
            if (current == (byte)'/' && index + 1 < content.Length && content[index + 1] == (byte)'*')
            {
                var start = index += 2;
                while (index + 1 < content.Length &&
                       !(content[index] == (byte)'*' && content[index + 1] == (byte)'/'))
                {
                    budget.Check(index);
                    index++;
                }
                if (index + 1 >= content.Length)
                    throw Ambiguous("unterminated block comment");
                ParseComment(content, start, index, profile);
                index += 2;
                continue;
            }
            if (current == (byte)'/' && canStartRegularExpression &&
                (index == 0 || content[index - 1] != (byte)'<'))
            {
                if (!SkipRegularExpression(content, ref index, budget))
                    throw Ambiguous("unterminated regular-expression literal");
                canStartRegularExpression = false;
                expectControlParenthesis = false;
                continue;
            }
            if (current == (byte)'{')
            {
                braceDepth++;
                index++;
                canStartRegularExpression = true;
                expectControlParenthesis = false;
                continue;
            }
            if (current == (byte)'}')
            {
                if (stopAtTemplateExpressionEnd && braceDepth == 0)
                {
                    index++;
                    return;
                }
                braceDepth = Math.Max(0, braceDepth - 1);
                index++;
                canStartRegularExpression = false;
                expectControlParenthesis = false;
                continue;
            }
            if (current == (byte)'(')
            {
                controlParentheses.Push(expectControlParenthesis);
                expectControlParenthesis = false;
                canStartRegularExpression = true;
                index++;
                continue;
            }
            if (current == (byte)')')
            {
                var control = controlParentheses.Count > 0 && controlParentheses.Pop();
                canStartRegularExpression = control;
                expectControlParenthesis = false;
                index++;
                continue;
            }
            if (IsIdentifierStart(current))
            {
                var keywordPosition = canStartRegularExpression;
                var start = index++;
                while (index < content.Length && IsIdentifierPart(content[index]))
                {
                    budget.Check(index);
                    index++;
                }
                var identifier = Encoding.ASCII.GetString(content.Slice(start, index - start));
                expectControlParenthesis = keywordPosition &&
                    identifier is "if" or "while" or "for" or "with" or "switch" or "catch";
                canStartRegularExpression = keywordPosition && ExpressionPrefixKeywords.Contains(identifier);
                continue;
            }
            if (current is >= (byte)'0' and <= (byte)'9')
            {
                index++;
                while (index < content.Length && IsNumberPart(content[index]))
                {
                    budget.Check(index);
                    index++;
                }
                canStartRegularExpression = false;
                expectControlParenthesis = false;
                continue;
            }

            if (current is (byte)'+' or (byte)'-' && index + 1 < content.Length &&
                content[index + 1] == current)
            {
                index += 2;
                expectControlParenthesis = false;
                canStartRegularExpression = false;
                continue;
            }
            index++;
            expectControlParenthesis = false;
            canStartRegularExpression = current switch
            {
                (byte)']' => false,
                (byte)'.' => false,
                _ => true
            };
        }

        if (stopAtTemplateExpressionEnd)
            throw Ambiguous("unterminated template expression");
    }

    private static bool ScanTemplate(
        ReadOnlySpan<byte> content,
        ref int index,
        SuppressionProfile profile,
        ScanBudget budget)
    {
        index++;
        var states = new Stack<TemplateState>();
        states.Push(new(false));
        while (index < content.Length && states.Count > 0)
        {
            budget.Check(index);
            if (states.Count > MaximumNestingDepth)
                throw Ambiguous($"template nesting exceeds the {MaximumNestingDepth}-level ceiling");
            var state = states.Peek();
            if (!state.IsCode)
            {
                if (content[index] == (byte)'\\')
                {
                    index = Math.Min(content.Length, index + 2);
                    continue;
                }
                if (content[index] == (byte)'`')
                {
                    index++;
                    states.Pop();
                    continue;
                }
                if (content[index] == (byte)'$' && index + 1 < content.Length &&
                    content[index + 1] == (byte)'{')
                {
                    index += 2;
                    states.Push(new(true));
                    continue;
                }
                index++;
                continue;
            }

            var current = content[index];
            if (current is (byte)'\'' or (byte)'"')
            {
                if (!SkipQuoted(content, ref index, current, budget))
                    throw Ambiguous("unterminated string literal in template expression");
                continue;
            }
            if (current == (byte)'`')
            {
                index++;
                states.Push(new(false));
                continue;
            }
            if (current == (byte)'/' && index + 1 < content.Length && content[index + 1] == (byte)'/')
            {
                var start = index += 2;
                while (index < content.Length && content[index] is not ((byte)'\r') and not ((byte)'\n') &&
                       !IsUnicodeLineTerminator(content, index))
                    index++;
                ParseComment(content, start, index, profile);
                continue;
            }
            if (current == (byte)'/' && index + 1 < content.Length && content[index + 1] == (byte)'*')
            {
                var start = index += 2;
                while (index + 1 < content.Length &&
                       !(content[index] == (byte)'*' && content[index + 1] == (byte)'/'))
                    index++;
                if (index + 1 >= content.Length)
                    throw Ambiguous("unterminated block comment in template expression");
                ParseComment(content, start, index, profile);
                index += 2;
                continue;
            }
            if (current == (byte)'{')
            {
                state.BraceDepth++;
                index++;
                continue;
            }
            if (current == (byte)'}')
            {
                index++;
                if (state.BraceDepth == 0)
                    states.Pop();
                else
                    state.BraceDepth--;
                continue;
            }
            index++;
        }
        return states.Count == 0;
    }

    private static bool SkipQuoted(ReadOnlySpan<byte> content, ref int index, byte quote, ScanBudget budget)
    {
        index++;
        while (index < content.Length)
        {
            budget.Check(index);
            if (content[index] == (byte)'\\')
            {
                if (index + 1 < content.Length && content[index + 1] is (byte)'\r' or (byte)'\n')
                {
                    index += content[index + 1] == (byte)'\r' && index + 2 < content.Length &&
                        content[index + 2] == (byte)'\n' ? 3 : 2;
                    continue;
                }
                index = Math.Min(content.Length, index + 2);
                continue;
            }
            if (content[index] is (byte)'\r' or (byte)'\n')
                return false;
            if (content[index++] == quote)
                return true;
        }
        return false;
    }

    private static bool SkipRegularExpression(ReadOnlySpan<byte> content, ref int index, ScanBudget budget)
    {
        index++;
        var inCharacterClass = false;
        while (index < content.Length && content[index] is not ((byte)'\r') and not ((byte)'\n'))
        {
            budget.Check(index);
            if (content[index] == (byte)'\\')
            {
                index = Math.Min(content.Length, index + 2);
                continue;
            }
            if (content[index] == (byte)'[')
                inCharacterClass = true;
            else if (content[index] == (byte)']')
                inCharacterClass = false;
            else if (content[index] == (byte)'/' && !inCharacterClass)
            {
                index++;
                while (index < content.Length && IsIdentifierPart(content[index]))
                    index++;
                return true;
            }
            index++;
        }
        return false;
    }

    private static void ParseComment(ReadOnlySpan<byte> content, int start, int end, SuppressionProfile profile)
    {
        if (end - start > MaximumDirectiveBodyBytes)
        {
            // Long non-directive comments remain harmless. Once the prefix is a directive,
            // however, silently truncating would make the rule semantics ambiguous.
            var prefix = SkipWhitespace(content, start, end);
            if (MatchDirective(content, prefix, end) is not null)
                throw new EvidenceRepositoryException(
                    $"Suppression directive body exceeds the {MaximumDirectiveBodyBytes}-byte ceiling.");
            return;
        }

        var cursor = SkipWhitespace(content, start, end);
        var directive = MatchDirective(content, cursor, end);
        if (directive is null)
            return;
        cursor += directive.Name.Length;
        if (cursor < end && !IsWhitespace(content[cursor]))
            return;
        if (++profile.DirectiveCount > MaximumDirectives)
            throw new EvidenceRepositoryException(
                $"JavaScript-family blob exceeds the {MaximumDirectives}-directive ceiling.");
        cursor = SkipWhitespace(content, cursor, end);
        var rulesEnd = end;
        for (var index = cursor; index + 1 < end; index++)
        {
            if (content[index] == (byte)'-' && content[index + 1] == (byte)'-')
            {
                rulesEnd = index;
                break;
            }
        }
        rulesEnd = TrimWhitespaceEnd(content, cursor, rulesEnd);
        if (cursor == rulesEnd)
        {
            profile.Events.Add(new(directive.Scope, "*", directive.Disables,
                directive.TargetLineOffset, profile.LineAt(start), profile.LineBytesAt(start)));
            return;
        }

        var ruleCount = 0;
        while (cursor <= rulesEnd)
        {
            var comma = cursor;
            while (comma < rulesEnd && content[comma] != (byte)',')
                comma++;
            var ruleStart = SkipWhitespace(content, cursor, comma);
            var ruleEnd = TrimWhitespaceEnd(content, ruleStart, comma);
            if (ruleStart == ruleEnd)
                throw new EvidenceRepositoryException("Suppression directive contains an ambiguous empty rule entry.");
            if (ruleEnd - ruleStart > MaximumRuleBytes)
                throw new EvidenceRepositoryException(
                    $"Suppression directive rule exceeds the {MaximumRuleBytes}-byte ceiling.");
            if (++ruleCount > MaximumRulesPerDirective)
                throw new EvidenceRepositoryException(
                    $"Suppression directive exceeds the {MaximumRulesPerDirective}-rule ceiling.");
            var rule = Encoding.UTF8.GetString(content.Slice(ruleStart, ruleEnd - ruleStart));
            profile.Events.Add(new(directive.Scope, rule, directive.Disables,
                directive.TargetLineOffset, profile.LineAt(start), profile.LineBytesAt(start)));
            if (comma == rulesEnd)
                break;
            cursor = comma + 1;
        }
    }

    private static DirectiveName? MatchDirective(ReadOnlySpan<byte> content, int start, int end)
    {
        foreach (var directive in DirectiveNames)
        {
            if (end - start < directive.Name.Length)
                continue;
            var match = true;
            for (var index = 0; index < directive.Name.Length; index++)
                match &= content[start + index] == (byte)directive.Name[index];
            if (match)
                return directive;
        }
        return null;
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> content, int start, int end)
    {
        while (start < end)
        {
            if (IsWhitespace(content[start]))
            {
                start++;
                continue;
            }
            if (start + 1 < end && content[start] == 0xc2 && content[start + 1] == 0xa0)
            {
                start += 2;
                continue;
            }
            if (start + 2 < end && content[start] == 0xef && content[start + 1] == 0xbb && content[start + 2] == 0xbf)
            {
                start += 3;
                continue;
            }
            break;
        }
        return start;
    }

    private static int TrimWhitespaceEnd(ReadOnlySpan<byte> content, int start, int end)
    {
        while (end > start)
        {
            if (IsWhitespace(content[end - 1]))
            {
                end--;
                continue;
            }
            if (end - start >= 2 && content[end - 2] == 0xc2 && content[end - 1] == 0xa0)
            {
                end -= 2;
                continue;
            }
            if (end - start >= 3 && content[end - 3] == 0xef && content[end - 2] == 0xbb && content[end - 1] == 0xbf)
            {
                end -= 3;
                continue;
            }
            break;
        }
        return end;
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v';

    private static bool IsUnicodeLineTerminator(ReadOnlySpan<byte> content, int index) =>
        index + 2 < content.Length && content[index] == 0xe2 && content[index + 1] == 0x80 &&
        content[index + 2] is 0xa8 or 0xa9;

    private static bool IsLikelyJsxText(ReadOnlySpan<byte> content, int index)
    {
        var previous = index - 1;
        while (previous >= 0 && IsWhitespace(content[previous]))
            previous--;
        while (previous >= 0 && content[previous] is not (byte)'>' and not (byte)'{' and not (byte)'}' &&
               content[previous] is not (byte)'\r' and not (byte)'\n')
            previous--;
        if (previous < 0 || content[previous] != (byte)'>')
            return false;
        for (var next = index + 1; next < content.Length; next++)
        {
            if (content[next] == (byte)'<')
                return true;
            if (content[next] == (byte)'{')
                return false;
        }
        return false;
    }

    private static bool IsAsciiAlphaNumeric(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9';

    private static bool IsIdentifierStart(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_' or (byte)'$' || value >= 0x80;

    private static bool IsIdentifierPart(byte value) => IsIdentifierStart(value) || value is >= (byte)'0' and <= (byte)'9';

    private static bool IsNumberPart(byte value) =>
        IsAsciiAlphaNumeric(value) || value is (byte)'.' or (byte)'_';

    private static EvidenceRepositoryException Ambiguous(string detail) =>
        new($"JavaScript-family suppression scan is ambiguous: {detail}.");

    private static readonly HashSet<string> ExpressionPrefixKeywords = new(StringComparer.Ordinal)
    {
        // await/yield/of can be identifiers in supported JavaScript-family grammars. Treating
        // a following slash as division may reject ambiguity, but cannot hide a directive.
        "case", "delete", "do", "else", "in", "instanceof", "new", "return", "throw",
        "typeof", "void"
    };

    private sealed record DirectiveName(
        string Name, SuppressionScope Scope, bool Disables, int TargetLineOffset);

    private sealed class TemplateState(bool isCode)
    {
        internal bool IsCode { get; } = isCode;
        internal int BraceDepth { get; set; }
    }

    private sealed class ScanBudget(CancellationToken cancellationToken, DateTimeOffset? deadline)
    {
        private int nextCheck;

        internal void Check(int consumedBytes, bool force = false)
        {
            if (!force && consumedBytes < nextCheck)
                return;
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline is not null && DateTimeOffset.UtcNow >= deadline)
                throw new OperationCanceledException("The finite suppression scan deadline expired.");
            nextCheck = consumedBytes > int.MaxValue - CheckIntervalBytes
                ? int.MaxValue
                : consumedBytes + CheckIntervalBytes;
        }
    }
}

internal enum SuppressionScope
{
    Local = 1,
    File = 2
}

internal sealed record SuppressionDirectiveEvent(
    SuppressionScope Scope,
    string Rule,
    bool Disables,
    int TargetLineOffset,
    int DirectiveLine,
    string DirectiveLineBytes);

internal sealed record SuppressionCoverage(
    SuppressionScope Scope,
    string Rule,
    int DirectiveLine,
    int StartLine,
    int EndLine,
    string DirectiveLineBytes)
{
    internal bool IsDangerous =>
        Rule == "*" ||
        Rule.StartsWith("anti-slop/", StringComparison.Ordinal) ||
        Rule.StartsWith("anti-slop-effect/", StringComparison.Ordinal) ||
        string.Equals(Rule, "oxc/no-accumulating-spread", StringComparison.Ordinal) ||
        KnownBareAntiSlopRules.Contains(Rule) ||
        !Rule.Contains('/') && !KnownSafeBareRules.Contains(Rule);

    private static readonly HashSet<string> KnownBareAntiSlopRules = new(StringComparer.Ordinal)
    {
        "no-array-filter-map", "no-chained-type-assertions", "no-conditional-empty-object-spread",
        "no-known-value-widening", "no-module-mocking", "no-object-parameters",
        "no-reduce-accumulator-copy", "no-reflect-apply", "no-reflect-get",
        "no-runtime-typeof", "no-shape-in-symbol-names", "no-unknown-parameters",
        "no-unknown-returns", "no-unknown-type-aliases", "no-unsafe-dictionary-type",
        "no-widen-then-assert", "require-readable-spacing",
        "require-safety-comment-for-type-assertion", "no-manual-effect-error-tag",
        "no-manual-tag-comparison", "no-manual-tagged-construction",
        "no-service-constructor-imports", "prefer-effect-match"
    };

    // This allow-list is intentionally tiny. Pinned Oxlint accepts bare and even unknown
    // suppression names without diagnostics, so unclassified bare names fail closed.
    private static readonly HashSet<string> KnownSafeBareRules = new(StringComparer.Ordinal)
    {
        "no-console", "no-unused-vars", "eqeqeq", "prefer-const"
    };
}

internal sealed class SuppressionProfile
{
    private readonly int sourceLength;
    private readonly int[] lineStarts;
    private readonly byte[][] lineHashes;

    internal SuppressionProfile(ReadOnlySpan<byte> source)
    {
        sourceLength = source.Length;
        var starts = new List<int> { 0 };
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == (byte)'\r')
            {
                if (index + 1 < source.Length && source[index + 1] == (byte)'\n')
                    index++;
                starts.Add(index + 1);
            }
            else if (source[index] == (byte)'\n')
                starts.Add(index + 1);
            else if (index + 2 < source.Length && source[index] == 0xe2 && source[index + 1] == 0x80 &&
                     source[index + 2] is 0xa8 or 0xa9)
            {
                index += 2;
                starts.Add(index + 1);
            }
        }
        lineStarts = starts.ToArray();
        lineHashes = new byte[lineStarts.Length][];
        for (var line = 0; line < lineStarts.Length; line++)
        {
            var end = line + 1 < lineStarts.Length ? lineStarts[line + 1] : source.Length;
            while (end > lineStarts[line] && source[end - 1] is (byte)'\r' or (byte)'\n')
                end--;
            if (end >= lineStarts[line] + 3 && source[end - 3] == 0xe2 && source[end - 2] == 0x80 &&
                source[end - 1] is 0xa8 or 0xa9)
                end -= 3;
            lineHashes[line] = System.Security.Cryptography.SHA256.HashData(
                source.Slice(lineStarts[line], end - lineStarts[line]));
        }
    }

    internal int DirectiveCount { get; set; }
    internal List<SuppressionDirectiveEvent> Events { get; } = [];
    internal List<SuppressionCoverage> Coverages { get; } = [];

    internal int LineAt(int byteOffset)
    {
        var line = Array.BinarySearch(lineStarts, Math.Clamp(byteOffset, 0, sourceLength));
        return line >= 0 ? line : ~line - 1;
    }

    internal string LineBytesAt(int byteOffset)
    {
        var line = LineAt(byteOffset);
        return line < lineHashes.Length ? Convert.ToHexString(lineHashes[line]) : string.Empty;
    }

    internal void FinalizeRanges()
    {
        var open = new List<SuppressionDirectiveEvent>();
        foreach (var directive in Events.OrderBy(value => value.DirectiveLine))
        {
            if (directive.Scope == SuppressionScope.Local && directive.Disables)
            {
                var target = Math.Min(lineHashes.Length - 1, directive.DirectiveLine + directive.TargetLineOffset);
                Coverages.Add(new(directive.Scope, directive.Rule, directive.DirectiveLine,
                    target, target, directive.DirectiveLineBytes));
                continue;
            }
            if (directive.Disables)
            {
                open.Add(directive);
                continue;
            }
            for (var index = open.Count - 1; index >= 0; index--)
            {
                var disabled = open[index];
                if (directive.Rule != "*" && disabled.Rule != "*" &&
                    !string.Equals(directive.Rule, disabled.Rule, StringComparison.Ordinal))
                    continue;
                Coverages.Add(new(disabled.Scope, disabled.Rule, disabled.DirectiveLine,
                    disabled.DirectiveLine, Math.Max(disabled.DirectiveLine, directive.DirectiveLine - 1),
                    disabled.DirectiveLineBytes));
                open.RemoveAt(index);
            }
        }
        foreach (var disabled in open)
            Coverages.Add(new(disabled.Scope, disabled.Rule, disabled.DirectiveLine,
                disabled.DirectiveLine, Math.Max(disabled.DirectiveLine, lineHashes.Length - 1),
                disabled.DirectiveLineBytes));
    }

    internal bool HasInheritedCoverage(SuppressionCoverage candidate, SuppressionProfile @base)
    {
        foreach (var inherited in @base.Coverages)
        {
            if (inherited.Scope != candidate.Scope || inherited.Rule != candidate.Rule ||
                !string.Equals(inherited.DirectiveLineBytes, candidate.DirectiveLineBytes, StringComparison.Ordinal))
                continue;
            var candidateLength = candidate.EndLine - candidate.StartLine + 1;
            if (candidateLength != inherited.EndLine - inherited.StartLine + 1)
                continue;
            var identical = true;
            for (var offset = 0; offset < candidateLength; offset++)
            {
                if (!lineHashes[candidate.StartLine + offset]
                    .AsSpan().SequenceEqual(@base.lineHashes[inherited.StartLine + offset]))
                {
                    identical = false;
                    break;
                }
            }
            if (identical)
                return true;
        }
        return false;
    }
}
