using System.Diagnostics;
namespace DeliveryForge.Evidence;

internal static class CommandEnvironment
{
    internal static void Complete(ProcessStartInfo start, CommandInvocation invocation)
    {
        if (!invocation.EnvironmentIsComplete) return;
        var values = invocation.Environment ?? throw new ArgumentException("Complete environment snapshot required.");
        if (values.Count > 512) throw new ArgumentException("Complete environment exceeds512 entries before copy.");
        var rows = new List<KeyValuePair<string, string>>(); var total = 0; var keys = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var row in values) { if (rows.Count >= 512 || row.Key is null || row.Value is null || row.Key.Length is 0 or > 4096 || row.Value.Length > 32768 || row.Key.Contains('=') || row.Key.Contains('\0') || row.Value.Contains('\0') || !keys.Add(row.Key) || total > 131072 - row.Key.Length - row.Value.Length) throw new ArgumentException("Complete environment finite identity/value profile refused."); total += row.Key.Length + row.Value.Length; rows.Add(row); }
        start.Environment.Clear(); foreach (var row in rows) start.Environment.Add(row.Key, row.Value);
    }
}
