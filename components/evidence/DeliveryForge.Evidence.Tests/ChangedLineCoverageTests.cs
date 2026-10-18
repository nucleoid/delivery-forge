using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DeliveryForge.Evidence.Tests;

// UNEXECUTED source tests. Real temporary files exercise complete byte/source joins;
// the XML/manifest remain DATA controls, never current producer or native authority.
public sealed class ChangedLineCoverageTests
{
    [Fact]
    public void Actual_line_denominator_passes_at_eighty_then_refuses_a_missed_changed_line()
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

    private sealed class LineFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-line-" + Guid.NewGuid().ToString("N"));
        private readonly InstalledToolFiles _files = new(TimeSpan.FromSeconds(30));
        internal string SourcePath => Path.Combine(_root, "src", "Subject.cs");
        internal byte[] SourceBytes { get; } = "one\ntwo\nthree\nfour\nfive\n"u8.ToArray();
        internal LineFixture()
        {
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            _files.WriteNew(SourcePath, SourceBytes);
        }
        internal ChangedLineCoverage.Result Read(int[] visits, bool duplicateFirst = false,
            string coordinates = "line", bool mutateHash = false)
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
            var coverage = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["source.bin"] = SourceBytes, ["coverage.xml"] = coverage };
            using var manifest = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
            {
                contexts = new[] { new { id = "c", inputs = new[] { new { role = "source", logicalPath = "src/Subject.cs",
                    repositoryPath = "src/Subject.cs", locator = "source.bin", generated = false, encoding = "utf-8",
                    length = SourceBytes.Length, sha256 = InstalledToolFiles.Identity(SourceBytes)[7..] } } } },
                builds = new[] { new { id = "b", contextId = "c", moduleIdentity = "Subject" } },
                executions = new[] { new { id = "e", contextId = "c", buildId = "b", completed = true, exitCode = 0 } },
                artifacts = new[] { new { id = "a", kind = "coverage", locator = "coverage.xml", contextId = "c", buildId = "b",
                    executionId = "e", format = "opencover", coordinateKind = coordinates, length = coverage.Length,
                    sha256 = mutateHash ? new string('0', 64) : InstalledToolFiles.Identity(coverage)[7..] } }
            }));
            var diff = Encoding.UTF8.GetBytes("diff --git a/src/Subject.cs b/src/Subject.cs\n--- /dev/null\n+++ b/src/Subject.cs\n@@ -0,0 +1,5 @@\n+one\n+two\n+three\n+four\n+five\n");
            return ChangedLineCoverage.Read(diff, manifest.RootElement, _root, path => data[path], _files);
        }
        public void Dispose() => _files.ReleaseKnown(); // Files preserved, not recursively deleted.
    }
}
