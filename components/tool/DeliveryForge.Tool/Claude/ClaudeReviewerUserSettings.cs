using System.Globalization;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Evidence;

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
    readonly bool ownerSelected;
    readonly IReadOnlyDictionary<string, string>? childEnvironment;
    const int MaximumBytes = 65536;
    // Exact known personal profile only. --restricted does not read this user
    // settings source; retain its complete pin without importing arbitrary new
    // settings or silently dropping a future configured model selection.
    static readonly HashSet<string> OwnerTopLevel = new(StringComparer.Ordinal)
    {
        "env", "permissions", "hooks", "statusLine", "enabledPlugins", "extraKnownMarketplaces", "effortLevel",
        "autoUpdatesChannel", "autoMemoryEnabled", "autoDreamEnabled", "skipDangerousModePermissionPrompt",
        "skipWorkflowUsageWarning", "verbose", "agentPushNotifEnabled", "skipAutoPermissionPrompt"
    };
    static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    ClaudeReviewerUserSettings(ToolLifetime owner, string selectedDirectory, IReadOnlyList<string> protectedRoots,
        InstalledHostAdmission.RuntimeTrust? runtimeTrust = null, IndependentClaudeReviewSelection? reviewer = null)
    {
        original = owner; original.Check();
        directory = Normalize(BoundedFiles.Plain(selectedDirectory, true));
        RequireSeparate(protectedRoots);
        settings = System.IO.Path.Combine(directory, "settings.json");
        var current = Read(); existed = current is not null;
        ownerSelected = runtimeTrust is not null;
        if (runtimeTrust is not null)
            runtimeTrust.RequireSelectedReviewerSettings(original, reviewer ?? throw new ToolUsageException("Exact selected reviewer required."), settings, current);
        if (current is not null)
        {
            if (ownerSelected) RequireOwnerSelectedSettings(current); else RequireSafeSettings(current);
            bytes = current.LongLength; digest = BoundedFiles.Sha(current);
        }
        if (ownerSelected)
        {
            // No CLAUDE_CONFIG_DIR is inherited. Refuse a custom auth/config
            // selection instead of silently switching its existing store.
            var standard = Normalize(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
            if (!string.Equals(directory, standard, PathComparison))
                throw new ToolUsageException("Owner-selected reviewer requires its existing standard configuration directory; no authentication selection is changed.");
            childEnvironment = ClaudeIndependentReviewData.CapturePinnedSettingsEnvironment(Environment.GetEnvironmentVariable);
        }
        original.Check();
    }

    internal static ClaudeReviewerUserSettings CaptureActual(ToolLifetime original, params string[] protectedRoots) => new(original, ActualDirectory(), protectedRoots);
    internal static ClaudeReviewerUserSettings CaptureActual(ToolLifetime original, InstalledHostAdmission.RuntimeTrust? runtimeTrust,
        IndependentClaudeReviewSelection reviewer, params string[] protectedRoots) => new(original, ActualDirectory(), protectedRoots, runtimeTrust, reviewer);
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

    // Both parsers are DATA checks only. Only the private original trust path
    // above may construct a captured settings owner using the narrow profile.
    internal static void RequireSafeSettings(byte[] complete) => RequireSettings(complete, false);
    internal static void RequireOwnerSelectedSettings(byte[] complete) => RequireSettings(complete, true);
    static void RequireSettings(byte[] complete, bool selectedPersonalProfile)
    {
        if (complete.Length > MaximumBytes) throw new ToolUsageException("Reviewer user settings exceed the finite complete input profile.");
        try
        {
            _ = CanonicalJson.Canonicalize(complete);
            using var doc = JsonDocument.Parse(complete);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (selectedPersonalProfile && !OwnerTopLevel.Contains(property.Name)) throw new JsonException();
                if (new[] { "apiKeyHelper", "awsAuthRefresh", "awsCredentialExport", "otelHeadersHelper" }.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new JsonException();
                if (string.Equals(property.Name, "env", StringComparison.OrdinalIgnoreCase))
                {
                    if (!selectedPersonalProfile || property.Name != "env") throw new JsonException();
                    RequireInertEnvironment(property.Value);
                }
                else if (string.Equals(property.Name, "hooks", StringComparison.OrdinalIgnoreCase))
                {
                    // The exact private profile uses --restricted, which excludes
                    // user settings, plus --safe-mode. Never execute or copy values.
                    if (!selectedPersonalProfile || property.Name != "hooks" || property.Value.ValueKind != JsonValueKind.Object) throw new JsonException();
                }
                else if (string.Equals(property.Name, "permissions", StringComparison.OrdinalIgnoreCase))
                {
                    if (!selectedPersonalProfile || property.Name != "permissions") throw new JsonException();
                    RequireInertPermissions(property.Value);
                }
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            throw new ToolUsageException("Reviewer user settings are malformed or contain unsupported security-sensitive customization; no settings are changed.");
        }
    }
    static string BoundedText(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String) throw new JsonException();
        var text = value.GetString()!;
        if (text.Length is < 1 || text.Length > maximum || text.Any(char.IsControl)) throw new JsonException();
        return text;
    }
    static void RequireInertEnvironment(JsonElement environment)
    {
        if (environment.ValueKind != JsonValueKind.Object) throw new JsonException();
        foreach (var property in environment.EnumerateObject())
        {
            var text = BoundedText(property.Value, 256);
            switch (property.Name)
            {
                case "ANTHROPIC_CUSTOM_MODEL_OPTION":
                    // Picker metadata only; this neither chooses the session
                    // model nor overrides any built-in model alias or provider.
                    var id = text.EndsWith("[1m]", StringComparison.Ordinal) ? text[..^4] : text;
                    if (!id.StartsWith("claude-", StringComparison.Ordinal) || id.Length < 8 || id[7..].Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
                        throw new JsonException();
                    break;
                case "ANTHROPIC_CUSTOM_MODEL_OPTION_NAME":
                case "ANTHROPIC_CUSTOM_MODEL_OPTION_DESCRIPTION":
                    break;
                case "CLAUDE_AUTOCOMPACT_PCT_OVERRIDE":
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) || percent is < 1 or > 100)
                        throw new JsonException();
                    break;
                default:
                    // No model/default/fallback, provider/auth/endpoint,
                    // launcher, shell or unknown environment override.
                    throw new JsonException();
            }
        }
    }
    static void RequireInertPermissions(JsonElement permissions)
    {
        if (permissions.ValueKind != JsonValueKind.Object) throw new JsonException();
        foreach (var property in permissions.EnumerateObject())
        {
            if (property.Name is "allow" or "ask" or "deny")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > 512) throw new JsonException();
                foreach (var rule in property.Value.EnumerateArray()) _ = BoundedText(rule, 1024);
            }
            else if (property.Name == "defaultMode")
            {
                if (BoundedText(property.Value, 64) is not ("default" or "acceptEdits" or "plan" or "dontAsk" or "bypassPermissions" or "auto")) throw new JsonException();
            }
            else throw new JsonException();
        }
    }
    internal string[] ObservationArguments(string[] arguments) => ownerSelected
        ? ClaudeIndependentReviewData.RestrictedObservationArguments(arguments)
        : ClaudeIndependentReviewData.IsolatedObservationArguments(arguments);
    internal string[] ReviewArguments(decimal maximumCost) => ownerSelected
        ? ClaudeIndependentReviewData.RestrictedReviewArguments(maximumCost)
        : ClaudeIndependentReviewData.IsolatedReviewArguments(maximumCost);
    internal CommandInvocation Invocation(string image, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? ordinaryEnvironment = null) => childEnvironment is not null
            ? ClaudeIndependentReviewData.PinnedSettingsInvocation(image, arguments, workingDirectory, childEnvironment)
            : new(image, arguments, workingDirectory, ordinaryEnvironment);
    internal void RequireIsolationArguments(CommandInvocation actual, bool observation)
    {
        original.Check();
        if (ownerSelected)
        {
            ClaudeIndependentReviewData.RequirePinnedSettingsIsolation(actual.Arguments, observation);
            ClaudeIndependentReviewData.RequirePinnedSettingsInvocation(actual, childEnvironment!);
            ClaudeIndependentReviewData.RequirePinnedSettingsEnvironmentCurrent(childEnvironment!, Environment.GetEnvironmentVariable);
        }
        original.Check();
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
