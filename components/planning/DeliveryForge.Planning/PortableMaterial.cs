using System.Text.RegularExpressions;

namespace DeliveryForge.Planning;

internal static partial class PortableMaterial
{
    private static readonly HashSet<string> NetworkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "ssh", "git", "ftp", "ftps"
    };

    public static bool ContainsPrivateMaterial(string value)
    {
        if (Credential().IsMatch(value)) return true;

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
               HomeAliasPath().IsMatch(withoutUrls) ||
               SingleBackslashRoot().IsMatch(withoutUrls);
    }

    public static bool IsAbsolutePath(string value) =>
        value.StartsWith('/') ||
        value.StartsWith('\\') ||
        WindowsDrivePath().IsMatch(value);

    [GeneratedRegex(
        """(?:(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/\s]+[\\/][^\\/\s]+|~[\\/](?:[^\\/\s]+[\\/])*[^\\/\s]+|/(?!/)(?:[^/\s`|\[\]{}<>"']+/)+[^/\s`|\[\]{}<>"']+))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostPath();

    [GeneratedRegex(
        """(?<![A-Za-z0-9])(?:~[A-Za-z0-9._-]*|\$HOME|\$\{HOME\}|%(?:USERPROFILE|HOMEPATH)%)[\\/][^\s`|\[\]{}<>"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HomeAliasPath();

    [GeneratedRegex(
        """(?<![A-Za-z0-9\\])\\(?:Users|Documents and Settings|ProgramData|Windows|home|tmp|var|opt)\\[^\s`|\[\]{}<>"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleBackslashRoot();

    [GeneratedRegex(
        """[a-z][a-z0-9+.-]*:(?://|\\\\)[^\s`|\[\]{}<>"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemeUrl();

    [GeneratedRegex(
        """(?:-----BEGIN\s|Bearer\s+\S+|Authorization\s*:\s*(?:Basic|Bearer)\s+\S+|"?(?:api[_-]?key|password|passwd|token|secret|client[_-]?secret|access[_-]?key|aws[_-]?access[_-]?key[_-]?id)"?\s*[:=]\s*"?\S+|(?:^|[^A-Za-z0-9])(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Credential();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDrivePath();
}
