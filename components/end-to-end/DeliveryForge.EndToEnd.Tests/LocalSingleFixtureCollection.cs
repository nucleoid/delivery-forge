using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

// The public scheduler intentionally has one process-wide parent lane. Harness consumers
// share that policy instead of competing as unrelated parents inside the same test process.
[CollectionDefinition(LocalSingleFixtureCollection.Name, DisableParallelization = true)]
public sealed class LocalSingleFixtureCollection
{
    public const string Name = "LocalSingle public fixture parent";
}
