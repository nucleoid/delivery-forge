using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Execution.Git;

// A private bridge between the actual closed callable invocation and its SAME
// containing parent. Neither configuration nor saved receipt DATA can bind it.
internal sealed class CompletedQualityOutputs
{
    readonly InstalledToolHostScope scope;
    readonly GateRunResult result;
    readonly IOwnedDuplexBudget original;
    readonly ICoordinatedEffectAdmission parent;
    readonly string run, root;
    readonly RepositoryIdentity revision;
    readonly FileSnapshot completedWorker;
    readonly IReadOnlyDictionary<string, string> after, added;

    internal CompletedQualityOutputs(InstalledToolHostScope scope, GateRunResult result,
        IOwnedDuplexBudget original, ICoordinatedEffectAdmission parent, string run, string root,
        RepositoryIdentity revision, FileSnapshot completedWorker)
    {
        this.scope = scope; this.result = result; this.original = original; this.parent = parent;
        this.run = run; this.root = Path.GetFullPath(root); this.revision = revision;
        this.completedWorker = new(new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            completedWorker.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
        var measured = Read(verifyCurrent: false);
        RequireFiles(this.completedWorker.Files, measured.Before);
        after = measured.After;
        added = SelectAddedOutputs(measured.Before, measured.After);
    }

    InstalledSourceInventory Read(bool verifyCurrent) => scope.CompletedSourceInventory(result, original, parent, run, root, revision, verifyCurrent);

    internal void RequireBinding(IOwnedDuplexBudget actualOriginal, ICoordinatedEffectAdmission actualParent,
        string actualRun, string actualRoot, string actualBase, string actualHead)
    {
        original.CheckWork();
        if (!ReferenceEquals(original, actualOriginal) || !ReferenceEquals(parent, actualParent) || actualRun != run ||
            !string.Equals(Path.GetFullPath(actualRoot), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            actualBase != revision.BaseCommit || actualHead != revision.HeadCommit)
            throw new WorktreeBoundaryException("Completed quality outputs cannot cross parent, original, worktree or revision.");
    }

    // Observe the same retained actual scope; no caller flag can supply this state.
    internal bool HasUncertainOriginals => scope.HasUncertainOriginals;

    internal IReadOnlyDictionary<string, string> RequireCurrentFiles(string headTree)
    {
        original.CheckWork();
        if (headTree != revision.TreeId) throw new WorktreeBoundaryException("Completed quality output tree changed.");
        _ = Read(verifyCurrent: true); return added;
    }

    internal void RequireCurrent(WorktreeObservation observed)
    {
        if (!string.Equals(Path.GetFullPath(observed.RootPath), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            observed.HeadCommit != revision.HeadCommit || observed.TreeId != revision.TreeId)
            throw new WorktreeBoundaryException("Current quality worktree or committed revision changed.");
        _ = RequireCurrentFiles(observed.TreeId);
        RequireObservation(observed, completedWorker, after, added);
    }

    // Pure comparisons below issue no provenance. Production calls them only
    // after selecting the exact private actual completed row above.
    internal static IReadOnlyDictionary<string, string> SelectAddedOutputs(IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        if (before.Count > 100000 || after.Count > 100000 || before.Any(pair => !after.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
            throw new WorktreeBoundaryException("Original source membership or bytes changed during quality.");
        var projectRoots = before.Keys.Where(path => path.EndsWith(".csproj", StringComparison.Ordinal))
            .Select(path => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "").ToArray();
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in after.Where(pair => !before.ContainsKey(pair.Key)))
        {
            var path = pair.Key;
            if (path.Contains('\\') || path.Contains(':') || path.Split('/').Any(part => part is "" or "." or "..") ||
                !projectRoots.Any(project => path.StartsWith(project + "bin/", StringComparison.Ordinal) || path.StartsWith(project + "obj/", StringComparison.Ordinal)))
                throw new WorktreeBoundaryException("Quality added a file outside original project SDK output roots.");
            outputs.Add(path, pair.Value);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(outputs);
    }

    internal static void RequireObservation(WorktreeObservation observed, FileSnapshot worker,
        IReadOnlyDictionary<string, string> after, IReadOnlyDictionary<string, string> added)
    {
        if (!worker.Files.TryGetValue(".git", out var metadata) || !observed.Snapshot.Files.TryGetValue(".git", out var actualMetadata) || metadata != actualMetadata)
            throw new WorktreeBoundaryException("Assigned worktree Git metadata changed during quality.");
        RequireFiles(observed.Snapshot.Files, after);
        foreach (var entry in observed.StatusEntries)
        {
            if (!entry.StartsWith("!! ", StringComparison.Ordinal))
                throw new WorktreeBoundaryException("Tracked, staged or untracked source changed during quality.");
            var path = entry[3..];
            var directory = path.EndsWith('/');
            var matches = after.Keys.Where(file => directory ? file.StartsWith(path, StringComparison.Ordinal) : file == path).ToArray();
            if (matches.Length == 0 || matches.Any(file => !added.ContainsKey(file)))
                throw new WorktreeBoundaryException("An ignored path is not exactly accounted by the actual closed quality output inventory.");
        }
    }

    static void RequireFiles(IReadOnlyDictionary<string, string> snapshot, IReadOnlyDictionary<string, string> measured)
    {
        var files = snapshot.Where(pair => pair.Key != ".git").ToArray();
        if (files.Length != measured.Count || files.Any(pair => !measured.TryGetValue(pair.Key, out var hash) || !InventoryHashMatches(hash, pair.Value)))
            throw new WorktreeBoundaryException("Complete current file membership or bytes differ from the actual measured quality inventory.");
    }

    static bool InventoryHashMatches(string inventory, string rawHash)
    {
        var colon = inventory.IndexOf(':');
        return colon > 0 && long.TryParse(inventory.AsSpan(0, colon), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var length) && length >= 0 &&
            inventory[(colon + 1)..] == "sha256:" + rawHash;
    }
}
