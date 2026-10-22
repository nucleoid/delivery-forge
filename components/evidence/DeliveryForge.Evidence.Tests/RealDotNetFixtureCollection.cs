namespace DeliveryForge.Evidence.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealDotNetFixtureCollection
{
    public const string Name = "Actual evidence .NET sequential fixture";
}
