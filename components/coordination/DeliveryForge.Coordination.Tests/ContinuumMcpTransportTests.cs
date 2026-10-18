using System.Net;
using System.Text;
using System.Text.Json;
using DeliveryForge.Coordination;
using DeliveryForge.Coordination.LiveControls;

namespace DeliveryForge.Coordination.Tests;

public sealed class ContinuumMcpTransportTests
{
    [Fact]
    public async Task Authenticated_protocol_projects_explicit_scope_and_exact_grant_identity()
    {
        await using var sample = new Sample();
        var capabilities = await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.True(capabilities.Fixture);
        Assert.Contains(CoordinationOperation.Release, capabilities.Operations);
        Assert.Equal(1, sample.Handler.ScopeProbes);
        var grant = await sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(TransportOutcome.Acquired, grant.Outcome);
        Assert.Equal("42", grant.FencingToken);
        Assert.Equal("project:delivery-forge-uat", sample.Handler.LastScope);
        Assert.Equal(sample.Owner.RunId, sample.Handler.LastRun);
        Assert.Equal(1, sample.Handler.Acquisitions);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("resource")]
    [InlineData("run_id")]
    [InlineData("fencing_token")]
    public async Task Malformed_or_foreign_acquire_echo_invalidates_without_replay(string field)
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        sample.Handler.CorruptField = field;
        await Assert.ThrowsAsync<CoordinationUncertainException>(() =>
            sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoordinationUncertainException>(() =>
            sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, sample.Handler.Acquisitions);
    }

