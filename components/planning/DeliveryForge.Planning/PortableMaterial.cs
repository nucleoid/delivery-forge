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
        Header,
        UserInfo,
        ProxyUserInfo,
        Certificate,
        Url
    }

    private enum CurlOptionArity
    {
        NoValue,
        OptionalValue,
        RequiredValue
    }

    private readonly record struct CurlOptionSpec(CurlOptionArity Arity, CurlOptionValueKind Kind);

    // Curl 8.5.0's complete option catalogue is represented explicitly by arity. Required
    // values consume their following argument even when it starts with '-', --help is the sole
    // optional-value form, and no-value flags remain visible inside short groups.
    private static readonly IReadOnlyDictionary<string, CurlOptionSpec> CurlLongOptions = BuildCurlLongOptions();

    private static readonly IReadOnlyDictionary<char, CurlOptionSpec> CurlShortOptions =
        BuildCurlShortOptions();

    private static readonly HashSet<string> NetworkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "ssh", "git", "ftp", "ftps"
    };

    private static IReadOnlyDictionary<string, CurlOptionSpec> BuildCurlLongOptions()
    {
        var options = new Dictionary<string, CurlOptionSpec>(StringComparer.OrdinalIgnoreCase);

        Add(
            CurlOptionArity.RequiredValue,
            CurlOptionValueKind.Ordinary,
            "--abstract-unix-socket --alt-svc --aws-sigv4 --cacert --capath --cert-type --ciphers " +
            "--config --connect-timeout --connect-to --continue-at --cookie-jar --create-file-mode --crlfile " +
            "--curves --data --data-ascii --data-binary --data-raw --data-urlencode --delegation " +
            "--dns-interface --dns-ipv4-addr --dns-ipv6-addr --dns-servers --doh-url --dump-header " +
            "--egd-file --engine --etag-compare --etag-save --expect100-timeout --form --form-string " +
            "--ftp-account --ftp-alternative-to-user --ftp-method --ftp-port --ftp-ssl-ccc-mode " +
            "--happy-eyeballs-timeout-ms --haproxy-clientip --header --hostpubmd5 --hostpubsha256 --hsts --interface " +
            "--ipfs-gateway --json --keepalive-time --key --key-type --krb --libcurl --limit-rate " +
            "--local-port --login-options --mail-auth --mail-from --mail-rcpt --max-filesize --max-redirs " +
            "--max-time --netrc-file --noproxy --output --output-dir --parallel-max --pinnedpubkey --proto " +
            "--proto-default --proto-redir --proxy-cacert --proxy-capath --proxy-cert-type --proxy-ciphers " +
            "--proxy-crlfile --proxy-header --proxy-key --proxy-key-type --proxy-pinnedpubkey " +
            "--proxy-service-name --proxy-tls13-ciphers --proxy-tlsauthtype --proxy-tlsuser --pubkey " +
            "--quote --random-file --range --rate --referer --request --request-target --resolve --retry " +
            "--retry-delay --retry-max-time --sasl-authzid --service-name --socks5-gssapi-service " +
            "--speed-limit --speed-time --stderr --telnet-option --tftp-blksize --time-cond --tls-max " +
            "--tls13-ciphers --tlsauthtype --tlsuser --trace --trace-ascii --trace-config --unix-socket " +
            "--upload-file --url-query --user-agent --variable --write-out");

        Add(
            CurlOptionArity.NoValue,
            CurlOptionValueKind.Ordinary,
            "--anyauth --append --basic --ca-native --cert-status --compressed --compressed-ssh " +
            "--create-dirs --crlf --digest --disable --disable-eprt --disable-epsv " +
            "--disallow-username-in-url --doh-cert-status --doh-insecure --fail --fail-early " +
            "--fail-with-body --false-start --form-escape --ftp-create-dirs --ftp-pasv --ftp-pret " +
            "--ftp-skip-pasv-ip --ftp-ssl-ccc --ftp-ssl-control --get --globoff " +
            "--haproxy-protocol --head --http0.9 --http1.0 --http1.1 --http2 --http2-prior-knowledge " +
            "--http3 --http3-only --ignore-content-length --include --insecure --ipv4 --ipv6 " +
            "--junk-session-cookies --list-only --location --location-trusted --mail-rcpt-allowfails " +
            "--manual --metalink --negotiate --netrc --netrc-optional --next --no-alpn --no-buffer " +
            "--no-clobber --no-keepalive --no-npn --no-progress-meter --no-sessionid --ntlm --ntlm-wb " +
            "--parallel --parallel-immediate --path-as-is --post301 --post302 --post303 --progress-bar " +
            "--proxy-anyauth --proxy-basic --proxy-ca-native --proxy-digest --proxy-http2 " +
            "--proxy-insecure --proxy-negotiate --proxy-ntlm --proxy-ssl-allow-beast " +
            "--proxy-ssl-auto-client-cert --proxy-tlsv1 --proxytunnel --raw --remote-header-name " +
            "--remote-name --remote-name-all --remote-time --remove-on-error --retry-all-errors " +
            "--retry-connrefused --sasl-ir --show-error --silent --socks5-basic --socks5-gssapi " +
            "--socks5-gssapi-nec --ssl --ssl-allow-beast --ssl-auto-client-cert --ssl-no-revoke " +
            "--ssl-reqd --ssl-revoke-best-effort --sslv2 --sslv3 --styled-output " +
            "--suppress-connect-headers --tcp-fastopen --tcp-nodelay --tftp-no-options --tlsv1 " +
            "--tlsv1.0 --tlsv1.1 --tlsv1.2 --tlsv1.3 --tr-encoding --trace-ids --trace-time " +
            "--use-ascii --verbose --version --xattr");

        Add(CurlOptionArity.OptionalValue, CurlOptionValueKind.Ordinary, "--help");
        Add(
            CurlOptionArity.RequiredValue,
            CurlOptionValueKind.Secret,
            "--oauth2-bearer --pass --proxy-pass --tlspassword --proxy-tlspassword --cookie");
        Add(CurlOptionArity.RequiredValue, CurlOptionValueKind.UserInfo, "--user --proxy-user");
        Add(
            CurlOptionArity.RequiredValue,
            CurlOptionValueKind.ProxyUserInfo,
            "--proxy --proxy1.0 --preproxy --socks4 --socks4a --socks5 --socks5-hostname");
        Add(CurlOptionArity.RequiredValue, CurlOptionValueKind.Certificate, "--cert --proxy-cert");
        Add(CurlOptionArity.RequiredValue, CurlOptionValueKind.Url, "--url");
        options["--header"] = new CurlOptionSpec(CurlOptionArity.RequiredValue, CurlOptionValueKind.Header);
        options["--proxy-header"] = new CurlOptionSpec(CurlOptionArity.RequiredValue, CurlOptionValueKind.Header);
        return options;

        void Add(CurlOptionArity arity, CurlOptionValueKind kind, string names)
        {
            foreach (var name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                options.Add(name, new CurlOptionSpec(arity, kind));
            }
        }
    }

    private static IReadOnlyDictionary<char, CurlOptionSpec> BuildCurlShortOptions()
    {
        var options = new Dictionary<char, CurlOptionSpec>();
        Add(CurlOptionArity.NoValue, CurlOptionValueKind.Ordinary, "#012346:BGIJLMNORSVZafgijklnpqsv");
        Add(CurlOptionArity.OptionalValue, CurlOptionValueKind.Ordinary, "h");
        Add(CurlOptionArity.RequiredValue, CurlOptionValueKind.Ordinary, "ACDFHKPQTXYcdemortwyz");
        Add(CurlOptionArity.RequiredValue, CurlOptionValueKind.Secret, "b");
        Add(CurlOptionArity.RequiredValue, CurlOptionValueKind.UserInfo, "uU");
        Add(CurlOptionArity.RequiredValue, CurlOptionValueKind.ProxyUserInfo, "x");
        Add(CurlOptionArity.RequiredValue, CurlOptionValueKind.Certificate, "E");
        options['H'] = new CurlOptionSpec(CurlOptionArity.RequiredValue, CurlOptionValueKind.Header);
        return options;

        void Add(CurlOptionArity arity, CurlOptionValueKind kind, string names)
        {
            foreach (var name in names)
            {
                options.Add(name, new CurlOptionSpec(arity, kind));
            }
        }
    }

    internal static IReadOnlyList<string> CurlOptionArityAudit() =>
        CurlLongOptions
            .Select(option => $"long {option.Key} {option.Value.Arity}")
            .Concat(CurlShortOptions.Select(option => $"short -{option.Key} {option.Value.Arity}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

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
        var scan = CurlScanLines(NormalizeContinuations(value));
        if (scan.AbandonedExecStructure)
        {
            return true;
        }

        foreach (var line in scan.Lines)
        {
            foreach (var command in TokenizeCommands(line))
            {
                if (ContainsCurlCredential(command, depth: 0))
                {
                    return true;
                }
            }

            var normalized = NormalizeEscapedQuotes(line);
            if (!normalized.Equals(line, StringComparison.Ordinal))
            {
                foreach (var command in TokenizeCommands(normalized))
                {
                    if (ContainsCurlCredential(command, depth: 0))
                    {
                        return true;
                    }
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
                    if (IsCredentialBearingCurlValue(kind, optionValue) ||
                        HasSchemelessUrlUserInfo(optionValue))
                    {
                        return true;
                    }

                    continue;
                }

                if (IsCurlScanBoundary(command, index))
                {
                    break;
                }

                if (!command[index].StartsWith("-", StringComparison.Ordinal) &&
                    HasSchemelessUrlUserInfo(command[index]))
                {
                    return true;
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
            var noVariant = false;
            if (!TryResolveCurlLongOption(option, out var spec, out var ambiguous))
            {
                if (option.StartsWith("--expand-", StringComparison.OrdinalIgnoreCase))
                {
                    option = "--" + option["--expand-".Length..];
                }
                else if (option.StartsWith("--no-", StringComparison.OrdinalIgnoreCase))
                {
                    option = "--" + option["--no-".Length..];
                    noVariant = true;
                }

                if (!TryResolveCurlLongOption(option, out spec, out ambiguous))
                {
                    kind = CurlOptionValueKind.Secret;
                    value = ambiguous
                        ? token
                        : ReadUnknownCurlOptionValue(command, ref index, token, equals);
                    return true;
                }
            }

            kind = spec.Kind;
            var arity = noVariant && spec.Arity != CurlOptionArity.NoValue
                ? CurlOptionArity.OptionalValue
                : spec.Arity;
            value = ReadCurlOptionValue(command, ref index, token, equals, arity);
            return true;
        }

        if (token.Length >= 2 && token[0] == '-' && token[1] != '-')
        {
            for (var cursor = 1; cursor < token.Length; cursor++)
            {
                if (!CurlShortOptions.TryGetValue(token[cursor], out var spec))
                {
                    continue;
                }

                if (spec.Arity == CurlOptionArity.NoValue)
                {
                    continue;
                }

                kind = spec.Kind;
                value = cursor + 1 < token.Length
                    ? token[(cursor + 1)..]
                    : ReadSeparateCurlOptionValue(command, ref index, spec.Arity);
                return true;
            }

            kind = CurlOptionValueKind.Ordinary;
            value = string.Empty;
            return true;
        }

        kind = default;
        value = string.Empty;
        return false;
    }

    private static bool TryResolveCurlLongOption(
        string option,
        out CurlOptionSpec spec,
        out bool ambiguous)
    {
        if (CurlLongOptions.TryGetValue(option, out spec))
        {
            ambiguous = false;
            return true;
        }

        var matches = CurlLongOptions
            .Where(candidate => candidate.Key.StartsWith(option, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        ambiguous = matches.Length > 1;
        if (matches.Length == 1)
        {
            spec = matches[0].Value;
            return true;
        }

        spec = default;
        return false;
    }

    private static string ReadUnknownCurlOptionValue(
        IReadOnlyList<string> command,
        ref int index,
        string token,
        int equals)
    {
        if (equals >= 0)
        {
            return token[(equals + 1)..];
        }

        if (index + 1 >= command.Count ||
            command[index + 1].StartsWith("-", StringComparison.Ordinal) ||
            IsCurlScanBoundary(command, index + 1))
        {
            return string.Empty;
        }

        return command[++index];
    }

    private static string ReadCurlOptionValue(
        IReadOnlyList<string> command,
        ref int index,
        string token,
        int equals,
        CurlOptionArity arity)
    {
        if (equals >= 0)
        {
            return token[(equals + 1)..];
        }

        return ReadSeparateCurlOptionValue(command, ref index, arity);
    }

    private static string ReadSeparateCurlOptionValue(
        IReadOnlyList<string> command,
        ref int index,
        CurlOptionArity arity)
    {
        if (arity == CurlOptionArity.NoValue || index + 1 >= command.Count)
        {
            return string.Empty;
        }

        if (arity == CurlOptionArity.OptionalValue &&
            command[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return command[++index];
    }

    private static bool IsCredentialBearingCurlValue(CurlOptionValueKind kind, string value) =>
        kind switch
        {
            CurlOptionValueKind.Secret => !string.IsNullOrWhiteSpace(value),
            CurlOptionValueKind.Header => HasPopulatedCookieHeader(value),
            CurlOptionValueKind.UserInfo => HasUserInfo(value, requireAtSign: false),
            CurlOptionValueKind.ProxyUserInfo => HasUserInfo(value, requireAtSign: true),
            CurlOptionValueKind.Certificate => HasCertificatePassphrase(value),
            CurlOptionValueKind.Url => HasSchemelessUrlUserInfo(value),
            _ => false
        };

    private static bool HasPopulatedCookieHeader(string value)
    {
        var separator = value.IndexOfAny(':', '=');
        if (separator < 0)
        {
            return false;
        }

        var key = value[..separator].Trim(' ', '\t', '\'', '"');
        return (key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) &&
               HasPopulatedValue(value.AsSpan(separator + 1));
    }

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

    private static bool HasSchemelessUrlUserInfo(string value)
    {
        var candidate = value.Trim(' ', '\t', '\'', '"', '(', ')', ',', ';');
        var scheme = candidate.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            candidate = candidate[(scheme + 3)..];
        }

        var atSign = candidate.IndexOf('@');
        if (atSign < 0 || atSign + 1 >= candidate.Length)
        {
            return false;
        }

        var userInfo = candidate[..atSign];
        var colon = userInfo.IndexOf(':');
        if (colon < 0 || colon == 0 && colon + 1 >= userInfo.Length ||
            IsDocumentedUserInfoPlaceholder(userInfo))
        {
            return false;
        }

        var host = candidate.AsSpan(atSign + 1);
        var terminator = host.IndexOfAny('/', '?', '#');
        if (terminator >= 0)
        {
            host = host[..terminator];
        }

        return !host.IsEmpty && !host.Contains(' ') && !host.Contains('\t');
    }

    private static bool IsDocumentedUserInfoPlaceholder(string userInfo) =>
        userInfo.Equals("<user>:<password>", StringComparison.OrdinalIgnoreCase) ||
        userInfo.Equals("${USER}:${PASSWORD}", StringComparison.OrdinalIgnoreCase) ||
        userInfo.Equals("{{user}}:{{password}}", StringComparison.OrdinalIgnoreCase);

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

    [Flags]
    private enum StructuredAbandonmentReason
    {
        None = 0,
        LineLimit = 1,
        CharacterLimit = 2,
        UnclosedDelimiter = 4
    }

    private sealed record ExecFragment(
        string Key,
        string Text,
        int StartLine,
        int EndLine,
        int Indent,
        StructuredAbandonmentReason AbandonmentReason);

    private sealed record CurlScanResult(
        IReadOnlyList<string> Lines,
        StructuredAbandonmentReason AbandonmentReasons)
    {
        public bool AbandonedExecStructure => AbandonmentReasons != StructuredAbandonmentReason.None;
    }

    private static CurlScanResult CurlScanLines(string value)
    {
        var lines = PhysicalLines(value).ToArray();
        var results = new List<string>(lines);
        var fragments = new List<ExecFragment>();
        var abandonmentReasons = StructuredAbandonmentReason.None;
        const int maximumSequenceLines = 32;
        const int maximumSequenceCharacters = 2048;

        for (var start = 0; start < lines.Length; start++)
        {
            if (!TryReadExecKey(lines[start], out var key, out var indent))
            {
                continue;
            }

            var logical = new StringBuilder(lines[start]);
            var end = start;
            var fragmentAbandonment = logical.Length > maximumSequenceCharacters
                ? StructuredAbandonmentReason.CharacterLimit
                : StructuredAbandonmentReason.None;
            var bracketDepth = 0;
            var bracketQuote = '\0';
            UpdateBracketDepth(lines[start], ref bracketDepth, ref bracketQuote);
            if (bracketDepth > 0)
            {
                for (var index = start + 1; bracketDepth > 0 && index < lines.Length; index++)
                {
                    if (index - start >= maximumSequenceLines)
                    {
                        fragmentAbandonment |= StructuredAbandonmentReason.LineLimit;
                        break;
                    }

                    if (logical.Length + lines[index].Length + 1 > maximumSequenceCharacters)
                    {
                        fragmentAbandonment |= StructuredAbandonmentReason.CharacterLimit;
                        break;
                    }

                    logical.Append(' ').Append(lines[index]);
                    UpdateBracketDepth(lines[index], ref bracketDepth, ref bracketQuote);
                    end = index;
                }

                if (bracketDepth != 0 && fragmentAbandonment == StructuredAbandonmentReason.None)
                {
                    fragmentAbandonment |= StructuredAbandonmentReason.UnclosedDelimiter;
                }
            }
            else
            {
                var execValue = ReadExecValue(lines[start], key);
                var blockScalar = IsBlockScalarIndicator(execValue);
                var plainScalar = execValue.Length > 0 &&
                                  execValue[0] != '[' &&
                                  execValue[0] != '{';
                if (blockScalar || plainScalar)
                {
                    for (var index = start + 1; index < lines.Length; index++)
                    {
                        if (index - start >= maximumSequenceLines)
                        {
                            fragmentAbandonment |= StructuredAbandonmentReason.LineLimit;
                            break;
                        }

                        var itemIndent = CountLeadingWhitespace(lines[index]);
                        var item = lines[index].TrimStart();
                        if (item.Length == 0)
                        {
                            end = index;
                            continue;
                        }

                        if (itemIndent <= indent || !blockScalar && LooksLikeYamlMappingEntry(item))
                        {
                            break;
                        }

                        if (logical.Length + lines[index].Length + 1 > maximumSequenceCharacters)
                        {
                            fragmentAbandonment |= StructuredAbandonmentReason.CharacterLimit;
                            break;
                        }

                        logical.Append(' ').Append(item);
                        end = index;
                    }
                }
                else
                {
                    var sequenceIndent = -1;
                    var sequenceBlockScalar = false;
                    var followingLineScalar = false;
                    var followingLineBlockScalar = false;
                    var followingLineFlow = false;
                    var followingLineBracketDepth = 0;
                    var followingLineBracketQuote = '\0';
                    for (var index = start + 1; index < lines.Length; index++)
                    {
                        if (index - start >= maximumSequenceLines)
                        {
                            fragmentAbandonment |= StructuredAbandonmentReason.LineLimit;
                            break;
                        }

                        var itemIndent = CountLeadingWhitespace(lines[index]);
                        var item = lines[index].TrimStart();
                        if (item.Length == 0 || item.StartsWith('#'))
                        {
                            end = index;
                            continue;
                        }

                        if (sequenceIndent < 0 && !followingLineScalar && !followingLineFlow)
                        {
                            if (itemIndent < indent ||
                                itemIndent == indent && !item.StartsWith("- ", StringComparison.Ordinal))
                            {
                                break;
                            }

                            if (item.StartsWith("- ", StringComparison.Ordinal))
                            {
                                sequenceIndent = itemIndent;
                                item = item[2..].TrimStart();
                                sequenceBlockScalar = IsBlockScalarIndicator(item);
                            }
                            else if (item[0] is '[' or '{')
                            {
                                followingLineFlow = true;
                                UpdateBracketDepth(item, ref followingLineBracketDepth, ref followingLineBracketQuote);
                            }
                            else
                            {
                                followingLineScalar = true;
                                followingLineBlockScalar = IsBlockScalarIndicator(item);
                            }
                        }
                        else if (sequenceIndent >= 0)
                        {
                            if (itemIndent < sequenceIndent)
                            {
                                break;
                            }

                            if (itemIndent == sequenceIndent)
                            {
                                if (!item.StartsWith("- ", StringComparison.Ordinal))
                                {
                                    break;
                                }

                                item = item[2..].TrimStart();
                                sequenceBlockScalar = IsBlockScalarIndicator(item);
                            }
                            else if (!sequenceBlockScalar && LooksLikeYamlMappingEntry(item))
                            {
                                break;
                            }
                        }
                        else if (followingLineFlow)
                        {
                            if (followingLineBracketDepth <= 0 || itemIndent <= indent)
                            {
                                break;
                            }

                            UpdateBracketDepth(item, ref followingLineBracketDepth, ref followingLineBracketQuote);
                        }
                        else if (itemIndent <= indent ||
                                 !followingLineBlockScalar && LooksLikeYamlMappingEntry(item))
                        {
                            break;
                        }

                        if (logical.Length + lines[index].Length + 1 > maximumSequenceCharacters)
                        {
                            fragmentAbandonment |= StructuredAbandonmentReason.CharacterLimit;
                            break;
                        }

                        logical.Append(' ').Append(item);
                        end = index;
                    }

                    if (followingLineFlow &&
                        followingLineBracketDepth != 0 &&
                        fragmentAbandonment == StructuredAbandonmentReason.None)
                    {
                        fragmentAbandonment |= StructuredAbandonmentReason.UnclosedDelimiter;
                    }
                }
            }

            var fragment = new ExecFragment(key, logical.ToString(), start, end, indent, fragmentAbandonment);
            fragments.Add(fragment);
            if (fragmentAbandonment == StructuredAbandonmentReason.None)
            {
                results.Add(fragment.Text);
            }
            else
            {
                abandonmentReasons |= fragmentAbandonment;
            }

            start = end;
        }

        for (var firstIndex = 0; firstIndex + 1 < fragments.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < fragments.Count; secondIndex++)
            {
                var first = fragments[firstIndex];
                var second = fragments[secondIndex];
                var joinable = AreJoinableExecFragments(first, second) &&
                               NoExecFragmentBetween(lines, fragments, firstIndex, secondIndex) &&
                               ShareStructuredContext(lines, first, second);
                var containsCurl = ContainsCurlExecutable(first.Text) || ContainsCurlExecutable(second.Text);
                if (second.StartLine - first.EndLine >= maximumSequenceLines)
                {
                    if (joinable && containsCurl)
                    {
                        abandonmentReasons |= StructuredAbandonmentReason.LineLimit;
                    }

                    break;
                }

                if (first.Text.Length + second.Text.Length + 1 > maximumSequenceCharacters)
                {
                    if (joinable && containsCurl)
                    {
                        abandonmentReasons |= StructuredAbandonmentReason.CharacterLimit;
                    }

                    continue;
                }

                var fragmentAbandonment = first.AbandonmentReason | second.AbandonmentReason;
                if (joinable && fragmentAbandonment != StructuredAbandonmentReason.None && containsCurl)
                {
                    abandonmentReasons |= fragmentAbandonment;
                }
                else if (joinable)
                {
                    results.Add(JoinExecFragments(first, second));
                }
            }
        }

        return new CurlScanResult(results, abandonmentReasons);
    }

    private static void UpdateBracketDepth(string value, ref int depth, ref char quote)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote == '\0' && character == '#' && IsYamlCommentStart(value, index))
            {
                break;
            }

            if (quote != '\0')
            {
                if (character == quote && !IsEscaped(value, index))
                {
                    quote = '\0';
                }

                continue;
            }

            if (IsQuote(character) && !IsEscaped(value, index))
            {
                quote = character;
            }
            else if (character == '[')
            {
                depth++;
            }
            else if (character == ']')
            {
                depth--;
            }
        }
    }

    private static bool IsYamlCommentStart(string value, int index) =>
        !IsEscaped(value, index) &&
        (index == 0 || char.IsWhiteSpace(value[index - 1]));

    private static string ReadExecValue(string line, string key)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..].TrimStart();
        }

        var separator = trimmed.IndexOf(':');
        if (separator >= 0 &&
            trimmed[..separator].Trim(' ', '\t', '{', ',', '\'', '"')
                .Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[(separator + 1)..].Trim();
        }

        var whitespace = trimmed.IndexOfAny(' ', '\t');
        return whitespace < 0 ? string.Empty : trimmed[(whitespace + 1)..].Trim();
    }

    private static bool IsBlockScalarIndicator(string value) =>
        value is ">" or ">-" or ">+" or "|" or "|-" or "|+";

    private static bool LooksLikeYamlMappingEntry(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 ||
            separator + 1 < value.Length && !char.IsWhiteSpace(value[separator + 1]))
        {
            return false;
        }

        var key = value.AsSpan(0, separator).Trim().Trim('\'').Trim('"');
        if (key.IsEmpty)
        {
            return false;
        }

        foreach (var character in key)
        {
            if (!char.IsLetterOrDigit(character) && character is not '_' and not '-' and not '.')
            {
                return false;

            }
        }

        return true;
    }
    private static bool ContainsCurlExecutable(string text) =>
        TokenizeCommands(text).Any(command => FindCurlExecutable(command) >= 0);

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

        if (trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..].TrimStart();
            indent += 2;
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

    private static bool AreJoinableExecFragments(ExecFragment first, ExecFragment second)
    {
        if (first.Indent != second.Indent)
        {
            return false;
        }

        return first.Key switch
        {
            "command" => second.Key is "args" or "entrypoint",
            "args" => second.Key == "command",
            "entrypoint" => second.Key is "command" or "cmd",
            "cmd" => second.Key == "entrypoint",
            _ => false
        };
    }

    private static string JoinExecFragments(ExecFragment first, ExecFragment second)
    {
        var executable = first.Key is "entrypoint" or "command" &&
                         second.Key is "args" or "cmd" or "command"
            ? first
            : second;
        var arguments = ReferenceEquals(executable, first) ? second : first;
        return executable.Text + " " + arguments.Text;
    }

    private static bool NoExecFragmentBetween(
        IReadOnlyList<string> lines,
        IReadOnlyList<ExecFragment> fragments,
        int firstIndex,
        int secondIndex)
    {
        var first = fragments[firstIndex];
        var second = fragments[secondIndex];
        var firstStage = DockerfileStageAt(lines, first.StartLine);
        if (firstStage >= 0 && firstStage == DockerfileStageAt(lines, second.StartLine))
        {
            return true;
        }

        var parentIndent = first.Indent;
        for (var index = firstIndex + 1; index < secondIndex; index++)
        {
            if (fragments[index].Indent <= parentIndent)
            {
                return false;
            }
        }

        return true;
    }

    private static int DockerfileStageAt(IReadOnlyList<string> lines, int line)
    {
        var stage = -1;
        for (var index = 0; index <= line; index++)
        {
            if (lines[index].TrimStart().StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
            {
                stage++;
            }
        }

        return stage;
    }

    private static bool ShareStructuredContext(
        IReadOnlyList<string> lines,
        ExecFragment first,
        ExecFragment second)
    {
        var secondTrimmed = lines[second.StartLine].TrimStart();
        if (secondTrimmed.StartsWith("- ", StringComparison.Ordinal) ||
            secondTrimmed.StartsWith('{'))
        {
            return false;
        }

        for (var index = first.EndLine + 1; index < second.StartLine; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed is "---" or "..." ||
                trimmed.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var indent = CountLeadingWhitespace(lines[index]);
            if (indent < first.Indent)
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
                else if (character is '\\' or '`' or '^' &&
                         index + 1 < line.Length &&
                         IsQuote(line[index + 1]))
                {
                    token.Append(line[++index]);
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
                if (token.ToString().Contains('@') ||
                    RemainderBeforeWhitespaceContainsAt(line, index))
                {
                    token.Append(character);
                }
                else
                {
                    CompleteToken();
                }
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

    private static bool RemainderBeforeWhitespaceContainsAt(string value, int start)
    {
        for (var index = start; index < value.Length && !char.IsWhiteSpace(value[index]); index++)
        {
            if (value[index] == '@')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCommandSeparator(char value) => value is ';' or '&' or '|' or '(' or ')';

    private static bool IsQuoteOpening(string value, int index) =>
        IsQuote(value[index]) &&
        !IsEscaped(value, index) &&
        (index == 0 || !char.IsLetterOrDigit(value[index - 1]) || IsAttachedShortOptionQuote(value, index)) &&
        FindUnescapedQuote(value, index + 1, value[index]) >= 0;

    private static bool IsAttachedShortOptionQuote(string value, int quoteIndex)
    {
        var start = quoteIndex - 1;
        while (start >= 0 && !char.IsWhiteSpace(value[start]) && !IsCommandSeparator(value[start]))
        {
            start--;
        }

        start++;
        if (quoteIndex - start < 2 || value[start] != '-' || value[start + 1] == '-')
        {
            return false;
        }

        for (var index = start + 1; index < quoteIndex; index++)
        {
            if (!CurlShortOptions.TryGetValue(value[index], out var spec) ||
                index + 1 < quoteIndex && spec.Arity != CurlOptionArity.NoValue)
            {
                return false;
            }

            if (index + 1 == quoteIndex && spec.Arity == CurlOptionArity.NoValue)
            {
                return false;
            }
        }

        return true;
    }

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
