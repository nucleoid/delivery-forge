using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Coordination;

/// <summary>One authenticated Streamable-HTTP session for one explicitly configured project/principal.
/// It never retries a mutation, changes its request UUID, follows redirects or reconnects automatically.</summary>
public sealed class ContinuumMcpTransport : ICoordinationTransport, IAsyncDisposable
{
    // Bounded invocation DATA, not proof of HTTP send, server outcome, or absence.
    // The reducer's exact PreparedRenew/actual Task remains the operation ledger.
    public sealed record ActualRenewalData(string RequestId, string Scope, string Resource, string LeaseId, string RunId, string FencingToken, TransportOutcome Outcome)
    {
        public string InvocationState { get; init; } = "Returned";
    }
    readonly List<ActualRenewalData> _actualRenewals = []; int _renewalObservationReservations;
    public IReadOnlyList<ActualRenewalData> ReadActualRenewalData() { lock (_state) return Array.AsReadOnly(_actualRenewals.ToArray()); }
    private const string Protocol = "2025-11-25";
    private const int MaximumResponseBytes = 1048576;
    private readonly Uri _endpoint;
    private readonly string _project;
    private readonly string _principalId;
    private readonly ContinuumApiKeyFile _credential;
    private readonly IContinuumDeploymentEvidenceSource _deployment;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly bool _fixture;
    private readonly Action? _beforeProjectionForContractTests;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _state = new();
    private byte[]? _credentialIdentity;
    private byte[]? _expectedCredentialIdentity;
    private string? _session;
    private bool _invalid;
    private bool _disposed;
    private bool _disposeEntered;
    private Exception? _disposeFailure;
    private Exception? _remoteSessionCloseFailure;
    private Exception? _handlerCloseFailure;
    // Data-only actual first-disposition evidence for internal contract assertions; never grants authority.
    internal (Exception? RemoteSession, Exception? Handler) FirstDispositionFailuresForContractTests
        => (_remoteSessionCloseFailure, _handlerCloseFailure);
    private ContinuumDeploymentEvidence? _evidence;
    private long _evidenceObservedAt;
    private TimeSpan _evidenceRemaining;
    private CoordinationCapabilities? _capabilities;

