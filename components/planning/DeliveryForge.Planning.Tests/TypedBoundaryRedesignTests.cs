using System.Text;

namespace DeliveryForge.Planning.Tests;

public sealed class TypedBoundaryRedesignTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Private_advisory_conflict_text_never_enters_the_frozen_plan()
    {
        const string privateAdvisory = "Project Phoenix is coordinated from attic seven";
        var draft = CreateDraft();
        var imported = new ImportedContextEnvelope(
            "1.0.0",
            [new("memory", "memory:opaque-1", "Bounded private advisory", null, ObservedAt)],
            [privateAdvisory],
            []);
        var intake = IntakePlanner.Assess(draft.Request, draft.Provenance, imported);

        var frozen = PlanFreezer.Freeze(draft with { Intake = intake }, "typed-boundary-red", ObservedAt);

        Assert.DoesNotContain(privateAdvisory, Encoding.UTF8.GetString(frozen.CanonicalBytes), StringComparison.Ordinal);
    }

    private static PlanDraft CreateDraft()
    {
        var request = new PlanningRequest(
            "nucleoid/delivery-forge", "#4", "implement", "Build planning core",
            ["planning"], ["execution"], ["Behavior is deterministic"], "implement");
        var file = new RepositoryFile(
            "README.md", new string('c', 40), "100644", Encoding.UTF8.GetBytes("exact bytes"),
            isSymlink: false, escapesWorktree: false, "not-detected", SymlinkResolution.NotSymlink,
            new string('a', 40), new string('b', 40));
        EvidenceItem[] provenance = [EvidenceItem.FromRepositoryFile(file, ObservedAt, [])];
        var repository = RepositoryContext.Create(
            "/portable/display-only", "HEAD", new string('a', 40), new string('b', 40),
            detachedHead: false, dirty: false, shallow: false, submodules: [], limitations: []);

        return new PlanDraft(
            request,
            repository,
            provenance,
            [new("components/planning/Core.cs", "PlanFreezer", "freeze plans")],
            [new("contracts", [], "issue #3 is integrated")],
            [new("test", new PlanCommand("dotnet", [PlanCommandArgument.Literal("test")]), "all tests pass")],
            new("additive", "none", "none", "none", "none", "none", "test results", "revert commit", []),
            [],
            IntakePlanner.Assess(request, provenance));
    }
}