    [Fact]
    public async Task Other_run_of_same_principal_is_masked_before_projection()
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        sample.Handler.InspectionRun = Guid.NewGuid().ToString("D");
        var inspection = await sample.Transport.InspectAsync(new(sample.Resource, sample.Owner,
            Guid.NewGuid().ToString("D")), TestContext.Current.CancellationToken);
        Assert.True(inspection.Held);
        Assert.False(inspection.CallerOwnsLease);
        Assert.Null(inspection.LeaseId);
        Assert.Null(inspection.FencingToken);
    }

    [Fact]
    public async Task Foreign_scope_and_owner_refuse_before_authentication_or_http()
    {
        await using var sample = new Sample();
        await Assert.ThrowsAsync<CoordinationProtocolException>(() => sample.Transport.AcquireAsync(
            sample.Request() with { Resource = CoordinationResource.GitHubIssue("other", "synthetic", "fixture", 1) }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoordinationProtocolException>(() => sample.Transport.AcquireAsync(
            sample.Request() with { Owner = new LeaseOwner(Guid.NewGuid().ToString("D"), sample.Owner.RunId) }, TestContext.Current.CancellationToken));
        Assert.Equal(0, sample.Handler.Requests);
    }

    [Fact]
    public async Task First_remote_disposal_failure_is_retained_and_never_becomes_repeat_success()
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        sample.Handler.FailDelete = true;
        sample.ExpectedDisposeFailure = true;
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.DisposeAsync().AsTask());
        Assert.True(sample.Handler.Disposed);
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.DisposeAsync().AsTask());
        Assert.Equal(1, sample.Handler.Deletions);
    }

    [Fact]
    public async Task Cancellation_revokes_actual_adapter_dispatch_and_dry_run_publication_without_resending()
    {
        await using var sample = new Sample();
        var clock = new Clock();
        var adapter = await ContinuumCoordinationAdapter.CreateForContractTestsAsync(sample.Transport,
            new Quiescence(clock), TestContext.Current.CancellationToken, clock, TimeSpan.FromSeconds(2),
            new Checkpoints(), TimeSpan.FromMilliseconds(10));
        var acquired = await adapter.AcquireAllAsync([sample.Resource], sample.Owner,
            ContinuumCoordinationAdapter.DefaultTtl, TestContext.Current.CancellationToken);
        var authority = Assert.IsType<DispatchAuthority>(acquired.Authority);
        var sink = new DryRunSink(adapter);
        Assert.True(sink.TryPublish(authority));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        var renewal = await adapter.RenewAllAsync(authority, cancelled.Token);
        Assert.Null(renewal.Authority);
        Assert.False(adapter.MayDispatch(authority));
        Assert.False(sink.TryPublish(authority));
        Assert.Equal(1, sink.Accepted);
        Assert.Equal(0, sample.Handler.Renewals);
        Assert.Equal(1, sample.Handler.Acquisitions);
    }

    [Fact]
    public async Task Fixture_http_handler_cannot_construct_production_adapter()
    {
        await using var sample = new Sample();
        var clock = new Clock();
        await Assert.ThrowsAsync<CoordinationUnavailableException>(() => ContinuumCoordinationAdapter.CreateProductionAsync(
            sample.Transport, new(new Quiescence(clock), clock, new Checkpoints(), TimeSpan.Zero, TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Projection_retains_the_operation_gate_until_foreign_echo_invalidates()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var sample = new Sample(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic projection barrier expired.");
        });
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        sample.Handler.CorruptField = "scope";
        var first = Task.Run(() => sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken));
            Assert.Equal(1, sample.Handler.Acquisitions);
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => first);
    }

    [Fact]
    public async Task Positive_renewal_and_release_bind_real_wire_identity_and_ttl()
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        var grant = await sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken);
        var lease = new LeaseEvidence(sample.Resource, sample.Owner, grant.LeaseId!, grant.FencingToken!,
            grant.ServerTime!.Value, grant.ExpiresAt!.Value, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(100), Guid.NewGuid().ToString("D"));
        var renewed = await sample.Transport.RenewAsync(new(lease, Guid.NewGuid().ToString("D")), TestContext.Current.CancellationToken);
        Assert.Equal(TransportOutcome.Renewed, renewed.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(300), renewed.ExpiresAt - renewed.ServerTime);
        var released = await sample.Transport.ReleaseAsync(new(lease.LeaseId, sample.Owner, lease.FencingToken,
            Guid.NewGuid().ToString("D")), TestContext.Current.CancellationToken);
        Assert.Equal(TransportOutcome.Released, released.Outcome);
        Assert.Equal(1, sample.Handler.Renewals);
        Assert.Equal(1, sample.Handler.Releases);
    }

    [Fact]
    public async Task Streamable_http_sse_reply_is_parsed_as_one_bounded_correlated_message()
    {
        await using var sample = new Sample();
        sample.Handler.UseSse = true;
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TransportOutcome.Acquired, (await sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task Rotated_key_cannot_initialize_as_attested_principal_or_delete_old_session()
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        sample.RotateSyntheticCredential();
        sample.ExpectedDisposeFailure = true;
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(0, sample.Handler.Acquisitions);
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.DisposeAsync().AsTask());
        Assert.Equal(0, sample.Handler.Deletions);
        Assert.True(sample.Handler.Disposed);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(1)]
    public async Task Stale_or_future_operator_evidence_refuses_before_http(int minutes)
    {
        await using var sample = new Sample(evidenceOffsetMinutes: minutes);
        await Assert.ThrowsAsync<CoordinationUnavailableException>(() => sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, sample.Handler.Requests);
    }

    [Fact]
    public async Task Remote_delete_and_local_handler_failures_both_attempt_once_and_remain_nonpass()
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        sample.Handler.FailDelete = true;
        sample.Handler.FailDispose = true;
        sample.ExpectedDisposeFailure = true;
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.DisposeAsync().AsTask());
        Assert.Equal(1, sample.Handler.Deletions);
        Assert.Equal(1, sample.Handler.Dispositions);
        var retained = sample.Transport.FirstDispositionFailuresForContractTests;
        Assert.NotNull(retained.RemoteSession);
        Assert.IsType<IOException>(retained.Handler);
        Assert.NotSame(retained.RemoteSession, retained.Handler);
        Assert.Same(retained.RemoteSession, sample.Transport.FirstDispositionFailuresForContractTests.RemoteSession);
        Assert.Same(retained.Handler, sample.Transport.FirstDispositionFailuresForContractTests.Handler);
    }

    [Fact]
    public async Task Initially_wrong_key_cannot_claim_attested_principal_before_any_http()
    {
        await using var sample = new Sample();
        sample.RotateSyntheticCredential();
        await Assert.ThrowsAsync<CoordinationUnavailableException>(() => sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, sample.Handler.Requests);
    }

    [Fact]
    public async Task Flushed_append_with_cancelled_readback_reconciles_original_bytes_before_next_append()
    {
        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelOnce = true;
        var store = new LiveCheckpointStore(root, beforeReadbackForContractTests: path =>
        {
            if (cancelOnce && path.EndsWith("-record.json", StringComparison.Ordinal)) { cancelOnce = false; cancelled.Cancel(); }
        });
        try
        {
            var lifecycle = Guid.NewGuid().ToString("D");
            var claim = await store.ClaimWriterAsync(lifecycle, Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken);
            var first = new CoordinationCheckpointRecord(1, 1, 1, CoordinationIntentKind.Acquire, claim.WriterEpoch, 1, null, null);
            await Assert.ThrowsAsync<AggregateException>(() => store.AppendAsync(claim, 1, first, cancelled.Token));
            Assert.True(File.Exists(Path.Combine(root, lifecycle, "000000000001-record.json")));
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.AppendAsync(claim, 1, first, TestContext.Current.CancellationToken));
            var rows = await store.ReadLifecycleAsync(lifecycle, TestContext.Current.CancellationToken);
            Assert.Equal(first, Assert.Single(rows));
            await store.AppendAsync(claim, 2, first with { RecordSequence = 2 }, TestContext.Current.CancellationToken);
            Assert.Equal(2, (await store.ReadLifecycleAsync(lifecycle, TestContext.Current.CancellationToken)).Count);
        }
        finally { await store.DisposeAsync(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Failed_child_first_close_retains_actual_exclusive_writer_and_repeat_unknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FileStream? actualExclusive = null;
        FileStream? actualChild = null;
        var store = new LiveCheckpointStore(root,
            firstCloseForContractTests: stream => { actualChild = stream; return ValueTask.FromException(new IOException("Synthetic first-close refusal; actual child retained.")); },
            exclusiveCreatedForContractTests: stream => actualExclusive = stream);
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => store.ClaimWriterAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.DisposeAsync().AsTask());
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(root, "parent-writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.DisposeAsync().AsTask());
            Assert.NotNull(actualChild);
            Assert.False(actualChild.SafeFileHandle.IsClosed);
        }
        finally
        {
            // Explicit fixture teardown of the retained original handles; never a successful store disposition.
            if (actualChild is not null) await actualChild.DisposeAsync();
            if (actualExclusive is not null) await actualExclusive.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Sample : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "delivery-forge-continuum-" + Guid.NewGuid().ToString("N"));
        internal WireHandler Handler { get; } = new();
        internal LeaseOwner Owner { get; } = new("a714fc76-487f-4d64-84d9-4ce8409e62e1", Guid.NewGuid().ToString("D"));
        internal CoordinationResource Resource { get; } = CoordinationResource.GitHubIssue("delivery-forge-uat", "synthetic", "protocol-control", 1);
        internal ContinuumMcpTransport Transport { get; }
        internal bool ExpectedDisposeFailure { get; set; }

        internal Sample(Action? projection = null, int evidenceOffsetMinutes = 0)
        {
            Directory.CreateDirectory(_root);
            var credential = Path.Combine(_root, "synthetic.key");
            File.WriteAllText(credential, "synthetic-test-value-not-a-real-service-key", new UTF8Encoding(false));
            Transport = new(new Uri("https://continuum.invalid/mcp"), "delivery-forge-uat", Owner.PrincipalId,
                new ContinuumApiKeyFile(credential), new Deployment(Owner.PrincipalId, evidenceOffsetMinutes), TimeSpan.FromSeconds(10), Handler,
                beforeProjectionForContractTests: projection);
        }

        internal void RotateSyntheticCredential() => File.WriteAllText(Path.Combine(_root, "synthetic.key"), "synthetic-rotated-value-not-a-real-key", new UTF8Encoding(false));

        internal AcquireRequest Request() => new(Resource, Owner, TimeSpan.FromSeconds(300), Guid.NewGuid().ToString("D"));

        public async ValueTask DisposeAsync()
        {
            try { await Transport.DisposeAsync(); }
            catch (CoordinationUncertainException) when (ExpectedDisposeFailure && Handler.Disposed) { }
            // This exact owned leaf contains only an inert test key; all HTTP work has returned.
            Assert.True(Handler.Disposed);
            File.Delete(Path.Combine(_root, "synthetic.key"));
            Directory.Delete(_root, recursive: false);
        }
    }

    private sealed class Deployment(string principal, int offsetMinutes) : IContinuumDeploymentEvidenceSource
    {
        public Task<ContinuumDeploymentEvidence> ObserveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow.AddMinutes(offsetMinutes);
            return Task.FromResult(new ContinuumDeploymentEvidence(new("https://continuum.invalid/mcp"), "delivery-forge-uat", principal,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-test-value-not-a-real-service-key"))).ToLowerInvariant(),
                "synthetic-test-operator", new('a', 40), new('b', 64), new('c', 64), new('d', 64), now, now.AddMinutes(10)));
        }
    }

    private sealed class WireHandler : HttpMessageHandler
    {
        internal int Requests, Acquisitions, Renewals, Releases, Deletions, ScopeProbes, Dispositions;
        internal string? LastScope, LastRun, CorruptField, InspectionRun;
        internal bool FailDelete, Disposed, FailDispose, UseSse;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("ApiKey", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Delete)
            {
                Deletions++;
                return new(FailDelete ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent) { RequestMessage = request };
            }
            using var input = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var root = input.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted) { RequestMessage = request };
            object result;
            if (method == "initialize")
                result = new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "continuum", version = "0.1.0" } };
            else if (method == "tools/list")
                result = new { tools = new[] { Tool("acquire", ["scope", "resource", "run_id", "request_id"]), Tool("renew", ["lease_id", "run_id", "request_id"]), Tool("release", ["lease_id", "run_id", "request_id"]), Tool("inspect", ["scope", "resource"]) } };
            else
            {
                var parameters = root.GetProperty("params");
                var name = parameters.GetProperty("name").GetString();
                var args = parameters.GetProperty("arguments");
                var server = "2026-10-18T00:00:00.000000Z";
                var expiry = "2026-10-18T00:05:00.000000Z";
                Dictionary<string, object?> payload;
                if (name == "continuum.lock_inspect")
                {
                    var resource = args.GetProperty("resource").GetString()!;
                    var probe = resource.Contains("/capability-", StringComparison.Ordinal);
                    if (probe) ScopeProbes++;
                    payload = new() { ["held"] = !probe && InspectionRun is not null, ["scope"] = args.GetProperty("scope").GetString(), ["resource"] = resource, ["server_time"] = server };
                    if (!probe && InspectionRun is not null)
                    {
                        payload["expires_at"] = expiry; payload["lease_id"] = "42ec0ed3-1c10-4d7e-8dc1-52c1f1b912ab";
                        payload["run_id"] = InspectionRun; payload["fencing_token"] = "42";
                    }
                }
                else if (name == "continuum.lock_acquire")
                {
                    Acquisitions++; LastScope = args.GetProperty("scope").GetString(); LastRun = args.GetProperty("run_id").GetString();
                    payload = new() { ["acquired"] = true, ["scope"] = LastScope, ["resource"] = args.GetProperty("resource").GetString(), ["run_id"] = LastRun,
                        ["lease_id"] = "42ec0ed3-1c10-4d7e-8dc1-52c1f1b912ab", ["fencing_token"] = "42", ["server_time"] = server, ["expires_at"] = expiry };
                    if (CorruptField is not null) payload[CorruptField] = CorruptField == "fencing_token" ? "042" : "foreign";
                }
                else if (name == "continuum.lock_renew")
                {
                    Renewals++;
                    Assert.Equal(300, args.GetProperty("ttl_seconds").GetInt32());
                    payload = new() { ["renewed"] = true, ["lease_id"] = args.GetProperty("lease_id").GetString(), ["run_id"] = args.GetProperty("run_id").GetString(),
                        ["fencing_token"] = "42", ["server_time"] = "2026-10-18T00:00:01.000000Z", ["expires_at"] = "2026-10-18T00:05:01.000000Z" };
                }
                else if (name == "continuum.lock_release") { Releases++; Assert.False(args.TryGetProperty("fencing_token", out _)); payload = new() { ["released"] = true }; }
                else throw new InvalidOperationException("Unexpected synthetic request.");
                result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload) } } };
            }
            var rpc = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetString(), result });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(UseSse ? "event: message\ndata: " + rpc + "\n\n" : rpc, Encoding.UTF8, UseSse ? "text/event-stream" : "application/json"),
            };
            if (method == "initialize") response.Headers.Add("Mcp-Session-Id", "synthetic-session");
            return response;
        }

        private static object Tool(string operation, string[] required) => new
        {
            name = "continuum.lock_" + operation,
            inputSchema = new { type = "object", required, properties = required.ToDictionary(name => name, _ => new { type = "string" }) },
        };

        protected override void Dispose(bool disposing) { Dispositions++; Disposed = true; if (FailDispose) throw new IOException("Synthetic handler first-close failure."); base.Dispose(disposing); }
    }

    private sealed class Clock : IMonotonicClock
    {
        private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
        public string DomainId { get; } = Guid.NewGuid().ToString("D");
        public TimeSpan Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(_start);
    }

    private sealed class Quiescence(Clock clock) : IOwnedRunQuiescenceSource
    {
        public QuiescenceSourceBinding Binding { get; } = new("synthetic-issuer", "synthetic-empty-owned-set", clock.DomainId, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), true);
        public Task<QuiescenceEvidence> ObserveAsync(LeaseOwner owner, CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuiescenceEvidence(owner, Binding.IssuerId, Binding.SourceId, clock.DomainId, clock.Elapsed, Guid.NewGuid().ToString("D"), true));
    }

    private sealed class Checkpoints : ICoordinationCheckpointStore
    {
        private readonly Dictionary<string, List<CoordinationCheckpointRecord>> _rows = [];
        public Task<CoordinationWriterClaim> ClaimWriterAsync(string lifecycleId, string proposedWriterEpoch, string? trustedTakeoverProof, CancellationToken cancellationToken = default)
        {
            _rows.Add(lifecycleId, []);
            return Task.FromResult(new CoordinationWriterClaim(lifecycleId, proposedWriterEpoch, 1));
        }
        public Task AppendAsync(CoordinationWriterClaim claim, long expectedRecordSequence, CoordinationCheckpointRecord record, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_rows[claim.LifecycleId].Count + 1, expectedRecordSequence);
            _rows[claim.LifecycleId].Add(record);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<CoordinationCheckpointRecord>> ReadLifecycleAsync(string lifecycleId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoordinationCheckpointRecord>>(_rows[lifecycleId].ToArray());
    }

    private sealed class DryRunSink(ContinuumCoordinationAdapter adapter)
    {
        internal int Accepted { get; private set; }
        internal bool TryPublish(DispatchAuthority authority)
        {
            if (!adapter.MayDispatch(authority)) return false;
            Accepted++;
            return true;
        }
    }
}
