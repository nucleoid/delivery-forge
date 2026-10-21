using System.Text.Json;
using DeliveryForge.Contracts.Serialization;

namespace DeliveryForge.Tool;

// Selected input can only restrict this live original. Saved control DATA never
// constructs an owner, dispatch ticket, session or replacement lifetime.
internal sealed class ProductionBatchControlInput(string path, string actualRunId, string actualSelectionIdentity,
    ToolLifetime original, ProductionBatchControl control)
{
    internal static void ValidatePath(string storeRoot, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(BoundedFiles.Plain(storeRoot, true));
        var file = BoundedFiles.Plain(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(file), root, comparison) ||
            new[] { "batch-selection.json", "runs" }.Any(name => string.Equals(Path.GetFileName(file), name, comparison)))
            throw new ToolUsageException("Batch control must select a nonreserved plain direct child of its fresh store.");
    }
    internal static void RequireInitiallyAbsent(string path, ToolLifetime original)
    {
        original.Check(); BoundedFiles.Plain(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new ToolUsageException("Batch control input must be absent for a newly created original; no replay.");
        original.Check();
    }
    internal bool ReadBoundary()
    {
        original.Check(); BoundedFiles.Plain(path);
        if (!File.Exists(path)) { original.Check(); return false; }
        var bytes = BoundedFiles.Read(path, original, 1024);
        _ = CanonicalJson.Canonicalize(bytes);
        using var document = JsonDocument.Parse(bytes);
        var row = document.RootElement;
        if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 4 ||
            row.EnumerateObject().Any(property => property.Name is not ("Kind" or "BatchRunId" or "SelectionIdentity" or "Action")))
            throw new ToolUsageException("Complete exact batch control object required.");
        string Read(string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ToolUsageException("Exact batch control strings required.");
        var action = Read("Action");
        if (Read("Kind") != "ProductionBatchControl-v1" || Read("BatchRunId") != actualRunId ||
            Read("SelectionIdentity") != actualSelectionIdentity || action is not ("pause" or "stop"))
            throw new ToolUsageException("Foreign or unsupported batch control cannot affect this original.");
        original.Check(); var beforePaused = control.Paused; var beforeStopped = control.Stopped;
        if (action == "stop") control.Stop(); else control.Pause();
        original.Check(); return beforePaused != control.Paused || beforeStopped != control.Stopped;
    }
}
