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

    private static readonly HashSet<string> CurlUserOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--user", "--proxy-user"
    };

    private static readonly HashSet<string> CurlProxyOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--proxy", "--proxy1.0", "--preproxy", "--socks4", "--socks4a",
        "--socks5", "--socks5-hostname"
    };

    private static readonly HashSet<string> CurlLongOptionsWithValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "--abstract-unix-socket", "--alt-svc", "--aws-sigv4", "--cacert", "--capath",
        "--cert", "--cert-type", "--ciphers", "--connect-timeout", "--connect-to", "--cookie",
        "--cookie-jar", "--create-file-mode", "--data", "--data-ascii", "--data-binary",
        "--data-raw", "--data-urlencode", "--delegation", "--dns-interface", "--dns-ipv4-addr",
        "--dns-ipv6-addr", "--dns-servers", "--doh-url", "--dump-header", "--egd-file",
        "--engine", "--etag-compare", "--etag-save", "--expect100-timeout", "--form",
        "--form-string", "--ftp-account", "--ftp-alternative-to-user", "--ftp-method",
        "--ftp-port", "--ftp-ssl-ccc-mode", "--happy-eyeballs-timeout-ms", "--header",
        "--hostpubmd5", "--hsts", "--interface", "--key", "--key-type", "--krb",
        "--libcurl", "--limit-rate", "--local-port", "--login-options", "--mail-auth",
        "--mail-from", "--mail-rcpt", "--max-filesize", "--max-redirs", "--max-time",
        "--noproxy", "--oauth2-bearer", "--output", "--pass", "--pinnedpubkey", "--proto",
        "--proto-default", "--proto-redir", "--pubkey", "--quote", "--range", "--referer",
        "--request", "--resolve", "--retry", "--retry-delay", "--retry-max-time",
        "--sasl-authzid", "--service-name", "--speed-limit", "--speed-time", "--tls-max",
        "--tls13-ciphers", "--unix-socket", "--upload-file", "--url", "--user-agent",
        "--write-out"
    };

    private static readonly HashSet<char> CurlShortOptionsWithValues =
        ['A', 'b', 'c', 'd', 'D', 'e', 'E', 'F', 'H', 'K', 'm', 'o', 'P', 'Q', 'r', 'R', 'T', 'w', 'X', 'y', 'Y'];

    private static readonly HashSet<string> NetworkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "ssh", "git", "ftp", "ftps"
    };

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
            if (character is '-' or '_')
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
            foreach (var command in TokenizeCommands(line))
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
                var token = command[index];
                if (TryReadLongOption(command, ref index, token, CurlUserOptions, out var userValue) &&
                    HasUserInfo(userValue, requireAtSign: false))
                {
                    return true;
                }

                if (TryReadLongOption(command, ref index, token, CurlProxyOptions, out var proxyValue) &&
                    HasUserInfo(proxyValue, requireAtSign: true))
                {
                    return true;
                }

                if (TryReadShortOption(command, ref index, token, ['u', 'U'], out userValue) &&
                    HasUserInfo(userValue, requireAtSign: false))
                {
                    return true;
                }

                if (TryReadShortOption(command, ref index, token, ['x'], out proxyValue) &&
                    HasUserInfo(proxyValue, requireAtSign: true))
                {
                    return true;
                }

                if (TrySkipCurlOptionValue(command, ref index, token))
                {
                    continue;
                }

                if (IsCurlScanBoundary(token))
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

    private static bool TrySkipCurlOptionValue(IReadOnlyList<string> command, ref int index, string token)
    {
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            var equals = token.IndexOf('=');
            var option = equals >= 0 ? token[..equals] : token;
            if (!CurlLongOptionsWithValues.Contains(option))
            {
                return false;
            }

            if (equals < 0 && index + 1 < command.Count)
            {
                index++;
            }

            return true;
        }

        if (token.Length < 2 || token[0] != '-' || token[1] == '-')
        {
            return false;
        }

        for (var cursor = 1; cursor < token.Length; cursor++)
        {
            if (!CurlShortOptionsWithValues.Contains(token[cursor]))
            {
                continue;
            }

            if (cursor == token.Length - 1 && index + 1 < command.Count)
            {
                index++;
            }

            return true;
        }

        return false;
    }

    private static int FindCurlExecutable(IReadOnlyList<string> command)
    {
        for (var index = 0; index < command.Count; index++)
        {
            if (!IsCurlExecutable(command[index]))
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static bool IsCurlExecutable(string token)
    {
        var executable = token.Trim('[', ']', '{', '}', ',', ':');
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

    private static bool IsCurlScanBoundary(string token) =>
        token.Equals("and", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("but", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("then", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("docker", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("podman", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("kubectl", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadLongOption(
        IReadOnlyList<string> command,
        ref int index,
        string token,
        IReadOnlySet<string> options,
        out string value)
    {
        var equals = token.IndexOf('=');
        var option = equals >= 0 ? token[..equals] : token;
        if (!options.Contains(option))
        {
            value = string.Empty;
            return false;
        }

        if (equals >= 0)
        {
            value = token[(equals + 1)..];
            return true;
        }

        value = index + 1 < command.Count ? command[++index] : string.Empty;
        return true;
    }

    private static bool TryReadShortOption(
        IReadOnlyList<string> command,
        ref int index,
        string token,
        ReadOnlySpan<char> options,
        out string value)
    {
        if (token.Length < 2 || token[0] != '-' || token[1] == '-')
        {
            value = string.Empty;
            return false;
        }

        for (var cursor = 1; cursor < token.Length; cursor++)
        {
            if (!options.Contains(token[cursor]))
            {
                continue;
            }

            value = cursor + 1 < token.Length ? token[(cursor + 1)..] :
                index + 1 < command.Count ? command[++index] : string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
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

    private static IEnumerable<string> CurlScanLines(string value)
    {
        var lines = PhysicalLines(value).ToArray();
        foreach (var line in lines)
        {
            yield return line;
        }

        const int maximumSequenceLines = 32;
        const int maximumSequenceCharacters = 8192;
        for (var start = 0; start < lines.Length; start++)
        {
            var trimmed = lines[start].Trim();
            if (IsExecArrayStart(trimmed))
            {
                var logical = new StringBuilder(lines[start]);
                var bracketDepth = Count(trimmed, '[') - Count(trimmed, ']');
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
                    if (bracketDepth == 0)
                    {
                        yield return logical.ToString();
                        start = index;
                    }
                }

                continue;
            }

            if (!IsExecSequenceKey(trimmed))
            {
                continue;
            }

            var sequence = new StringBuilder(lines[start]);
            var last = start;
            for (var index = start + 1;
                 index < lines.Length && index - start < maximumSequenceLines;
                 index++)
            {
                var item = lines[index].TrimStart();
                if (!item.StartsWith("- ", StringComparison.Ordinal))
                {
                    break;
                }

                if (sequence.Length + lines[index].Length + 1 > maximumSequenceCharacters)
                {
                    break;
                }

                sequence.Append(' ').Append(item[2..]);
                last = index;
            }

            if (last > start)
            {
                yield return sequence.ToString();
                start = last;
            }
        }
    }

    private static bool IsExecArrayStart(string line) =>
        line.Contains('[', StringComparison.Ordinal) &&
        (line.StartsWith("HEALTHCHECK", StringComparison.OrdinalIgnoreCase) ||
         line.StartsWith("CMD", StringComparison.OrdinalIgnoreCase) ||
         line.StartsWith("ENTRYPOINT", StringComparison.OrdinalIgnoreCase) ||
         ExecSequenceKey().IsMatch(line));

    private static bool IsExecSequenceKey(string line) => ExecSequenceKey().IsMatch(line);

    private static int Count(string value, char character) => value.Count(candidate => candidate == character);

    private static IEnumerable<IReadOnlyList<string>> TokenizeCommands(string line)
    {
        var command = new List<string>();
        var token = new StringBuilder();
        var quote = '\0';

        void CompleteToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            command.Add(token.ToString());
            token.Clear();
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

    private static bool IsKeyCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '-';

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
        """(?:\"\"[A-Za-z][A-Za-z0-9_-]*\"\"|''[A-Za-z][A-Za-z0-9_-]*'')\s*[:=]""",
        RegexOptions.CultureInvariant)]
    private static partial Regex DoubledQuotedKey();

    [GeneratedRegex(
        """[a-z][a-z0-9+.-]*:(?://|\\\\)[^\s`|\[\]{}<>"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemeUrl();

    [GeneratedRegex(
        """(?:-----BEGIN\s|Bearer\s+\S+|--password(?:=|\s+)\S+|(?:^|[^A-Za-z0-9])(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObviousCredential();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDrivePath();
}
