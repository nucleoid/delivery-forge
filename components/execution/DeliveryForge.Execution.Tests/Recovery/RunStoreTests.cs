using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using DeliveryForge.Execution;

namespace DeliveryForge.Execution.Tests.Recovery;

public sealed class RunStoreTests
{
    [Fact]
    public async Task Recovers_latest_immutable_record_when_pointer_is_torn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var store = new RunStore(directory.Path);
        await store.AppendAsync("run-1", "prepared", new { value = 1 }, cancellationToken);
        var latest = await store.AppendAsync("run-1", "checkpoint", new { value = 2 }, cancellationToken);
        var pointerPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(latest.Path)!, "current.json");
        await File.WriteAllTextAsync(pointerPath, "{torn", cancellationToken);

        var recovered = await new RunStore(directory.Path).RecoverAsync("run-1", cancellationToken);

        Assert.Equal(latest.Sequence, recovered.Latest!.Sequence);
        Assert.True(recovered.PointerWasRecovered);
        Assert.Equal(["prepared", "checkpoint"], recovered.Records.Select(record => record.Kind));
        using var repairedPointer = JsonDocument.Parse(await File.ReadAllBytesAsync(
            pointerPath, cancellationToken));
        Assert.Equal(System.IO.Path.GetFileName(latest.Path), repairedPointer.RootElement.GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task Immutable_record_corruption_blocks_recovery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var store = new RunStore(directory.Path);
        var record = await store.AppendAsync("run-1", "prepared", new { value = 1 }, cancellationToken);
        await File.AppendAllTextAsync(record.Path, " ", cancellationToken);

        await Assert.ThrowsAsync<RunStoreCorruptionException>(() => store.RecoverAsync("run-1", cancellationToken));
    }

    [Fact]
    public async Task Missing_pointer_is_recovered_from_immutable_records()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var store = new RunStore(directory.Path);
        var record = await store.AppendAsync("run-1", "prepared", new { value = 1 }, cancellationToken);
        File.Delete(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(record.Path)!, "current.json"));

        var recovered = await store.RecoverAsync("run-1", cancellationToken);

        Assert.True(recovered.PointerWasRecovered);
        Assert.Equal(record.Sha256, recovered.Latest!.Sha256);
    }

    [Fact]
    public async Task Sequence_gap_blocks_recovery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var store = new RunStore(directory.Path);
        var first = await store.AppendAsync("run-1", "prepared", new { value = 1 }, cancellationToken);
        await store.AppendAsync("run-1", "accepted", new { value = 2 }, cancellationToken);
        File.Delete(first.Path);

        await Assert.ThrowsAsync<RunStoreCorruptionException>(() => store.RecoverAsync("run-1", cancellationToken));
    }

    [Fact]
    public async Task Run_namespaces_have_independent_sequences_and_pointers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var store = new RunStore(directory.Path);

        var one = await store.AppendAsync("run-1", "prepared", new { value = 1 }, cancellationToken);
        var two = await store.AppendAsync("run-2", "prepared", new { value = 2 }, cancellationToken);

        Assert.Equal(1, one.Sequence);
        Assert.Equal(1, two.Sequence);
        Assert.NotEqual(System.IO.Path.GetDirectoryName(one.Path), System.IO.Path.GetDirectoryName(two.Path));
    }

    [Fact]
    public async Task Filename_hash_cannot_hide_an_internal_run_or_sequence_mismatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var store = new RunStore(directory.Path);
        var record = await store.AppendAsync("run-1", "prepared", new { value = 1 }, cancellationToken);
        var text = await File.ReadAllTextAsync(record.Path, cancellationToken);
        var altered = Encoding.UTF8.GetBytes(text.Replace("\"runId\":\"run-1\"", "\"runId\":\"run-X\"", StringComparison.Ordinal));
        var hash = Convert.ToHexStringLower(SHA256.HashData(altered));
        var replacement = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(record.Path)!, $"{record.Sequence:D12}-sha256-{hash}.json");
        File.Delete(record.Path);
        await File.WriteAllBytesAsync(replacement, altered, cancellationToken);

        await Assert.ThrowsAsync<RunStoreCorruptionException>(() => store.RecoverAsync("run-1", cancellationToken));
    }

    [Fact]
    public async Task Concurrent_appends_are_serialized_without_duplicate_sequences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var store = new RunStore(directory.Path);

        await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(value => store.AppendAsync("run-1", "checkpoint", new { value }, cancellationToken)));
        var recovery = await store.RecoverAsync("run-1", cancellationToken);

        Assert.Equal(Enumerable.Range(1, 12).Select(value => (long)value), recovery.Records.Select(record => record.Sequence));
    }
}
