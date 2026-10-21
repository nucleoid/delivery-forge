using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace DeliveryForge.Evidence.Tests;

// These controls read genuine standard-build PE/PDB/source bytes. The manifest and
// visit rows are DATA fixtures, never a production producer receipt or issuer.
public sealed class CompilerLineCensusTests
{
    [Fact]
    public void Genuine_ordinary_class_has_complete_source_bound_points_including_excluded_method()
    {
        using var fixture = new Fixture();
        var inventory = fixture.Inventory();
        Assert.True(inventory.Complete, inventory.Reason);
        Assert.Empty(inventory.Limitations);
        Assert.Equal(3, inventory.Points.Select(point => point.StartLine).Distinct().Count());
        foreach (var marker in new[] { "CENSUS_FIRST", "CENSUS_EXCLUDED", "CENSUS_LAST" })
            Assert.Contains(inventory.Points, point => point.StartLine == fixture.Line(marker) && point.LogicalPath == "Subject.cs");
        var result = fixture.Read();
        Assert.True(result.CompilerCensusComplete);
        Assert.Equal(GateOutcome.Pass, result.Outcome); Assert.Equal(3, result.ChangedExecutableLines);
        Assert.Equal(3, result.CoveredChangedLines); Assert.Equal(100m, result.Percent);
    }

    [Fact]
    public void Genuine_declaration_only_input_does_not_add_unmeasured_lines_to_a_fully_covered_compiler_change()
    {
        using var fixture = new Fixture();
        using var stream = new MemoryStream(fixture.Pe, writable: false);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var declaration = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
            .Single(type => metadata.GetString(type.Name) == "IDeclarations");
        var method = metadata.GetMethodDefinition(Assert.Single(declaration.GetMethods()));
        Assert.Equal("Ping", metadata.GetString(method.Name)); Assert.Equal(0, method.RelativeVirtualAddress);
        var result = fixture.Read(includeDeclarations: true);
        Assert.True(result.CompilerCensusComplete); Assert.Equal(GateOutcome.Pass, result.Outcome);
        Assert.Equal(3, result.ChangedExecutableLines); Assert.Equal(3, result.CoveredChangedLines);
        Assert.Equal(100m, result.Percent); Assert.Equal(0, result.UnmeasuredChangedLines);
        // Raw report DATA still cannot infer executability from absent file membership.
        var raw = fixture.Read(includeDeclarations: true, rawReports: true);
        Assert.False(raw.CompilerCensusComplete); Assert.Equal(GateOutcome.Incomplete, raw.Outcome);
        Assert.True(raw.UnmeasuredChangedLines > 0);
    }

