using System.Text.Json;
using DeliveryForge.Contracts.Serialization;

namespace DeliveryForge.Tool.Claude;

// Restrictive DATA binding only. Never imports credentials, writes settings,
// selects a model, or supplies execution/review authority. The exact CLI still
// requires hostile-settings behavioral qualification; cooperative file checks
// do not prevent an arbitrary same-user writer racing a native process.
internal sealed class ClaudeReviewerUserSettings
{
    readonly ToolLifetime original;
    readonly string directory, settings;
    readonly bool existed;
    readonly long bytes;
    readonly string? digest;
    const int MaximumBytes = 65536;
    static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    ClaudeReviewerUserSettings(ToolLifetime owner, string selectedDirectory, IReadOnlyList<string> protectedRoots)
    {
        original = owner; original.Check();
        directory = Normalize(BoundedFiles.Plain(selectedDirectory, true));
        RequireSeparate(protectedRoots);
        settings = System.IO.Path.Combine(directory, "settings.json");
        var current = Read(); existed = current is not null;
        if (current is not null)
        {
            RequireSafeSettings(current);
            bytes = current.LongLength; digest = BoundedFiles.Sha(current);
        }
        original.Check();
    }

    internal static ClaudeReviewerUserSettings CaptureActual(ToolLifetime original, params string[] protectedRoots) => new(original, ActualDirectory(), protectedRoots);
    // Testable ordinary-file boundary, not a production selection or DTO issuer.
    internal static ClaudeReviewerUserSettings Capture(ToolLifetime original, string selectedDirectory, params string[] protectedRoots) => new(original, selectedDirectory, protectedRoots);
    void RequireSeparate(IReadOnlyList<string> protectedRoots)
    {
        original.Check();
        foreach (var source in protectedRoots) BoundedFiles.Separate(source, directory);
        original.Check();
    }
    static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    internal static string ActualDirectory()
    {
        var selected = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (selected is not null)
        {
            if (string.IsNullOrWhiteSpace(selected) || !System.IO.Path.IsPathFullyQualified(selected))
                throw new ToolUsageException("Reviewer requires an unambiguous absolute existing configuration directory selection.");
            return System.IO.Path.GetFullPath(selected);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var inheritedHome = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (!System.IO.Path.IsPathFullyQualified(home) || inheritedHome is not null &&
            (!System.IO.Path.IsPathFullyQualified(inheritedHome) || !string.Equals(Normalize(inheritedHome), Normalize(home), PathComparison)))
            throw new ToolUsageException("Reviewer home selection is ambiguous; no configuration or authentication override.");
        return System.IO.Path.Combine(home, ".claude");
    }

    byte[]? Read()
    {
        original.Check(); BoundedFiles.Plain(directory, true); BoundedFiles.Plain(settings);
        var present = File.Exists(settings); original.Check();
        return present ? BoundedFiles.Read(settings, original, MaximumBytes) : null;
    }
    internal void Check()
    {
        var current = Read();
        if ((current is not null) != existed || current is not null &&
            (current.LongLength != bytes || BoundedFiles.Sha(current) != digest))
            throw new ToolUsageException("Reviewer user settings changed after complete source binding; no native retry.");
        original.Check();
    }
    internal void CheckActual()
    {
        original.Check();
        if (!string.Equals(Normalize(ActualDirectory()), directory, PathComparison))
            throw new ToolUsageException("Reviewer configuration directory selection changed after binding.");
        Check();
    }

    internal static void RequireSafeSettings(byte[] complete)
    {
        if (complete.Length > MaximumBytes) throw new ToolUsageException("Reviewer user settings exceed the finite complete input profile.");
        try
        {
            _ = CanonicalJson.Canonicalize(complete);
            using var doc = JsonDocument.Parse(complete);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                // Current supported user settings that can change environment,
                // invoke custom code, replace auth, or grant tool permissions.
                // Other preferences (including the configured model) are kept.
                if (new[] { "env", "hooks", "apiKeyHelper", "permissions", "awsAuthRefresh", "awsCredentialExport", "otelHeadersHelper" }.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new JsonException();
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Parser/property/value text may include credentials. No raw error,
            // inner exception, key name or user value leaves this boundary.
            throw new ToolUsageException("Reviewer user settings are malformed or contain unsupported security-sensitive customization; no settings are changed.");
        }
    }

    internal void RequireObservedConfigurationDirectory(byte[] actualAuthStatus)
    {
        original.Check(); string? observed = null;
        try
        {
            if (actualAuthStatus.Length > MaximumBytes) throw new JsonException();
            _ = CanonicalJson.Canonicalize(actualAuthStatus);
            using var doc = JsonDocument.Parse(actualAuthStatus);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("configDirectory", out var value) && value.ValueKind == JsonValueKind.String)
                observed = value.GetString();
            if (observed is null || !System.IO.Path.IsPathFullyQualified(observed) ||
                !string.Equals(Normalize(observed), directory, PathComparison)) throw new JsonException();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            throw new ToolUsageException("Actual reviewer configuration directory is unavailable or differs from the captured user-settings source.");
        }
        original.Check();
    }
}
