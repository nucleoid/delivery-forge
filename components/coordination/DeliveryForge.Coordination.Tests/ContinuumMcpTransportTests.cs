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

    [Theory]
    [InlineData("priming")]
    [InlineData("progress")]
    [InlineData("logging")]
    [InlineData("multiline")]
    [InlineData("chunks")]
    [InlineData("event-limit")]
    public async Task Same_sse_body_accepts_bounded_priming_or_inert_notifications_without_another_request(string framing)
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        var before = sample.Handler.Requests;
        sample.Handler.UseSse = true;
        sample.Handler.SsePrefix = framing switch
        {
            "priming" => ": keepalive\nid: event-1\nretry: 1000\ndata:\n\n",
            "progress" => "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progressToken\":\"data-only\",\"progress\":1,\"total\":2}}\n\n",
            "logging" => "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{\"level\":\"info\",\"data\":{\"fixture\":true}}}\n\n",
            "chunks" => ": keepalive\r\nid: event-1\r\nretry: 1000\r\ndata:\r\n\r\n",
            "event-limit" => string.Concat(Enumerable.Repeat("id: priming\ndata:\n\n", 31)),
            _ => ""
        };
        sample.Handler.MultilineSse = framing == "multiline";
        sample.Handler.ChunkedSse = framing == "chunks";
        var grant = await sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(TransportOutcome.Acquired, grant.Outcome);
        Assert.Equal(before + 1, sample.Handler.Requests); Assert.Equal(1, sample.Handler.Acquisitions);
    }

    [Theory]
    [InlineData("\r", 1)]
    [InlineData("\n", 1)]
    [InlineData("\r\n", 1)]
    [InlineData("\r\n", 3)]
    [InlineData("mixed", 1)]
    [InlineData("mixed", 4)]
    public async Task Sse_line_endings_and_fragmented_CRLF_preserve_one_correlated_response(string ending, int chunkBytes)
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        var before = sample.Handler.Requests;
        sample.Handler.UseSse = sample.Handler.MultilineSse = sample.Handler.ChunkedSse = true;
        sample.Handler.SseLineEnding = ending; sample.Handler.SseChunkBytes = chunkBytes;
        sample.Handler.SsePrefix = ": keepalive\nid: priming\nretry: 1000\ndata:\n\n" +
            "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{\"level\":\"info\",\"data\":\"café\"}}\n\n";
        var grant = await sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(TransportOutcome.Acquired, grant.Outcome);
        Assert.Equal(before + 1, sample.Handler.Requests); Assert.Equal(1, sample.Handler.Acquisitions);
    }

    [Theory]
    [InlineData("\r", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r", true)]
    public async Task Sse_line_framing_still_requires_a_complete_event_and_strict_UTF8(string ending, bool malformedUtf8)
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        sample.Handler.UseSse = sample.Handler.ChunkedSse = true;
        sample.Handler.SseLineEnding = ending; sample.Handler.SseChunkBytes = 1;
        sample.Handler.OmitSseFinalBlankLine = !malformedUtf8; sample.Handler.MalformedSseUtf8 = malformedUtf8;
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, sample.Handler.Acquisitions); // No reconnect or second send after invalidation.
    }

    [Theory]
    [InlineData("schema-change")]
    [InlineData("cancelled")]
    [InlineData("resource-notification")]
    [InlineData("events-cr")]
    [InlineData("events-crlf")]
    [InlineData("server-request")]
    [InlineData("foreign-response")]
    [InlineData("malformed-notification")]
    [InlineData("events")]
    [InlineData("bytes")]
    public async Task Unsupported_sse_messages_or_extent_refuse_without_reconnect_or_replay(string framing)
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        sample.Handler.UseSse = true;
        sample.Handler.SsePrefix = framing switch
        {
            "schema-change" => "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\",\"params\":{}}\n\n",
            "cancelled" => "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":\"unknown\",\"reason\":\"cancelled\"}}\n\n",
            "resource-notification" => "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/resources/updated\",\"params\":{\"uri\":\"fixture:resource\"}}\n\n",
            "events-cr" or "events-crlf" => string.Concat(Enumerable.Repeat("id: priming\ndata:\n\n", 32)),
            "server-request" => "data: {\"jsonrpc\":\"2.0\",\"id\":\"server-request\",\"method\":\"ping\",\"params\":{}}\n\n",
            "foreign-response" => "data: {\"jsonrpc\":\"2.0\",\"id\":\"foreign\",\"result\":{}}\n\n",
            "malformed-notification" => "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}\n\n",
            "events" => string.Concat(Enumerable.Repeat("id: priming\ndata:\n\n", 32)),
            _ => ":" + new string('x', 1048576) + "\n\n"
        };
        if (framing is "events-cr" or "events-crlf")
        {
            sample.Handler.SseLineEnding = framing == "events-cr" ? "\r" : "\r\n";
            sample.Handler.ChunkedSse = true; sample.Handler.SseChunkBytes = 1;
        }
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(1, sample.Handler.Acquisitions); // Second explicit fixture call is refused before send.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_anchored_evidence_restricts_same_original_whole_interval_before_any_lease(bool fits)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await using var sample = new Sample();
        var capabilities = await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        var before = sample.Handler.Requests;
        if (fits) sample.Transport.RequireOriginalEvidenceCoverage(started, TimeSpan.FromMinutes(5));
        else Assert.Throws<CoordinationUnavailableException>(() => sample.Transport.RequireOriginalEvidenceCoverage(started, TimeSpan.FromMinutes(20)));
        Assert.Equal(before, sample.Handler.Requests); Assert.Equal(0, sample.Handler.Acquisitions);
        Assert.True(capabilities.Fixture); Assert.False(sample.Transport.ProductionCurrent);
    }

    [Fact]
    public async Task Fixed_evidence_endpoint_survives_handoff_and_exact_boundary_checks_without_resampling()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        var requests = sample.Handler.Requests;
        var clipped = sample.Transport.GetOriginalEvidenceWholeLimit(started, TimeSpan.FromMinutes(20));
        Assert.True(clipped > TimeSpan.Zero && clipped < TimeSpan.FromMinutes(11));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        for (var index = 0; index < 100; index++)
        {
            Assert.Equal(clipped, sample.Transport.GetOriginalEvidenceWholeLimit(started, TimeSpan.FromMinutes(20)));
            sample.Transport.RequireOriginalEvidenceCoverage(started, clipped);
            await Task.Yield();
        }
        Assert.Throws<CoordinationUnavailableException>(() => sample.Transport.RequireOriginalEvidenceCoverage(started, clipped + TimeSpan.FromTicks(1)));
        Assert.Equal(requests, sample.Handler.Requests); Assert.Equal(0, sample.Handler.Acquisitions);
        Assert.False(sample.Transport.ProductionCurrent); // Marked HTTP contract fixture, never authority.
    }

    [Fact]
    public async Task Anchored_limit_never_expands_a_shorter_original_and_missing_discovery_is_not_a_limit()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await using var sample = new Sample();
        Assert.Throws<CoordinationUnavailableException>(() => sample.Transport.GetOriginalEvidenceWholeLimit(started, TimeSpan.FromSeconds(30)));
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(30), sample.Transport.GetOriginalEvidenceWholeLimit(started, TimeSpan.FromSeconds(30)));
        sample.Transport.RequireOriginalEvidenceCoverage(started, TimeSpan.FromSeconds(30));
        Assert.Equal(0, sample.Handler.Acquisitions);
    }

    [Fact]
    public async Task Renew_invocation_uses_same_reserved_row_from_pending_to_actual_return()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var armed = false;
        await using var sample = new Sample(() =>
        {
            if (!armed) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic projection barrier expired.");
        });
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        var grant = await sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken);
        var lease = new LeaseEvidence(sample.Resource, sample.Owner, grant.LeaseId!, grant.FencingToken!,
            grant.ServerTime!.Value, grant.ExpiresAt!.Value, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(100), Guid.NewGuid().ToString("D"));
        armed = true; var request = Guid.NewGuid().ToString("D");
        var actual = Task.Run(() => sample.Transport.RenewAsync(new(lease, request), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var pending = Assert.Single(sample.Transport.ReadActualRenewalData());
            Assert.Equal(request, pending.RequestId); Assert.Equal("Pending", pending.InvocationState);
            Assert.Equal(TransportOutcome.Uncertain, pending.Outcome);
        }
        finally { release.Set(); }
        Assert.Equal(TransportOutcome.Renewed, (await actual).Outcome);
        var returned = Assert.Single(sample.Transport.ReadActualRenewalData());
        Assert.Equal(request, returned.RequestId); Assert.Equal("Returned", returned.InvocationState);
        Assert.Equal(TransportOutcome.Renewed, returned.Outcome); Assert.Equal(1, sample.Handler.Renewals);
    }

    [Fact]
    public async Task Renew_invocation_data_retains_uncertain_attempts_including_a_refusal_before_http()
    {
        await using var sample = new Sample();
        await sample.Transport.DetectCapabilitiesAsync(TestContext.Current.CancellationToken);
        var grant = await sample.Transport.AcquireAsync(sample.Request(), TestContext.Current.CancellationToken);
        var lease = new LeaseEvidence(sample.Resource, sample.Owner, grant.LeaseId!, grant.FencingToken!,
            grant.ServerTime!.Value, grant.ExpiresAt!.Value, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(100), Guid.NewGuid().ToString("D"));
        sample.Handler.CorruptRenewal = true;
        var first = Guid.NewGuid().ToString("D"); var second = Guid.NewGuid().ToString("D");
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.RenewAsync(new(lease, first), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => sample.Transport.RenewAsync(new(lease, second), TestContext.Current.CancellationToken));
        var rows = sample.Transport.ReadActualRenewalData(); Assert.Equal(2, rows.Count);
        Assert.Equal(first, rows[0].RequestId); Assert.Equal(second, rows[1].RequestId);
        Assert.All(rows, row => { Assert.Equal("Uncertain", row.InvocationState); Assert.Equal(TransportOutcome.Uncertain, row.Outcome); Assert.Equal(lease.LeaseId, row.LeaseId); });
        Assert.Equal(1, sample.Handler.Renewals); // Invocation is explicitly not proof of send.
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
        string? flushedPath = null;
        var store = new FileCoordinationCheckpointStore(root, beforeReadbackForContractTests: path =>
        {
            if (cancelOnce && path.EndsWith("-record.json", StringComparison.Ordinal)) { flushedPath = path; cancelOnce = false; cancelled.Cancel(); }
        }, firstCloseForContractTests: null, exclusiveCreatedForContractTests: null);
        try
        {
            var lifecycle = Guid.NewGuid().ToString("D");
            var claim = await store.ClaimWriterAsync(lifecycle, Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken);
            var first = new CoordinationCheckpointRecord(1, 1, 1, CoordinationIntentKind.Acquire, claim.WriterEpoch, 1, null, null);
            await Assert.ThrowsAsync<AggregateException>(() => store.AppendAsync(claim, 1, first, cancelled.Token));
            Assert.NotNull(flushedPath);
            var originalBytes = JsonSerializer.SerializeToUtf8Bytes(first);
            var expectedName = $"000000000001-sha256-{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(originalBytes))}-record.json";
            Assert.Equal(Path.Combine(root, lifecycle, expectedName), flushedPath);
            Assert.True(File.Exists(flushedPath));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(flushedPath, TestContext.Current.CancellationToken));
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
        var store = new FileCoordinationCheckpointStore(root, beforeReadbackForContractTests: null,
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

    [Fact]
    public async Task Live_checkpoint_composition_refuses_visible_record_without_original_durable_flush()
    {
        var compositionRoot = Path.Combine(Path.GetTempPath(), "delivery-forge-live-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(compositionRoot);
        // This is the exact construction method called by LiveProgram, not a supplied factory/type label.
        var composed = LiveControlHost.CreateCheckpointStore(compositionRoot);
        try { Assert.IsType<FileCoordinationCheckpointStore>(composed); }
        finally { await composed.DisposeAsync(); Directory.Delete(compositionRoot, recursive: true); }

        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-live-durability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FileStream? originalExclusive = null;
        var interruption = new IOException("Explicit conformance interruption before original durable flush; not an OS Flush failure.");
        // The same concrete production store, using its existing friend-only fault seam.
        var store = new FileCoordinationCheckpointStore(root, null, null, stream => originalExclusive = stream,
            path => { if (path.EndsWith("-record.json", StringComparison.Ordinal)) throw interruption; });
        Assert.Equal(composed.GetType(), store.GetType());
        try
        {
            var claim = await store.ClaimWriterAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), null, TestContext.Current.CancellationToken);
            var record = new CoordinationCheckpointRecord(1, 1, 1, CoordinationIntentKind.Acquire, claim.WriterEpoch, 1, null, null);
            var failure = await Assert.ThrowsAsync<AggregateException>(() => store.AppendAsync(claim, 1, record, TestContext.Current.CancellationToken));
            Assert.Contains(interruption, failure.InnerExceptions);
            var recordPath = Assert.Single(Directory.GetFiles(Path.Combine(root, claim.LifecycleId), "*-record.json"));
            Assert.InRange(new FileInfo(recordPath).Length, 1, 65536);
            Assert.Equal(record, JsonSerializer.Deserialize<CoordinationCheckpointRecord>(
                await File.ReadAllBytesAsync(recordPath, TestContext.Current.CancellationToken)));
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.ReadLifecycleAsync(claim.LifecycleId, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.AppendAsync(claim, 1, record, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<CoordinationUncertainException>(() => store.DisposeAsync().AsTask());
            Assert.NotNull(originalExclusive);
            Assert.False(originalExclusive.SafeFileHandle.IsClosed);
            Assert.True(FileCoordinationCheckpointStore.RetainsOriginalWriterForContractTests(originalExclusive));
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(root, "parent-writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        finally
        {
            // Explicit teardown of the captured original fixture handle; never store cleanup success.
            if (originalExclusive is not null) await originalExclusive.DisposeAsync();
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
        internal bool FailDelete, Disposed, FailDispose, UseSse, MultilineSse, ChunkedSse, CorruptRenewal;
        internal string SsePrefix = "";
        internal string? SseLineEnding;
        internal int SseChunkBytes = 3;
        internal bool OmitSseFinalBlankLine, MalformedSseUtf8;

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
                    payload = new()
                    {
                        ["acquired"] = true,
                        ["scope"] = LastScope,
                        ["resource"] = args.GetProperty("resource").GetString(),
                        ["run_id"] = LastRun,
                        ["lease_id"] = "42ec0ed3-1c10-4d7e-8dc1-52c1f1b912ab",
                        ["fencing_token"] = "42",
                        ["server_time"] = server,
                        ["expires_at"] = expiry
                    };
                    if (CorruptField is not null) payload[CorruptField] = CorruptField == "fencing_token" ? "042" : "foreign";
                }
                else if (name == "continuum.lock_renew")
                {
                    Renewals++;
                    Assert.Equal(300, args.GetProperty("ttl_seconds").GetInt32());
                    payload = new()
                    {
                        ["renewed"] = !CorruptRenewal,
                        ["lease_id"] = args.GetProperty("lease_id").GetString(),
                        ["run_id"] = args.GetProperty("run_id").GetString(),
                        ["fencing_token"] = "42",
                        ["server_time"] = "2026-10-18T00:00:01.000000Z",
                        ["expires_at"] = "2026-10-18T00:05:01.000000Z"
                    };
                }
                else if (name == "continuum.lock_release") { Releases++; Assert.False(args.TryGetProperty("fencing_token", out _)); payload = new() { ["released"] = true }; }
                else throw new InvalidOperationException("Unexpected synthetic request.");
                result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload) } } };
            }
            var rpc = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetString(), result });
            var sse = "event: message\ndata: " + rpc + "\n\n";
            if (MultilineSse)
            {
                var split = rpc.IndexOf(',') + 1;
                sse = "event: message\ndata: " + rpc[..split] + "\ndata: " + rpc[split..] + "\n\n";
            }
            if (OmitSseFinalBlankLine) sse = sse[..^1];
            var body = UseSse ? SsePrefix + sse : rpc;
            if (UseSse && SseLineEnding is { } ending)
            {
                var line = 0;
                body = string.Join("", body.Split('\n').Select((text, index) => index == 0 ? text :
                    (ending == "mixed" ? new[] { "\r", "\r\n", "\n" }[line++ % 3] : ending) + text));
            }
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            if (MalformedSseUtf8) bodyBytes[0] = 0xff;
            HttpContent content = ChunkedSse ? new StreamContent(new ChunkedFixtureStream(bodyBytes, SseChunkBytes))
                : new StringContent(body, Encoding.UTF8, UseSse ? "text/event-stream" : "application/json");
            if (ChunkedSse) content.Headers.ContentType = new("text/event-stream");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
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

    // Finite HTTP fixture bytes only; actual parser still owns the same body.
    private sealed class ChunkedFixtureStream(byte[] bytes, int maximumChunkBytes = 3) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunkBytes)], cancellationToken);
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
