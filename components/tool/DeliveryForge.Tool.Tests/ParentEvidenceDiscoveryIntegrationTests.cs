using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Coordination;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// Actual lifetime + actual marked HTTP contract transport. No live service,
// lease, production facet or caller-created clock/issuer is used by these tests.
public sealed class ParentEvidenceDiscoveryIntegrationTests
{
    [Theory]
    [InlineData(1200)]
    [InlineData(30)]
    public async Task Pinned_restriction_and_discovery_share_one_nonexpanding_original(int seconds)
    {
        await using var sample = new Sample();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken);
        var started = life.OriginalStarted; var token = life.Token;
        var initialWhole = life.WholeLimit; var initialWork = life.WorkLimit;
        await DiscoverAsync(sample, life);
        var beforeWhole = life.WholeLimit; var beforeWork = life.WorkLimit;
        var requests = sample.Handler.Requests;
        var limit = sample.Transport.GetOriginalEvidenceWholeLimit(started, beforeWhole);
        life.Restrict(limit);
        sample.Transport.RequireOriginalEvidenceCoverage(started, life.WholeLimit);
        life.Check();

        await Task.Yield(); // The receiving owner keeps the fixed endpoint.
        Assert.Equal(limit, sample.Transport.GetOriginalEvidenceWholeLimit(started, beforeWhole));
        sample.Transport.RequireOriginalEvidenceCoverage(started, life.WholeLimit);
        life.Check();
        Assert.Equal(started, life.OriginalStarted); Assert.Equal(token, life.Token);
        Assert.Equal(limit, life.WholeLimit);
        Assert.True(life.WholeLimit <= beforeWhole && beforeWhole <= initialWhole);
        Assert.True(life.WorkLimit <= beforeWork && beforeWork <= initialWork);
        Assert.False(life.HasExplicitUnknown);
        if (seconds == 30) Assert.Equal(initialWhole, life.WholeLimit);
        AssertNoLeaseOrRefresh(sample, requests);
    }

    [Fact]
    public async Task Existing_caller_cancellation_survives_discovery_restriction_without_another_effect()
    {
        await using var sample = new Sample();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), cancellation.Token);
        await DiscoverAsync(sample, life);
        var started = life.OriginalStarted; var token = life.Token;
        life.Restrict(sample.Transport.GetOriginalEvidenceWholeLimit(started, life.WholeLimit));
        sample.Transport.RequireOriginalEvidenceCoverage(started, life.WholeLimit);
        life.Check();
        var whole = life.WholeLimit; var work = life.WorkLimit;
        var requests = sample.Handler.Requests;

        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ParentCoordinationHost.RestrictOriginalToPinnedEvidence(life, sample.Deployment.Evidence));
        Assert.ThrowsAny<OperationCanceledException>(life.Check);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(started, life.OriginalStarted); Assert.Equal(token, life.Token);
        Assert.Equal(whole, life.WholeLimit); Assert.Equal(work, life.WorkLimit);
        AssertNoLeaseOrRefresh(sample, requests);
    }

    [Fact]
    public async Task Later_anchored_restriction_keeps_the_actual_earliest_cleanup_endpoint_and_work_closed()
    {
        await using var sample = new Sample();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(1200), TestContext.Current.CancellationToken);
        await DiscoverAsync(sample, life);
        var started = life.OriginalStarted; var token = life.Token;
        var whole = life.WholeLimit; var work = life.WorkLimit;
        var requests = sample.Handler.Requests;
        Assert.True(whole > TimeSpan.FromMinutes(5)); // Once60, rather than whole, binds cleanup.
        var first = life.CleanupRemaining;
        await Task.Delay(20, TestContext.Current.CancellationToken);

        // The projection grants no WORK authority. Even a valid later
        // restriction on this same object must not restart its cleanup phase.
        life.Restrict(sample.Transport.GetOriginalEvidenceWholeLimit(started, life.WholeLimit));
        sample.Transport.RequireOriginalEvidenceCoverage(started, life.WholeLimit);
        var remaining = life.CleanupRemaining;
        Assert.True(remaining < first - TimeSpan.FromMilliseconds(10));
        Assert.True(remaining > TimeSpan.Zero);
        Assert.Throws<TimeoutException>(life.Check);
        Assert.Equal(started, life.OriginalStarted); Assert.Equal(token, life.Token);
        Assert.True(life.WholeLimit <= whole); Assert.True(life.WorkLimit <= work);
        Assert.False(life.HasExplicitUnknown);
        AssertNoLeaseOrRefresh(sample, requests);
    }

    private static async Task DiscoverAsync(Sample sample, ToolLifetime life)
    {
        // Exactly the immutable observation used by the pre-store restriction;
        // discovery observes this same DATA object once, never a newer timestamp.
        var observed = await sample.Deployment.ObserveAsync(life.Token);
        ParentCoordinationHost.RestrictOriginalToPinnedEvidence(life, observed);
        var capabilities = await sample.Transport.DetectCapabilitiesAsync(life.Token);
        Assert.True(capabilities.Fixture); Assert.False(sample.Transport.ProductionCurrent);
        Assert.Equal(2, sample.Deployment.Observations);
    }

    private static void AssertNoLeaseOrRefresh(Sample sample, int requests)
    {
        Assert.Equal(requests, sample.Handler.Requests);
        Assert.Equal(2, sample.Deployment.Observations);
        Assert.Equal(0, sample.Handler.Acquisitions);
        Assert.Equal(0, sample.Handler.Renewals);
        Assert.Equal(0, sample.Handler.Releases);
        Assert.False(sample.Transport.ProductionCurrent);
    }

    private sealed class Sample : IAsyncDisposable
    {
        private const string Key = "synthetic-test-value-not-a-real-service-key";
        private readonly string root = Path.Combine(Path.GetTempPath(), "delivery-forge-parent-window-" + Guid.NewGuid().ToString("N"));
        internal WireHandler Handler { get; } = new();
        internal FixedDeployment Deployment { get; }
        internal ContinuumMcpTransport Transport { get; }

        internal Sample()
        {
            Directory.CreateDirectory(root);
            var credential = Path.Combine(root, "synthetic.key");
            File.WriteAllText(credential, Key, new UTF8Encoding(false));
            var now = DateTimeOffset.UtcNow;
            Deployment = new(new ContinuumDeploymentEvidence(new("https://continuum.invalid/mcp"),
                "delivery-forge-uat", "a714fc76-487f-4d64-84d9-4ce8409e62e1",
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key))),
                "synthetic-test-operator", new('a', 40), new('b', 64), new('c', 64), new('d', 64),
                now - TimeSpan.FromSeconds(30), now + TimeSpan.FromMinutes(10)));
            Transport = new(Deployment.Evidence.Endpoint, Deployment.Evidence.Project, Deployment.Evidence.PrincipalId,
                new ContinuumApiKeyFile(credential), Deployment, TimeSpan.FromSeconds(10), Handler);
        }

        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync();
            Assert.True(Handler.Disposed);
            // Only the inert synthetic key is removed after actual HTTP closure.
            File.Delete(Path.Combine(root, "synthetic.key"));
            Directory.Delete(root, recursive: false);
        }
    }

    private sealed class FixedDeployment(ContinuumDeploymentEvidence evidence) : IContinuumDeploymentEvidenceSource
    {
        internal ContinuumDeploymentEvidence Evidence { get; } = evidence;
        internal int Observations { get; private set; }

        public Task<ContinuumDeploymentEvidence> ObserveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observations++;
            return Task.FromResult(Evidence);
        }
    }

    // Closed subset of the existing Coordination.Tests WireHandler. Injecting
    // it uses the transport's marked-fixture constructor, never real HTTP/DNS.
    private sealed class WireHandler : HttpMessageHandler
    {
        internal int Requests, Acquisitions, Renewals, Releases;
        internal bool Disposed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("ApiKey", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Delete)
                return new(HttpStatusCode.NoContent) { RequestMessage = request };
            using var input = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var root = input.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized")
                return new(HttpStatusCode.Accepted) { RequestMessage = request };
            object result;
            if (method == "initialize")
                result = new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "continuum", version = "0.1.0" } };
            else if (method == "tools/list")
                result = new
                {
                    tools = new[] { Tool("acquire", ["scope", "resource", "run_id", "request_id"]),
                        Tool("renew", ["lease_id", "run_id", "request_id"]), Tool("release", ["lease_id", "run_id", "request_id"]),
                        Tool("inspect", ["scope", "resource"]) }
                };
            else if (method == "tools/call")
            {
                var parameters = root.GetProperty("params");
                var name = parameters.GetProperty("name").GetString();
                if (name == "continuum.lock_acquire") Acquisitions++;
                if (name == "continuum.lock_renew") Renewals++;
                if (name == "continuum.lock_release") Releases++;
                if (name != "continuum.lock_inspect") throw new InvalidOperationException("Lease mutation is outside this discovery-only fixture.");
                var arguments = parameters.GetProperty("arguments");
                result = new
                {
                    content = new[] { new { type = "text", text = JsonSerializer.Serialize(new
                    {
                        held = false, scope = arguments.GetProperty("scope").GetString(),
                        resource = arguments.GetProperty("resource").GetString(), server_time = "2026-10-18T00:00:00.000000Z"
                    }) } }
                };
            }
            else throw new InvalidOperationException("Unexpected discovery-only fixture request.");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetString(), result }), Encoding.UTF8, "application/json")
            };
            if (method == "initialize") response.Headers.Add("Mcp-Session-Id", "synthetic-session");
            return response;
        }

        private static object Tool(string operation, string[] required) => new
        {
            name = "continuum.lock_" + operation,
            inputSchema = new { type = "object", required, properties = required.ToDictionary(name => name, _ => new { type = "string" }) }
        };

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
