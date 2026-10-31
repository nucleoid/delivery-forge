using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Coordination;
using DeliveryForge.Coordination.LiveControls;

// This executable is invoked only by an admitted Root validation pipeline. It
// has no server administration, worker/model execution or GitHub publication port.
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var original = Stopwatch.GetTimestamp();
        var steps = new List<string>();
        var failures = new List<Exception>();
        ContinuumMcpTransport? a = null, b = null;
        FileCoordinationCheckpointStore? store = null;
        var root = "";
        using var entire = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            if (args.Length != 4 || args[0] != "--configuration" || args[2] != "--sha256" ||
                !Path.IsPathFullyQualified(args[1]) || args[3].Length != 64) throw new ArgumentException("Exact Root configuration path/hash required.");
            var input = new FileInfo(args[1]);
            if (!input.Exists || input.Length is < 1 or > 65536 || (input.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Bounded plain private Root configuration required.");
            byte[] bytes;
            await using (var inputStream = new FileStream(input.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                var length = inputStream.Length;
                if (length is < 1 or > 65536) throw new ArgumentException("Private Root configuration length changed.");
                bytes = new byte[(int)length];
                await inputStream.ReadExactlyAsync(bytes, entire.Token);
                if (inputStream.ReadByte() != -1 || inputStream.Length != length) throw new ArgumentException("Private Root configuration EOF changed.");
            }
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != args[3])
                throw new ArgumentException("Exact whole private Root configuration differs.");
            var config = JsonSerializer.Deserialize<Configuration>(bytes, new JsonSerializerOptions
            {
                MaxDepth = 16,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            }) ?? throw new ArgumentException("Private Root configuration is invalid.");
            if (config.A.PrincipalId == config.B.PrincipalId || config.A.CredentialSha256 == config.B.CredentialSha256 ||
                config.Endpoint.AbsoluteUri != "https://continuum.pragmaticcoder.com/mcp" || config.Project != "delivery-forge-uat")
                throw new ArgumentException("Two distinct operator-bound principals on the isolated approved project required.");
            root = Path.GetFullPath(config.JournalRoot);
            if (Directory.Exists(root) || File.Exists(root)) throw new ArgumentException("Fresh owned journal directory required.");
            Directory.CreateDirectory(root);
            store = LiveControlHost.CreateCheckpointStore(root);
            var timeout = TimeSpan.FromSeconds(10);
            a = Transport(config, config.A, timeout);
            b = Transport(config, config.B, timeout);
            var ownerA = new LeaseOwner(config.A.PrincipalId, Guid.NewGuid().ToString("D"));
            var ownerB = new LeaseOwner(config.B.PrincipalId, Guid.NewGuid().ToString("D"));
            var host = new LiveControlHost(ownerA);
            var adapter = await ContinuumCoordinationAdapter.CreateProductionAsync(a,
                new(host, host, store, TimeSpan.FromMilliseconds(500), timeout), entire.Token);
            host.Bind(adapter);
            var bCapabilities = await b.DetectCapabilitiesAsync(entire.Token);
            Require(!adapter.Capabilities.Fixture && !bCapabilities.Fixture);
            steps.Add("fresh-authenticated-project-and-trusted-deployment-bindings");
            var seed = "synthetic-" + Guid.NewGuid().ToString("N");
            CoordinationResource Resource(long number) => CoordinationResource.GitHubIssue(config.Project, "delivery-forge-uat", seed, number);

            var resource = Resource(1);
            var request = new AcquireRequest(resource, ownerA, TimeSpan.FromSeconds(30), Guid.NewGuid().ToString("D"));
            var first = await a.AcquireAsync(request, entire.Token);
            Require(first.Outcome == TransportOutcome.Acquired);
            var replay = await a.AcquireAsync(request, entire.Token);
            Require(replay == first);
            Require((await a.AcquireAsync(request with { RequestId = Guid.NewGuid().ToString("D") }, entire.Token)).Outcome == TransportOutcome.Contended);
            var bRequest = new AcquireRequest(resource, ownerB, TimeSpan.FromSeconds(30), Guid.NewGuid().ToString("D"));
            Require((await b.AcquireAsync(bRequest, entire.Token)).Outcome == TransportOutcome.Contended);
            var masked = await b.InspectAsync(new(resource, ownerB, Guid.NewGuid().ToString("D")), entire.Token);
            Require(masked.Held && !masked.CallerOwnsLease && masked.LeaseId is null && masked.FencingToken is null);
            Require((await b.ReleaseAsync(new(first.LeaseId!, ownerB, first.FencingToken!, Guid.NewGuid().ToString("D")), entire.Token)).Outcome == TransportOutcome.LeaseLost);
            var release = new ReleaseRequest(first.LeaseId!, ownerA, first.FencingToken!, Guid.NewGuid().ToString("D"));
            Require((await a.ReleaseAsync(release, entire.Token)).Outcome == TransportOutcome.Released);
            Require((await a.ReleaseAsync(release, entire.Token)).Outcome == TransportOutcome.Released);
            var successor = await b.AcquireAsync(bRequest with { RequestId = Guid.NewGuid().ToString("D") }, entire.Token);
            Require(successor.Outcome == TransportOutcome.Acquired && Greater(successor.FencingToken!, first.FencingToken!));
            Require((await a.ReleaseAsync(release with { RequestId = Guid.NewGuid().ToString("D") }, entire.Token)).Outcome == TransportOutcome.LeaseLost);
            Require((await b.InspectAsync(new(resource, ownerB, Guid.NewGuid().ToString("D")), entire.Token)).LeaseId == successor.LeaseId);
            Require((await b.ReleaseAsync(new(successor.LeaseId!, ownerB, successor.FencingToken!, Guid.NewGuid().ToString("D")), entire.Token)).Outcome == TransportOutcome.Released);
            steps.Add("replay-nonreentrancy-contention-masking-owner-loss-release-successor-fencing");

            var expiringResource = Resource(2);
            var expiryRequest = request with { Resource = expiringResource, RequestId = Guid.NewGuid().ToString("D") };
            var expiring = await a.AcquireAsync(expiryRequest, entire.Token);
            Require(expiring.Outcome == TransportOutcome.Acquired);
            var actualWait = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(actualWait) < TimeSpan.FromSeconds(31))
                await Task.Delay(100, entire.Token);
            var takeover = await b.AcquireAsync(bRequest with { Resource = expiringResource, RequestId = Guid.NewGuid().ToString("D") }, entire.Token);
            Require(takeover.Outcome == TransportOutcome.Acquired && Greater(takeover.FencingToken!, expiring.FencingToken!));
            Require((await a.ReleaseAsync(new(expiring.LeaseId!, ownerA, expiring.FencingToken!, Guid.NewGuid().ToString("D")), entire.Token)).Outcome == TransportOutcome.LeaseLost);
            Require((await b.InspectAsync(new(expiringResource, ownerB, Guid.NewGuid().ToString("D")), entire.Token)).LeaseId == takeover.LeaseId);
            Require((await b.ReleaseAsync(new(takeover.LeaseId!, ownerB, takeover.FencingToken!, Guid.NewGuid().ToString("D")), entire.Token)).Outcome == TransportOutcome.Released);
            steps.Add("genuine-30second-expiry-and-successor-preservation");

            var boundResource = Resource(3);
            var held = await adapter.AcquireAllAsync([boundResource], ownerA, ContinuumCoordinationAdapter.DefaultTtl, entire.Token);
            var authority = held.Authority ?? throw new InvalidOperationException("Actual adapter authority unavailable.");
            Require(host.TryDispatch(authority) && host.TryPublish(authority));
            // External lease-loss injection is permitted only because this sealed
            // host's tracked local actions have actually returned and it has no child/process port.
            var quiet = await host.ObserveAsync(ownerA, entire.Token);
            Require(quiet.Quiescent);
            var ownedLease = held.Snapshot.Leases.Single();
            Require((await a.ReleaseAsync(new(ownedLease.LeaseId, ownerA, ownedLease.FencingToken, Guid.NewGuid().ToString("D")), entire.Token)).Outcome == TransportOutcome.Released);
            var replacement = await b.AcquireAsync(bRequest with { Resource = boundResource, RequestId = Guid.NewGuid().ToString("D") }, entire.Token);
            Require(replacement.Outcome == TransportOutcome.Acquired && Greater(replacement.FencingToken!, ownedLease.FencingToken));
            var lost = await adapter.RenewAllAsync(authority, entire.Token);
            Require(lost.Authority is null && lost.ReleaseContinuation is not null);
            Require(!adapter.MayDispatch(authority) && !host.TryDispatch(authority) && !host.TryPublish(authority));
            Require(host.Dispatches == 1 && host.Publications == 1);
            var cleanup = await adapter.ContinueReleaseAsync(lost.ReleaseContinuation!, entire.Token);
            Require(cleanup.Continuation is null && cleanup.Leases.All(row => row.State is LeaseReleaseState.NotOwned or LeaseReleaseState.Released));
            Require((await b.InspectAsync(new(boundResource, ownerB, Guid.NewGuid().ToString("D")), entire.Token)).LeaseId == replacement.LeaseId);
            Require((await b.ReleaseAsync(new(replacement.LeaseId!, ownerB, replacement.FencingToken!, Guid.NewGuid().ToString("D")), entire.Token)).Outcome == TransportOutcome.Released);
            steps.Add("real-adapter-server-lease-loss-stops-dispatch-and-dry-run-publication-preserving-successor");
            // Actual successful renewal uses a new current adapter authority, never the old lost one.
            var renewedHeld = await adapter.AcquireAllAsync([Resource(4)], ownerA, ContinuumCoordinationAdapter.DefaultTtl, entire.Token);
            var renewed = await adapter.RenewAllAsync(renewedHeld.Authority!, entire.Token);
            Require(renewed.Authority is not null);
            Require((await adapter.ReleaseAllAsync(renewed.Authority!, entire.Token)).Continuation is null);
            steps.Add("real-adapter-renewal-quiescent-release-and-flushed-checkpoint-readback");
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            // Independent actual first dispositions; one failure never hides or skips another.
            if (a is not null) try { await a.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            if (b is not null) try { await b.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            if (store is not null) try { await store.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        }
        var pass = failures.Count == 0 && steps.Count == 5 && Stopwatch.GetElapsedTime(original) < TimeSpan.FromMinutes(3);
        var summary = new
        {
            Kind = "ContinuumActualDryRunControls-v1",
            Pass = pass,
            Steps = steps,
            Failures = failures.Count,
            ErrorTypes = failures.Select(error => error.GetType().Name).ToArray(),
            ProductionWorkflowAccepted = false,
            RealWorkerInvocations = 0,
            GitHubWrites = 0,
            ServerChanges = 0,
            ElapsedSeconds = Stopwatch.GetElapsedTime(original).TotalSeconds,
            UncertainLeasesRequireReconciliationOrTtl = !pass
        };
        if (root.Length != 0)
        {
            try
            {
                await using var output = new FileStream(Path.Combine(root, "control-result.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(output, summary);
                await output.FlushAsync(); output.Flush(true);
            }
            catch { pass = false; }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Kind = summary.Kind,
            Pass = pass,
            CompletedSteps = steps.Count,
            Failures = failures.Count,
            ProductionWorkflowAccepted = false,
            RealWorkerInvocations = 0,
            GitHubWrites = 0,
            ServerChanges = 0
        }));
        return pass ? 0 : 1;
    }

    private static ContinuumMcpTransport Transport(Configuration config, PrincipalProfile principal, TimeSpan timeout) =>
        new(config.Endpoint, config.Project, principal.PrincipalId, new ContinuumApiKeyFile(principal.PrivateKeyPath),
            new OperatorEvidence(config, principal), timeout);

    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Actual isolated Continuum control failed."); }
    private static bool Greater(string candidate, string prior) => long.Parse(candidate, CultureInfo.InvariantCulture) > long.Parse(prior, CultureInfo.InvariantCulture);

    private sealed record PrincipalProfile(string PrincipalId, string PrivateKeyPath, string CredentialSha256);
    private sealed record Configuration(Uri Endpoint, string Project, PrincipalProfile A, PrincipalProfile B, string JournalRoot,
        string OperatorIssuer, string SourceRevision, string ImageSha256, string MigrationEvidenceSha256, string IdentityProfileEvidenceSha256,
        DateTimeOffset VerifiedAt, DateTimeOffset ValidUntil);
    private sealed class OperatorEvidence(Configuration configuration, PrincipalProfile principal) : IContinuumDeploymentEvidenceSource
    {
        public Task<ContinuumDeploymentEvidence> ObserveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ContinuumDeploymentEvidence(configuration.Endpoint, configuration.Project, principal.PrincipalId,
                principal.CredentialSha256, configuration.OperatorIssuer, configuration.SourceRevision, configuration.ImageSha256,
                configuration.MigrationEvidenceSha256, configuration.IdentityProfileEvidenceSha256, configuration.VerifiedAt, configuration.ValidUntil));
        }
    }
}
