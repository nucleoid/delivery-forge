using System.Text.Json;
using DeliveryForge.Execution;

namespace DeliveryForge.Tool;

// Private append-only observation records; not canonical v1 contracts and not
// capabilities. Reconstructed phases can never instantiate an actual owner.
internal sealed record BatchMarker(string Kind, string RunId, string ConfigurationSha256, string SelectionIdentity);
internal sealed record BatchObservation(int Version, string ConfigurationSha256, string SelectionIdentity,
    string PolicyIdentity, string FairnessIdentity, long OriginalStarted, TimeSpan OriginalWhole, TimeSpan OriginalWork,
    TimeSpan ObservedWorkRemaining, IReadOnlyDictionary<string, string> Plans, IReadOnlyDictionary<string, string> Phases,
    IReadOnlyDictionary<string, string> CompletedHeads, bool Paused, bool Stopped);
internal sealed record ReconstructedBatchData(string RunId, IReadOnlyDictionary<string, string> Phases,
    IReadOnlyDictionary<string, string> Plans, string PolicyIdentity, bool Paused, bool Stopped,
    IReadOnlyList<string> Limitations)
{
    public bool MayDispatch => false;
}

internal sealed partial class ProductionBatchOwner
{
    internal static string SelectionIdentity(ProductionBatchSelection selection) => "sha256:" + BoundedFiles.Sha(
        DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(JsonSerializer.SerializeToUtf8Bytes(selection, BoundedFiles.Json)));

    internal async Task<ReconstructedBatchData> ReadQueueDataAsync()
    {
        _ = InstalledHostAdmission.ReadExact(containingPin, original, 65536);
        var marker = BoundedFiles.ReadJson<BatchMarker>(Path.Combine(selected.StoreRoot, "batch-selection.json"), original, 65536);
        var identity = SelectionIdentity(selected);
        if (marker.Kind != "ProductionBatchSelectionData-v1" || marker.ConfigurationSha256 != containingPin.Sha256 || marker.SelectionIdentity != identity ||
            !System.Text.RegularExpressions.Regex.IsMatch(marker.RunId, "^batch-[a-f0-9]{32}$"))
            throw new IOException("Stored batch selection differs from the exact independently selected configuration.");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in selected.Nodes)
        {
            _ = InstalledHostAdmission.ReadExact(node.Plan, original, 524288);
            var leaf = TrustedConfiguration.LoadSelected(node.Configuration.Path, original, node.Configuration);
            if (string.IsNullOrWhiteSpace(leaf.ExpectedBundleIdentity)) throw new IOException("Stored node lacks independent plan expectations.");
            expected.Add(node.NodeId, leaf.ExpectedBundleIdentity);
        }
        var store = new RunStore(selected.StoreRoot);
        var recovered = await Observe(store.RecoverAsync(marker.RunId, original.Token)).ConfigureAwait(false);
        if (recovered.Records.Count is < 1 or > 128 || recovered.Records.Any(record => record.Kind != "production-batch-observation"))
            throw new IOException("Missing, foreign or oversized batch history cannot reconstruct queue data.");
        var rows = recovered.Records.Select(store.ReadPayload<BatchObservation>).ToArray();
        ValidateHistory(rows, containingPin.Sha256, identity, selected, expected);
        var latest = rows[^1];
        return new(marker.RunId, new Dictionary<string, string>(latest.Phases, StringComparer.Ordinal),
            new Dictionary<string, string>(latest.Plans, StringComparer.Ordinal), latest.PolicyIdentity, latest.Paused, latest.Stopped,
            ["Reconstructed DATA only; no owner, ticket, writer, resource reservation or host session was restored.",
             "Fresh supported Git/child/writer/lease/host reconciliation is required; stored terminal/paused phases never authorize resume.",
             "Historical original clocks and reserved spend are not a new execution allowance."]);
    }

    internal static void ValidateHistory(IReadOnlyList<BatchObservation> rows, string configurationSha, string identity,
        ProductionBatchSelection selection, IReadOnlyDictionary<string, string> expectedPlans)
    {
        if (rows.Count is < 1 or > 128) throw new IOException("Finite nonempty batch history required.");
        var first = rows[0]; BatchObservation? previous = null;
        if (first.Phases is null || first.CompletedHeads is null || first.Phases.Values.Any(phase => phase != "Pending") || first.CompletedHeads.Count != 0)
            throw new IOException("Batch history must begin with its original unstarted queue data.");
        foreach (var row in rows)
        {
            if (row.Version != 1 || row.ConfigurationSha256 != configurationSha || row.SelectionIdentity != identity ||
                row.PolicyIdentity != selection.Policy.Identity || row.FairnessIdentity != selection.Fairness.Identity ||
                row.OriginalStarted <= 0 || row.OriginalStarted != first.OriginalStarted || row.OriginalWhole != first.OriginalWhole ||
                row.OriginalWork != first.OriginalWork || row.OriginalWhole > TimeSpan.FromSeconds(1200) || row.OriginalWhole <= TimeSpan.Zero ||
                row.OriginalWork <= TimeSpan.Zero || row.OriginalWork >= row.OriginalWhole || row.ObservedWorkRemaining <= TimeSpan.Zero ||
                row.ObservedWorkRemaining > row.OriginalWork || previous is not null && row.ObservedWorkRemaining > previous.ObservedWorkRemaining ||
                row.Plans is null || row.Phases is null || row.CompletedHeads is null || row.Plans.Count != expectedPlans.Count || row.Phases.Count != expectedPlans.Count ||
                expectedPlans.Any(pair => !row.Plans.TryGetValue(pair.Key, out var plan) || plan != pair.Value || !row.Phases.ContainsKey(pair.Key)) ||
                row.Phases.Values.Any(phase => phase is not ("Pending" or "Selected" or "Pass" or "Fail" or "Incomplete" or "ReplanRequired" or "LocalComplete")) ||
                row.CompletedHeads.Any(pair => !expectedPlans.ContainsKey(pair.Key) || row.Phases[pair.Key] is "Pending" or "Selected" or "ReplanRequired" || pair.Value.Length is not (40 or 64) || !pair.Value.All(char.IsAsciiHexDigitLower)) ||
                row.Phases.Any(pair => pair.Value is "Pass" or "Fail" or "Incomplete" or "LocalComplete" && !row.CompletedHeads.ContainsKey(pair.Key)))
                throw new IOException("Batch history changed its immutable selection, policy, node identity or original finite lifetime.");
            if (previous is not null)
            {
                if (previous.Stopped && !row.Stopped || previous.Paused && !row.Paused ||
                    previous.Phases.Any(pair => !ValidPhaseAdvance(pair.Value, row.Phases[pair.Key])) ||
                    previous.CompletedHeads.Any(pair => !row.CompletedHeads.TryGetValue(pair.Key, out var head) || head != pair.Value))
                    throw new IOException("Batch history rewrote retained outcomes, controls or committed heads.");
            }
            previous = row;
        }
    }
    static bool ValidPhaseAdvance(string previous, string next) => previous == next ||
        previous == "Pending" && (next is "Selected" or "ReplanRequired") ||
        previous == "Selected" && (next is "Pass" or "Fail" or "Incomplete" or "LocalComplete");
}
