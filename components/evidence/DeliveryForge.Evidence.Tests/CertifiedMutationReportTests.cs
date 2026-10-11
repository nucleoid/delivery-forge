using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence.Tests;

public sealed class CertifiedMutationReportTests
{
    [Theory]
    [InlineData("strong", GateOutcome.Pass, 0)]
    [InlineData("weak", GateOutcome.Fail, 3)]
    [InlineData("red", GateOutcome.Incomplete, 2)]
    [InlineData("zero", GateOutcome.NotApplicable, 5)]
    public async Task Complete_report_reader_reconciles_producer_claims_without_issuing_authority(
        string name, GateOutcome expected, int exit)
    {
        var bytes = await MutationReportInputs.ReadAsync(name);
        using var original = JsonDocument.Parse(bytes);
        var data = Mutate4CSharpReportV2.ReadData(bytes);
        Assert.Equal(expected, data.ClaimedOutcome);
        Assert.Equal(exit, data.ClaimedExitCode);
        Assert.Equal(original.RootElement.GetProperty("runId").GetString(), data.RunId);
        var counts = original.RootElement.GetProperty("counts");
        Assert.Equal(counts.GetProperty("selected").GetInt32(), data.Selected);
        Assert.Equal(counts.GetProperty("executed").GetInt32(), data.Executed);
        Assert.Equal(data.Selected, data.Executed + data.FreshUncovered + data.Omitted);
        if (data.Enumerated is { } enumerated) Assert.Equal(enumerated, data.Selected);
        if (name == "zero")
        {
            Assert.Equal(0, data.Enumerated);
            Assert.Equal(0, data.Killed);
            Assert.False(string.IsNullOrWhiteSpace(data.NotApplicableRationale));
        }
        // Even a complete historical producer claim cannot mint a current execution/profile.
        var unbound = new Mutate4CSharpAdapter().Normalize(bytes,
            new ToolCapability("mutate4csharp", "0.1.0-format-only", "2", true, true));
        Assert.Equal(GateOutcome.Incomplete, unbound.Outcome);
        Assert.False(unbound.ProductionCapable);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("exit")]
    [InlineData("baseline")]
    [InlineData("unknown-field")]
    [InlineData("unknown-disposition")]
    [InlineData("duplicate-unit")]
    [InlineData("numeric-enum")]
    [InlineData("diagnostic-positive")]
    [InlineData("suite-members")]
    [InlineData("coverage-sha")]
    [InlineData("unknown-schema")]
    [InlineData("plan-positive")]
    public async Task Inconsistent_or_unknown_complete_claims_are_rejected(string corruption)
    {
        var node = JsonNode.Parse(await MutationReportInputs.ReadAsync("strong"))!.AsObject();
        switch (corruption)
        {
            case "count": node["counts"]!["selected"] = 999; break;
            case "exit": node["exitCode"] = 3; break;
            case "baseline": node["baseline"] = "RED"; break;
            case "unknown-field": node["callerApproved"] = true; break;
            case "unknown-disposition": node["units"]![0]!["disposition"] = "CLAIMED_KILLED"; break;
            case "duplicate-unit": node["units"]!.AsArray().Add(node["units"]![0]!.DeepClone()); break;
            case "numeric-enum": node["outcome"] = 0; break;
            case "diagnostic-positive": node["diagnosticPartial"] = true; break;
            case "suite-members": node["suites"]![0]!["accounting"]!["accountedMembers"] = new JsonArray("foreign.dll"); break;
            case "coverage-sha": node["suites"]![0]!["accounting"]!["coverageReportSha256"] = "sha256:not-a-raw-sha"; break;
            case "unknown-schema": node["schemaVersion"] = "1"; break;
            case "plan-positive": node["mode"] = "plan"; break;
        }
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        Assert.Throws<MutationReportFormatException>(() => Mutate4CSharpReportV2.ReadData(bytes));
    }

    [Fact]
    public async Task Duplicate_JSON_properties_do_not_become_last_value_authority()
    {
        var source = Encoding.UTF8.GetString(await MutationReportInputs.ReadAsync("strong"));
        var duplicate = "{\"schemaVersion\":\"2\"," + source[(source.IndexOf('{') + 1)..];
        Assert.Throws<MutationReportFormatException>(() => Mutate4CSharpReportV2.ReadData(Encoding.UTF8.GetBytes(duplicate)));
    }

