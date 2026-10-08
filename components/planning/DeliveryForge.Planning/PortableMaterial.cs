using System.Text;
using System.Text.RegularExpressions;

namespace DeliveryForge.Planning;

internal static partial class PortableMaterial
{
    private static readonly HashSet<string> CredentialKeys = new(StringComparer.Ordinal)
    {
        "api_key", "apikey", "authorization", "client_secret", "clientsecret",
        "access_key", "accesskey", "access_token", "accesstoken", "password",
        "passwd", "pgpassword", "proxy_authorization", "secret", "token",
        "aws_access_key_id", "aws_secret_access_key", "x_api_key"
    };

    private enum CurlOptionValueKind
    {
        Ordinary,
        Secret,
        UserInfo,
        ProxyUserInfo,
        Certificate
    }

    // This is the single arity registry for supported curl options. A listed option always consumes
    // exactly one value, so words that resemble prose or command boundaries remain option values.
    private static readonly IReadOnlyDictionary<string, CurlOptionValueKind> CurlLongOptions = BuildCurlLongOptions();

    private static readonly IReadOnlyDictionary<char, CurlOptionValueKind> CurlShortOptions =
        new Dictionary<char, CurlOptionValueKind>
        {
            ['A'] = CurlOptionValueKind.Ordinary,
            ['b'] = CurlOptionValueKind.Secret,
            ['c'] = CurlOptionValueKind.Ordinary,
            ['C'] = CurlOptionValueKind.Ordinary,
            ['d'] = CurlOptionValueKind.Ordinary,
            ['D'] = CurlOptionValueKind.Ordinary,
            ['e'] = CurlOptionValueKind.Ordinary,
            ['E'] = CurlOptionValueKind.Certificate,
            ['F'] = CurlOptionValueKind.Ordinary,
            ['H'] = CurlOptionValueKind.Ordinary,
            ['K'] = CurlOptionValueKind.Ordinary,
            ['m'] = CurlOptionValueKind.Ordinary,
            ['o'] = CurlOptionValueKind.Ordinary,
            ['P'] = CurlOptionValueKind.Ordinary,
            ['Q'] = CurlOptionValueKind.Ordinary,
            ['r'] = CurlOptionValueKind.Ordinary,
            ['R'] = CurlOptionValueKind.Ordinary,
            ['T'] = CurlOptionValueKind.Ordinary,
            ['u'] = CurlOptionValueKind.UserInfo,
            ['U'] = CurlOptionValueKind.UserInfo,
            ['w'] = CurlOptionValueKind.Ordinary,
            ['x'] = CurlOptionValueKind.ProxyUserInfo,
            ['X'] = CurlOptionValueKind.Ordinary,
            ['y'] = CurlOptionValueKind.Ordinary,
            ['Y'] = CurlOptionValueKind.Ordinary
        };

