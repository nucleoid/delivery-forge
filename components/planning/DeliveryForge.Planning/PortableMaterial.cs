using System.Text.RegularExpressions;
using System.Text;

namespace DeliveryForge.Planning;

internal static partial class PortableMaterial
{
    private static readonly string[] CredentialKeys =
    [
        "api_key", "api-key", "password", "passwd", "token", "secret",
        "client_secret", "client-secret", "access_key", "access-key",
        "aws_access_key_id", "aws-access-key-id"
    ];

    private static readonly HashSet<string> CurlUserOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--user", "--proxy-user"
    };

    private static readonly HashSet<string> CurlProxyOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--proxy", "--proxy1.0", "--preproxy", "--socks4", "--socks4a",
        "--socks5", "--socks5-hostname"
    };

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
        foreach (var line in PhysicalLines(value))
        {
            foreach (var key in CredentialKeys)
            {
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

                    var quote = keyIndex > 0 && IsQuote(line[keyIndex - 1]) ? line[keyIndex - 1] : '\0';
                    var cursor = searchFrom;
                    if (quote != '\0')
                    {
                        if (cursor >= line.Length || line[cursor] != quote)
                        {
                            continue;
                        }

                        cursor++;
                    }

                    SkipHorizontalWhitespace(line, ref cursor);
                    if (cursor < line.Length && (line[cursor] == ':' || line[cursor] == '=') &&
                        HasPopulatedValue(line.AsSpan(cursor + 1)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ContainsPopulatedAuthorization(string value)
    {
        const string key = "Authorization";
        foreach (var line in PhysicalLines(value))
        {
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
        foreach (var line in PhysicalLines(NormalizeContinuations(value)))
        {
            foreach (var command in TokenizeCommands(line))
            {
                if (command.Count == 0 || !string.Equals(command[0], "curl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                for (var index = 1; index < command.Count; index++)
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
                }
            }
        }

        return false;
    }

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

        foreach (var character in line)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    token.Append(character);
                }

                continue;
            }

            if (IsQuote(character))
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                CompleteToken();
            }
            else if (character is ';' or '&' or '|')
            {
                CompleteToken();
                if (command.Count > 0)
                {
                    yield return command.ToArray();
                    command.Clear();
                }
            }
            else
            {
                token.Append(character);
            }
        }

        CompleteToken();
        if (command.Count > 0)
        {
            yield return command;
        }
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
