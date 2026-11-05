using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace DeliveryForge.Evidence.Tests;

// UNEXECUTED source tests. Real temporary files exercise complete byte/source joins;
// the XML/manifest remain DATA controls, never current producer or native authority.
public sealed class ChangedLineCoverageTests
{
    [Fact]
    public void Observed_reported_line_ratio_is_eighty_then_sixty_without_claiming_an_exhaustive_census()
    {
        using var fixture = new LineFixture();
        var positive = fixture.Read([1, 1, 1, 1, 0]);
        Assert.Equal(GateOutcome.Pass, positive.Outcome);
        Assert.Equal(5, positive.ChangedExecutableLines);
        Assert.Equal(4, positive.CoveredChangedLines);
        Assert.Equal(80m, positive.Percent);
        var negative = fixture.Read([1, 1, 1, 0, 0]);
        Assert.Equal(GateOutcome.Fail, negative.Outcome);
        Assert.Equal(60m, negative.Percent);
    }

    [Fact]
    public void Duplicate_method_points_do_not_inflate_the_true_line_denominator()
    {
        using var fixture = new LineFixture();
        var positive = fixture.Read([1, 1, 1, 1, 0]);
        Assert.Equal(GateOutcome.Pass, positive.Outcome);
        var result = fixture.Read([1, 0], duplicateFirst: true);
        Assert.Equal(2, result.ChangedExecutableLines);
        Assert.Equal(1, result.CoveredChangedLines);
        Assert.Equal(50m, result.Percent);
        Assert.Equal(GateOutcome.Fail, result.Outcome);
    }

    [Fact]
    public void Genuine_source_bytes_precede_source_substitution_and_line_shape_refusals()
    {
        using var fixture = new LineFixture();
        Assert.Equal(GateOutcome.Pass, fixture.Read([1, 1, 1, 1, 0]).Outcome);
        File.AppendAllText(fixture.SourcePath, "substituted\n");
        Assert.Throws<EvidenceWriteException>(() => fixture.Read([1, 1, 1, 1, 0]));
        File.WriteAllBytes(fixture.SourcePath, fixture.SourceBytes);
        Assert.Throws<EvidenceWriteException>(() => fixture.Read([1, -1]));
        Assert.Throws<EvidenceWriteException>(() => fixture.Read([1], coordinates: "sequence-point"));
        Assert.Throws<EvidenceWriteException>(() => fixture.Read([1], mutateHash: true));
    }

    [Fact]
    public void Complete_added_new_side_hunks_precede_truncation_and_quoted_path_refusals()
    {
        var diff = Encoding.UTF8.GetBytes("diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,2 @@\n-old\n+one\n+two\n");
        var lines = ChangedLineCoverage.ReadHunks(diff);
        Assert.Equal(new[] { 1, 2 }, lines["src/A.cs"].Order().ToArray());
        Assert.Throws<EvidenceWriteException>(() => ChangedLineCoverage.ReadHunks(diff[..^5]));
        Assert.Throws<EvidenceWriteException>(() => ChangedLineCoverage.ReadHunks(Encoding.UTF8.GetBytes("+++ \"b/quoted.cs\"\n@@ -0,0 +1 @@\n+one\n")));
    }

    [Fact]
    public void Changed_file_absent_from_an_actual_context_report_is_unmeasured_not_a_zero_denominator_exemption()
    {
        using var fixture = new LineFixture();
        Assert.Equal(GateOutcome.Pass, fixture.Read([1, 1, 1, 1, 0]).Outcome);
        var result = fixture.Read([], xmlChange: xml => { xml.Descendants("File").Remove(); xml.Descendants("Method").Remove(); });
        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Equal(5, result.UnmeasuredChangedLines);
        Assert.Equal(0, result.ChangedExecutableLines); Assert.Null(result.Percent);
        Assert.Contains("unmeasured", result.Reason, StringComparison.Ordinal);
        var noReport = fixture.Read([], manifestChange: manifest => manifest["artifacts"] = new JsonArray());
        Assert.Equal(GateOutcome.Incomplete, noReport.Outcome); Assert.Equal(5, noReport.UnmeasuredChangedLines);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("file-ref")]
    [InlineData("uid")]
    [InlineData("module")]
    [InlineData("duplicate-module")]
    [InlineData("manifest")]
    public void Malformed_complete_inputs_refuse_as_evidence_without_raw_parser_shape_exceptions(string fault)
    {
        using var fixture = new LineFixture();
        Action<XElement>? xmlChange = fault switch
        {
            "file-ref" => xml => xml.Descendants("FileRef").Remove(),
            "uid" => xml => xml.Descendants("File").Single().Attribute("uid")!.Remove(),
            "module" => xml => xml.Descendants("Module").Remove(),
            "duplicate-module" => xml => xml.Element("Modules")!.Add(new XElement(xml.Descendants("Module").Single())),
            _ => null
        };
        Assert.Throws<EvidenceWriteException>(() => fixture.Read([1], xmlChange: xmlChange,
            coverageOverride: fault == "xml" ? "<broken"u8.ToArray() : null,
            manifestChange: fault == "manifest" ? manifest => manifest.Remove("contexts") : null));
    }

