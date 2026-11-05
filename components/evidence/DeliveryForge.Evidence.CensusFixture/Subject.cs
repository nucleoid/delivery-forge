namespace DeliveryForge.Evidence.CensusFixture;

// Deliberately an ordinary class with a genuine compiler-emitted implicit constructor in
// addition to these single-line authored bodies. No remapping or invocation.
public class Subject
{
    public static int First(int value) => value + 11; // CENSUS_FIRST

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public static int Excluded(int value) => value + 19; // CENSUS_EXCLUDED

    public static int Last(int value) => value + 23; // CENSUS_LAST
}
