namespace DeliveryForge.Evidence.CensusFixture;

// Deliberately all-static, ordinary single-line authored bodies. No remapping,
// generated-body exemption or runtime invocation is needed to inspect this build.
public static class Subject
{
    public static int First(int value) => value + 11; // CENSUS_FIRST

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public static int Excluded(int value) => value + 19; // CENSUS_EXCLUDED

    public static int Last(int value) => value + 23; // CENSUS_LAST
}
