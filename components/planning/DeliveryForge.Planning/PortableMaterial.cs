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
        while (!value.IsEmpty && value[0] is ' ' or '\t')
        {
            value = value[1..];
        }

        if (!value.IsEmpty && IsQuote(value[0]) &&
            line.AsSpan(0, separator).Count(value[0]) % 2 != 0)
        {
            return false;
        }

        return HasPopulatedValue(value);
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

        if (!IsEnvironmentStyleKey(key))
        {
            return false;
        }

        var words = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 &&
               (words[^1] is "password" or "passwd" or "token" or "secret" or "authorization" ||
                words.Length > 2 && words[^2] is ("access" or "api") && words[^1] == "key");
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
                if (char.IsUpper(character) && index > 0 && char.IsLower(key[index - 1]) && normalized[^1] != '_')
                {
                    normalized.Append('_');
                }

                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        return normalized.ToString();
    }

    private static bool IsEnvironmentStyleKey(string key) =>
        key.All(character => char.IsUpper(character) || char.IsDigit(character) || character is '_' or '-');

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
                var executable = FindCurlExecutable(command);
                if (executable < 0)
                {
                    continue;
                }

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
                }
            }
        }

        return false;
    }

    private static int FindCurlExecutable(IReadOnlyList<string> command)
    {
        for (var index = 0; index < command.Count; index++)
        {
            var token = command[index];
            var executable = token.Trim('(', ')', '$', '`');
            if (!string.Equals(executable, "curl", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(executable, "curl.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index == 0 || token[0] is '(' or '$' or '`' || IsSupportedCommandPrefix(command, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSupportedCommandPrefix(IReadOnlyList<string> command, int executable)
    {
        if (executable == 1 && (command[0] == "$" || command[0].EndsWith('>')))
        {
            return true;
        }

        var wrapper = command[0];
        if (!string.Equals(wrapper, "env", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(wrapper, "sudo", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(wrapper, "command", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(wrapper, "nohup", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var index = 1; index < executable; index++)
        {
            if (!command[index].StartsWith('-') && !command[index].Contains('='))
            {
                return false;
            }
        }

        return true;
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
