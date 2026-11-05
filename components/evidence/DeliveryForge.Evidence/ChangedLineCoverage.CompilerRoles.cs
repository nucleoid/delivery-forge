using System.Text.Json;

namespace DeliveryForge.Evidence;

internal static partial class ChangedLineCoverage
{
    private static void BindCompilerPhysicalRoles(JsonElement context, string repository,
        Dictionary<string, Source> sources, InstalledToolFiles files)
    {
        // Exact same-context file records only, not general prefix PathMap
        // resolution. No mapping/PDB path is opened or used for authored reads.
        var exactSources = new Dictionary<string, Source>(StringComparer.Ordinal);
        foreach (var source in sources.Values)
        {
            files.Check();
            if (source.RepositoryPath is { } relative) InstalledToolProfile.Relative(relative);
            exactSources.Add(source.LogicalPath, source);
        }
        if (!context.TryGetProperty("pathPolicy", out var policy)) return;
        if (policy.ValueKind != JsonValueKind.Object) throw Refuse("Malformed compiler path-policy object.");
        if (!policy.TryGetProperty("reportRootMappings", out var rows)) return;
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 100000)
            throw Refuse("Compiler path mappings exceed the existing bounded report-array profile.");
        var logicalSeen = new HashSet<string>(StringComparer.Ordinal);
        var physicalSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            files.Check();
            if (row.ValueKind != JsonValueKind.Object) throw Refuse("Malformed compiler path mapping.");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in row.EnumerateObject())
                if (field.Name is not ("reportRoot" or "logicalRoot") || !fields.Add(field.Name))
                    throw Refuse("Malformed or duplicate compiler path-mapping field.");
            if (fields.Count != 2) throw Refuse("Incomplete compiler path mapping.");
            var logical = Text(row, "logicalRoot"); InstalledToolProfile.Relative(logical);
            if (logical.Any(char.IsControl) || !logicalSeen.Add(logical))
                throw Refuse("Duplicate or malformed compiler logical mapping.");
            if (!exactSources.TryGetValue(logical, out var source) || source.RepositoryPath is not { } relative)
                throw Refuse("Compiler mapping does not name an exact same-context repository source.");
            var physical = Text(row, "reportRoot");
            if (physical.Length > 16384 || physical.Any(char.IsControl) || !Path.IsPathFullyQualified(physical))
                throw Refuse("Malformed absolute compiler physical mapping.");
            string canonical, expected;
            try
            {
                canonical = Path.GetFullPath(physical);
                expected = Path.GetFullPath(Path.Combine(repository, relative));
            }
            catch (Exception parser) when (parser is ArgumentException or NotSupportedException or PathTooLongException)
            { throw Refuse("Malformed compiler mapping path.", parser); }
            files.Check();
            if (!string.Equals(physical, canonical, StringComparison.Ordinal))
                throw Refuse("Compiler mapping must use the exact canonical physical spelling.");
            if (!InstalledToolGateProvider.Inside(canonical, repository))
                throw Refuse("Compiler mapping is outside the selected repository.");
            if (!string.Equals(canonical, expected, StringComparison.Ordinal))
                throw Refuse("Compiler mapping differs from the exact repository-relative source role.");
            if (!physicalSeen.Add(canonical)) throw Refuse("Duplicate compiler physical mapping.");
            if (source.Generated)
                sources[source.LogicalPath] = source with { CompilerPhysicalRole = canonical };
        }
        files.Check();
    }
}
