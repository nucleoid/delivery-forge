using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeliveryForge.Evidence.Adapters;

// Parsed producer claims are data. They never issue capability, baseline or gate authority.
internal sealed record MutationReportData(
    GateOutcome ClaimedOutcome,
    int ClaimedExitCode,
    string RunId,
    int? Enumerated,
    int Selected,
    int Executed,
    int Killed,
    int Survived,
    int FreshUncovered,
    int Omitted,
    int CompileInvalid,
    int Errors,
    string? NotApplicableRationale);

internal sealed class MutationReportFormatException(string message) : Exception(message);

internal static class Mutate4CSharpReportV2
{
    // Initial named consumer profile limit, not a claim about every producer-emitted report.
    internal const int MaximumReportBytes = 8 * 1024 * 1024;

    internal static MutationReportData ReadData(ReadOnlySpan<byte> report)
    {
        Require(report.Length is > 0 and <= MaximumReportBytes, "Report exceeds the named byte bound.");
        try
        {
            // Validate decoding before JsonDocument: invalid UTF8 must not turn into replacement characters.
            _ = new UTF8Encoding(false, true).GetCharCount(report);
            ValidateEscapedUnicode(report);
            using var document = JsonDocument.Parse(report.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            var elements = 0;
            CheckBounds(root, ref elements);
            var schemaError = Mutate4CSharpReportSchema.Validate(root);
            Require(schemaError is null, schemaError ?? "Invalid schema.");
            return ReadValidated(root);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or OverflowException)
        {
            throw new MutationReportFormatException("Malformed or unbounded producer data: " + exception.Message);
        }
    }

    private static void ValidateEscapedUnicode(ReadOnlySpan<byte> report)
    {
        var reader = new Utf8JsonReader(report, new JsonReaderOptions { MaxDepth = 64 });
        while (reader.Read())
        {
            if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName)) continue;
            var token = reader.ValueSpan;
            for (var index = 0; index < token.Length; index++)
            {
                if (token[index] != (byte)'\\') continue;
                index++;
                if (token[index] != (byte)'u') continue;
                var scalar = Escape(token.Slice(index + 1, 4));
                index += 4;
                if (scalar is >= 0xD800 and <= 0xDBFF)
                {
                    Require(index + 6 < token.Length && token[index + 1] == (byte)'\\' && token[index + 2] == (byte)'u', "Unpaired high surrogate escape.");
                    var low = Escape(token.Slice(index + 3, 4));
                    Require(low is >= 0xDC00 and <= 0xDFFF, "Unpaired high surrogate escape.");
                    index += 6;
                }
                else Require(scalar is not (>= 0xDC00 and <= 0xDFFF), "Unpaired low surrogate escape.");
            }
        }

