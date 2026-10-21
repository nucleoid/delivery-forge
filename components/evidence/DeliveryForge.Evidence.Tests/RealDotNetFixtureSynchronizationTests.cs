namespace DeliveryForge.Evidence.Tests;

public sealed class RealDotNetFixtureSynchronizationTests
{
    [Fact]
    public void Real_dotnet_fixture_uses_a_nonparallel_collection()
    {
        var assembly = typeof(RealDotNetFixtureTests).Assembly;
        var collectionType = assembly.GetType(
            "DeliveryForge.Evidence.Tests.RealDotNetFixtureCollection");
        Assert.NotNull(collectionType);

        var fixtureCollection = typeof(RealDotNetFixtureTests).GetCustomAttributesData()
            .SingleOrDefault(attribute => attribute.AttributeType == typeof(CollectionAttribute));
        Assert.NotNull(fixtureCollection);
        Assert.Equal(
            "Actual evidence .NET sequential fixture",
            fixtureCollection.ConstructorArguments.Single().Value);

        var definition = collectionType.GetCustomAttributesData()
            .SingleOrDefault(attribute => attribute.AttributeType == typeof(CollectionDefinitionAttribute));
        Assert.NotNull(definition);
        Assert.Contains(definition.NamedArguments, argument =>
            argument.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization) &&
            argument.TypedValue.Value is true);
    }
}
