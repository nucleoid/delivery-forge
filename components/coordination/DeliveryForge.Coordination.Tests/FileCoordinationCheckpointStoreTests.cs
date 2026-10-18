using System.Text.Json;
using DeliveryForge.Coordination;

namespace DeliveryForge.Coordination.Tests;

public sealed class FileCoordinationCheckpointStoreTests
{
    static string Root() { var root = Path.Combine(Path.GetTempPath(), "delivery-forge-parent-store-" + Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root; }
    static CoordinationCheckpointRecord Record(CoordinationWriterClaim claim) => new(1, 1, 1, CoordinationIntentKind.Acquire, claim.WriterEpoch, 1, null, null);

    [Fact]
    public async Task Versioned_parent_binding_and_records_have_real_readback_exclusive_owner_and_no_restart_adoption()
    {
        var root = Root();
        try
        {
            await using (var store = new FileCoordinationCheckpointStore(root))
            {
                var binding = new ParentCheckpointBinding(1, "run-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("D"), "bundle", "contract", "fixture/repo", "#17", CoordinationResource.GitHubIssue("fixture", "fixture", "repo", 17));
                await store.BindParentAsync(binding, TestContext.Current.CancellationToken);
                using var physical = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "parent-run-binding-v1.json")));
                Assert.Equal(binding.ParentRunId, physical.RootElement.GetProperty("ParentRunId").GetString());
                Assert.Equal(binding.LeaseRunId, physical.RootElement.GetProperty("LeaseRunId").GetString());
                Assert.True(File.Exists(Path.Combine(root, "coordination-store-v1.json")));
                Assert.Throws<IOException>(() => new FileStream(Path.Combine(root, "parent-writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                var claim = await store.ClaimWriterAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken);
                var row = Record(claim);
                await store.AppendAsync(claim, 1, row, TestContext.Current.CancellationToken);
                Assert.Equal(row, Assert.Single(await store.ReadLifecycleAsync(claim.LifecycleId, TestContext.Current.CancellationToken)));
                Assert.Contains("-sha256-", Assert.Single(Directory.GetFiles(Path.Combine(root, claim.LifecycleId), "*-record.json")), StringComparison.Ordinal);
                await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.BindParentAsync(binding, TestContext.Current.CancellationToken));
            }
            Assert.Throws<CoordinationProtocolException>(() => new FileCoordinationCheckpointStore(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_or_truncated_original_record_refuses_physical_readback(bool truncated)
    {
        var root = Root();
        try
        {
            await using var store = new FileCoordinationCheckpointStore(root);
            var claim = await store.ClaimWriterAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken);
            var row = Record(claim);await store.AppendAsync(claim, 1, row, TestContext.Current.CancellationToken);
            var file = Assert.Single(Directory.GetFiles(Path.Combine(root, claim.LifecycleId), "*-record.json"));
            File.WriteAllText(file, truncated ? "{" : JsonSerializer.Serialize(row with { ProgressRevision = 2 }));
            await Assert.ThrowsAnyAsync<Exception>(() => store.ReadLifecycleAsync(claim.LifecycleId, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Foreign_epoch_and_sequence_refuse_before_record_creation_and_changed_manifest_cannot_be_adopted()
    {
        var root = Root();
        try
        {
            await using var store = new FileCoordinationCheckpointStore(root);
            var claim = await store.ClaimWriterAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<CoordinationCheckpointNotWrittenException>(() => store.AppendAsync(claim, 1, Record(claim) with { WriterEpoch = Guid.NewGuid().ToString("D") }, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<CoordinationCheckpointNotWrittenException>(() => store.AppendAsync(claim, 2, Record(claim) with { RecordSequence = 2 }, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, claim.LifecycleId), "*-record.json"));
            File.WriteAllText(Path.Combine(root, "coordination-store-v1.json"), "{}");
            await Assert.ThrowsAsync<CoordinationProtocolException>(() => store.ReadLifecycleAsync(claim.LifecycleId, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Genuine_flushed_append_cancelled_before_readback_reconciles_only_original_bytes_then_advances()
    {
        var root = Root();using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);var once = true;
        try
        {
            await using var store = new FileCoordinationCheckpointStore(root, path => { if (once && path.EndsWith("-record.json", StringComparison.Ordinal)) { once = false;cancellation.Cancel(); } }, null, null);
            var claim = await store.ClaimWriterAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken);
            var row = Record(claim);
            await Assert.ThrowsAsync<AggregateException>(() => store.AppendAsync(claim, 1, row, cancellation.Token));
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.AppendAsync(claim, 1, row, TestContext.Current.CancellationToken));
            Assert.Equal(row, Assert.Single(await store.ReadLifecycleAsync(claim.LifecycleId, TestContext.Current.CancellationToken)));
            await store.AppendAsync(claim, 2, row with { RecordSequence = 2 }, TestContext.Current.CancellationToken);
            Assert.Equal(2, (await store.ReadLifecycleAsync(claim.LifecycleId, TestContext.Current.CancellationToken)).Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Injected_first_close_refusal_retains_actual_child_and_exclusive_owner_without_repeat_success()
    {
        var root = Root();FileStream? child = null, exclusive = null;
        var store = new FileCoordinationCheckpointStore(root, null, stream => { child = stream;return ValueTask.FromException(new IOException("Explicit conformance first-close refusal; not an OS Dispose failure.")); }, stream => exclusive = stream);
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => store.ClaimWriterAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.DisposeAsync().AsTask());
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(root, "parent-writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            Assert.NotNull(child);Assert.False(child.SafeFileHandle.IsClosed);
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.DisposeAsync().AsTask());
        }
        finally
        {
            // Explicit teardown of these captured ORIGINAL fixture resources; never successful store disposition.
            if (child is not null) await child.DisposeAsync();if (exclusive is not null) await exclusive.DisposeAsync();Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task Visible_record_without_original_durable_flush_cannot_reconcile_or_release_writer()
    {
        var root=Root();FileStream? exclusive=null;
        var store=new FileCoordinationCheckpointStore(root,null,null,stream=>exclusive=stream,
            path=>{if(path.EndsWith("-record.json",StringComparison.Ordinal))throw new IOException("Explicit conformance interruption before original durable flush; not an OS Flush failure.");});
        try {
            var claim=await store.ClaimWriterAsync(Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),null,TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<AggregateException>(()=>store.AppendAsync(claim,1,Record(claim),TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(Path.Combine(root,claim.LifecycleId),"*-record.json"));
            await Assert.ThrowsAsync<CoordinationUncertainException>(()=>store.ReadLifecycleAsync(claim.LifecycleId,TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<CoordinationUncertainException>(()=>store.DisposeAsync().AsTask());
            Assert.Throws<IOException>(()=>new FileStream(Path.Combine(root,"parent-writer.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None));
        } finally {if(exclusive is not null)await exclusive.DisposeAsync();Directory.Delete(root,recursive:true);}
    }
    [Fact]
    public async Task Constructor_callback_failure_retains_the_actual_returned_original_writer()
    {
        var root=Root();FileStream? exclusive=null;
        try {
            Assert.Throws<IOException>(()=>new FileCoordinationCheckpointStore(root,null,null,stream=>{exclusive=stream;throw new IOException("Explicit post-acquisition fixture callback failure.");}));
            Assert.NotNull(exclusive);Assert.True(FileCoordinationCheckpointStore.RetainsOriginalWriterForContractTests(exclusive));
            Assert.Throws<IOException>(()=>new FileStream(Path.Combine(root,"parent-writer.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None));
        } finally {if(exclusive is not null)await exclusive.DisposeAsync();Directory.Delete(root,recursive:true);}
    }

}