    private static readonly HashSet<string> NetworkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "ssh", "git", "ftp", "ftps"
    };

    private static IReadOnlyDictionary<string, CurlOptionValueKind> BuildCurlLongOptions()
    {
        var options = new Dictionary<string, CurlOptionValueKind>(StringComparer.OrdinalIgnoreCase);
        const string ordinaryOptions =
            "--abstract-unix-socket --alt-svc --aws-sigv4 --cacert --capath --cert-type --ciphers " +
            "--config --connect-timeout --connect-to --continue-at --cookie-jar --create-file-mode --crlfile " +
            "--curves --data --data-ascii --data-binary --data-raw --data-urlencode --delegation " +
            "--dns-interface --dns-ipv4-addr --dns-ipv6-addr --dns-servers --doh-url --dump-header " +
            "--egd-file --engine --etag-compare --etag-save --expect100-timeout --form --form-string " +
            "--ftp-account --ftp-alternative-to-user --ftp-method --ftp-port --ftp-ssl-ccc-mode " +
            "--happy-eyeballs-timeout-ms --header --hostpubmd5 --hsts --interface --ip-tos --json " +
            "--keepalive-time --key --key-type --krb --libcurl --limit-rate --local-port --login-options " +
            "--mail-auth --mail-from --mail-rcpt --max-filesize --max-redirs --max-time --noproxy " +
            "--output --output-dir --parallel-max --pinnedpubkey --proto --proto-default --proto-redir " +
            "--proxy-cacert --proxy-capath --proxy-cert-type --proxy-ciphers --proxy-crlfile --proxy-header " +
            "--proxy-key --proxy-key-type --proxy-service-name --proxy-tls13-ciphers --proxy-tlsauthtype " +
            "--pubkey --quote --range --referer --request --request-target --resolve --retry --retry-delay " +
            "--retry-max-time --sasl-authzid --service-name --speed-limit --speed-time --tls-max " +
            "--tls13-ciphers --tlsauthtype --unix-socket --upload-file --url --url-query --user-agent " +
            "--variable --vlan-priority --write-out";
        foreach (var option in ordinaryOptions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            options.Add(option, CurlOptionValueKind.Ordinary);
        }

        foreach (var option in new[] { "--oauth2-bearer", "--pass", "--proxy-pass", "--tlspassword", "--proxy-tlspassword", "--cookie" })
        {
            options.Add(option, CurlOptionValueKind.Secret);
        }

        options.Add("--user", CurlOptionValueKind.UserInfo);
        options.Add("--proxy-user", CurlOptionValueKind.UserInfo);
        foreach (var option in new[] { "--proxy", "--proxy1.0", "--preproxy", "--socks4", "--socks4a", "--socks5", "--socks5-hostname" })
        {
            options.Add(option, CurlOptionValueKind.ProxyUserInfo);
        }

        options.Add("--cert", CurlOptionValueKind.Certificate);
        options.Add("--proxy-cert", CurlOptionValueKind.Certificate);
        return options;
    }

    public static bool ContainsPrivateMaterial(string value)
    {
        if (ObviousCredential().IsMatch(value) ||
            ContainsCredentialAssignment(value) ||
            ContainsPopulatedAuthorization(value) ||
            ContainsCurlCredential(value))
        {
            return true;
        }

        var invalidUrl = false;
        var withoutUrls = SchemeUrl().Replace(value, match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ||
                !NetworkSchemes.Contains(uri.Scheme) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                invalidUrl = true;
                return match.Value;
            }

            return "portable-url";
        });
        return invalidUrl ||
               HostPath().IsMatch(withoutUrls) ||
               OptionAttachedHostPath().IsMatch(withoutUrls) ||
               HomeAliasPath().IsMatch(withoutUrls) ||
               SingleBackslashRoot().IsMatch(withoutUrls);
    }

    private static bool ContainsCredentialAssignment(string value)
    {
        foreach (var physicalLine in PhysicalLines(value))
        {
            var line = NormalizeEscapedQuotes(physicalLine);
            for (var separator = 0; separator < line.Length; separator++)
            {
                if (line[separator] is not (':' or '=') ||
                    !TryReadAssignmentKey(line, separator, out var key) ||
                    !IsCredentialKey(key) ||
                    !HasPopulatedAssignmentValue(line, separator))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool HasPopulatedAssignmentValue(string line, int separator)
    {
        var value = line.AsSpan(separator + 1);
        var outerQuote = FindOuterAssignmentQuote(line, separator);
        if (outerQuote != '\0')
        {
            var closingQuote = FindUnescapedQuote(line, separator + 1, outerQuote);
            if (closingQuote >= 0)
            {
                value = line.AsSpan(separator + 1, closingQuote - separator - 1);
            }
        }

        while (!value.IsEmpty && value[0] is ' ' or '\t')
        {
            value = value[1..];
        }

        return HasPopulatedValue(value);
    }

    private static char FindOuterAssignmentQuote(string line, int separator)
    {
        var end = separator;
        while (end > 0 && line[end - 1] is ' ' or '\t')
        {
            end--;
        }

        if (end > 0 && IsQuote(line[end - 1]))
        {
            return '\0';
        }

        var start = end;
        while (start > 0 && IsKeyCharacter(line[start - 1]))
        {
            start--;
        }

        return start > 0 && IsQuote(line[start - 1]) && !IsEscaped(line, start - 1)
            ? line[start - 1]
            : '\0';
    }

    private static bool TryReadAssignmentKey(string line, int separator, out string key)
    {
        var end = separator;
        while (end > 0 && line[end - 1] is ' ' or '\t')
        {
            end--;
        }

        if (end > 0 && IsQuote(line[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > 0 && IsKeyCharacter(line[start - 1]))
        {
            start--;
        }

        key = line[start..end];
        return key.Length > 0;
    }

    private static bool IsCredentialKey(string key)
    {
        var normalized = NormalizeCredentialKey(key);
        if (CredentialKeys.Contains(normalized))
        {
            return true;
        }

        var words = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 &&
               (words[^1] is "password" or "passwd" or "token" or "secret" or "authorization" ||
                words[^1] == "key" && words[^2] is "access" or "api" or "secret" or "private" or "signing" or "encryption");
    }

    private static string NormalizeCredentialKey(string key)
    {
        var normalized = new StringBuilder(key.Length + 4);
        for (var index = 0; index < key.Length; index++)
        {
            var character = key[index];
            if (character is '-' or '_' or '.')
            {
                if (normalized.Length > 0 && normalized[^1] != '_')
                {
                    normalized.Append('_');
                }
            }
            else
            {
                if (char.IsUpper(character) && index > 0 &&
                    (char.IsLower(key[index - 1]) ||
                     char.IsUpper(key[index - 1]) && index + 1 < key.Length && char.IsLower(key[index + 1])) &&
                    normalized[^1] != '_')
                {
                    normalized.Append('_');
                }

                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        return normalized.ToString();
    }

    private static bool ContainsPopulatedAuthorization(string value)
    {
        const string key = "Authorization";
        foreach (var physicalLine in PhysicalLines(value))
        {
            var line = NormalizeEscapedQuotes(physicalLine);
            var searchFrom = 0;
            while (searchFrom < line.Length)
            {
                var keyIndex = line.IndexOf(key, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (keyIndex < 0)
                {
                    break;
                }

                searchFrom = keyIndex + key.Length;
                if ((keyIndex > 0 && IsKeyCharacter(line[keyIndex - 1])) ||
                    (searchFrom < line.Length && IsKeyCharacter(line[searchFrom])))
                {
                    continue;
                }

                var leadingQuote = keyIndex > 0 && IsQuote(line[keyIndex - 1]) ? line[keyIndex - 1] : '\0';
                var cursor = searchFrom;
                var quotedKey = leadingQuote != '\0' && cursor < line.Length && line[cursor] == leadingQuote;
                if (quotedKey)
                {
                    cursor++;
                }

                SkipHorizontalWhitespace(line, ref cursor);
                if (cursor >= line.Length || (line[cursor] != ':' && line[cursor] != '='))
                {
                    continue;
                }

                var valueText = line.AsSpan(cursor + 1);
                if (leadingQuote != '\0' && !quotedKey)
                {
                    var closingQuote = valueText.IndexOf(leadingQuote);
                    if (closingQuote >= 0)
                    {
                        valueText = valueText[..closingQuote];
                    }
                }

                if (HasPopulatedValue(valueText))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsCurlCredential(string value)
    {
        foreach (var line in CurlScanLines(NormalizeContinuations(value)))
        {
            foreach (var command in TokenizeCommands(NormalizeEscapedQuotes(line)))
            {
                if (ContainsCurlCredential(command, depth: 0))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsCurlCredential(IReadOnlyList<string> command, int depth)
    {
        var executable = FindCurlExecutable(command);
        if (executable >= 0)
        {
            for (var index = executable + 1; index < command.Count; index++)
            {
                if (TryReadCurlOption(command, ref index, out var kind, out var optionValue))
                {
                    if (IsCredentialBearingCurlValue(kind, optionValue))
                    {
                        return true;
                    }

                    continue;
                }

                if (IsCurlScanBoundary(command, index))
                {
                    break;
                }
            }
        }

        if (depth >= 2)
        {
            return false;
        }

        foreach (var token in command.Where(token => token.Any(char.IsWhiteSpace)))
        {
            foreach (var nested in TokenizeCommands(token))
            {
                if (ContainsCurlCredential(nested, depth + 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadCurlOption(
        IReadOnlyList<string> command,
        ref int index,
        out CurlOptionValueKind kind,
        out string value)
    {
        var token = command[index];
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            var equals = token.IndexOf('=');
            var option = equals >= 0 ? token[..equals] : token;
            if (!CurlLongOptions.TryGetValue(option, out kind))
            {
                value = string.Empty;
                return false;
            }

            value = equals >= 0
                ? token[(equals + 1)..]
                : index + 1 < command.Count ? command[++index] : string.Empty;
            return true;
        }

        if (token.Length >= 2 && token[0] == '-' && token[1] != '-')
        {
            for (var cursor = 1; cursor < token.Length; cursor++)
            {
                if (!CurlShortOptions.TryGetValue(token[cursor], out kind))
                {
                    continue;
                }

                value = cursor + 1 < token.Length
                    ? token[(cursor + 1)..]
                    : index + 1 < command.Count ? command[++index] : string.Empty;
                return true;
            }
        }

        kind = default;
        value = string.Empty;
        return false;
    }

    private static bool IsCredentialBearingCurlValue(CurlOptionValueKind kind, string value) =>
        kind switch
        {
            CurlOptionValueKind.Secret => !string.IsNullOrWhiteSpace(value),
            CurlOptionValueKind.UserInfo => HasUserInfo(value, requireAtSign: false),
            CurlOptionValueKind.ProxyUserInfo => HasUserInfo(value, requireAtSign: true),
            CurlOptionValueKind.Certificate => HasCertificatePassphrase(value),
            _ => false
        };

    private static bool HasCertificatePassphrase(string value)
    {
        var separator = value.LastIndexOf(':');
        return separator > 0 && separator + 1 < value.Length;
    }

    private static int FindCurlExecutable(IReadOnlyList<string> command)
    {
        for (var index = 0; index < command.Count; index++)
        {
            if (IsCurlExecutable(command[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsCurlExecutable(string token)
    {
        var executable = token.Trim('[', ']', '{', '}', ',', ':', '\'', '"', '`', '^');
        if (string.Equals(executable, "curl", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(executable, "curl.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(executable, "\\curl", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(executable, "\\curl.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = executable.Replace('\\', '/');
        if (!normalized.StartsWith("./", StringComparison.Ordinal) &&
            !normalized.StartsWith("../", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 &&
               segments[..^1].All(segment => segment is "." or "..") &&
               (string.Equals(segments[^1], "curl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segments[^1], "curl.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCurlScanBoundary(IReadOnlyList<string> command, int index)
    {
        if (!command[index].Equals("and", StringComparison.OrdinalIgnoreCase) &&
            !command[index].Equals("but", StringComparison.OrdinalIgnoreCase) &&
            !command[index].Equals("then", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return index + 1 < command.Count &&
               (command[index + 1].Equals("docker", StringComparison.OrdinalIgnoreCase) ||
                command[index + 1].Equals("podman", StringComparison.OrdinalIgnoreCase) ||
                command[index + 1].Equals("kubectl", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasUserInfo(string value, bool requireAtSign)
    {
        var userInfoEnd = value.IndexOf('@');
        if (requireAtSign && userInfoEnd < 0)
        {
            return false;
        }

        if (userInfoEnd < 0)
        {
            userInfoEnd = value.Length;
        }

        var colon = value.AsSpan(0, userInfoEnd).IndexOf(':');
        return colon >= 0 && (colon > 0 || colon + 1 < userInfoEnd);
    }

    private static string NormalizeContinuations(string value)
    {
        var normalized = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if ((current == '\\' || current == '`' || current == '^') && index + 1 < value.Length)
            {
                var newline = index + 1;
                if (value[newline] == '\r' && newline + 1 < value.Length && value[newline + 1] == '\n')
                {
                    normalized.Append(' ');
                    index = newline + 1;
                    continue;
                }

                if (value[newline] == '\n')
                {
                    normalized.Append(' ');
                    index = newline;
                    continue;
                }
            }

            normalized.Append(current);
        }

        return normalized.ToString();
    }

    private sealed record ExecFragment(string Key, string Text, int StartLine, int EndLine, int Indent);

    private static IEnumerable<string> CurlScanLines(string value)
    {
        var lines = PhysicalLines(value).ToArray();
        var results = new List<string>(lines);
        var fragments = new List<ExecFragment>();
        const int maximumSequenceLines = 32;
        const int maximumSequenceCharacters = 8192;

        for (var start = 0; start < lines.Length; start++)
        {
            if (!TryReadExecKey(lines[start], out var key, out var indent))
            {
                continue;
            }

            var logical = new StringBuilder(lines[start]);
            var end = start;
            var bracketDepth = Count(lines[start], '[') - Count(lines[start], ']');
            if (bracketDepth > 0)
            {
                for (var index = start + 1;
                     bracketDepth > 0 && index < lines.Length && index - start < maximumSequenceLines;
                     index++)
                {
                    if (logical.Length + lines[index].Length + 1 > maximumSequenceCharacters)
                    {
                        break;
                    }

                    logical.Append(' ').Append(lines[index]);
                    bracketDepth += Count(lines[index], '[') - Count(lines[index], ']');
                    end = index;
                }

                if (bracketDepth != 0)
                {
                    continue;
                }
            }
            else
            {
                for (var index = start + 1;
                     index < lines.Length && index - start < maximumSequenceLines;
                     index++)
                {
                    var itemIndent = CountLeadingWhitespace(lines[index]);
                    var item = lines[index].TrimStart();
                    if (itemIndent < indent || !item.StartsWith("- ", StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (logical.Length + lines[index].Length + 1 > maximumSequenceCharacters)
                    {
                        break;
                    }

                    logical.Append(' ').Append(item[2..]);
                    end = index;
                }
            }

            var fragment = new ExecFragment(key, logical.ToString(), start, end, indent);
            fragments.Add(fragment);
            results.Add(fragment.Text);
            start = end;
        }

        for (var index = 0; index + 1 < fragments.Count; index++)
        {
            var first = fragments[index];
            var second = fragments[index + 1];
            if (AreJoinableExecFragments(first, second) &&
                OnlyBlankLinesBetween(lines, first.EndLine, second.StartLine))
            {
                results.Add(first.Text + " " + second.Text);
            }
        }

        return results;
    }

    private static bool TryReadExecKey(string line, out string key, out int indent)
    {
        indent = CountLeadingWhitespace(line);
        var trimmed = line.Trim();
        foreach (var instruction in new[] { "HEALTHCHECK", "ENTRYPOINT", "CMD", "RUN" })
        {
            if (trimmed.Equals(instruction, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(instruction + " ", StringComparison.OrdinalIgnoreCase))
            {
                key = instruction.ToLowerInvariant();
                return true;
            }
        }

        var separator = trimmed.IndexOf(':');
        if (separator < 0)
        {
            key = string.Empty;
            return false;
        }

        key = trimmed[..separator].Trim(' ', '\t', '{', ',', '\'', '"').ToLowerInvariant();
        return key is "command" or "args" or "entrypoint" or "cmd" or "run" or "test" or "healthcheck.test";
    }

    private static bool AreJoinableExecFragments(ExecFragment first, ExecFragment second) =>
        first.Indent == second.Indent &&
        (first.Key == "command" && second.Key == "args" ||
         first.Key == "entrypoint" && second.Key == "cmd");

    private static bool OnlyBlankLinesBetween(string[] lines, int firstEnd, int secondStart)
    {
        for (var index = firstEnd + 1; index < secondStart; index++)
        {
            if (!string.IsNullOrWhiteSpace(lines[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountLeadingWhitespace(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] is ' ' or '\t')
        {
            count++;
        }

        return count;
    }

    private static int Count(string value, char character) => value.Count(candidate => candidate == character);

    private static IEnumerable<IReadOnlyList<string>> TokenizeCommands(string line)
    {
        var command = new List<string>();
        var token = new StringBuilder();
        var quote = '\0';
        var tokenStarted = false;

        void CompleteToken()
        {
            if (!tokenStarted)
            {
                return;
            }

            command.Add(token.ToString());
            token.Clear();
            tokenStarted = false;
        }

        IReadOnlyList<string>? CompleteCommand()
        {
            CompleteToken();
            if (command.Count == 0)
            {
                return null;
            }

            var completed = command.ToArray();
            command.Clear();
            return completed;
        }

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == quote && !IsEscaped(line, index))
                {
                    quote = '\0';
                }
                else
                {
                    token.Append(character);
                }

                continue;
            }

            if (IsQuoteOpening(line, index))
            {
                quote = character;
                tokenStarted = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                CompleteToken();
            }
            else if (IsCommandSeparator(character) || character == '`')
            {
                var completed = CompleteCommand();
                if (completed is not null)
                {
                    yield return completed;
                }
            }
            else if (character is ',' or '[' or ']' or '{' or '}')
            {
                CompleteToken();
            }
            else
            {
                token.Append(character);
                tokenStarted = true;
            }
        }

        var final = CompleteCommand();
        if (final is not null)
        {
            yield return final;
        }
    }

    private static bool IsCommandSeparator(char value) => value is ';' or '&' or '|' or '(' or ')';

    private static bool IsQuoteOpening(string value, int index) =>
        IsQuote(value[index]) &&
        !IsEscaped(value, index) &&
        (index == 0 || !char.IsLetterOrDigit(value[index - 1])) &&
        FindUnescapedQuote(value, index + 1, value[index]) >= 0;

    private static string NormalizeEscapedQuotes(string value)
    {
        var normalized = new StringBuilder(value.Length);
        var collapseDoubledQuotes = DoubledQuotedKey().IsMatch(value);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is '\\' or '`' or '^' &&
                index + 1 < value.Length &&
                IsQuote(value[index + 1]))
            {
                normalized.Append(value[++index]);
                continue;
            }

            if (collapseDoubledQuotes &&
                IsQuote(value[index]) &&
                index + 1 < value.Length &&
                value[index + 1] == value[index])
            {
                normalized.Append(value[index]);
                index++;
                continue;
            }

            normalized.Append(value[index]);
        }

        return normalized.ToString();
    }

    private static int FindUnescapedQuote(string value, int start, char quote)
    {
        for (var index = start; index < value.Length; index++)
        {
            if (value[index] == quote && !IsEscaped(value, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsEscaped(string value, int index)
    {
        var escapeCount = 0;
        for (var cursor = index - 1; cursor >= 0 && value[cursor] == '\\'; cursor--)
        {
            escapeCount++;
        }

        return escapeCount % 2 != 0 ||
               index > 0 && value[index - 1] is '`' or '^';
    }

    private static IEnumerable<string> PhysicalLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static bool HasPopulatedValue(ReadOnlySpan<char> value)
    {
        while (!value.IsEmpty && value[0] is ' ' or '\t')
        {
            value = value[1..];
        }

        if (value.IsEmpty || value[0] is '}' or ']' or ',' or ';')
        {
            return false;
        }

        if (IsQuote(value[0]))
        {
            var quote = value[0];
            value = value[1..];
            var closingQuote = value.IndexOf(quote);
            if (closingQuote >= 0)
            {
                value = value[..closingQuote];
            }
        }

        return !value.Trim().IsEmpty;
    }

    private static void SkipHorizontalWhitespace(string value, ref int cursor)
    {
        while (cursor < value.Length && value[cursor] is ' ' or '\t')
        {
            cursor++;
        }
    }

    private static bool IsQuote(char value) => value is '\'' or '"';

    private static bool IsKeyCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '-' or '.';

    public static bool IsAbsolutePath(string value) =>
        value.StartsWith('/') ||
        value.StartsWith('\\') ||
        WindowsDrivePath().IsMatch(value);

    public static bool IsSingleBackslashCurlCommand(string value)
    {
        var match = SingleBackslashCurl().Match(value);
        return match.Success && match.Index == 0;
    }

    [GeneratedRegex(
        """(?:(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/\s]+[\\/][^\\/\s]+|~[\\/](?:[^\\/\s]+[\\/])*[^\\/\s]+)|(?<![A-Za-z0-9.])/(?!/)(?:[^/\s`|\[\]{}<>"']+/)+[^/\s`|\[\]{}<>"']+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostPath();

    [GeneratedRegex(
        """(?:(?:^|[^A-Za-z0-9])-[A-Za-z]+|(?:^|[^A-Za-z0-9.])/[A-Za-z]+)(?:[=:,])?(?:[A-Za-z]:[\\/][^\s`|\[\]{}<>"']+|/(?![-/])(?:[^/\s`|\[\]{}<>"']+/)*[^/\s`|\[\]{}<>"']+|[\\/]{2}[^\\/\s`|\[\]{}<>"']+[\\/][^\\/\s`|\[\]{}<>"']+|\\(?![\\/.])(?:[^\\/\s`|\[\]{}<>"']+\\)+[^\\/\s`|\[\]{}<>"']+|(?:~[A-Za-z0-9._-]*|\$HOME|\$\{HOME\}|\$\{?env:(?:USERPROFILE|HOME|HOMEPATH|APPDATA|LOCALAPPDATA)\}?|%(?:USERPROFILE|HOMEPATH|APPDATA|LOCALAPPDATA)%)[\\/][^\s`|\[\]{}<>"']+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OptionAttachedHostPath();

    [GeneratedRegex(
        """(?<![A-Za-z0-9])(?:~[A-Za-z0-9._-]*|\$HOME|\$\{HOME\}|\$\{?env:(?:USERPROFILE|HOME|HOMEPATH|APPDATA|LOCALAPPDATA)\}?|%(?:USERPROFILE|HOMEPATH|APPDATA|LOCALAPPDATA)%)[\\/][^\s`|\[\]{}<>"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HomeAliasPath();

    [GeneratedRegex(
        """(?<![A-Za-z0-9.\\])\\(?![\\/.])(?:[^\\/\s`|\[\]{}<>"']+\\)+[^\\/\s`|\[\]{}<>"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleBackslashRoot();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9.\\])\\curl(?:\.exe)?(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleBackslashCurl();

    [GeneratedRegex(
        """^(?:[\"']?(?:command|args|entrypoint)[\"']?\s*:|(?:HEALTHCHECK\s+)?(?:CMD|ENTRYPOINT)\b)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecSequenceKey();

    [GeneratedRegex(
        """(?:\"\"[A-Za-z][A-Za-z0-9_.-]*\"\"|''[A-Za-z][A-Za-z0-9_.-]*'')\s*[:=]""",
        RegexOptions.CultureInvariant)]
    private static partial Regex DoubledQuotedKey();

    [GeneratedRegex(
        """[a-z][a-z0-9+.-]*:(?://|\\\\)[^\s`|\[\]{}<>"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemeUrl();

    [GeneratedRegex(
        """(?:-----BEGIN\s|(?<![-A-Za-z0-9])Bearer\s+\S+|--password(?:=|\s+)\S+|(?:^|[^A-Za-z0-9])(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObviousCredential();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDrivePath();
}