    public ContinuumMcpTransport(Uri endpoint, string project, string principalId,
        ContinuumApiKeyFile credential, IContinuumDeploymentEvidenceSource deployment,
        TimeSpan operationTimeout)
        : this(endpoint, project, principalId, credential, deployment, operationTimeout,
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                MaxConnectionsPerServer = 1,
                MaxResponseHeadersLength = 16,
                ConnectTimeout = operationTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            }, false, null)
    { }

    internal ContinuumMcpTransport(Uri endpoint, string project, string principalId,
        ContinuumApiKeyFile credential, IContinuumDeploymentEvidenceSource deployment,
        TimeSpan operationTimeout, HttpMessageHandler handler,
        Action? beforeProjectionForContractTests = null)
        : this(endpoint, project, principalId, credential, deployment, operationTimeout, handler, true, beforeProjectionForContractTests) { }

    private ContinuumMcpTransport(Uri endpoint, string project, string principalId,
        ContinuumApiKeyFile credential, IContinuumDeploymentEvidenceSource deployment,
        TimeSpan operationTimeout, HttpMessageHandler handler, bool fixture,
        Action? beforeProjectionForContractTests)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(deployment);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath != "/mcp")
            throw new ArgumentException("A canonical HTTPS MCP endpoint without query or credentials is required.", nameof(endpoint));
        _ = new CoordinationResource(project, "schema-migrations");
        RequireUuid(principalId);
        if (operationTimeout <= TimeSpan.Zero || operationTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _endpoint = endpoint;
        _project = project;
        _principalId = principalId;
        _credential = credential;
        _deployment = deployment;
        _timeout = operationTimeout;
        _fixture = fixture;
        if (!fixture && beforeProjectionForContractTests is not null)
            throw new ArgumentException("Projection barriers are confined to marked contract fixtures.");
        _beforeProjectionForContractTests = beforeProjectionForContractTests;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal bool ProductionCurrent
    {
        get
        {
            lock (_state)
                return !_fixture && !_invalid && !_disposed && _session is not null &&
                    _capabilities is not null && EvidenceCurrent(_evidence);
        }
    }

    // A restriction on the SAME original interval, never a freshness issuer or
    // a fresh cleanup clock. Caller has already required the production facet.
    public TimeSpan GetOriginalEvidenceWholeLimit(long originalStarted, TimeSpan originalWholeLimit)
    {
        if (originalStarted <= 0 || originalStarted > Stopwatch.GetTimestamp() ||
            originalWholeLimit <= TimeSpan.Zero || originalWholeLimit > TimeSpan.FromSeconds(1200))
            throw new ArgumentOutOfRangeException(nameof(originalWholeLimit));
        lock (_state)
        {
            if (_invalid || _disposed || _session is null || _capabilities is null || _evidence is null || _evidenceObservedAt == 0)
                throw new CoordinationUnavailableException("Actual anchored deployment evidence is unavailable before lease admission.");
            // Both limits have the SAME original monotonic origin. Return the
            // fixed endpoint, not remaining time that a later handoff can extend.
            var endpoint = Stopwatch.GetElapsedTime(originalStarted, _evidenceObservedAt) + _evidenceRemaining;
            var elapsed = Stopwatch.GetElapsedTime(originalStarted);
            if (originalWholeLimit <= elapsed || endpoint <= elapsed)
                throw new CoordinationUnavailableException("Pinned deployment evidence cannot cover the SAME original remaining whole interval; no lease or worker may start.");
            return endpoint < originalWholeLimit ? endpoint : originalWholeLimit;
        }
    }

    public void RequireOriginalEvidenceCoverage(long originalStarted, TimeSpan originalWholeLimit)
    {
        if (GetOriginalEvidenceWholeLimit(originalStarted, originalWholeLimit) < originalWholeLimit)
            throw new CoordinationUnavailableException("Pinned deployment evidence cannot cover the SAME original remaining whole interval; no lease or worker may start.");
    }

    public async Task<CoordinationCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (!await _serial.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw Uncertain();
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(_timeout);
        try
        {
            lock (_state)
            {
                if (_invalid || _disposed) throw Uncertain();
                if (_capabilities is not null)
                {
                    if (!EvidenceCurrent(_evidence)) throw Uncertain();
                    return _capabilities;
                }
            }
            var evidence = await _deployment.ObserveAsync(bound.Token).ConfigureAwait(false);
            bound.Token.ThrowIfCancellationRequested();
            ValidateDeployment(evidence);
            lock (_state)
            {
                _expectedCredentialIdentity = Convert.FromHexString(evidence.CredentialSha256);
                // Sample monotonic time first: the UTC read cannot move the
                // admitted endpoint later by the time between the two samples.
                _evidenceObservedAt = Stopwatch.GetTimestamp();
                var now = DateTimeOffset.UtcNow;
                _evidenceRemaining = TimeSpan.FromTicks(Math.Min((evidence.ValidUntil - now).Ticks,
                    (TimeSpan.FromMinutes(20) - (now - evidence.VerifiedAt)).Ticks));
            }
            var initialization = await RpcAsync("initialize", new
            {
                protocolVersion = Protocol,
                capabilities = new { },
                clientInfo = new { name = "delivery-forge", version = "1" },
            }, bound.Token).ConfigureAwait(false);
            ContinuumWireJson.Shape(initialization, ["protocolVersion", "capabilities", "serverInfo"], "instructions");
            if (ContinuumWireJson.Text(initialization, "protocolVersion", 32) != Protocol)
                throw ContinuumWireJson.Invalid();
            var server = initialization.GetProperty("serverInfo");
            ContinuumWireJson.Shape(server, ["name", "version"], "title", "websiteUrl", "icons");
            if (ContinuumWireJson.Text(server, "name", 128) != "continuum" ||
                ContinuumWireJson.Text(server, "version", 32) != "0.1.0" || _session is null)
                throw ContinuumWireJson.Invalid();
            await RpcAsync("notifications/initialized", null, bound.Token, notification: true).ConfigureAwait(false);
            var listing = await RpcAsync("tools/list", new { }, bound.Token).ConfigureAwait(false);
            ContinuumWireJson.Shape(listing, ["tools"], "nextCursor");
            if (listing.TryGetProperty("nextCursor", out _) || listing.GetProperty("tools").ValueKind != JsonValueKind.Array ||
                listing.GetProperty("tools").GetArrayLength() > 128) throw ContinuumWireJson.Invalid();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in listing.GetProperty("tools").EnumerateArray())
            {
                var name = ContinuumWireJson.Text(tool, "name", 128);
                if (!names.Add(name)) throw ContinuumWireJson.Invalid();
                if (name.StartsWith("continuum.lock_", StringComparison.Ordinal)) ValidateToolSchema(name, tool);
            }
            foreach (var required in new[] { "acquire", "renew", "release", "inspect" })
                if (!names.Contains("continuum.lock_" + required)) throw ContinuumWireJson.Invalid();
            // Discovery does not exercise exact-project authorization. Inspect a fresh
            // synthetic key through the authenticated service without acquiring a lease.
            var probe = CoordinationResource.GitHubIssue(_project, "delivery-forge-uat",
                "capability-" + Guid.NewGuid().ToString("N"), 1);
            var probeEnvelope = await RpcAsync("tools/call", new
            {
                name = "continuum.lock_inspect",
                arguments = new { scope = WireScope, resource = probe.ResourceKey },
            }, bound.Token).ConfigureAwait(false);
            var probeResult = ToolPayload(probeEnvelope);
            ContinuumWireJson.Shape(probeResult, ["held", "scope", "resource", "server_time"]);
            MatchResource(probeResult, probe);
            if (ContinuumWireJson.Boolean(probeResult, "held")) throw ContinuumWireJson.Invalid();
            _ = ContinuumWireJson.Time(probeResult, "server_time");
            // Protocol name/version/discovery do not prove migrations or principal identity.
            // Those claims come only from the independently supplied trusted host evidence.
            var capabilities = new CoordinationCapabilities(CoordinationCapabilities.V1ContractVersion,
                CoordinationAvailability.Available, _fixture,
                new HashSet<CoordinationOperation>(Enum.GetValues<CoordinationOperation>()),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(900), TimeSpan.FromHours(24), true, true, true,
                ["Lease semantics bind operator deployment evidence plus fresh authenticated MCP discovery; discovery alone is insufficient.",
                 "Release has no wire fencing-token parameter; exact lease/run identity supplies the server generation check.",
                 "GitHub and filesystem side effects are not transactional or server-fenced."]);
            bound.Token.ThrowIfCancellationRequested();
            lock (_state) { _evidence = evidence; _capabilities = capabilities; }
            return capabilities;
        }
        catch { Invalidate(); throw new CoordinationUnavailableException("Continuum initialization or deployment readiness is unavailable."); }
        finally { _serial.Release(); }
    }

    public async Task<AcquireTransportResult> AcquireAsync(AcquireRequest request, CancellationToken cancellationToken = default)
    {
        RequireResource(request.Resource); RequireOwner(request.Owner); RequireUuid(request.RequestId);
        if (request.Ttl.TotalSeconds != Math.Truncate(request.Ttl.TotalSeconds) || request.Ttl.TotalSeconds is < 30 or > 900)
            throw new CoordinationProtocolException("Continuum TTL must be an integer from30through900 seconds.");
        return await ToolAsync("continuum.lock_acquire", new
        {
            scope = WireScope,
            resource = request.Resource.ResourceKey,
            run_id = request.Owner.RunId,
            request_id = request.RequestId,
            ttl_seconds = (int)request.Ttl.TotalSeconds,
        }, result =>
        {
            if (ErrorCode(result) is not null) return AcquireTransportResult.Uncertain("continuum-acquire-refused");
            MatchResource(result, request.Resource);
            if (!ContinuumWireJson.Boolean(result, "acquired"))
            {
                ContinuumWireJson.Shape(result, ["acquired", "reason", "scope", "resource", "expires_at", "retry_after_seconds", "server_time"]);
                if (ContinuumWireJson.Text(result, "reason", 32) != "LOCK_HELD" ||
                    !result.GetProperty("retry_after_seconds").TryGetInt32(out var delay) || delay is < 1 or > 900 ||
                    ContinuumWireJson.Time(result, "expires_at") <= ContinuumWireJson.Time(result, "server_time"))
                    throw ContinuumWireJson.Invalid();
                return AcquireTransportResult.Contended("LOCK_HELD");
            }
            ContinuumWireJson.Shape(result, ["acquired", "scope", "resource", "lease_id", "run_id", "fencing_token", "expires_at", "server_time"]);
            if (ContinuumWireJson.Uuid(result, "run_id") != request.Owner.RunId) throw ContinuumWireJson.Invalid();
            var (server, expiry) = GrantTimes(result, request.Ttl);
            return AcquireTransportResult.Acquired(ContinuumWireJson.Uuid(result, "lease_id"), ContinuumWireJson.Fence(result), server, expiry);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RenewTransportResult> RenewAsync(RenewRequest request, CancellationToken cancellationToken = default)
    {
        RequireResource(request.Lease.Resource); RequireOwner(request.Lease.Owner); RequireUuid(request.RequestId);
        RequireUuid(request.Lease.LeaseId); request.Lease.Validate();
        int observation;
        lock (_state)
        {
            if (_renewalObservationReservations >= 512) throw new CoordinationUnavailableException("Finite actual renewal observation bound.");
            _renewalObservationReservations++; observation = _actualRenewals.Count;
            _actualRenewals.Add(new(request.RequestId, request.Lease.Resource.Scope, request.Lease.Resource.ResourceKey,
                request.Lease.LeaseId, request.Lease.Owner.RunId, request.Lease.FencingToken, TransportOutcome.Uncertain)
            { InvocationState = "Pending" });
        }
        try
        {
            var actual = await ToolAsync("continuum.lock_renew", new
            {
                lease_id = request.Lease.LeaseId,
                run_id = request.Lease.Owner.RunId,
                request_id = request.RequestId,
                ttl_seconds = 300,
            }, result =>
            {
                var code = ErrorCode(result);
                if (code is not null) return new RenewTransportResult(code == "LEASE_LOST" ? TransportOutcome.LeaseLost : TransportOutcome.Uncertain, null, null, "continuum-renew-refused");
                ContinuumWireJson.Shape(result, ["renewed", "lease_id", "run_id", "fencing_token", "expires_at", "server_time"]);
                if (!ContinuumWireJson.Boolean(result, "renewed") || ContinuumWireJson.Uuid(result, "lease_id") != request.Lease.LeaseId ||
                    ContinuumWireJson.Uuid(result, "run_id") != request.Lease.Owner.RunId || ContinuumWireJson.Fence(result) != request.Lease.FencingToken)
                    throw ContinuumWireJson.Invalid();
                var (server, expiry) = GrantTimes(result, ContinuumCoordinationAdapter.DefaultTtl);
                return RenewTransportResult.Renewed(server, expiry);
            }, cancellationToken).ConfigureAwait(false);
            lock (_state) _actualRenewals[observation] = _actualRenewals[observation] with { Outcome = actual.Outcome, InvocationState = "Returned" };
            return actual;
        }
        catch
        {
            lock (_state) _actualRenewals[observation] = _actualRenewals[observation] with { Outcome = TransportOutcome.Uncertain, InvocationState = "Uncertain" };
            throw;
        }
    }

    public async Task<InspectTransportResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default)
    {
        RequireResource(request.Resource); RequireOwner(request.Owner); RequireUuid(request.RequestId);
        return await ToolAsync("continuum.lock_inspect", new { scope = WireScope, resource = request.Resource.ResourceKey }, result =>
        {
            if (ErrorCode(result) is not null)
                return new InspectTransportResult(TransportOutcome.Uncertain, false, false, null, null, null, null, "continuum-inspect-refused");
            MatchResource(result, request.Resource);
            var held = ContinuumWireJson.Boolean(result, "held");
            var server = ContinuumWireJson.Time(result, "server_time");
            if (!held)
            {
                ContinuumWireJson.Shape(result, ["held", "scope", "resource", "server_time"]);
                return InspectTransportResult.Available();
            }
            if (ContinuumWireJson.Time(result, "expires_at") <= server) throw ContinuumWireJson.Invalid();
            if (!result.TryGetProperty("lease_id", out _))
            {
                ContinuumWireJson.Shape(result, ["held", "scope", "resource", "server_time", "expires_at"]);
                return InspectTransportResult.HeldByOther();
            }
            ContinuumWireJson.Shape(result, ["held", "scope", "resource", "server_time", "expires_at", "lease_id", "run_id", "fencing_token"]);
            var lease = ContinuumWireJson.Uuid(result, "lease_id");
            var run = ContinuumWireJson.Uuid(result, "run_id");
            var fence = ContinuumWireJson.Fence(result);
            // Server exposes identity for the authenticated principal, not necessarily this run.
            if (run != request.Owner.RunId) return InspectTransportResult.HeldByOther();
            return InspectTransportResult.HeldByCaller(lease, fence, server, ContinuumWireJson.Time(result, "expires_at"));
        }, cancellationToken, cleanup: true).ConfigureAwait(false);
    }

    public async Task<ReleaseTransportResult> ReleaseAsync(ReleaseRequest request, CancellationToken cancellationToken = default)
    {
        RequireOwner(request.Owner); RequireUuid(request.RequestId); RequireUuid(request.LeaseId);
        if (!long.TryParse(request.FencingToken, NumberStyles.None, CultureInfo.InvariantCulture, out var fence) ||
            fence < 1 || request.FencingToken != fence.ToString(CultureInfo.InvariantCulture)) throw ContinuumWireJson.Invalid();
        return await ToolAsync("continuum.lock_release", new
        {
            lease_id = request.LeaseId,
            run_id = request.Owner.RunId,
            request_id = request.RequestId,
        }, result =>
        {
            var code = ErrorCode(result);
            if (code is not null) return code == "LEASE_LOST" ? ReleaseTransportResult.LeaseLost() : ReleaseTransportResult.Uncertain("continuum-release-refused");
            ContinuumWireJson.Shape(result, ["released"], "already_released");
            if (!ContinuumWireJson.Boolean(result, "released") ||
                (result.TryGetProperty("already_released", out _) && !ContinuumWireJson.Boolean(result, "already_released"))) throw ContinuumWireJson.Invalid();
            return ReleaseTransportResult.Released();
        }, cancellationToken, cleanup: true).ConfigureAwait(false);
    }

    private string WireScope => "project:" + _project;

    private void RequireResource(CoordinationResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Scope != _project) throw new CoordinationProtocolException("Resource scope differs from the configured project.");
    }

    private void RequireOwner(LeaseOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.PrincipalId != _principalId) throw new CoordinationProtocolException("Owner differs from the operator-bound authenticated principal.");
        RequireUuid(owner.RunId);
    }

    private static void RequireUuid(string text)
    {
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D"))
            throw new CoordinationProtocolException("Canonical nonempty UUID required by Continuum.");
    }

    private void MatchResource(JsonElement result, CoordinationResource resource)
    {
        if (ContinuumWireJson.Text(result, "scope", 160) != WireScope || ContinuumWireJson.Text(result, "resource") != resource.ResourceKey)
            throw ContinuumWireJson.Invalid();
    }

    private static (DateTimeOffset Server, DateTimeOffset Expiry) GrantTimes(JsonElement result, TimeSpan ttl)
    {
        var server = ContinuumWireJson.Time(result, "server_time");
        var expiry = ContinuumWireJson.Time(result, "expires_at");
        if (expiry - server != ttl) throw ContinuumWireJson.Invalid();
        return (server, expiry);
    }

    private static string? ErrorCode(JsonElement value)
    {
        if (!value.TryGetProperty("error", out var error)) return null;
        ContinuumWireJson.Shape(value, ["error"]);
        ContinuumWireJson.Shape(error, ["code", "message"]);
        _ = ContinuumWireJson.Text(error, "message", 4096);
        return ContinuumWireJson.Text(error, "code", 64);
    }

    private async Task<T> ToolAsync<T>(string name, object arguments, Func<JsonElement, T> project,
        CancellationToken cancellationToken, bool cleanup = false)
    {
        if (!await _serial.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw Uncertain();
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(_timeout);
        try
        {
            lock (_state)
                if (_invalid || _disposed || _capabilities is null || _session is null || (!cleanup && !EvidenceCurrent(_evidence))) throw Uncertain();
            var result = await RpcAsync("tools/call", new { name, arguments }, bound.Token).ConfigureAwait(false);
            var value = ToolPayload(result);
            if (value.TryGetProperty("error", out _) && ErrorCode(value) != "LEASE_LOST") Invalidate();
            _beforeProjectionForContractTests?.Invoke();
            var projected = project(value);
            bound.Token.ThrowIfCancellationRequested();
            return projected;
        }
        catch { Invalidate(); throw Uncertain(); }
        finally { _serial.Release(); }
    }

    private static JsonElement ToolPayload(JsonElement result)
    {
        ContinuumWireJson.Shape(result, ["content"], "isError");
        var content = result.GetProperty("content");
        if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1) throw ContinuumWireJson.Invalid();
        var text = content[0];
        ContinuumWireJson.Shape(text, ["type", "text"]);
        if (ContinuumWireJson.Text(text, "type", 16) != "text") throw ContinuumWireJson.Invalid();
        var payload = text.GetProperty("text");
        if (payload.ValueKind != JsonValueKind.String || payload.GetString()!.Length > 65536) throw ContinuumWireJson.Invalid();
        using var document = ContinuumWireJson.Parse(Encoding.UTF8.GetBytes(payload.GetString()!));
        var value = document.RootElement.Clone();
        var failed = result.TryGetProperty("isError", out _) && ContinuumWireJson.Boolean(result, "isError");
        if (failed != value.TryGetProperty("error", out _)) throw ContinuumWireJson.Invalid();
        return value;
    }

    private async Task<JsonElement> RpcAsync(string method, object? parameters, CancellationToken cancellationToken, bool notification = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("D");
        var message = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
        if (!notification) message.Add("id", id);
        if (parameters is not null) message.Add("params", parameters);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > 16384) throw ContinuumWireJson.Invalid();
        var authorization = await ReadBoundCredentialAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_session is not null)
        {
            request.Headers.Add("Mcp-Session-Id", _session);
            request.Headers.Add("MCP-Protocol-Version", Protocol);
        }
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.RequestMessage?.RequestUri != _endpoint || response.Content.Headers.ContentEncoding.Count != 0) throw Uncertain();
        if (notification)
        {
            if (response.StatusCode != HttpStatusCode.Accepted) throw Uncertain();
            return default;
        }
        if (response.StatusCode != HttpStatusCode.OK) throw Uncertain();
        if (method == "initialize")
        {
            if (!response.Headers.TryGetValues("Mcp-Session-Id", out var values)) throw Uncertain();
            var sessions = values.ToArray();
            if (sessions.Length != 1 || sessions[0].Length is < 1 or > 256 || sessions[0].Any(character => character < '!' || character > '~')) throw Uncertain();
            _session = sessions[0];
        }
        else if (response.Headers.TryGetValues("Mcp-Session-Id", out var values) &&
                 !values.SequenceEqual([_session], StringComparer.Ordinal)) throw Uncertain();
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media is not ("application/json" or "text/event-stream") ||
            response.Content.Headers.ContentLength is > MaximumResponseBytes) throw Uncertain();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await ReadMessageAsync(stream, media == "text/event-stream", id, cancellationToken).ConfigureAwait(false);
        using var document = ContinuumWireJson.Parse(payload);
        var root = document.RootElement;
        ContinuumWireJson.Shape(root, ["jsonrpc", "id", "result"]);
        if (ContinuumWireJson.Text(root, "jsonrpc", 8) != "2.0" || ContinuumWireJson.Text(root, "id", 36) != id)
            throw ContinuumWireJson.Invalid();
        return root.GetProperty("result").Clone();
    }

    private async Task<AuthenticationHeaderValue> ReadBoundCredentialAsync(CancellationToken cancellationToken)
    {
        var (authorization, identity) = await _credential.ReadAsync(cancellationToken).ConfigureAwait(false);
        lock (_state)
        {
            if (_expectedCredentialIdentity is null || !CryptographicOperations.FixedTimeEquals(_expectedCredentialIdentity, identity) ||
                (_credentialIdentity is not null && !CryptographicOperations.FixedTimeEquals(_credentialIdentity, identity)))
                throw Uncertain();
            _credentialIdentity ??= identity;
        }
        return authorization;
    }

    private static async Task<byte[]> ReadMessageAsync(Stream stream, bool sse, string responseId, CancellationToken cancellationToken)
    {
        var bytes = new byte[MaximumResponseBytes + 1];
        var used = 0;
        var start = 0;
        var lineStart = 0;
        var scan = 0;
        var skipLf = false;
        var events = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(bytes.AsMemory(used, Math.Min(65536, bytes.Length - used)), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            used += read;
            if (used > MaximumResponseBytes) throw ContinuumWireJson.Invalid();
            if (!sse && read == 0) return bytes[..used];
            if (sse)
            {
                for (; scan < used; scan++)
                {
                    var index = scan;
                    if (skipLf)
                    {
                        skipLf = false;
                        if (bytes[index] == 10)
                        {
                            // A CR ended the previous line, including when CR/LF
                            // arrived in separate reads. Its LF is not another line.
                            if (start == index) start = index + 1;
                            lineStart = index + 1;
                            continue;
                        }
                    }
                    if (bytes[index] is not (10 or 13)) continue;
                    skipLf = bytes[index] == 13;
                    var emptyLine = index == lineStart;
                    lineStart = index + 1;
                    if (!emptyLine) continue;
                    if (++events > 32) throw ContinuumWireJson.Invalid();
                    var text = new UTF8Encoding(false, true).GetString(bytes, start, index - start);
                    var data = new List<string>();
                    foreach (var line in text.Split(['\r', '\n']))
                    {
                        if (line.Length == 0 || line.StartsWith(':')) continue;
                        var colon = line.IndexOf(':');
                        var field = colon < 0 ? line : line[..colon];
                        var part = colon < 0 ? "" : line[(colon + 1)..];
                        if (part.StartsWith(' ')) part = part[1..];
                        // These are SSE framing metadata only. No resume cursor,
                        // timer, GET/reconnect or replay is created from them.
                        if (field is "id" or "retry") continue;
                        if (field == "event") { if (part is not ("" or "message")) throw ContinuumWireJson.Invalid(); continue; }
                        if (field == "data") data.Add(part);
                        // SSE ignores fields not defined by its grammar.
                    }
                    start = index + 1;
                    var payload = Encoding.UTF8.GetBytes(string.Join('\n', data));
                    if (payload.Length == 0) continue; // Includes id + empty-data priming.
                    using var document = ContinuumWireJson.Parse(payload);
                    var message = document.RootElement;
                    if (message.TryGetProperty("method", out _))
                    {
                        // The client advertised no server-request capability.
                        // Only inert progress/logging notifications may precede
                        // this response; tool/schema changes must invalidate it.
                        ContinuumWireJson.Shape(message, ["jsonrpc", "method", "params"]);
                        if (ContinuumWireJson.Text(message, "jsonrpc", 8) != "2.0" ||
                            ContinuumWireJson.Text(message, "method", 128) is not ("notifications/progress" or "notifications/message") ||
                            message.GetProperty("params").ValueKind != JsonValueKind.Object) throw ContinuumWireJson.Invalid();
                        var parameters = message.GetProperty("params");
                        if (parameters.TryGetProperty("_meta", out var meta) && meta.ValueKind != JsonValueKind.Object) throw ContinuumWireJson.Invalid();
                        if (message.GetProperty("method").GetString() == "notifications/progress")
                        {
                            ContinuumWireJson.Shape(parameters, ["progressToken", "progress"], "total", "message", "_meta");
                            var token = parameters.GetProperty("progressToken");
                            if (token.ValueKind != JsonValueKind.String && !(token.ValueKind == JsonValueKind.Number && token.TryGetInt64(out _)) ||
                                !parameters.GetProperty("progress").TryGetDouble(out var progress) || !double.IsFinite(progress) ||
                                parameters.TryGetProperty("total", out var total) && (!total.TryGetDouble(out var value) || !double.IsFinite(value)) ||
                                parameters.TryGetProperty("message", out var detail) && detail.ValueKind != JsonValueKind.String) throw ContinuumWireJson.Invalid();
                        }
                        else
                        {
                            ContinuumWireJson.Shape(parameters, ["level", "data"], "logger", "_meta");
                            if (ContinuumWireJson.Text(parameters, "level", 16) is not ("debug" or "info" or "notice" or "warning" or "error" or "critical" or "alert" or "emergency") ||
                                parameters.TryGetProperty("logger", out var logger) && logger.ValueKind != JsonValueKind.String) throw ContinuumWireJson.Invalid();
                        }
                        continue;
                    }
                    ContinuumWireJson.Shape(message, ["jsonrpc", "id", "result"]);
                    if (ContinuumWireJson.Text(message, "jsonrpc", 8) != "2.0" || ContinuumWireJson.Text(message, "id", 36) != responseId)
                        throw ContinuumWireJson.Invalid();
                    for (var trailing = start; trailing < used; trailing++)
                        if (bytes[trailing] is not (9 or 10 or 13 or 32)) throw ContinuumWireJson.Invalid();
                    return payload;
                }
                if (read == 0) throw ContinuumWireJson.Invalid();
            }
        }
    }

    private static void ValidateToolSchema(string name, JsonElement tool)
    {
        var schema = tool.GetProperty("inputSchema");
        if (ContinuumWireJson.Text(schema, "type", 16) != "object") throw ContinuumWireJson.Invalid();
        var required = schema.GetProperty("required");
        if (required.ValueKind != JsonValueKind.Array) throw ContinuumWireJson.Invalid();
        var expected = name switch
        {
            "continuum.lock_acquire" => new[] { "scope", "resource", "run_id", "request_id" },
            "continuum.lock_renew" or "continuum.lock_release" => ["lease_id", "run_id", "request_id"],
            "continuum.lock_inspect" => ["scope", "resource"],
            _ => throw ContinuumWireJson.Invalid(),
        };
        var actual = required.EnumerateArray().Select(value => value.GetString()).ToArray();
        if (actual.Length != expected.Length || !new HashSet<string?>(actual, StringComparer.Ordinal).SetEquals(expected)) throw ContinuumWireJson.Invalid();
        var properties = schema.GetProperty("properties");
        foreach (var field in expected)
            if (ContinuumWireJson.Text(properties.GetProperty(field), "type", 16) != "string") throw ContinuumWireJson.Invalid();
    }

    private bool EvidenceCurrent(ContinuumDeploymentEvidence? evidence)
    {
        if (evidence is not null && ReferenceEquals(evidence, _evidence) && _evidenceObservedAt != 0)
            return Stopwatch.GetElapsedTime(_evidenceObservedAt) < _evidenceRemaining;
        var now = DateTimeOffset.UtcNow;
        return evidence is not null && evidence.VerifiedAt <= now && evidence.ValidUntil > now &&
            now - evidence.VerifiedAt <= TimeSpan.FromMinutes(20);
    }

    private void ValidateDeployment(ContinuumDeploymentEvidence evidence)
    {
        if (evidence.Endpoint != _endpoint || evidence.Project != _project || evidence.PrincipalId != _principalId ||
            !EvidenceCurrent(evidence) || evidence.ValidUntil - evidence.VerifiedAt > TimeSpan.FromMinutes(20) ||
            string.IsNullOrWhiteSpace(evidence.OperatorIssuer) || evidence.OperatorIssuer.Length > 256 || evidence.OperatorIssuer.Any(char.IsControl) ||
            !Hex(evidence.SourceRevision, 40) || !Hex(evidence.ImageSha256, 64) ||
            !Hex(evidence.CredentialSha256, 64) ||
            !Hex(evidence.MigrationEvidenceSha256, 64) || !Hex(evidence.IdentityProfileEvidenceSha256, 64))
            throw new CoordinationUnavailableException("Trusted host deployment evidence is missing, stale or mismatched.");
    }

    private static bool Hex(string text, int length) => text is not null && text.Length == length && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private void Invalidate() { lock (_state) _invalid = true; }
    private static CoordinationUncertainException Uncertain() => new("Continuum operation is unavailable or uncertain; retain ownership state and reconcile without automatic replay.");

    public async ValueTask DisposeAsync()
    {
        // Never dispose a handler or release session accounting while its actual send/read is unreturned.
        if (!await _serial.WaitAsync(0).ConfigureAwait(false)) throw Uncertain();
        try
        {
            lock (_state)
            {
                if (_disposeEntered)
                {
                    if (_disposeFailure is not null || !_disposed) throw Uncertain();
                    return;
                }
                _disposeEntered = true;
                _invalid = true;
            }
            using var bound = new CancellationTokenSource(_timeout);
            try
            {
                if (_session is not null)
                {
                    var authorization = await ReadBoundCredentialAsync(bound.Token).ConfigureAwait(false);
                    using var request = new HttpRequestMessage(HttpMethod.Delete, _endpoint);
                    request.Headers.Authorization = authorization;
                    request.Headers.Add("Mcp-Session-Id", _session);
                    request.Headers.Add("MCP-Protocol-Version", Protocol);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bound.Token).ConfigureAwait(false);
                    bound.Token.ThrowIfCancellationRequested();
                    if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound)) throw Uncertain();
                }
            }
            catch (Exception error) { _remoteSessionCloseFailure = error; }
            // A refused remote DELETE is not remote-session cleanup proof. It must not
            // skip the local handler's independent actual first disposition.
            try { _http.Dispose(); lock (_state) _disposed = true; }
            catch (Exception error) { _handlerCloseFailure = error; }
            if (_credentialIdentity is not null) CryptographicOperations.ZeroMemory(_credentialIdentity);
            if (_expectedCredentialIdentity is not null) CryptographicOperations.ZeroMemory(_expectedCredentialIdentity);
            var failures = new List<Exception>();
            if (_remoteSessionCloseFailure is not null) failures.Add(_remoteSessionCloseFailure);
            if (_handlerCloseFailure is not null) failures.Add(_handlerCloseFailure);
            if (bound.IsCancellationRequested) failures.Add(new TimeoutException("Original Continuum session-disposition allowance expired."));
            if (failures.Count != 0)
            {
                _disposeFailure = new AggregateException("Independent Continuum session and local handler dispositions.", failures);
                throw Uncertain();
            }
        }
        catch (Exception error) { _disposeFailure ??= error; throw Uncertain(); }
        finally { _serial.Release(); }
    }
}
