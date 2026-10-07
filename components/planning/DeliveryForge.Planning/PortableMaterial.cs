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
        return invalidUrl || HostPath().IsMatch(withoutUrls);
    }

    public static bool IsAbsolutePath(string value) =>
        value.StartsWith('/') ||
        value.StartsWith('\\') ||
        WindowsDrivePath().IsMatch(value);

    [GeneratedRegex("""(?ix)(?:^|[\s=:'"(\[\]`{|;,])(?:[a-z]:[\\/]|[\\/]{2}[^\\/\s]+[\\/][^\\/\s]+|/(?!/)(?:[a-z0-9._~-]+/)+[a-z0-9._~-]+)""")]
    private static partial Regex HostPath();

    [GeneratedRegex("""(?i)\b[a-z][a-z0-9+.-]*://[^\s`|\[\]{}<>"']+""")]
    private static partial Regex SchemeUrl();

    [GeneratedRegex(@"(?i)(?:-----BEGIN\s|Bearer\s|api[_-]?key\s*=|password\s*=|token\s*=|(?:^|[^A-Za-z0-9])(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}))")]
    private static partial Regex Credential();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDrivePath();
}
