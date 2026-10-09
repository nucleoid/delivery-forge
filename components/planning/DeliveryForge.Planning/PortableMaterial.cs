using System.Text;
using System.Text.RegularExpressions;

namespace DeliveryForge.Planning;

// Defense in depth for already-typed public values. These bounded checks catch common mistakes;
// they are deliberately not a parser, an allowlist for arbitrary documents, or a privacy proof.
internal static partial class PortableMaterial
{
    public static bool ContainsPrivateMaterial(string value)
    {
        if (value.Any(character => character == '\0' ||
                                   char.IsControl(character) && character is not '\t' and not '\n' and not '\r'))
        {
            return true;
        }

        var withoutPublicUrls = PublicUrl().Replace(value, match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            string.IsNullOrEmpty(uri.UserInfo)
                ? "public-url"
                : match.Value);

        return PrivateHostPath().IsMatch(withoutPublicUrls) ||
               HomeAliasPath().IsMatch(withoutPublicUrls) ||
               HighConfidenceCredential().IsMatch(value);
    }

    public static bool IsAbsolutePath(string value) =>
        value.StartsWith('/') || value.StartsWith('\\') || WindowsDrivePath().IsMatch(value);

    public static bool IsSafeIdentifier(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    [GeneratedRegex(@"[a-z][a-z0-9+.-]*://[^\s`|\[\]{}<>\""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublicUrl();

    [GeneratedRegex("""(?:(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/\s]+[\\/][^\\/\s]+)|(?<![A-Za-z0-9.])/(?:home|Users|root|tmp|var|opt|run|workspace|data)(?:/[^/\s`|\[\]{}<>\"']+)+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateHostPath();

    [GeneratedRegex("""(?<![A-Za-z0-9])(?:~[A-Za-z0-9._-]*|\$HOME|\$\{HOME\}|\$env:(?:USERPROFILE|HOME|APPDATA|LOCALAPPDATA)|%(?:USERPROFILE|APPDATA|LOCALAPPDATA)%)[\\/][^\s`|\[\]{}<>\"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HomeAliasPath();

    [GeneratedRegex("""(?:-----BEGIN\s+(?:RSA |EC |OPENSSH )?PRIVATE KEY-----|(?<![-A-Za-z0-9])Bearer\s+\S+|(?:^|[^A-Za-z0-9])(?:password|passwd|api[_-]?key|client[_-]?secret|access[_-]?token)\s*[:=]\s*\S+|github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16})""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HighConfidenceCredential();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDrivePath();
}

internal static class CommandRenderer
{
    public static string Render(PlanCommand command)
    {
        Validate(command);
        return string.Join(' ', new[] { Quote(command.Executable) }.Concat(command.Arguments.Select(RenderArgument)));
    }

    public static void Validate(PlanCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Executable) || command.Executable.Any(char.IsWhiteSpace) ||
            PortableMaterial.IsAbsolutePath(command.Executable) || PortableMaterial.ContainsPrivateMaterial(command.Executable))
        {
            throw new PlanningException("Command executable must be one portable executable token.");
        }

        if (command.Arguments.Count > 256)
            throw new PlanningException("Command exceeds the 256-argument limit.");

        foreach (var argument in command.Arguments)
        {
            if (string.IsNullOrEmpty(argument.Value) || argument.Value.Length > 4096 ||
                argument.Value.Any(character => character == '\0' || character is '\r' or '\n'))
                throw new PlanningException("Command arguments must be non-empty bounded tokens without controls or newlines.");

            if (argument.Kind == CommandArgumentKind.Literal && PortableMaterial.ContainsPrivateMaterial(argument.Value))
                throw new PlanningException("Literal command arguments contain high-confidence private material.");
            if (argument.Kind is CommandArgumentKind.Placeholder or CommandArgumentKind.SecretReference &&
                !PortableMaterial.IsSafeIdentifier(argument.Value))
                throw new PlanningException("Command placeholder and secret-reference names must be portable identifiers.");
        }
    }

    private static string RenderArgument(PlanCommandArgument argument) => argument.Kind switch
    {
        CommandArgumentKind.Literal => Quote(argument.Value),
        CommandArgumentKind.Placeholder => $"<placeholder:{argument.Value}>",
        CommandArgumentKind.SecretReference => $"<secret-ref:{argument.Value}>",
        _ => throw new PlanningException("Command argument kind is not recognized.")
    };

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':' or '=' or '@' or '+'))
            return value;

        var escaped = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            if (character is '\\' or '"') escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.Append('"').ToString();
    }
}
