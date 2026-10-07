using System.Text.Json;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Contracts.Tests;

public sealed class AppendOnlyContractStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"delivery-forge-contract-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Writes_canonical_immutable_record_and_atomic_current_pointer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new AppendOnlyContractStore(_root);
        var source = await File.ReadAllBytesAsync(FixturePath("run-manifest.json"), cancellationToken);
        var identity = ContractValidator.ParseAndValidate(source).Identity;

        var path = await store.WriteImmutableAsync(source, cancellationToken);
        var secondPath = await store.WriteImmutableAsync(source, cancellationToken);
        await store.UpdateCurrentPointerAsync(identity, cancellationToken);

        Assert.Equal(path, secondPath);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain(':', Path.GetFileName(path));
        Assert.Equal($"sha256-{identity[7..]}.json", Path.GetFileName(path));
        using var pointer = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(_root, "manifest-current.json"), cancellationToken));
        Assert.Equal(identity, pointer.RootElement.GetProperty("identity").GetString());
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Current_pointer_rejects_non_manifest_records()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new AppendOnlyContractStore(_root);
        var source = await File.ReadAllBytesAsync(FixturePath("plan.json"), cancellationToken);
        var identity = ContractValidator.ParseAndValidate(source).Identity;
        await store.WriteImmutableAsync(source, cancellationToken);

        await Assert.ThrowsAsync<AppendOnlyContractException>(() =>
            store.UpdateCurrentPointerAsync(identity, cancellationToken));
    }

    [Fact]
    public async Task Pointer_cannot_reference_missing_immutable_record()
    {
        var store = new AppendOnlyContractStore(_root);
        await Assert.ThrowsAsync<AppendOnlyContractException>(() =>
            store.UpdateCurrentPointerAsync("sha256:" + new string('a', 64), TestContext.Current.CancellationToken));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static string FixturePath(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Valid", file);
}