    [Fact]
    public void Invalid_actual_source_or_diff_UTF8_refuses_with_the_evidence_type()
    {
        using var fixture = new LineFixture([0xff]);
        Assert.Throws<EvidenceWriteException>(() => fixture.Read([1]));
        Assert.Throws<EvidenceWriteException>(() => ChangedLineCoverage.ReadHunks([0xff]));
    }

    [Fact]
    public void Actual_owner_cancellation_remains_cancellation_instead_of_becoming_a_parser_refusal()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new LineFixture(token: cancellation.Token);
        Assert.Equal(GateOutcome.Pass, fixture.Read([1]).Outcome);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Read([1], coverageOverride: "<broken"u8.ToArray()));
    }

    [Theory]
    [InlineData("one\n")]
    [InlineData("one\r\n")]
    [InlineData("one\r")]
    [InlineData("one\u0085")]
    [InlineData("one\u2028")]
    [InlineData("one\u2029")]
    [InlineData("one")]
    [InlineData("one\n\u001a")]
    public void Terminated_and_unterminated_source_records_do_not_admit_a_phantom_next_line(string source)
    {
        using var fixture = new LineFixture(Encoding.UTF8.GetBytes(source));
        Assert.Equal(1, fixture.Read([1]).ChangedExecutableLines);
        Assert.Throws<EvidenceWriteException>(() => fixture.Read([1, 1]));
    }

    [Fact]
    public void Empty_source_cannot_have_a_line_one_counter()
    {
        using var fixture = new LineFixture([]);
        Assert.Throws<EvidenceWriteException>(() => fixture.Read([1]));
    }

    private sealed class LineFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-line-" + Guid.NewGuid().ToString("N"));
        private readonly InstalledToolFiles _files;
        internal string SourcePath => Path.Combine(_root, "src", "Subject.cs");
        internal byte[] SourceBytes { get; }
        internal LineFixture(byte[]? sourceBytes = null, CancellationToken token = default)
        {
            _files = new(TimeSpan.FromSeconds(30), token);
            SourceBytes = sourceBytes ?? "one\ntwo\nthree\nfour\nfive\n"u8.ToArray();
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            _files.WriteNew(SourcePath, SourceBytes);
        }
        internal ChangedLineCoverage.Result Read(int[] visits, bool duplicateFirst = false,
            string coordinates = "line", bool mutateHash = false, Action<XElement>? xmlChange = null,
            byte[]? coverageOverride = null, Action<JsonObject>? manifestChange = null)
        {
            var points = visits.Select((count, index) => new XElement("SequencePoint", new XAttribute("sl", index + 1),
                new XAttribute("vc", count), new XAttribute("fileid", "1"))).ToList();
            if (duplicateFirst) points.Add(new XElement(points[0]));
            var method = new XElement("Method", new XElement("FileRef", new XAttribute("uid", "1")),
                new XElement("SequencePoints", points));
            var classes = new XElement("Classes", new XElement("Class", new XElement("Methods", method)));
            var module = new XElement("Module", new XElement("ModuleName", "Subject"),
                new XElement("Files", new XElement("File", new XAttribute("uid", "1"), new XAttribute("fullPath", SourcePath))), classes);
            var xml = new XElement("CoverageSession", new XElement("Modules", module));
            xmlChange?.Invoke(xml);
            var coverage = coverageOverride ?? Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["source.bin"] = SourceBytes, ["coverage.xml"] = coverage };
            var manifestNode = JsonSerializer.SerializeToNode(new
            {
                contexts = new[] { new { id = "c", inputs = new[] { new { role = "source", logicalPath = "src/Subject.cs",
                    repositoryPath = "src/Subject.cs", locator = "source.bin", generated = false, encoding = "utf-8",
                    length = SourceBytes.Length, sha256 = InstalledToolFiles.Identity(SourceBytes)[7..] } } } },
                builds = new[] { new { id = "b", contextId = "c", moduleIdentity = "Subject" } },
                executions = new[] { new { id = "e", contextId = "c", buildId = "b", completed = true, exitCode = 0 } },
                artifacts = new[] { new { id = "a", kind = "coverage", locator = "coverage.xml", contextId = "c", buildId = "b",
                    executionId = "e", format = "opencover", coordinateKind = coordinates, length = coverage.Length,
                    sha256 = mutateHash ? new string('0', 64) : InstalledToolFiles.Identity(coverage)[7..] } }
            })!.AsObject();
            manifestChange?.Invoke(manifestNode);
            using var manifest = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(manifestNode));
            var diff = Encoding.UTF8.GetBytes("diff --git a/src/Subject.cs b/src/Subject.cs\n--- /dev/null\n+++ b/src/Subject.cs\n@@ -0,0 +1,5 @@\n+one\n+two\n+three\n+four\n+five\n");
            return ChangedLineCoverage.Read(diff, manifest.RootElement, _root, path => data[path], _files);
        }
        public void Dispose() => _files.ReleaseKnown(); // Files preserved, not recursively deleted.
    }
}
