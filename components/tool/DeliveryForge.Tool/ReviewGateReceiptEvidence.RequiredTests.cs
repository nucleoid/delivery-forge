using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;

namespace DeliveryForge.Tool;

internal static partial class ReviewGateReceiptEvidence
{
    const int ReviewArtifactMaximum = 16 * 1024 * 1024;
    // One shared presentation budget for ALL gates in this review. This is a
    // restrictive subset of the producer contract, not a new read allowance.
    internal sealed class RequiredTestsBudget
    {
        internal int ReadsRemaining { get; private set; } = 3;
        internal int BytesRemaining { get; private set; } = 256 * 1024;
        internal int CasesRemaining { get; private set; } = 16;
        // Completed installed quality validates exactly two gates. Count up to
        // two entire fixed unavailable markers inside the SAME 8 KiB envelope,
        // even when a presented result also fits (conservative double reserve).
        int encodedRemaining = 8 * 1024 - 2 * 1024;
        internal void ReadCompleted(int bytes) { ReadsRemaining--; BytesRemaining -= bytes; }
        internal void CasesSelected(int count) { Need(count <= CasesRemaining, "case-list-exceeds-review-profile"); CasesRemaining -= count; }
        internal void Include(JsonElement projection)
        {
            // Same relative nesting/indentation and second JSON string encoding
            // used by QualityEvidence inside the full native prompt. Counting the
            // small carrier each time is conservative across multiple gates.
            var encoded = EncodedBytes(projection);
            Need(encoded <= encodedRemaining, "case-list-exceeds-review-profile");
            encodedRemaining -= encoded;
        }
        internal static int EncodedBytes(JsonElement projection)
        {
            var carrier = JsonSerializer.Serialize(new { QualityEvidence = new[] { new { RequiredTests = projection } } }, BoundedFiles.Json);
            return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(carrier));
        }
    }
    const string CaseQualification = "Producer-reported retained TRX claims, digest-bound to the actual receipt and cross-checked against producer inventory/execution DATA. The gate does not independently verify these per-case outcomes; this presentation issues no authority or certification.";
    sealed class PresentationUnavailable(string reason) : Exception(reason) { }
    internal static JsonElement Unavailable(string reason)
    {
        // Only fixed diagnostic codes, never arbitrary producer text. Keep the
        // ENTIRE escaped unavailable contribution inside its reserved 1 KiB.
        if (reason.Length > 64 || reason.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            reason = "unsupported-presentation";
        var marker = JsonSerializer.SerializeToElement(new
        { Presented = false, Reason = reason, Qualification = CaseQualification }, BoundedFiles.Json);
        if (RequiredTestsBudget.EncodedBytes(marker) > 1024)
            throw new InvalidOperationException("Fixed required-test unavailable marker exceeds its closed profile.");
        return marker;
    }

    // Presentation only. Callback I/O, ownership/currentness, cancellation and
    // budget failures escape unchanged. Only locally classified DATA failures
    // become unavailable; no caught failure can issue a passing gate or review.
    internal static JsonElement RequiredTestOutcomes(GateRunResult actual, JsonElement receipt,
        Func<string, int, byte[]?> readArtifact, RequiredTestsBudget budget, Func<int, long, bool>? hasReadCapacity = null)
    {
        if (actual.Outcome != GateOutcome.Pass || actual.Fixture || actual.SourceChanged || actual.Before != actual.After)
            return Unavailable("not-current-nonfixture-pass");
        var hashes = receipt.GetProperty("artifactHashes");
        if (receipt.GetProperty("baseCommit").GetString() != actual.After.BaseCommit ||
            receipt.GetProperty("headCommit").GetString() != actual.After.HeadCommit ||
            receipt.GetProperty("treeId").GetString() != actual.After.TreeId ||
            receipt.GetProperty("policyIdentity").GetString() != actual.PolicyIdentity ||
            hashes.EnumerateObject().Count() != actual.ArtifactHashes.Count ||
            hashes.EnumerateObject().Any(row => !actual.ArtifactHashes.TryGetValue(row.Name, out var hash) || hash != row.Value.GetString()))
            throw new WorkerEnvelopeException("Required-test presentation requires the same actual current gate tuple and receipt inventory.");
        if (!hashes.TryGetProperty("producer-bundle/manifest.json", out _)) return Unavailable("no-producer-manifest");
        byte[] Read(string relative, int maximum)
        {
            if (!relative.StartsWith("producer-bundle/", StringComparison.Ordinal) || relative.Contains('\\') || relative.Contains(':') ||
                relative.Split('/').Any(part => part is "" or "." or "..") || !hashes.TryGetProperty(relative, out var expected))
                throw new WorkerEnvelopeException("Required-test artifact is outside the retained receipt inventory.");
            Need(budget.ReadsRemaining > 0 && budget.BytesRemaining > 0, "artifact-read-profile-exceeded");
            Need(hasReadCapacity is null || hasReadCapacity(checked(2 * budget.ReadsRemaining), checked((long)budget.BytesRemaining + budget.ReadsRemaining)), "artifact-read-credit-unavailable");
            maximum = Math.Min(maximum, budget.BytesRemaining);
            var bytes = readArtifact(relative, maximum);
            Need(bytes is not null, "artifact-presentation-size-unsupported");
            budget.ReadCompleted(bytes!.Length);
            if (bytes.Length > maximum || "sha256:" + BoundedFiles.Sha(bytes) != expected.GetString())
                throw new WorkerEnvelopeException("Required-test artifact bytes differ from the actual receipt.");
            return bytes;
        }
        try
        {
            // All-pass, scored-context-linked presentation SUBSET only. Successful
            // producer shapes with skips or no scored reachability are explicitly
            // unavailable here. ArtifactCaptureAdapter reserves the root name.
            // JSON parsing retains the existing ProducerJson profile, while the
            // smaller shared read profile can decline before parsing.
            using var manifest = PresentationJson(Read("producer-bundle/manifest.json", 8 * 1024 * 1024));
            var body = manifest.RootElement; var revision = Member(body, "revision");
            Need(Text(revision, "base") == actual.After.BaseCommit && Text(revision, "head") == actual.After.HeadCommit &&
                Flag(Member(body, "capture"), "actualBindingComplete") && Flag(Member(body, "capture"), "captureConsistent"), "manifest-current-binding-unavailable");
            var artifacts = Array(body, "artifacts");
            Need(artifacts.Select(row => Text(row, "id")).Distinct(StringComparer.Ordinal).Count() == artifacts.Length &&
                artifacts.Select(row => Text(row, "locator")).Distinct(StringComparer.Ordinal).Count() == artifacts.Length, "duplicate-artifact-association");
            byte[] Artifact(JsonElement row)
            {
                var length = Integer(row, "length");
                Need(length is > 0 and <= ReviewArtifactMaximum && length <= budget.BytesRemaining, "artifact-presentation-size-unsupported");
                var bytes = Read("producer-bundle/" + Text(row, "locator"), ReviewArtifactMaximum);
                if (bytes.LongLength != length || BoundedFiles.Sha(bytes) != Text(row, "sha256"))
                    throw new WorkerEnvelopeException("Required-test artifact extent or manifest hash differs.");
                return bytes;
            }
            var inventories = artifacts.Where(row => Text(row, "kind") == "required-test-inventory").ToArray();
            Need(inventories.Length == 1, "required-test-inventory-unavailable");
            using var inventory = PresentationJson(Artifact(inventories[0]));
            var root = inventory.RootElement;
            Need(Text(root, "provider") == "finite-sdk-required-test-inventory-v1" && !Flag(root, "reusableRecipe"), "inventory-provider-unsupported");
            var projected = new List<object>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            var executions = Array(body, "executions");
            Need(executions.Select(row => Text(row, "id")).Distinct(StringComparer.Ordinal).Count() == executions.Length, "duplicate-execution-association");
            foreach (var test in Array(root, "tests"))
            {
                var graph = Text(test, "graphId"); var project = Text(test, "project");
                var id = Text(test, "trx"); var passed = Integer(test, "passed");
                Need(Hex(graph, 64) && seen.Add(graph) && id == "required-test-" + graph + "-trx" &&
                    passed is > 0 and <= 32768 && Integer(test, "rawExitCode") == 0, "test-graph-association-unsupported");
                var linked = executions.Where(row => Text(row, "testProject") == project &&
                    Text(row, "buildId") is { Length: 70 } build && build.StartsWith("build-", StringComparison.Ordinal) && Hex(build[6..], 64) &&
                    Text(row, "id") == "test-" + build[6..] + "-" + graph).ToArray();
                Need(linked.Length > 0 && linked.All(row => Flag(row, "completed") && Integer(row, "exitCode") == 0 &&
                    Integer(row, "totalTests") == passed && Integer(row, "passedTests") == passed &&
                    Integer(row, "failedTests") == 0 && Integer(row, "skippedTests") == 0), "execution-counts-unsupported");
                var matches = artifacts.Where(row => Text(row, "id") == id && Text(row, "kind") == "required-test-result" && Text(row, "format") == "trx").ToArray();
                Need(matches.Length == 1, "graph-trx-unavailable");
                budget.CasesSelected((int)passed);
                var raw = Artifact(matches[0]); var cases = TrxCases(raw, (int)passed);
                projected.Add(new { Project = project, GraphId = graph, ArtifactId = id, Bytes = raw.LongLength,
                    Sha256 = "sha256:" + BoundedFiles.Sha(raw), CompleteCaseCount = cases.Length, Cases = cases });
            }
            Need(seen.Count > 0, "required-test-members-unavailable");
            var presentation = JsonSerializer.SerializeToElement(new { Presented = true, Qualification = CaseQualification, Tests = projected }, BoundedFiles.Json);
            budget.Include(presentation);
            return presentation;
        }
        catch (PresentationUnavailable error) { return Unavailable(error.Message); }
    }

    static object[] TrxCases(byte[] raw, int passed)
    {
        XDocument xml;
        // Parsing is purely in-memory. Never wrap the admitted artifact callback
        // in an XmlException/IOException or other broad exception filter.
        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ReviewArtifactMaximum });
            xml = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException) { throw new PresentationUnavailable("trx-xml-unsupported"); }
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var root = xml.Root;
        Need(root is not null && root.Name == ns + "TestRun", "trx-namespace-unsupported");
        XElement Single(XElement parent, string name)
        {
            var rows = parent.Elements(ns + name).ToArray();
            Need(rows.Length == 1, "trx-structure-unsupported"); return rows[0];
        }
        string Value(XElement row, string name)
        {
            var value = (string?)row.Attribute(name);
            Need(!string.IsNullOrWhiteSpace(value), "trx-identity-unavailable"); return value!;
        }
        Guid Id(XElement row, string name)
        {
            Need(Guid.TryParse(Value(row, name), out var id) && id != Guid.Empty, "trx-identity-unsupported"); return id;
        }
        int Number(XElement row, string name, bool required = false)
        {
            var value = (string?)row.Attribute(name);
            if (value is null && !required) return 0;
            Need(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0, "trx-counter-unsupported"); return number;
        }
        var results = Single(root!, "Results").Elements().ToArray();
        var definitions = Single(root!, "TestDefinitions").Elements().ToArray();
        var entries = Single(root!, "TestEntries").Elements().ToArray();
        Need(results.Length == passed && definitions.Length == passed && entries.Length == passed &&
            results.All(row => row.Name == ns + "UnitTestResult") && definitions.All(row => row.Name == ns + "UnitTest") &&
            entries.All(row => row.Name == ns + "TestEntry"), "trx-direct-results-unsupported");
        foreach (var name in new[] { "UnitTestResult", "UnitTest", "TestEntry" })
            Need(root!.Descendants().Count(row => row.Name.LocalName == name) == passed, "trx-nested-results-unsupported");
        var summary = Single(root!, "ResultSummary"); var counters = Single(summary, "Counters");
        Need(Value(summary, "outcome") is "Completed" or "Passed" && !root!.Descendants().Any(row => row.Name.LocalName is "RunInfo" or "ErrorInfo"), "trx-summary-unsupported");
        Need(Number(counters, "total", true) == passed && Number(counters, "executed", true) == passed && Number(counters, "passed", true) == passed, "trx-counts-unsupported");
        foreach (var name in new[] { "failed", "notExecuted", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "disconnected", "warning", "inProgress", "pending" })
            Need(Number(counters, name) == 0, "trx-nonpassing-counts");
        Need(Number(counters, "completed") is 0 || Number(counters, "completed") == passed, "trx-completed-count-unsupported");
        var byId = new Dictionary<Guid, XElement>(); var byExecution = new Dictionary<Guid, XElement>();
        foreach (var definition in definitions) Need(byId.TryAdd(Id(definition, "id"), definition), "trx-duplicate-test-identity");
        foreach (var entry in entries) Need(byExecution.TryAdd(Id(entry, "executionId"), entry), "trx-duplicate-execution-identity");
        var names = new HashSet<string>(StringComparer.Ordinal); var testIds = new HashSet<Guid>(); var executionIds = new HashSet<Guid>();
        var cases = new List<object>();
        foreach (var result in results)
        {
            var id = Id(result, "testId"); var execution = Id(result, "executionId"); var name = Value(result, "testName");
            Need(Value(result, "outcome") == "Passed" && names.Add(name) && testIds.Add(id) && executionIds.Add(execution), "trx-duplicate-or-nonpassing-result");
            Need(byId.ContainsKey(id) && byExecution.ContainsKey(execution), "trx-result-join-unavailable");
            var definition = byId[id]; var entry = byExecution[execution];
            Need(Value(definition, "name") == name && Id(Single(definition, "Execution"), "id") == execution &&
                Id(entry, "testId") == id && Id(entry, "testListId") == Id(result, "testListId"), "trx-result-join-unsupported");
            cases.Add(new { Name = name, Outcome = "Passed", TestId = id, ExecutionId = execution });
        }
        return cases.ToArray();
    }

    // Match the existing gate's ProducerJson limits/duplicate and Unicode rules;
    // keep this presentation parser private instead of widening its API/friends.
    static JsonDocument PresentationJson(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 8 * 1024 * 1024) throw new PresentationUnavailable("json-presentation-size-unsupported");
        try { _ = new UTF8Encoding(false, true).GetCharCount(bytes); }
        catch (DecoderFallbackException) { throw new PresentationUnavailable("json-utf8-unsupported"); }
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, new() { MaxDepth = 64 }); }
        catch (JsonException) { throw new PresentationUnavailable("json-syntax-unsupported"); }
        try { var count = 0; Check(document.RootElement, ref count); return document; }
        catch (PresentationUnavailable) { document.Dispose(); throw; }
        catch (InvalidOperationException) { document.Dispose(); throw new PresentationUnavailable("json-string-unsupported"); }
        static void String(string text)
        {
            Need(text.Length <= 16384, "json-string-size-unsupported");
            for (var index = 0; index < text.Length; index++)
                if (char.IsHighSurrogate(text[index])) Need(++index < text.Length && char.IsLowSurrogate(text[index]), "json-surrogate-unsupported");
                else Need(!char.IsLowSurrogate(text[index]), "json-surrogate-unsupported");
        }
        static void Check(JsonElement item, ref int count)
        {
            Need(++count <= 1000000, "json-value-count-unsupported");
            if (item.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    Need(property.Name.Length <= 128 && names.Add(property.Name), "json-duplicate-or-oversized-member");
                    String(property.Name); Check(property.Value, ref count);
                }
            }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                Need(item.GetArrayLength() <= 100000, "json-array-size-unsupported");
                foreach (var value in item.EnumerateArray()) Check(value, ref count);
            }
            else if (item.ValueKind == JsonValueKind.String) String(item.GetString()!);
        }
    }
    static void Need(bool condition, string reason) { if (!condition) throw new PresentationUnavailable(reason); }
    static JsonElement Member(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) ? value : throw new PresentationUnavailable("json-member-unavailable");
    static string Text(JsonElement row, string name) => Member(row, name) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrEmpty(value.GetString()) ? value.GetString()! : throw new PresentationUnavailable("json-text-unsupported");
    static bool Flag(JsonElement row, string name) => Member(row, name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new PresentationUnavailable("json-flag-unsupported") };
    static long Integer(JsonElement row, string name) => Member(row, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : throw new PresentationUnavailable("json-integer-unsupported");
    static JsonElement[] Array(JsonElement row, string name) => Member(row, name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray().ToArray() : throw new PresentationUnavailable("json-array-unsupported");
    static bool Hex(string value, int length) => value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
