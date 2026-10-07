using System.Text.RegularExpressions;

namespace DeliveryForge.Planning;

internal static partial class PortableMaterial
{
    public static bool ContainsPrivateMaterial(string value)
    {
        var withoutUrls = SchemeUrl().Replace(value, "portable-url");
        return HostPath().IsMatch(withoutUrls) || Credential().IsMatch(value);
    }

    public static bool IsAbsolutePath(string value) =>
        value.StartsWith('/') ||
        value.StartsWith('\\') ||
        WindowsDrivePath().IsMatch(value);

    [GeneratedRegex("""(?ix)(?:^|[\s=:'"(])(?:[a-z]:[\\/]|[\\/]{2}[^\\/\s]+[\\/][^\\/\s]+|/(?!/)(?:[a-z0-9._~-]+/)+[a-z0-9._~-]+)""")]
    private static partial Regex HostPath();

    [GeneratedRegex(@"(?i)\b[a-z][a-z0-9+.-]*://[^\s]+")]
    private static partial Regex SchemeUrl();

    [GeneratedRegex(@"(?i)(?:-----BEGIN\s|Bearer\s|api[_-]?key\s*=|password\s*=|token\s*=|(?:^|[^A-Za-z0-9])(?:gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}))")]
    private static partial Regex Credential();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDrivePath();
}
