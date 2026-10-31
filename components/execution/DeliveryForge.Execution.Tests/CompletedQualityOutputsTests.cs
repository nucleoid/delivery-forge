using DeliveryForge.Execution.Git;

namespace DeliveryForge.Execution.Tests;

// Comparisons only: these tests create no actual completed gate or authority.
public sealed class CompletedQualityOutputsTests
{
    static string Hash(char c) => new(c, 64);
    static Dictionary<string, string> Before() => new(StringComparer.Ordinal)
    {
        ["Library/Library.csproj"] = "10:sha256:" + Hash('a'),
        ["Library/Subject.cs"] = "20:sha256:" + Hash('b'),
        ["Tests/Tests.csproj"] = "30:sha256:" + Hash('c')
    };
    static Dictionary<string, string> After() => new(Before(), StringComparer.Ordinal)
    {
        ["Library/bin/Release/net10.0/Library.dll"] = "40:sha256:" + Hash('d'),
        ["Tests/obj/project.assets.json"] = "50:sha256:" + Hash('e')
    };
    static FileSnapshot Snapshot(IReadOnlyDictionary<string, string> inventory) => new(
        inventory.ToDictionary(p => p.Key, p => p.Value[(p.Value.LastIndexOf(':') + 1)..], StringComparer.Ordinal)
            .Append(new(".git", Hash('f'))).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    static WorktreeObservation Observation(FileSnapshot snapshot, params string[] status) =>
        new("/assigned", new('1', 40), new('2', 40), status.Length == 0, status, snapshot);

    [Fact]
    public void Exact_measured_outputs_are_allowed_without_changing_the_raw_dirty_observation()
    {
        var before = Before(); var after = After(); var added = CompletedQualityOutputs.SelectAddedOutputs(before, after);
        var observation = Observation(Snapshot(after), "!! Library/bin/", "!! Tests/obj/");
        Assert.False(observation.IsClean);
        CompletedQualityOutputs.RequireObservation(observation, Snapshot(before), after, added);
        Assert.False(observation.IsClean);
        after["Tests/obj/foreign.txt"] = "1:sha256:" + Hash('0');
        Assert.Equal(2, added.Count); // Captured additions cannot track later caller mutation.
    }

    [Theory]
    [InlineData("Library/new.cs")]
    [InlineData("foreign/bin/payload.dll")]
    [InlineData("Tests/obj/../secret.txt")]
    [InlineData("Tests\\obj\\secret.txt")]
    public void A_new_file_outside_original_project_output_roots_is_refused(string path)
    {
        var after = After(); after[path] = "1:sha256:" + Hash('0');
        Assert.Throws<WorktreeBoundaryException>(() => CompletedQualityOutputs.SelectAddedOutputs(Before(), after));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_changed_original_source_is_refused(bool remove)
    {
        var after = After();
        if (remove) after.Remove("Library/Subject.cs"); else after["Library/Subject.cs"] = "20:sha256:" + Hash('0');
        Assert.Throws<WorktreeBoundaryException>(() => CompletedQualityOutputs.SelectAddedOutputs(Before(), after));
    }

    [Theory]
    [InlineData("M  Library/Subject.cs")]
    [InlineData("?? Tests/obj/foreign.txt")]
    [InlineData("!! secrets/")]
    [InlineData("!! Library/bin/no-capture.dll")]
    [InlineData("!! \"Library/bin/\"")]
    public void Tracked_staged_untracked_foreign_and_ambiguous_ignored_status_stay_nonpass(string status)
    {
        var before = Before(); var after = After(); var added = CompletedQualityOutputs.SelectAddedOutputs(before, after);
        Assert.Throws<WorktreeBoundaryException>(() => CompletedQualityOutputs.RequireObservation(
            Observation(Snapshot(after), status), Snapshot(before), after, added));
    }

    [Theory]
    [InlineData(".git")]
    [InlineData("Library/bin/Release/net10.0/Library.dll")]
    [InlineData("Library/Subject.cs")]
    public void Metadata_original_source_and_generated_bytes_cannot_drift(string path)
    {
        var before = Before(); var after = After(); var files = Snapshot(after).Files.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        files[path] = Hash('0');
        Assert.Throws<WorktreeBoundaryException>(() => CompletedQualityOutputs.RequireObservation(
            Observation(new(files), "!! Library/bin/", "!! Tests/obj/"), Snapshot(before), after,
            CompletedQualityOutputs.SelectAddedOutputs(before, after)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Even_an_ignored_output_path_cannot_be_added_or_removed_after_capture(bool remove)
    {
        var before = Before(); var after = After(); var files = Snapshot(after).Files.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (remove) files.Remove("Tests/obj/project.assets.json"); else files["Tests/obj/foreign.dll"] = Hash('0');
        Assert.Throws<WorktreeBoundaryException>(() => CompletedQualityOutputs.RequireObservation(
            Observation(new(files), "!! Library/bin/", "!! Tests/obj/"), Snapshot(before), after,
            CompletedQualityOutputs.SelectAddedOutputs(before, after)));
    }
}