        static int Escape(ReadOnlySpan<byte> digits)
        {
            var value = 0;
            foreach (var digit in digits)
                value = value * 16 + (digit <= (byte)'9' ? digit - (byte)'0' : (digit | 32) - (byte)'a' + 10);
            return value;
        }
    }

    private static void CheckBounds(JsonElement value, ref int elements)
    {
        Require(++elements <= 1_000_000, "Report exceeds the named aggregate element bound.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(property.Name.Length <= 128 && names.Add(property.Name), "Duplicate or oversized property name.");
                CheckBounds(property.Value, ref elements);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            Require(value.GetArrayLength() <= 100_000, "Array exceeds the named element bound.");
            foreach (var item in value.EnumerateArray()) CheckBounds(item, ref elements);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            Require(text.Length <= 16_384, "String exceeds the named UTF16 bound.");
            // Reject escaped unpaired surrogates, rather than hashing replacement text.
            for (var index = 0; index < text.Length; index++)
                if (char.IsSurrogate(text[index]))
                {
                    Require(char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]), "Invalid Unicode scalar.");
                    index++;
                }
        }
        else if (value.ValueKind == JsonValueKind.Number)
            Require(value.TryGetInt64(out _), "Producer numeric fields must be bounded integer tokens.");
    }

    private static MutationReportData ReadValidated(JsonElement root)
    {
        Require(!string.IsNullOrWhiteSpace(Text(root, "runId")), "Empty run identity.");
        ValidateScope(root);
        foreach (var property in root.GetProperty("policy").EnumerateObject())
            if (property.Name != "allowNotApplicable")
                Require(property.Value.TryGetInt32(out var number) && number > 0, "Policy must contain positive Int32 values.");

        var units = Array(root, "units");
        var suites = Array(root, "suites");
        var conditions = Reasons(root, "incompleteConditions");
        var expectedReasons = new List<(string Code, string Message)>(conditions);
        var diagnostic = Flag(root, "diagnosticPartial");
        Require(!diagnostic || conditions.Any(reason => reason.Code == "TARGETED_DIAGNOSTIC"), "Diagnostic condition missing.");
        var baseline = Text(root, "baseline");
        if (diagnostic) AddReason(expectedReasons, "TARGETED_DIAGNOSTIC", "Targeted reruns are diagnostic and cannot prove full-scope success.");
        if (baseline != "GREEN") AddReason(expectedReasons, "BASELINE_" + baseline, $"The baseline status is {baseline}.");
        var counts = root.GetProperty("counts");
        int? enumerated = counts.GetProperty("enumerated").ValueKind == JsonValueKind.Null ? null : Integer(counts, "enumerated");
        if (enumerated is null) AddReason(expectedReasons, "ENUMERATION_INCOMPLETE", "The mutation-site total is unknown.");
        var totals = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["KILLED"] = 0, ["SURVIVED"] = 0, ["UNCOVERED"] = 0, ["COMPILE_INVALID"] = 0,
            ["ERROR"] = 0, ["OMITTED"] = 0, ["UNSTABLE"] = 0
        };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            Require(Identity(Text(unit, "unitId"), "mutation:v1:") && Identity(Text(unit, "evaluationUnitId"), "evaluation:v1:") &&
                ids.Add(Text(unit, "evaluationUnitId")), "Invalid or duplicate unit identity.");
            Require(Flag(unit, "diagnosticPartial") == diagnostic, "Diagnostic state disagrees with ledger.");
            ValidateEvidence(unit);
            var disposition = Text(unit, "disposition");
            Require(totals.ContainsKey(disposition), "Unknown unit disposition.");
            totals[disposition]++;
            var id = Text(unit, "unitId");
            var reason = disposition switch
            {
                "SURVIVED" => ("SURVIVED_MUTATION", $"Evaluation unit {id} survived."),
                "UNCOVERED" => ("UNCOVERED_MUTATION", $"Evaluation unit {id} is known to be uncovered."),
                "ERROR" => ("UNIT_ERROR", $"Evaluation unit {id} did not produce conclusive evidence."),
                "OMITTED" => ("UNIT_OMITTED", $"Evaluation unit {id} was omitted."),
                "UNSTABLE" => ("UNSTABLE_EVIDENCE", $"Evaluation unit {id} produced incompatible repeated evidence."),
                _ => default
            };
            if (reason.Item1 is not null) expectedReasons.Add(reason);
        }
        var errors = totals["ERROR"] + totals["UNSTABLE"];
        var executed = totals["KILLED"] + totals["SURVIVED"] + totals["COMPILE_INVALID"] + errors;
        var expectedCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["selected"] = units.Length, ["executed"] = executed, ["killed"] = totals["KILLED"],
            ["survived"] = totals["SURVIVED"], ["freshUncovered"] = totals["UNCOVERED"],
            ["omitted"] = totals["OMITTED"], ["compileInvalid"] = totals["COMPILE_INVALID"], ["errors"] = errors
        };
        foreach (var count in expectedCounts) Require(Integer(counts, count.Key) == count.Value, "Counts contradict complete unit ledger.");
        Require(enumerated is null || enumerated == units.Length, "Enumeration contradicts ledger.");
        var incomplete = baseline != "GREEN" || diagnostic || conditions.Count != 0 || enumerated is null || errors != 0 || totals["OMITTED"] != 0;
        var outcome = incomplete ? GateOutcome.Incomplete : totals["SURVIVED"] + totals["UNCOVERED"] != 0 ? GateOutcome.Fail :
            units.Length - totals["COMPILE_INVALID"] == 0 ? GateOutcome.NotApplicable : GateOutcome.Pass;
        var exit = outcome switch
        {
            GateOutcome.Incomplete => baseline is "RED" or "EMPTY" ? 2 : 4,
            GateOutcome.Fail => 3,
            GateOutcome.NotApplicable => root.GetProperty("policy").GetProperty("allowNotApplicable").GetBoolean() ? 0 : 5,
            _ => 0
        };
        if (outcome == GateOutcome.NotApplicable) AddReason(expectedReasons, "NO_EFFECTIVE_CANDIDATES", "No effective valid mutation candidates were available.");
        var outcomeText = outcome switch { GateOutcome.Pass => "PASS", GateOutcome.Fail => "FAIL", GateOutcome.NotApplicable => "NOT_APPLICABLE", _ => "INCOMPLETE" };
        Require(Text(root, "outcome") == outcomeText && Integer(root, "exitCode") == exit, "Outcome or exit contradicts reducer facts.");
        Require(Reasons(root, "reasons").SequenceEqual(expectedReasons), "Reasons contradict reducer facts or ordering.");
        Require(Text(root, "mode") != "plan" || outcome != GateOutcome.Pass, "Plan cannot represent success.");
        Require(outcome != GateOutcome.Pass || enumerated is > 0 && executed > 0 && totals["KILLED"] > 0 && suites.Length > 0, "Success lacks nonzero work.");
        ValidateEvidence(root);
        Require(suites.Select(suite => Text(suite, "suiteId")).Distinct(StringComparer.Ordinal).Count() == suites.Length, "Duplicate suite identity.");
        foreach (var suite in suites) ValidateEvidence(suite);
        ValidateFunctionalRefusals(root, outcome, enumerated, units.Length, suites.Length, conditions, expectedReasons);
        if (outcome != GateOutcome.Incomplete) ValidateConclusiveSuites(root, suites);
        return new(outcome, exit, Text(root, "runId"), enumerated, units.Length, executed, totals["KILLED"], totals["SURVIVED"],
            totals["UNCOVERED"], totals["OMITTED"], totals["COMPILE_INVALID"], errors,
            outcome == GateOutcome.NotApplicable ? expectedReasons.Single(reason => reason.Code == "NO_EFFECTIVE_CANDIDATES").Message : null);
    }

    // These are the pinned installed wrapper's accepted incomplete branches, not proof of
    // execution. Publication failures and unfinished previews never become functional reports.
    private static void ValidateFunctionalRefusals(JsonElement root, GateOutcome outcome, int? enumerated,
        int unitCount, int suiteCount, List<(string Code, string Message)> conditions, List<(string Code, string Message)> reasons)
    {
        Require(!conditions.Concat(reasons).Any(reason => reason.Code == "FINALIZATION_PENDING") &&
            !conditions.Any(reason => reason.Code == "SIDECAR_WRITE_FAILED") &&
            !Array(root, "evidence").Any(item => Text(item, "kind") == "SIDECAR_PUBLICATION_FAILURE"), "Unfinished or failed publication is not functional evidence.");
        if (outcome != GateOutcome.Incomplete) return;
        if (conditions.Count == 0)
        {
            Require(Text(root, "baseline") != "GREEN" || Integer(root.GetProperty("counts"), "omitted") > 0 ||
                Integer(root.GetProperty("counts"), "errors") > 0, "Incomplete report lacks an accepted reducer or refusal condition.");
            return;
        }
        var execution = new HashSet<string>(StringComparer.Ordinal)
        {
            "TARGETED_DIAGNOSTIC", "MUTATION_EXECUTION_FAILED", "COVERAGE_PROVENANCE_INCOMPLETE", "BASELINE_ACCOUNTING_INCOMPLETE",
            "OVERALL_DEADLINE_EXCEEDED", "EXECUTION_CANCELLED", "SNAPSHOT_CANCELLED", "BASELINE_TIMEOUT", "BASELINE_INCONCLUSIVE",
            "COVERAGE_MISSING", "SUITE_MEMBERS_MISSING", "MUTATION_ATTEMPT_OMITTED", "BUDGET_OMITTED"
        };
        var refusal = new HashSet<string>(StringComparer.Ordinal)
        {
            "ENUMERATION_ANCESTOR_BUILD_UNSUPPORTED", "ENUMERATION_COMPILE_INVENTORY_MISMATCH", "ENUMERATION_COMPILE_INVENTORY_UNSUPPORTED",
            "ENUMERATION_COMPILE_TRANSFORM_UNSUPPORTED", "ENUMERATION_CONDITION_UNSUPPORTED", "ENUMERATION_CONFIGURATION_REQUIRED",
            "ENUMERATION_CONFIGURATION_UNSUPPORTED", "ENUMERATION_ENCODING_UNSUPPORTED", "ENUMERATION_FRAMEWORK_UNSUPPORTED",
            "ENUMERATION_IMPLICIT_USINGS_UNSUPPORTED", "ENUMERATION_IMPORT_UNSUPPORTED", "ENUMERATION_INHERITED_BUILD_UNSUPPORTED",
            "ENUMERATION_ITEM_UNSUPPORTED", "ENUMERATION_LANGUAGE_UNSUPPORTED", "ENUMERATION_LIMIT_EXCEEDED", "ENUMERATION_NULLABLE_MISMATCH",
            "ENUMERATION_OUTPUT_TYPE_UNSUPPORTED", "ENUMERATION_PARSE_INVALID", "ENUMERATION_PROJECT_ELEMENT_UNSUPPORTED",
            "ENUMERATION_PROJECT_UNMAPPED", "ENUMERATION_PROJECT_UNSUPPORTED", "ENUMERATION_PROPERTY_UNSUPPORTED", "ENUMERATION_REFERENCE_UNSUPPORTED",
            "ENUMERATION_REQUIRES_GIT_SNAPSHOT", "ENUMERATION_SCOPE_INCOMPLETE", "ENUMERATION_SDK_UNSUPPORTED", "ENUMERATION_SEMANTIC_INVALID",
            "ENUMERATION_SYMBOL_MISMATCH", "ENUMERATION_SYMBOL_UNSUPPORTED", "AMBIGUOUS_PROJECT_OWNERSHIP", "UNMAPPED_PROJECT",
            "CONFIGURED_PATH_MISSING", "UNSUPPORTED_SYNTAX", "NO_SUPPORTED_DECLARATION", "UNSUPPORTED_CHANGED_INPUT",
            "EXACT_ID_RERUN_UNAVAILABLE", "TARGET_SELECTION_INVALID", "SCOPE_UNSUPPORTED_ENCODING", "DEPENDENCY_INPUT_UNAVAILABLE",
            "EXECUTION_ENVIRONMENT_UNAVAILABLE", "FINGERPRINT_UNAVAILABLE"
        };
        var executionIncomplete = conditions.All(reason => execution.Contains(reason.Code));
        var enumerationRefusal = conditions.All(reason => refusal.Contains(reason.Code));
        Require(executionIncomplete || enumerationRefusal, "Incomplete conditions are outside the pinned functional profile.");
        if (enumerationRefusal) Require(enumerated is null, "Enumeration refusal contradicts a known enumeration.");
        else
        {
            var unboundCancellation = enumerated is null && conditions.All(reason => reason.Code is "EXECUTION_CANCELLED" or "SNAPSHOT_CANCELLED");
            Require(unboundCancellation || enumerated is >= 0, "Execution incomplete lacks bounded enumeration.");
            if (unboundCancellation)
                Require(unitCount == 0 && suiteCount == 0 && !Array(root, "evidence").Any(item => Text(item, "kind") == "MUTATION_PLAN" ||
                    conditions.Any(reason => reason.Code == "SNAPSHOT_CANCELLED") && Text(item, "kind") == "INPUT_SNAPSHOT"), "Unbound cancellation retains bound execution facts.");
        }
    }

    private static void ValidateScope(JsonElement root)
    {
        var scope = root.GetProperty("scopePlan");
        Require(Text(scope, "selectionKind") == Text(root.GetProperty("selection"), "kind"), "Scope selection mismatch.");
        Require(!string.IsNullOrWhiteSpace(Text(scope, "root")), "Empty scope root.");
        _ = Reasons(scope, "reasons");
        foreach (var (array, property) in new[] { ("files", "path"), ("exclusions", "path"), ("projectUnits", "project") })
        {
            var items = Array(scope, array);
            Require(items.Select(item => Text(item, property)).Distinct(StringComparer.Ordinal).Count() == items.Length, "Duplicate scope path.");
            foreach (var item in items) Relative(Text(item, property));
        }
        foreach (var exclusion in Array(scope, "exclusions")) ValidText(Text(exclusion, "reasonCode"), Text(exclusion, "message"));
        foreach (var file in Array(scope, "files"))
        {
            if (file.GetProperty("basePath").ValueKind != JsonValueKind.Null) Relative(Text(file, "basePath"));
            _ = Reasons(file, "reasons");
            var declarations = Array(file, "declarations").Concat(Array(file, "removals")).ToArray();
            Require(declarations.Select(item => Text(item, "id")).Distinct(StringComparer.Ordinal).Count() == declarations.Length, "Duplicate declaration identity.");
            foreach (var declaration in declarations)
            {
                Require(!string.IsNullOrWhiteSpace(Text(declaration, "id")) && Text(declaration, "id").Length <= 512 &&
                    Integer(declaration, "startLine") >= 1 && Integer(declaration, "endLine") >= Integer(declaration, "startLine"), "Invalid declaration bounds.");
                Require(Code(Text(declaration, "reason")), "Invalid declaration reason.");
            }
        }
        foreach (var project in Array(scope, "projectUnits"))
        {
            Require(Array(project, "tests").Length > 0, "Project has no test suite.");
            foreach (var path in Array(project, "tests").Concat(Array(project, "inputs"))) Relative(path.GetString()!);
        }
    }

    private static void ValidateConclusiveSuites(JsonElement root, JsonElement[] suites)
    {
        Require(suites.Length > 0, "Conclusive report has no suites.");
        var configurationEvidence = Array(root, "evidence").Where(item => Text(item, "kind") == "CHECK_CONFIGURATION").ToArray();
        Require(configurationEvidence.Length == 1, "Configured suite set is absent or ambiguous.");
        var suiteIds = suites.Select(suite => Text(suite, "suiteId")).Order(StringComparer.Ordinal).ToArray();
        Require(SingleDiagnostic(configurationEvidence[0], "suiteCount=") == suites.Length.ToString(CultureInfo.InvariantCulture) &&
            SingleDiagnostic(configurationEvidence[0], "suiteSet=") == "sha256:" + Digest("configured-suite-set",
                suiteIds.Select((id, index) => ($"suite-{index:D8}", id))), "Configured suite set mismatch.");
        var snapshots = Array(root, "evidence").Where(item => Text(item, "kind") == "INPUT_SNAPSHOT")
            .SelectMany(item => Diagnostics(item)).Where(item => item.StartsWith("captureId=", StringComparison.Ordinal)).Select(item => item[10..]).ToArray();
        Require(snapshots.Length == 1 && Hex(snapshots[0]), "Snapshot is absent or ambiguous.");
        foreach (var suite in suites)
        {
            var evidence = Array(suite, "evidence");
            Require(Text(suite, "baseline") == "GREEN" && evidence.Length == 1 && Text(evidence[0], "kind") == "SUITE_BASELINE" &&
                suite.TryGetProperty("accounting", out var accounting) && accounting.ValueKind == JsonValueKind.Object, "Missing fresh baseline accounting.");
            accounting = suite.GetProperty("accounting");
            Require(Text(accounting, "runId") == Text(root, "runId") && Text(accounting, "snapshotId") == snapshots[0] &&
                Hex(Text(accounting, "coverageReportSha256")) && Hex(Text(accounting, "coverageIdentity")) &&
                accounting.GetProperty("coverageReportLength").TryGetInt64(out var length) && length > 0 &&
                Text(accounting, "pathMap") == "baseline-clone-to-snapshot-v1" && accounting.GetProperty("collectedFresh").GetBoolean(), "Invalid coverage binding.");
            var configuration = accounting.GetProperty("configuration");
            Require(Text(configuration, "runner") == "vstest" && Text(configuration, "framework") == "net10.0" &&
                Pattern(Text(configuration, "id"), @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z") &&
                Pattern(Text(configuration, "configuration"), @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z"), "Invalid suite configuration.");
            var path = Text(configuration, "path").Replace('\\', '/');
            Relative(path);
            var expected = Members(configuration, "expectedMembers");
            var actual = Members(accounting, "accountedMembers");
            Require(expected.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(actual.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase), "Suite members are incomplete.");
            var fields = new List<(string, string)>
            {
                ("path", path.ToUpperInvariant()), ("runner", "vstest"), ("framework", "net10.0"),
                ("configuration", Text(configuration, "configuration")), ("member-count", expected.Length.ToString(CultureInfo.InvariantCulture))
            };
            fields.AddRange(expected.Select((member, index) => ($"member.{index}", member)));
            Require(Text(suite, "suiteId") == "suite:v1:" + Digest("suite-execution", fields), "Suite identity mismatch.");
        }
    }

    private static string[] Members(JsonElement parent, string property)
    {
        var members = Array(parent, property).Select(item => item.GetString()!).ToArray();
        Require(members.Length is > 0 and <= 256 && members.Distinct(StringComparer.OrdinalIgnoreCase).Count() == members.Length &&
            members.All(member => !string.IsNullOrWhiteSpace(member) && member.Length <= 256 && member == member.Trim() &&
                !member.Contains('/') && !member.Contains('\\') && member is not ("." or "..")), "Invalid suite members.");
        return members;
    }

    private static string Digest(string domain, IEnumerable<(string Name, string Value)> fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, value) in new[] { ("identity-domain", domain) }.Concat(fields))
        {
            Append(name);
            Append(value);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    private static void ValidateEvidence(JsonElement parent)
    {
        foreach (var item in Array(parent, "evidence"))
        {
            ValidText(Text(item, "kind"), Text(item, "summary"));
            var diagnostics = Diagnostics(item);
            Require(diagnostics.Length <= 20 && diagnostics.All(value => value.Length <= 512), "Evidence diagnostics exceed bounds.");
        }
    }

    private static List<(string Code, string Message)> Reasons(JsonElement parent, string property)
    {
        var reasons = Array(parent, property).Select(item => (Code: Text(item, "code"), Message: Text(item, "message"))).ToList();
        foreach (var reason in reasons) ValidText(reason.Code, reason.Message);
        return reasons;
    }

    private static void AddReason(List<(string Code, string Message)> reasons, string code, string message)
    {
        if (reasons.All(reason => reason.Code != code)) reasons.Add((code, message));
    }

    private static string SingleDiagnostic(JsonElement evidence, string prefix)
    {
        var values = Diagnostics(evidence).Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).Select(value => value[prefix.Length..]).ToArray();
        Require(values.Length == 1, "Diagnostic is absent or ambiguous.");
        return values[0];
    }

    private static string[] Diagnostics(JsonElement evidence) => evidence.GetProperty("diagnostics").ValueKind == JsonValueKind.Null ? [] :
        Array(evidence, "diagnostics").Select(item => item.GetString()!).ToArray();
    private static JsonElement[] Array(JsonElement parent, string property) => parent.GetProperty(property).EnumerateArray().ToArray();
    private static string Text(JsonElement parent, string property) => parent.GetProperty(property).GetString()!;
    private static bool Flag(JsonElement parent, string property) => parent.TryGetProperty(property, out var flag) && flag.GetBoolean();
    private static int Integer(JsonElement parent, string property)
    {
        Require(parent.GetProperty(property).TryGetInt32(out var value), "Expected Int32 value.");
        return value;
    }
    private static bool Hex(string value) => Pattern(value, @"\A[0-9a-f]{64}\z");
    private static bool Identity(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) && Hex(value[prefix.Length..]);
    private static bool Code(string value) => Pattern(value, @"\A[A-Z][A-Z0-9_]*\z");
    private static bool Pattern(string value, string pattern) => Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static void ValidText(string code, string message) => Require(Code(code) && !string.IsNullOrWhiteSpace(message) && message.Length <= 1024, "Invalid reason or evidence text.");
    private static void Relative(string path) => Require(!string.IsNullOrWhiteSpace(path) && !path.Contains(':') && !path.Contains('\\') &&
        !path.StartsWith('/') && path.Split('/').All(part => part is not ("" or "." or "..")), "Path is not normalized relative data.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new MutationReportFormatException(message);
    }
}