    [Fact]
    public async Task Report_only_normalization_cannot_mint_authority_from_complete_claims_or_caller_flags()
    {
        foreach (var name in new[] { "strong", "weak", "red", "zero" })
        {
            var result = new Mutate4CSharpAdapter().Normalize(await MutationReportInputs.ReadAsync(name),
                new ToolCapability("mutate4csharp", "0.1.0-format-only", "2", true, false));
            Assert.Equal(GateOutcome.Incomplete, result.Outcome);
            Assert.False(result.ProductionCapable);
        }
    }

    [Fact]
    public void Truncated_and_oversized_wire_data_are_not_partial_success()
    {
        Assert.Throws<MutationReportFormatException>(() => Mutate4CSharpReportV2.ReadData("{"u8));
        Assert.Throws<MutationReportFormatException>(() => Mutate4CSharpReportV2.ReadData(new byte[Mutate4CSharpReportV2.MaximumReportBytes + 1]));
    }

    [Theory]
    [InlineData("expectedMembers")]
    [InlineData("accountedMembers")]
    public async Task Incomplete_report_rejects_decoded_equal_members_with_distinct_JSON_spellings(string property)
    {
        var node = JsonNode.Parse(await MutationReportInputs.ReadAsync("strong"))!.AsObject();
        var condition = new JsonObject
        {
            ["code"] = "OVERALL_DEADLINE_EXCEEDED",
            ["message"] = "The overall deadline was exceeded."
        };
        node["incompleteConditions"] = new JsonArray(condition);
        node["reasons"] = new JsonArray(condition.DeepClone());
        node["outcome"] = "INCOMPLETE";
        node["exitCode"] = 4;
        // A real producer-format incomplete control keeps its whole original ledger and
        // accounting. This prevents a different reducer/refusal failure masking uniqueness.
        var control = Mutate4CSharpReportV2.ReadData(Encoding.UTF8.GetBytes(node.ToJsonString()));
        Assert.Equal(GateOutcome.Incomplete, control.ClaimedOutcome);
        Assert.Equal(4, control.ClaimedExitCode);

        var accounting = node["suites"]![0]!["accounting"]!;
        var parent = property == "expectedMembers" ? accounting["configuration"]! : accounting;
        var member = parent[property]![0]!.GetValue<string>();
        Assert.True(char.IsAsciiLetter(member[0]));
        parent[property] = new JsonArray(member, member);
        var serialized = JsonSerializer.Serialize(member);
        var escaped = "\"\\u" + ((int)member[0]).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) + serialized[2..];
        var fragment = "\"" + property + "\":[" + serialized + "," + serialized + "]";
        var replacement = "\"" + property + "\":[" + serialized + "," + escaped + "]";
        var text = node.ToJsonString();
        Assert.Contains(fragment, text, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(text.Replace(fragment, replacement, StringComparison.Ordinal));
        using var decoded = JsonDocument.Parse(bytes);
        var parsedAccounting = decoded.RootElement.GetProperty("suites")[0].GetProperty("accounting");
        var parsedMembers = (property == "expectedMembers" ? parsedAccounting.GetProperty("configuration") : parsedAccounting).GetProperty(property);
        Assert.Equal(parsedMembers[0].GetString(), parsedMembers[1].GetString());
        Assert.NotEqual(parsedMembers[0].GetRawText(), parsedMembers[1].GetRawText());
        Assert.Throws<MutationReportFormatException>(() => Mutate4CSharpReportV2.ReadData(bytes));
    }
}

internal static class MutationReportInputs
{
    // Root supplies a private manifest for actual original bytes. Public fallback fixtures
    // exercise DATA FORMAT only: they carry no installed invocation or capability authority.
    internal static async Task<byte[]> ReadAsync(string name)
    {
        var manifestPath = Environment.GetEnvironmentVariable("DELIVERY_FORGE_MUTATION_CORPUS_MANIFEST");
        if (string.IsNullOrWhiteSpace(manifestPath))
            return await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MutationReportData", name + ".json"), TestContext.Current.CancellationToken);
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken));
        var entry = manifest.RootElement.GetProperty("reports").GetProperty(name);
        var bytes = await File.ReadAllBytesAsync(entry.GetProperty("path").GetString()!, TestContext.Current.CancellationToken);
        Assert.Equal(entry.GetProperty("length").GetInt64(), bytes.LongLength);
        Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return bytes;
    }
}