    [Fact]
    public void Genuine_declaration_only_change_has_no_missing_compiled_measurement_and_no_automatic_exemption()
    {
        using var fixture = new Fixture();
        var result = fixture.Read(declarationsOnly: true);
        Assert.True(result.CompilerCensusComplete); Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Equal(0, result.ChangedExecutableLines); Assert.Equal(0, result.UnmeasuredChangedLines);
        Assert.Null(result.Percent); Assert.Contains("No measurable", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ff1816ec-aa5e-4d10-87f7-6f4963833460")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Unsupported_checksum_algorithm_in_genuine_pdb_metadata_is_incomplete_without_source_attribution(string replacement)
    {
        using var fixture = new Fixture();
        var bytes = fixture.Pdb.ToArray();
        var original = new Guid("8829d00f-11b8-4213-878b-770e8597ac16").ToByteArray();
        var offsets = Enumerable.Range(0, bytes.Length - original.Length + 1)
            .Where(offset => bytes.AsSpan(offset, original.Length).SequenceEqual(original)).ToArray();
        new Guid(replacement).ToByteArray().CopyTo(bytes, Assert.Single(offsets));
        // Adversarial metadata DATA: retain the genuine PE and PDB content-id bytes,
        // changing only the declared checksum algorithm, never a production receipt.
        var inventory = CompilerLineCensus.Inspect(fixture.Pe, bytes, fixture.Build,
            [fixture.Document, fixture.DeclarationDocument], fixture.Files);
        Assert.False(inventory.Complete); Assert.Empty(inventory.Points);
        Assert.Contains("checksum algorithm is unsupported", inventory.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_sha256_extent_in_genuine_pdb_metadata_remains_an_identity_refusal()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Pdb.ToArray();
        var checksum = SHA256.HashData(fixture.Source);
        var offsets = Enumerable.Range(1, bytes.Length - checksum.Length)
            .Where(offset => bytes.AsSpan(offset, checksum.Length).SequenceEqual(checksum)).ToArray();
        var offset = Assert.Single(offsets);
        Assert.Equal((byte)32, bytes[offset - 1]); bytes[offset - 1] = 31;
        var error = Assert.Throws<EvidenceWriteException>(() => CompilerLineCensus.Inspect(fixture.Pe, bytes, fixture.Build,
            [fixture.Document, fixture.DeclarationDocument], fixture.Files));
        Assert.Contains("malformed byte extent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_excluded_method_measurement_cannot_shrink_the_compiler_denominator()
    {
        using var fixture = new Fixture();
        var result = fixture.Read(omitLine: fixture.Line("CENSUS_EXCLUDED"));
        Assert.True(result.CompilerCensusComplete);
        Assert.Equal(3, result.ChangedExecutableLines); Assert.Equal(2, result.CoveredChangedLines);
        Assert.Equal(1, result.UnmeasuredChangedLines); Assert.Equal(GateOutcome.Incomplete, result.Outcome);
    }

    [Fact]
    public void Missing_whole_authored_file_is_incomplete_with_the_same_three_compiled_lines()
    {
        using var fixture = new Fixture();
        var result = fixture.Read(omitFile: true);
        Assert.Equal(3, result.ChangedExecutableLines); Assert.Equal(0, result.CoveredChangedLines);
        Assert.Equal(3, result.UnmeasuredChangedLines); Assert.Equal(GateOutcome.Incomplete, result.Outcome);
    }

    [Fact]
    public void Missing_report_keeps_all_compiled_lines_unmeasured_and_comment_only_change_is_not_an_exemption()
    {
        using var fixture = new Fixture();
        var absent = fixture.Read(omitReport: true);
        Assert.Equal(GateOutcome.Incomplete, absent.Outcome); Assert.Equal(3, absent.ChangedExecutableLines);
        Assert.Equal(3, absent.UnmeasuredChangedLines);
        var comments = fixture.Read(commentOnly: true);
        Assert.True(comments.CompilerCensusComplete); Assert.Equal(GateOutcome.Incomplete, comments.Outcome);
        Assert.Equal(0, comments.ChangedExecutableLines); Assert.Null(comments.Percent);
    }

    [Fact]
    public void Exact_checksum_without_an_admitted_document_role_does_not_establish_mapping()
    {
        using var fixture = new Fixture();
        var inventory = fixture.Inventory(document: fixture.Document with
        { LogicalPath = "Other.cs", RepositoryPath = "other/Other.cs", PhysicalPath = null });
        Assert.False(inventory.Complete); Assert.Empty(inventory.Points);
        Assert.Contains("not completely", inventory.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Measured_zero_is_a_failure_while_missing_measurement_in_another_context_is_incomplete()
    {
        using var fixture = new Fixture();
        var line = fixture.Line("CENSUS_EXCLUDED");
        var zero = fixture.Read(zeroLine: line, secondContext: true);
        Assert.Equal(GateOutcome.Fail, zero.Outcome); Assert.Equal(3, zero.ChangedExecutableLines);
        Assert.Equal(2, zero.CoveredChangedLines);
        var missing = fixture.Read(omitLine: line, secondContext: true);
        Assert.Equal(GateOutcome.Incomplete, missing.Outcome); Assert.Equal(3, missing.ChangedExecutableLines);
        Assert.Equal(1, missing.UnmeasuredChangedLines);
    }

    [Theory]
    [InlineData("mvid", "module/MVID")]
    [InlineData("debug", "debug identity")]
    [InlineData("checksum", "document checksum")]
    [InlineData("duplicate", "unique actual")]
    [InlineData("context", "unique actual")]
    [InlineData("malformed-pdb", "Malformed")]
    [InlineData("assembly-hash", "length/hash")]
    public void Wrong_actual_compiler_join_refuses_at_its_specific_boundary(string fault, string expected)
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<EvidenceWriteException>(() => fixture.Read(fault: fault));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stamp")]
    [InlineData("age")]
    [InlineData("guid")]
    public void Real_portable_fixture_requires_actual_guid_age_and_stamp_association_even_with_recomputed_manifest_data(string fault)
    {
        using var fixture = new Fixture();
        var bytes = fixture.Pe.ToArray();
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var entries = pe.ReadDebugDirectory();
        var entryIndex = Enumerable.Range(0, entries.Length).Single(index => entries[index].Type == DebugDirectoryEntryType.CodeView);
        var entry = entries[entryIndex]; var cv = pe.ReadCodeViewDebugDirectoryData(entry);
        var peStamp = entry.Stamp; var age = cv.Age; var guid = cv.Guid;
        if (fault == "stamp")
        {
            peStamp ^= 1;
            var rva = pe.PEHeaders.PEHeader!.DebugTableDirectory.RelativeVirtualAddress;
            var section = pe.PEHeaders.SectionHeaders.Single(row => rva >= row.VirtualAddress && rva - row.VirtualAddress < row.SizeOfRawData);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section.PointerToRawData + rva - section.VirtualAddress + entryIndex * 28 + 4, 4), peStamp);
        }
        else if (fault == "age")
        {
            age = 2; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry.DataPointer + 20, 4), age);
        }
        else
        {
            guid = Guid.Empty; guid.TryWriteBytes(bytes.AsSpan(entry.DataPointer + 4, 16));
        }
        var claimed = fixture.Build with { DebugIdentity = CompilerLineCensus.DebugIdentity(guid, age, peStamp, entry.Stamp) };
        var error = Assert.Throws<EvidenceWriteException>(() => CompilerLineCensus.Inspect(bytes, fixture.Pdb, claimed, [fixture.Document], fixture.Files));
        Assert.Contains("GUID, age, stamp", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#line hidden")]
    [InlineData("\ufeff  # line 20 \"other.cs\"")]
    [InlineData("\u2003#pragma checksum \"other.cs\" \"guid\" \"checksum\"")]
    [InlineData("// comment\u0085# line default")]
    public void Source_mapping_directives_refuse_before_a_changed_checksum_can_mask_the_profile_guard(string directive)
    {
        using var fixture = new Fixture();
        var document = fixture.Document with { Text = directive + "\n" + fixture.Document.Text };
        var inventory = fixture.Inventory(document: document);
        Assert.False(inventory.Complete);
        Assert.Contains("Source-authored line/checksum remapping", inventory.Reason, StringComparison.Ordinal);
        Assert.Empty(inventory.Points);
    }

    [Fact]
    public void Giant_current_test_assembly_slice_does_not_claim_a_complete_compiler_context_or_hide_opaque_bodies()
    {
        using var fixture = new Fixture();
        var own = typeof(CompilerLineCensusTests).Assembly.Location;
        var pe = fixture.Files.Read(own, 128 * 1024 * 1024);
        var pdb = fixture.Files.Read(Path.ChangeExtension(own, ".pdb"), 128 * 1024 * 1024);
        var inventory = CompilerLineCensus.Inspect(pe, pdb, Expected(pe), [fixture.Document], fixture.Files);
        Assert.False(inventory.Complete);
        Assert.Contains(inventory.Limitations, reason => reason.Contains("not completely", StringComparison.Ordinal));
        Assert.Contains(inventory.Limitations, reason => reason.Contains("Debugger-hiding", StringComparison.Ordinal));
        Assert.Contains(inventory.Limitations, reason => reason.Contains("no usable nonhidden", StringComparison.Ordinal));
        Assert.Equal(8, DebuggerHiddenBody(7));
    }

    [DebuggerHidden]
    private static int DebuggerHiddenBody(int value) => value + 1;

    [Theory]
    [InlineData(1, 1, 1, 4, true)]
    [InlineData(1, 1, 2, 1, true)]
    [InlineData(1, 4, 2, 1, false)]
    [InlineData(1, 1, 2, 2, false)]
    [InlineData(1, 1, 3, 1, false)]
    [InlineData(0, 1, 1, 4, false)]
    [InlineData(1, 5, 1, 6, false)]
    [InlineData(1, 2, 1, 2, false)]
    public void Only_supported_physical_single_line_spans_can_enter_the_census(int start, int column, int end, int endColumn, bool supported)
        => Assert.Equal(supported, CompilerLineCensus.SupportedSingleLine(new(0x06000001, "Subject.cs", start, column, end, endColumn), [3, 5]));

    [Fact]
    public void Actual_original_cancellation_is_not_wrapped_as_a_metadata_problem()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture(cancellation.Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Inventory());
    }

    private static CompilerLineCensus.Build Expected(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader(); var module = reader.GetModuleDefinition();
        var entry = pe.ReadDebugDirectory().Single(row => row.Type == DebugDirectoryEntryType.CodeView);
        var cv = pe.ReadCodeViewDebugDirectoryData(entry);
        // Independently reproduce the existing producer's canonical tuple wire rule;
        // the actual MVID/name are read from the genuine fixture PE, not asserted DTOs.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Field(string value)
        {
            var raw = Encoding.UTF8.GetBytes(value); var count = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(count, raw.Length); hash.AppendData(count); hash.AppendData(raw);
        }
        Field("sha256-canonical-v1"); Field("portable-pdb-debug-identity-v1");
        foreach (var value in new[] { cv.Guid.ToString("D"), cv.Age.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     entry.Stamp.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), entry.Stamp.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) })
        { hash.AppendData([1]); Field(value); }
        return new(Path.GetFileNameWithoutExtension(reader.GetString(module.Name)), reader.GetGuid(module.Mvid).ToString("D"),
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private sealed class Fixture : IDisposable
    {
        private const string ProjectRole = "components/evidence/DeliveryForge.Evidence.CensusFixture";
        internal readonly InstalledToolFiles Files;
        internal readonly string Repository, SourcePath, DeclarationPath;
        internal readonly byte[] Source, DeclarationSource, Pe, Pdb;
        internal readonly CompilerLineCensus.Build Build;
        internal CompilerLineCensus.Document Document => new("Subject.cs", ProjectRole + "/Subject.cs", SourcePath,
            InstalledToolFiles.Identity(Source)[7..], new UTF8Encoding(false, true).GetString(Source), false);
        internal CompilerLineCensus.Document DeclarationDocument => new("Declarations.cs", ProjectRole + "/Declarations.cs", DeclarationPath,
            InstalledToolFiles.Identity(DeclarationSource)[7..], new UTF8Encoding(false, true).GetString(DeclarationSource), false);
        internal Fixture(CancellationToken token = default)
        {
            Files = new(TimeSpan.FromSeconds(30), token);
            var actualTestOutput = Path.GetDirectoryName(typeof(CompilerLineCensusTests).Assembly.Location)!;
            var framework = Path.GetFileName(actualTestOutput);
            var configuration = Directory.GetParent(actualTestOutput)!.Name;
            var evidence = Path.GetFullPath(Path.Combine(actualTestOutput, "../../../.."));
            Repository = Path.GetFullPath(Path.Combine(evidence, "../.."));
            var project = Path.Combine(evidence, "DeliveryForge.Evidence.CensusFixture");
            SourcePath = Path.Combine(project, "Subject.cs");
            Source = Files.Read(SourcePath, 128 * 1024 * 1024);
            DeclarationPath = Path.Combine(project, "Declarations.cs");
            DeclarationSource = Files.Read(DeclarationPath, 128 * 1024 * 1024);
            var output = Path.Combine(project, "bin", configuration, framework, "DeliveryForge.Evidence.CensusFixture.dll");
            Pe = Files.Read(output, 128 * 1024 * 1024); Pdb = Files.Read(Path.ChangeExtension(output, ".pdb"), 128 * 1024 * 1024);
            Build = Expected(Pe);
        }
        internal int Line(string marker)
        {
            var lines = Encoding.UTF8.GetString(Source).Split('\n');
            return Enumerable.Range(0, lines.Length).Single(index => lines[index].Contains(marker, StringComparison.Ordinal)) + 1;
        }
        internal CompilerLineCensus.Inventory Inventory(CompilerLineCensus.Document? document = null)
            => CompilerLineCensus.Inspect(Pe, Pdb, Build, [document ?? Document, DeclarationDocument], Files);
        internal ChangedLineCoverage.Result Read(int? omitLine = null, int? zeroLine = null, bool omitFile = false,
            bool secondContext = false, string? fault = null, bool omitReport = false, bool commentOnly = false,
            bool includeDeclarations = false, bool declarationsOnly = false, bool rawReports = false)
        {
            var rawSource = fault == "checksum" ? Source.Concat(" "u8.ToArray()).ToArray() : Source;
            // Only the checksum-negative uses a separate logical copy. Physical source
            // bytes remain untouched; direct metadata inspection reaches that boundary.
            if (fault == "checksum")
                return ReadChecksumNegative();
            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["source"] = rawSource,
                ["declaration"] = DeclarationSource,
                ["pe"] = Pe,
                ["pdb"] = fault == "malformed-pdb" ? [0xff] : Pdb
            };
            var contexts = new JsonArray(); var builds = new JsonArray(); var executions = new JsonArray(); var artifacts = new JsonArray();
            foreach (var context in secondContext ? new[] { "c", "c2" } : new[] { "c" })
            {
                contexts.Add(JsonSerializer.SerializeToNode(new
                {
                    id = context,
                    project = ProjectRole + "/DeliveryForge.Evidence.CensusFixture.csproj",
                    inputs = new[] { new { role = "source", logicalPath = "Subject.cs", repositoryPath = ProjectRole + "/Subject.cs", locator = "source",
                        length = Source.Length, sha256 = InstalledToolFiles.Identity(Source)[7..], generated = false, encoding = "utf-8" },
                        new { role = "source", logicalPath = "Declarations.cs", repositoryPath = ProjectRole + "/Declarations.cs", locator = "declaration",
                        length = DeclarationSource.Length, sha256 = InstalledToolFiles.Identity(DeclarationSource)[7..], generated = false, encoding = "utf-8" } }
                }));
                var build = context + "b";
                builds.Add(JsonSerializer.SerializeToNode(new
                {
                    id = build,
                    contextId = context,
                    moduleIdentity = Build.ModuleIdentity,
                    mvid = fault == "mvid" ? Guid.Empty.ToString("D") : Build.Mvid,
                    debugIdentity = fault == "debug" ? new string('0', 64) : Build.DebugIdentity,
                    assemblySha256 = fault == "assembly-hash" ? new string('0', 64) : InstalledToolFiles.Identity(Pe)[7..],
                    pdbSha256 = InstalledToolFiles.Identity(data["pdb"])[7..]
                }));
                foreach (var role in new[] { "assembly", "pdb" })
                {
                    var locator = role == "assembly" ? "pe" : "pdb";
                    var row = JsonSerializer.SerializeToNode(new
                    {
                        id = context + role,
                        kind = role,
                        locator,
                        contextId = fault == "context" && role == "pdb" ? "foreign" : context,
                        buildId = build,
                        length = data[locator].Length,
                        sha256 = InstalledToolFiles.Identity(data[locator])[7..]
                    })!;
                    artifacts.Add(row);
                    if (fault == "duplicate" && role == "pdb") artifacts.Add(row.DeepClone());
                }
                var execution = context + "e";
                executions.Add(JsonSerializer.SerializeToNode(new { id = execution, contextId = context, buildId = build, completed = true, exitCode = 0 }));
                var selected = !secondContext || context == "c2";
                var points = new[] { Line("CENSUS_FIRST"), Line("CENSUS_EXCLUDED"), Line("CENSUS_LAST") }
                    .Where(line => !selected || line != omitLine).Select(line => new XElement("Method",
                        new XElement("FileRef", new XAttribute("uid", "1")), new XElement("SequencePoints", new XElement("SequencePoint",
                            new XAttribute("fileid", "1"), new XAttribute("sl", line), new XAttribute("vc", selected && line == zeroLine ? 0 : 1)))));
                var xml = new XElement("CoverageSession", new XElement("Modules", new XElement("Module",
                    new XElement("ModuleName", Build.ModuleIdentity), new XElement("Files", omitFile ? null : new XElement("File",
                        new XAttribute("uid", "1"), new XAttribute("fullPath", SourcePath))),
                    new XElement("Classes", new XElement("Class", new XElement("Methods", omitFile ? null : points))))));
                var locatorCoverage = context + "coverage";
                data.Add(locatorCoverage, Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
                if (!omitReport) artifacts.Add(JsonSerializer.SerializeToNode(new
                {
                    id = locatorCoverage,
                    kind = "coverage",
                    locator = locatorCoverage,
                    contextId = context,
                    buildId = build,
                    executionId = execution,
                    format = "opencover",
                    coordinateKind = "line",
                    length = data[locatorCoverage].Length,
                    sha256 = InstalledToolFiles.Identity(data[locatorCoverage])[7..]
                }));
            }
            var node = new JsonObject { ["contexts"] = contexts, ["builds"] = builds, ["executions"] = executions, ["artifacts"] = artifacts };
            using var manifest = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(node));
            var sourceLines = Encoding.UTF8.GetString(Source).TrimEnd('\n').Split('\n');
            var diff = Encoding.UTF8.GetBytes("+++ b/" + ProjectRole + "/Subject.cs\n@@ -0,0 +1," + sourceLines.Length + " @@\n" +
                string.Join("\n", sourceLines.Select(line => "+" + line)) + "\n");
            if (commentOnly)
            {
                var line = Line("Deliberately");
                diff = Encoding.UTF8.GetBytes("+++ b/" + ProjectRole + "/Subject.cs\n@@ -0,0 +" + line + " @@\n+" + sourceLines[line - 1] + "\n");
            }
            if (includeDeclarations || declarationsOnly)
            {
                var lines = Encoding.UTF8.GetString(DeclarationSource).TrimEnd('\n').Split('\n');
                var declarationDiff = Encoding.UTF8.GetBytes("+++ b/" + ProjectRole + "/Declarations.cs\n@@ -0,0 +1," + lines.Length + " @@\n" +
                    string.Join("\n", lines.Select(line => "+" + line)) + "\n");
                diff = declarationsOnly ? declarationDiff : diff.Concat(declarationDiff).ToArray();
            }
            return rawReports
                ? ChangedLineCoverage.Read(diff, manifest.RootElement, Repository, path => data[path], Files)
                : ChangedLineCoverage.ReadCompilerBound(diff, manifest.RootElement, Repository, path => data[path], Files);
        }
        private ChangedLineCoverage.Result ReadChecksumNegative()
        {
            var document = Document with { Sha256 = new string('0', 64) };
            _ = CompilerLineCensus.Inspect(Pe, Pdb, Build, [document], Files);
            throw new InvalidOperationException("Checksum negative unexpectedly accepted.");
        }
        public void Dispose() => Files.ReleaseKnown();
    }
}
