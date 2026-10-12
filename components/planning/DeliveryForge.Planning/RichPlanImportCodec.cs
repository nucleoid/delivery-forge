using System.Globalization;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;

namespace DeliveryForge.Planning;

// Closed owner material codec. Claims remain DATA until fresh reader/intake reconciliation.
internal sealed class RichPlanImportCodec : IDisposable
{
    private readonly JsonDocument _document;
    internal JsonElement Envelope => _document.RootElement;
    internal JsonElement Material => Envelope.GetProperty("plan");
    internal string Identity => Text(Envelope, "identity");
    internal string ContractIdentity => Text(Envelope, "contractIdentity");
    internal string Revision => Text(Envelope, "declaredRevision");
    internal string? Supersedes => NullableText(Envelope, "supersedes");
    internal DateTimeOffset CreatedAt => Date(Text(Envelope, "createdAt"));
    internal RichPlanImportCodec(byte[] owned)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(owned);
            ValidateEscapedScalars(owned);
            _document = JsonDocument.Parse(owned, new JsonDocumentOptions { MaxDepth = 64 });
            var count = 0; Bounds(Envelope, ref count);
            if (!Envelope.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "planning-bundle")
                throw new PlanningException("A complete rich planning bundle is required; a PlanContract projection is insufficient.");
            Object(Envelope, "schemaVersion", "kind", "identity", "contractIdentity", "declaredRevision", "planRevision", "contentDigest", "supersedes", "createdAt", "plan");
            Require(Text(Envelope, "schemaVersion") == "1.0.0", "Unsupported planning bundle version.");
            foreach (var name in new[] { "identity", "contractIdentity", "contentDigest" }) Digest(Text(Envelope, name));
            if (Supersedes is not null) Digest(Supersedes);
            _ = CreatedAt;
            Require(Identity == CanonicalJson.ComputeIdentity(owned), "Rich bundle identity does not match its complete bytes.");
            Require(Text(Envelope, "planRevision") == Revision + "@" + Text(Envelope, "contentDigest"), "Plan revision/digest projection differs.");
            Shape();
        }
        catch (PlanningException) { _document?.Dispose(); throw; }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
        { _document?.Dispose(); throw new PlanningException("Malformed bounded rich planning bundle: " + error.Message); }
    }
    private void Shape()
    {
        Object(Material, "repository", "workItem", "mode", "intakeDepth", "intent", "requestedCeiling", "baseCommit", "baseTree", "scope", "acceptanceCriteria",
            "repositoryObservations", "intake", "provenance", "changeMap", "dependencyDag", "gates", "rollout", "unknowns", "limitations");
        foreach (var key in new[] { "repository", "workItem", "mode", "intakeDepth", "intent", "requestedCeiling", "baseCommit", "baseTree" }) _ = Text(Material, key);
        GitObject(Text(Material, "baseCommit")); GitObject(Text(Material, "baseTree"));
        _ = Depth(Text(Material, "intakeDepth"));
        Object(Material.GetProperty("scope"), "included", "excluded");
        foreach (var path in Strings(Material.GetProperty("scope"), "included").Concat(Strings(Material.GetProperty("scope"), "excluded"))) Path(path);
        _ = Strings(Material, "acceptanceCriteria"); _ = Strings(Material, "limitations");
        var observations = Material.GetProperty("repositoryObservations");
        Object(observations, "requestedRef", "exactBranchReferenceVerified", "headCommit", "headTree", "detachedHead", "dirty", "shallow", "submodules");
        foreach (var key in new[] { "requestedRef", "headCommit", "headTree" }) _ = Text(observations, key);
        GitObject(Text(observations, "headCommit")); GitObject(Text(observations, "headTree"));
        foreach (var key in new[] { "exactBranchReferenceVerified", "detachedHead", "dirty", "shallow" }) _ = Bool(observations, key);
        _ = Strings(observations, "submodules");
        var intake = Material.GetProperty("intake");
        Object(intake, "ready", "depth", "importedContextRequirement", "importedContextAvailable", "limitations", "advisoryCaveats", "advisoryMeanings", "advisoryNotices");
        _ = Bool(intake, "ready"); _ = Bool(intake, "importedContextAvailable"); _ = Strings(intake, "limitations");
        Require(Text(intake, "depth") == Text(Material, "intakeDepth"), "Intake depth projection differs.");
        _ = Requirement(Text(intake, "importedContextRequirement"));
        Advisory<EvidenceCaveatCode>(intake, "advisoryCaveats", PlanFreezer.CaveatWireValue, IntakePlanner.CaveatText);
        Advisory<ImportedMeaningCode>(intake, "advisoryMeanings", PlanFreezer.MeaningWireValue, IntakePlanner.MeaningText);
        Advisory<PlanningNoticeCode>(intake, "advisoryNotices", PlanFreezer.NoticeWireValue, IntakePlanner.NoticeText);
        foreach (var item in Array(Material, "provenance", 256))
        {
            Object(item, "sourceKind", "producerKind", "locatorKind", "locator", "digest", "observedAt", "caveats", "supersedes", "isComplete", "requirement", "repositorySafety");
            foreach (var key in new[] { "sourceKind", "producerKind", "locatorKind", "locator" }) _ = Text(item, key);
            _ = Date(Text(item, "observedAt")); _ = Bool(item, "isComplete"); _ = Strings(item, "caveats");
            _ = Requirement(Text(item, "requirement"));
            foreach (var key in new[] { "digest", "supersedes" }) if (NullableText(item, key) is { } digest && key == "digest") Digest(digest);
            var safety = item.GetProperty("repositorySafety");
            if (safety.ValueKind != JsonValueKind.Null)
            {
                Object(safety, "repositoryCommit", "repositoryTree", "repositoryIsSymlink", "symlinkResolution", "repositoryGenerationClassification");
                _ = Text(safety, "repositoryCommit"); _ = Text(safety, "repositoryTree"); _ = Bool(safety, "repositoryIsSymlink");
                _ = Text(safety, "symlinkResolution"); _ = Text(safety, "repositoryGenerationClassification");
            }
        }
        foreach (var item in Array(Material, "changeMap")) { Object(item, "path", "symbol", "effect"); Path(Text(item, "path")); _ = Text(item, "symbol"); _ = Text(item, "effect"); }
        foreach (var item in Array(Material, "dependencyDag")) { Object(item, "id", "dependsOn", "integrationCondition"); _ = Text(item, "id"); _ = Strings(item, "dependsOn"); _ = Text(item, "integrationCondition"); }
        foreach (var item in Array(Material, "gates", 256))
        {
            Object(item, "id", "command", "displayCommand", "expectedOutcome"); _ = Text(item, "id"); _ = Text(item, "expectedOutcome");
            var command = Command(item.GetProperty("command"));
            Require(command.Render() == Text(item, "displayCommand"), "Rendered command differs from its typed arguments.");
        }
        Object(Material.GetProperty("rollout"), "compatibility", "configuration", "secrets", "migration", "reauthentication", "backfill", "observability", "rollback", "operatorActions");
        foreach (var key in new[] { "compatibility", "configuration", "secrets", "migration", "reauthentication", "backfill", "observability", "rollback" }) _ = Text(Material.GetProperty("rollout"), key);
        _ = Strings(Material.GetProperty("rollout"), "operatorActions");
        foreach (var item in Array(Material, "unknowns")) { Object(item, "description", "owner", "blocksReadiness"); _ = Text(item, "description"); _ = Text(item, "owner"); _ = Bool(item, "blocksReadiness"); }
    }
    internal async Task<PlanDraft> Draft(RepositoryContext current, GitRepositoryContextReader reader, PlanImportLifetime life,
        ImportedContextEnvelope? freshImported)
    {
        var scope = Material.GetProperty("scope");
        var request = new PlanningRequest(Text(Material, "repository"), Text(Material, "workItem"), Text(Material, "mode"), Text(Material, "intent"),
            Strings(scope, "included"), Strings(scope, "excluded"), Strings(Material, "acceptanceCriteria"), Text(Material, "requestedCeiling"), Depth(Text(Material, "intakeDepth")));
        var evidence = new List<EvidenceItem>();
        foreach (var item in Array(Material, "provenance", 256))
        {
            life.Check();
            Require(Text(item, "locatorKind") == "public", "Portable provenance must have a public locator.");
            var source = Text(item, "sourceKind"); var producer = Text(item, "producerKind"); var locator = Text(item, "locator");
            var caveats = Strings(item, "caveats").Select(message => Enum.GetValues<EvidenceCaveatCode>().SingleOrDefault(code => IntakePlanner.CaveatText(code) == message,
                (EvidenceCaveatCode)(-1))).ToArray();
            Require(caveats.All(code => Enum.IsDefined(code)), "Unknown provenance caveat message.");
            var observed = Date(Text(item, "observedAt")); var required = Requirement(Text(item, "requirement"));
            EvidenceItem actual;
            if (source == "repository" && producer == "repository-at-base" && locator.StartsWith("git:", StringComparison.Ordinal))
            {
                Path(locator[4..]);
                actual = EvidenceItem.FromRepositoryFile(await life.Run(() => reader.ReadFileAsync(current, locator[4..], life.Token)), observed, caveats, required);
                Require(actual.Digest == NullableText(item, "digest"), "Repository evidence digest differs from actual base bytes.");
            }
            else if (source == "user" && producer == "explicit-user-public")
                actual = EvidenceItem.FromExplicitPublicUserIntent(locator, NullableText(item, "digest"), observed, caveats, NullableText(item, "supersedes"), Bool(item, "isComplete"), required);
            else if (source == "policy" && producer == "deterministic-generated")
                actual = EvidenceItem.FromDeterministicPolicyFact(locator, NullableText(item, "digest"), observed, caveats, NullableText(item, "supersedes"), Bool(item, "isComplete"), required);
            else throw new PlanningException("Unsupported public provenance producer/source combination.");
            evidence.Add(actual);
        }
        if (freshImported is not null)
        {
            var verified = new List<ImportedContextEntry>();
            foreach (var old in freshImported.Entries)
            {
                var entry = new ImportedContextEntry(old.Kind, old.Locator, old.Summary, old.Digest, old.ObservedAt, old.Stale, old.Truncated, old.Heuristic,
                    CheckoutVerification.Unverified, old.CheckoutDigest, old.Requirement, old.DistilledMeaning);
                if (entry.Locator.StartsWith("git:", StringComparison.Ordinal))
                    entry = ImportedContextVerifier.VerifyAgainst(entry, await life.Run(() => reader.ReadFileAsync(current, entry.Locator[4..], life.Token)));
                verified.Add(entry);
            }
            freshImported = new(freshImported.SchemaVersion, verified.ToArray(), freshImported.Conflicts.ToArray(), freshImported.Limitations.ToArray());
        }
        var intake = IntakePlanner.Assess(request, evidence, freshImported, Requirement(Text(Material.GetProperty("intake"), "importedContextRequirement")));
        var rollout = Material.GetProperty("rollout");
        return new(request, current, evidence,
            Array(Material, "changeMap").Select(item => new ChangeTarget(Text(item, "path"), Text(item, "symbol"), Text(item, "effect"))).ToArray(),
            Array(Material, "dependencyDag").Select(item => new DependencyNode(Text(item, "id"), Strings(item, "dependsOn"), Text(item, "integrationCondition"))).ToArray(),
            Array(Material, "gates", 256).Select(item => new PlanGate(Text(item, "id"), Command(item.GetProperty("command")), Text(item, "expectedOutcome"))).ToArray(),
            new(Text(rollout, "compatibility"), Text(rollout, "configuration"), Text(rollout, "secrets"), Text(rollout, "migration"), Text(rollout, "reauthentication"),
                Text(rollout, "backfill"), Text(rollout, "observability"), Text(rollout, "rollback"), Strings(rollout, "operatorActions")),
            Array(Material, "unknowns").Select(item => new PlanUnknown(Text(item, "description"), Text(item, "owner"), Bool(item, "blocksReadiness"))).ToArray(), intake);
    }
    private static PlanCommand Command(JsonElement value)
    {
        Object(value, "executable", "arguments");
        var args = Array(value, "arguments", 256).Select(item =>
        {
            Object(item, "kind", "value"); var text = Text(item, "value"); Require(text.Length <= 4096, "Command argument exceeds4096.");
            return Text(item, "kind") switch
            { "literal" => PlanCommandArgument.Literal(text), "placeholder" => PlanCommandArgument.Placeholder(text),
                "secret-reference" => PlanCommandArgument.SecretReference(text), _ => throw new PlanningException("Unknown command argument kind.") };
        }).ToArray();
        var result = new PlanCommand(Text(value, "executable"), args); CommandRenderer.Validate(result); return result;
    }
    private static void Advisory<T>(JsonElement intake, string key, Func<T, string> wire, Func<T, string> message) where T : struct, Enum
    {
        var known = Enum.GetValues<T>().ToDictionary(wire, message, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Array(intake, key, 256))
        { Object(item, "code", "message"); var code = Text(item, "code"); Require(seen.Add(code) && known.TryGetValue(code, out var expected) && expected == Text(item, "message"), "Unknown/duplicate advisory code or message."); }
    }
    private static void Bounds(JsonElement value, ref int count)
    {
        Require(++count <= 200_000, "Rich bundle aggregate element bound exceeded.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) { Require(keys.Add(field.Name) && field.Name.Length <= 128, "Duplicate decoded or oversized field."); Bounds(field.Value, ref count); }
        }
        else if (value.ValueKind == JsonValueKind.Array) { Require(value.GetArrayLength() <= 10_000, "Rich collection exceeds named10000 bound."); foreach (var item in value.EnumerateArray()) Bounds(item, ref count); }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!; Require(text.Length <= 16_384, "Rich string exceeds named bound.");
            for (var i = 0; i < text.Length; i++) if (char.IsSurrogate(text[i])) { Require(char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), "Invalid Unicode scalar."); i++; }
        }
        else if (value.ValueKind == JsonValueKind.Number) Require(value.TryGetInt64(out _), "Noninteger or overflowing rich number.");
    }
    internal static string Text(JsonElement value, string key)
    { var field = value.GetProperty(key); Require(field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString()), "Expected nonempty rich string: " + key); return field.GetString()!; }
    private static string? NullableText(JsonElement value, string key) => value.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Text(value, key);
    internal static bool Bool(JsonElement value, string key)
    { var field = value.GetProperty(key); Require(field.ValueKind is JsonValueKind.True or JsonValueKind.False, "Expected rich boolean: " + key); return field.GetBoolean(); }
    internal static string[] Strings(JsonElement value, string key) => Array(value, key).Select(item =>
    { Require(item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()), "Invalid rich string array: " + key); return item.GetString()!; }).ToArray();
    private static JsonElement[] Array(JsonElement value, string key, int maximum = 10_000)
    { var field = value.GetProperty(key); Require(field.ValueKind == JsonValueKind.Array && field.GetArrayLength() <= maximum, "Invalid/bounded rich array: " + key); return field.EnumerateArray().ToArray(); }
    private static void Object(JsonElement value, params string[] fields)
    { Require(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields), "Closed rich object fields differ."); }
    private static IntakeDepth Depth(string value) => value switch { "minimal" => IntakeDepth.Minimal, "deep" => IntakeDepth.Deep, _ => throw new PlanningException("Unknown intake depth.") };
    private static EvidenceRequirement Requirement(string value) => value switch { "optional" => EvidenceRequirement.Optional, "required" => EvidenceRequirement.Required, _ => throw new PlanningException("Unknown evidence requirement.") };
    private static DateTimeOffset Date(string value)
    { Require(DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result), "Rich time must be exact UTC."); return result; }
    private static void Path(string value)
    { Require(!value.Contains('\\') && !value.Contains('\0') && !PortableMaterial.IsAbsolutePath(value) && !value.Split('/').Any(part => part is "" or "." or ".."), "Rich repository path is unsafe."); }
    private static void ValidateEscapedScalars(ReadOnlySpan<byte> owned)
    {
        var reader = new Utf8JsonReader(owned, new JsonReaderOptions { MaxDepth = 64 });
        while (reader.Read())
        {
            if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName)) continue;
            var token = reader.ValueSpan;
            for (var i = 0; i < token.Length; i++)
            {
                if (token[i] != (byte)'\\') continue;
                Require(++i < token.Length, "Incomplete JSON string escape.");
                if (token[i] != (byte)'u') continue;
                var scalar = Hex(token, i + 1); i += 4;
                if (scalar is >= 0xD800 and <= 0xDBFF)
                {
                    Require(i + 6 < token.Length && token[i + 1] == (byte)'\\' && token[i + 2] == (byte)'u', "Unpaired high surrogate escape.");
                    var low = Hex(token, i + 3); Require(low is >= 0xDC00 and <= 0xDFFF, "Unpaired high surrogate escape."); i += 6;
                }
                else Require(scalar is < 0xDC00 or > 0xDFFF, "Unpaired low surrogate escape.");
            }
        }
    }
    private static int Hex(ReadOnlySpan<byte> bytes, int start)
    {
        Require(start + 4 <= bytes.Length, "Incomplete Unicode escape."); var value = 0;
        for (var i = start; i < start + 4; i++)
        { var digit = bytes[i] switch { >= (byte)'0' and <= (byte)'9' => bytes[i] - '0', >= (byte)'a' and <= (byte)'f' => bytes[i] - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => bytes[i] - 'A' + 10, _ => -1 }; Require(digit >= 0, "Invalid Unicode escape."); value = value * 16 + digit; }
        return value;
    }
    private static void Digest(string value) => Require(value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "Malformed rich digest.");
    private static void GitObject(string value) => Require(value.Length is 40 or 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "Malformed exact Git object identity.");
    internal static void Require(bool condition, string message) { if (!condition) throw new PlanningException(message); }
    public void Dispose() => _document.Dispose();
}
