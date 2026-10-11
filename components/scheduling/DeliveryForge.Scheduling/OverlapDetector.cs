namespace DeliveryForge.Scheduling;

public sealed record ChangeFootprint(IReadOnlyList<string> Paths, IReadOnlyList<string> SharedOwners, bool Unknown = false);

public static class OverlapDetector
{
    public static bool Conflicts(ChangeFootprint left, ChangeFootprint right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (!TryCapture(left, out var leftPaths, out var leftOwners) || !TryCapture(right, out var rightPaths, out var rightOwners)) return true;
        if (leftOwners.Intersect(rightOwners, StringComparer.OrdinalIgnoreCase).Any()) return true;
        return leftPaths.Any(a => rightPaths.Any(b => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryCapture(ChangeFootprint map, out string[] paths, out string[] owners)
    {
        paths = [];
        owners = [];
        if (map.Unknown || map.Paths is null || map.SharedOwners is null || map.Paths.Count > 4096 || map.SharedOwners.Count > 1024) return false;
        paths = map.Paths.Select(path => path?.Replace('\\', '/') ?? "").ToArray();
        var explicitOwners = map.SharedOwners.ToArray();
        var inferredOwners = new List<string>();
        foreach (var path in paths)
        {
            var segments = path.Split('/');
            if (segments.Any(segment => segment.Equals("migrations", StringComparison.OrdinalIgnoreCase) || segment.Equals("migration", StringComparison.OrdinalIgnoreCase))) inferredOwners.Add("implicit:migrations");
            if (segments.Any(segment => segment.Equals("schemas", StringComparison.OrdinalIgnoreCase) || segment.Equals("schema", StringComparison.OrdinalIgnoreCase))) inferredOwners.Add("implicit:schemas");
            if (segments[^1].Contains("baseline", StringComparison.OrdinalIgnoreCase)) inferredOwners.Add("implicit:baselines");
            if ((path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)) &&
                !explicitOwners.Any(owner => owner?.StartsWith("generated:", StringComparison.OrdinalIgnoreCase) == true)) inferredOwners.Add("implicit:generated-owner-unknown");
        }
        owners = explicitOwners.Concat(inferredOwners).ToArray();
        if (paths.Length == 0 && owners.Length == 0) return false;
        return paths.All(path => path.Length is > 0 and <= 4096 && !path.StartsWith('/') &&
            !path.Any(character => char.IsControl(character) || ":*?[]{}".Contains(character)) &&
            path.Split('/').All(segment => segment is not ("" or "." or ".."))) &&
            owners.All(owner => !string.IsNullOrWhiteSpace(owner) && owner.Length <= 256 && owner == owner.Trim() && !owner.Any(char.IsControl));
    }
}
