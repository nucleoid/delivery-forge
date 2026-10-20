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
    private const int CheckIntervalBytes = 16 * 1024;

    private static readonly DirectiveName[] DirectiveNames =
    [
        new("eslint-disable-next-line", SuppressionScope.Local, Disables: true),
        new("eslint-disable-line", SuppressionScope.Local, Disables: true),
        new("eslint-disable", SuppressionScope.File, Disables: true),
        new("eslint-enable", SuppressionScope.File, Disables: false),
        new("oxlint-disable-next-line", SuppressionScope.Local, Disables: true),
        new("oxlint-disable-line", SuppressionScope.Local, Disables: true),
        new("oxlint-disable", SuppressionScope.File, Disables: true),
        new("oxlint-enable", SuppressionScope.File, Disables: false)
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
        var profile = new SuppressionProfile();
        var index = 0;
        ScanCode(bytes, ref index, profile, budget, stopAtTemplateExpressionEnd: false);
        budget.Check(bytes.Length, force: true);
        return profile;
    }

    internal static bool IntroducesDangerousCoverage(SuppressionProfile head, SuppressionProfile? @base)
    {
        var inherited = @base?.Coverages ?? [];
        var used = new bool[inherited.Count];
        foreach (var candidate in head.Coverages.Where(coverage => coverage.IsDangerous))
        {
            var match = -1;
            for (var index = 0; index < inherited.Count; index++)
            {
                if (used[index] || !CanInherit(inherited[index], candidate))
                    continue;
                match = index;
                break;
            }
            if (match < 0)
                return true;
            used[match] = true;
        }
        return false;
    }

    private static bool CanInherit(SuppressionCoverage inherited, SuppressionCoverage head)
    {
        if (head.Rule == "*")
        {
            if (inherited.Rule != "*")
                return false;
        }
        else if (inherited.Rule != "*" && !string.Equals(inherited.Rule, head.Rule, StringComparison.Ordinal))
        {
            return false;
        }
        return inherited.Scope >= head.Scope;
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
                if (!ScanTemplate(content, ref index, profile, budget))
                    throw Ambiguous("unterminated template literal");
                canStartRegularExpression = false;
                expectControlParenthesis = false;
                continue;
            }
            if (current == (byte)'/' && index + 1 < content.Length && content[index + 1] == (byte)'/')
            {
                var start = index += 2;
                while (index < content.Length && content[index] is not ((byte)'\r') and not ((byte)'\n'))
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
                canStartRegularExpression = true;
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
                canStartRegularExpression = ExpressionPrefixKeywords.Contains(identifier);
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

            index++;
            expectControlParenthesis = false;
            canStartRegularExpression = current switch
            {
                (byte)']' => false,
                (byte)'.' => false,
                (byte)'+' or (byte)'-' when index < content.Length && content[index] == current => false,
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
        while (index < content.Length)
        {
            budget.Check(index);
            if (content[index] == (byte)'\\')
            {
                index = Math.Min(content.Length, index + 2);
                continue;
            }
            if (content[index] == (byte)'`')
            {
                index++;
                return true;
            }
            if (content[index] == (byte)'$' && index + 1 < content.Length && content[index + 1] == (byte)'{')
            {
                index += 2;
                ScanCode(content, ref index, profile, budget, stopAtTemplateExpressionEnd: true);
                continue;
            }
            index++;
        }
        return false;
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
        if (!directive.Disables)
            return;

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
            profile.Coverages.Add(new(directive.Scope, "*"));
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
            profile.Coverages.Add(new(directive.Scope, rule));
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
        while (start < end && IsWhitespace(content[start]))
            start++;
        return start;
    }

    private static int TrimWhitespaceEnd(ReadOnlySpan<byte> content, int start, int end)
    {
        while (end > start && IsWhitespace(content[end - 1]))
            end--;
        return end;
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f';

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
        "await", "case", "delete", "do", "else", "in", "instanceof", "new", "of", "return", "throw",
        "typeof", "void", "yield"
    };

    private sealed record DirectiveName(string Name, SuppressionScope Scope, bool Disables);

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

internal sealed record SuppressionCoverage(SuppressionScope Scope, string Rule)
{
    internal bool IsDangerous =>
        Rule == "*" ||
        Rule.StartsWith("anti-slop/", StringComparison.Ordinal) ||
        Rule.StartsWith("anti-slop-effect/", StringComparison.Ordinal) ||
        string.Equals(Rule, "oxc/no-accumulating-spread", StringComparison.Ordinal);
}

internal sealed class SuppressionProfile
{
    internal int DirectiveCount { get; set; }
    internal List<SuppressionCoverage> Coverages { get; } = [];
}
