namespace DeliveryForge.Planning.Tests;

// These genuine Git/helper/filesystem cases share the approved finite two-CPU validation
// workload. Serial execution preserves each original clock and all contention assertions.
[CollectionDefinition(RichPlanImportFixtureCollection.Name, DisableParallelization = true)]
public sealed class RichPlanImportFixtureCollection
{
    public const string Name = "Rich plan import actual finite Git fixtures";
}
