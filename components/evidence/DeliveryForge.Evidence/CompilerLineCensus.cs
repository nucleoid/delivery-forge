using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Evidence;

// Private DATA inspection of the already hash-bound compiler bytes. It cannot issue
// producer authority or authorize opening a document name found inside a PDB.
internal static class CompilerLineCensus
{
    private const int MaximumRows = 1_000_000; // Existing changed-line/point ceiling, never expanded here.
    internal sealed record Document(string LogicalPath, string? RepositoryPath, string? PhysicalPath,
        string Sha256, string Text, bool Generated);
    internal sealed record Build(string ModuleIdentity, string Mvid, string DebugIdentity);
    internal sealed record Point(int MethodToken, string LogicalPath, int StartLine, int StartColumn,
        int EndLine, int EndColumn);
    internal sealed record Inventory(bool Complete, string Reason, IReadOnlyList<Point> Points, IReadOnlyList<string> Limitations);

    internal static Inventory Inspect(byte[] assembly, byte[] pdb, Build expected,
        IReadOnlyList<Document> documents, InstalledToolFiles files)
    {
        files.Check();
        if (assembly.Length > 128 * 1024 * 1024 || pdb.Length > 128 * 1024 * 1024 || documents.Count > MaximumRows)
            throw Refuse("Compiler census exceeds the existing bounded artifact/point profile.");
        if (documents.Any(document => HasMappingDirective(document.Text, files)))
            return new(false, "Source-authored line/checksum remapping is unsupported by the complete compiler-line profile.", [], ["Source-authored line/checksum remapping is unsupported."]);
        try
        {
            using var peStream = new MemoryStream(assembly, writable: false);
            using var pe = new PEReader(peStream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) throw Refuse("Compiler census requires managed PE metadata.");
            var metadata = pe.GetMetadataReader();
            var module = metadata.GetModuleDefinition();
            var moduleName = metadata.GetString(module.Name);
            if (moduleName != expected.ModuleIdentity + ".dll" && moduleName != expected.ModuleIdentity + ".exe" ||
                metadata.GetGuid(module.Mvid).ToString("D") != expected.Mvid)
                throw Refuse("Compiler module/MVID differs from the exact manifest build.");
            var entries = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.CodeView).ToArray();
            if (entries.Length != 1) throw Refuse("Compiler PE must have one portable CodeView identity.");
            var codeView = pe.ReadCodeViewDebugDirectoryData(entries[0]);
            using var pdbStream = new MemoryStream(pdb, writable: false);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream, MetadataStreamOptions.LeaveOpen);
            var reader = provider.GetMetadataReader();
            var header = reader.DebugMetadataHeader ?? throw Refuse("Compiler PDB has no portable content identity.");
            if (header.Id.Length != 20) throw Refuse("Compiler PDB content identity has an unsupported complete byte extent.");
            var contentId = new BlobContentId(header.Id);
            if (codeView.Age != 1 || codeView.Guid != contentId.Guid || entries[0].Stamp != contentId.Stamp ||
                DebugIdentity(codeView.Guid, codeView.Age, entries[0].Stamp, contentId.Stamp) != expected.DebugIdentity)
                throw Refuse("Compiler PE/PDB GUID, age, stamp or recorded debug identity differs.");
            if (metadata.MethodDefinitions.Count > MaximumRows || metadata.TypeDefinitions.Count > MaximumRows ||
                metadata.CustomAttributes.Count > MaximumRows || reader.Documents.Count > MaximumRows ||
                reader.GetTableRowCount(TableIndex.MethodDebugInformation) != metadata.MethodDefinitions.Count)
                throw Refuse("Compiler PE/PDB method table membership is incomplete or exceeds the existing point ceiling.");
            files.Check();
            var reason = "Complete nonhidden authored compiler-line inventory.";
            var complete = true; var limitations = new HashSet<string>(StringComparer.Ordinal);
            void Unsupported(string message)
            {
                complete = false; limitations.Add(message);
                if (reason.StartsWith("Complete", StringComparison.Ordinal)) reason = message;
            }
            var roles = new Dictionary<string, List<Document>>(StringComparer.Ordinal);
            foreach (var source in documents)
            {
                files.Check();
                var admitted = new List<string> { source.LogicalPath };
                if (source.RepositoryPath is { } repository) { admitted.Add(repository); admitted.Add("/_/" + repository); }
                if (source.PhysicalPath is { } physical) admitted.Add(physical.Replace('\\', '/'));
                foreach (var role in admitted)
                {
                    if (role.Length is 0 or > 16384 || role.Any(char.IsControl)) throw Refuse("Captured compiler document role exceeds the supported identity profile.");
                    if (!roles.TryGetValue(role, out var owners))
                    {
                        if (roles.Count >= MaximumRows) throw Refuse("Compiler document role index exceeds the existing point ceiling.");
                        roles.Add(role, owners = []);
                    }
                    if (!owners.Any(owner => ReferenceEquals(owner, source))) owners.Add(source);
                }
            }
            var mapped = new Dictionary<DocumentHandle, Document>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var extents = new Dictionary<string, int[]>(StringComparer.Ordinal);
            var sha256 = new Guid("8829d00f-11b8-4213-878b-770e8597ac16");
            foreach (var handle in reader.Documents)
            {
                files.Check();
                var document = reader.GetDocument(handle);
                var name = reader.GetString(document.Name).Replace('\\', '/');
                if (name.Length is 0 or > 16384 || name.Any(char.IsControl) || !names.Add(name) ||
                    document.HashAlgorithm.IsNil || reader.GetGuid(document.HashAlgorithm) != sha256 ||
                    reader.GetBlobReader(document.Hash).Length != 32)
                    throw Refuse("Compiler document identity/checksum profile is unsupported or duplicated.");
                var hash = Convert.ToHexStringLower(reader.GetBlobBytes(document.Hash));
                if (!roles.TryGetValue(name, out var candidates) || candidates.Count != 1)
                {
                    Unsupported("Compiler document roles are not completely and unambiguously bound to the captured source vector.");
                    continue;
                }
                var source = candidates[0];
                if (source.Sha256 != hash) throw Refuse("Compiler document checksum differs from the captured source bytes.");
                mapped.Add(handle, source);
                if (!extents.ContainsKey(source.LogicalPath)) extents.Add(source.LogicalPath, LineExtents(source.Text, files));
            }
            var points = new List<Point>(); var totalPoints = 0;
            if (HidesAuthoredCode(metadata, metadata.CustomAttributes, files))
                Unsupported("Debugger-hiding metadata is unsupported by the complete compiler-line profile.");
            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                files.Check();
                var type = metadata.GetTypeDefinition(typeHandle);
                foreach (var methodHandle in type.GetMethods())
                {
                    files.Check();
                    var method = metadata.GetMethodDefinition(methodHandle);
                    var ilLength = 0;
                    if (method.RelativeVirtualAddress != 0)
                    {
                        if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL)
                            Unsupported("A non-IL compiler body is unsupported by the complete compiler-line profile.");
                        else ilLength = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader().Length;
                    }
                    var debug = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(methodHandle)));
                    var visible = 0;
                    foreach (var point in debug.GetSequencePoints())
                    {
                        files.Check();
                        if (++totalPoints > MaximumRows) throw Refuse("Compiler point census exceeds the existing point ceiling.");
                        if (point.IsHidden) continue;
                        if (point.Offset < 0 || point.Offset >= ilLength)
                            throw Refuse("Compiler point offset is outside its exact managed method body.");
                        if (++visible > MaximumRows || points.Count >= MaximumRows) throw Refuse("Compiler point census exceeds the existing point ceiling.");
                        var handle = point.Document.IsNil ? debug.Document : point.Document;
                        if (handle.IsNil || !mapped.TryGetValue(handle, out var source))
                        {
                            Unsupported("A nonhidden compiler point has no exact captured source-document role.");
                            continue;
                        }
                        var captured = new Point(MetadataTokens.GetToken(methodHandle), source.LogicalPath,
                            point.StartLine, point.StartColumn, point.EndLine, point.EndColumn);
                        points.Add(captured);
                        if (!SupportedSingleLine(captured, extents[source.LogicalPath]))
                            Unsupported("Multiline, remapped or out-of-extent compiler spans are unsupported; executable interior lines are not guessed.");
                    }
                    // RVA=0 declares no executable method body (abstract/import/runtime).
                    // An opaque IL body cannot be presumed compiler-generated or exempt.
                    if (method.RelativeVirtualAddress != 0 && visible == 0)
                        Unsupported("An executable compiler body has no usable nonhidden source points; authored completeness is unproven.");
                }
            }
            files.Check();
            return new(complete, reason, points.AsReadOnly(), Array.AsReadOnly(limitations.Order(StringComparer.Ordinal).ToArray()));
        }
        catch (BadImageFormatException error) { throw Refuse("Malformed managed PE/portable PDB compiler evidence.", error); }
    }

    // The role index above uses only exact captured logical/physical names and the
    // fixed /_/ root PathMap profile. No suffix or checksum-only attribution.
    internal static bool HasMappingDirective(string text, InstalledToolFiles? files = null)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029'))
            { if ((end & 4095) == 0) files?.Check(); end++; }
            var line = text.AsSpan(start, end - start);
            var offset = 0;
            while (offset < line.Length && (char.IsWhiteSpace(line[offset]) || offset == 0 && line[offset] == '\ufeff')) offset++;
            if (offset < line.Length && line[offset++] == '#')
            {
                while (offset < line.Length && char.IsWhiteSpace(line[offset])) offset++;
                var keyword = offset;
                while (offset < line.Length && char.IsAsciiLetter(line[offset])) offset++;
                if (line[keyword..offset].SequenceEqual("line")) return true;
                if (line[keyword..offset].SequenceEqual("pragma"))
                {
                    while (offset < line.Length && char.IsWhiteSpace(line[offset])) offset++;
                    keyword = offset;
                    while (offset < line.Length && char.IsAsciiLetter(line[offset])) offset++;
                    if (line[keyword..offset].SequenceEqual("checksum")) return true;
                }
            }
            start = end + 1;
        }
        return false;
    }

    internal static bool SupportedSingleLine(Point point, int[] lineLengths)
    {
        if (point.StartLine <= 0 || point.StartLine > lineLengths.Length || point.StartColumn <= 0 ||
            point.StartColumn > lineLengths[point.StartLine - 1] + 1 || point.EndColumn <= 0) return false;
        // Exclusive endpoint on the next physical line at column1 contains no
        // character on that next line. No executable interior/comment is invented.
        if (point.EndLine == point.StartLine + 1 && point.EndColumn == 1)
            return point.StartColumn <= lineLengths[point.StartLine - 1];
        return point.EndLine == point.StartLine && point.EndColumn > point.StartColumn &&
            point.EndColumn <= lineLengths[point.StartLine - 1] + 1;
    }

    private static int[] LineExtents(string text, InstalledToolFiles files)
    {
        var result = new List<int>();
        var length = text.Length;
        if (length > 0 && text[length - 1] == '\u001a') length--;
        var start = length > 0 && text[0] == '\ufeff' ? 1 : 0;
        for (var index = start; index < length; index++)
        {
            if ((index & 4095) == 0) files.Check();
            if (text[index] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029')) continue;
            if (result.Count >= MaximumRows) throw Refuse("Compiler source extent exceeds the existing point ceiling.");
            result.Add(index - start);
            if (text[index] == '\r' && index + 1 < length && text[index + 1] == '\n') index++;
            start = index + 1;
        }
        if (start < length)
        {
            if (result.Count >= MaximumRows) throw Refuse("Compiler source extent exceeds the existing point ceiling.");
            result.Add(length - start);
        }
        return result.ToArray();
    }

    private static bool HidesAuthoredCode(MetadataReader reader, CustomAttributeHandleCollection attributes, InstalledToolFiles files)
    {
        foreach (var handle in attributes)
        {
            files.Check();
            var constructor = reader.GetCustomAttribute(handle).Constructor;
            EntityHandle parent = constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
                _ => default
            };
            string name; string space;
            if (parent.Kind == HandleKind.TypeReference)
            {
                var type = reader.GetTypeReference((TypeReferenceHandle)parent);
                name = reader.GetString(type.Name); space = reader.GetString(type.Namespace);
            }
            else if (parent.Kind == HandleKind.TypeDefinition)
            {
                var type = reader.GetTypeDefinition((TypeDefinitionHandle)parent);
                name = reader.GetString(type.Name); space = reader.GetString(type.Namespace);
            }
            else continue;
            if (space == "System.Diagnostics" && name is "DebuggerHiddenAttribute" or "DebuggerNonUserCodeAttribute" or "DebuggerStepThroughAttribute") return true;
        }
        return false;
    }

    internal static string DebugIdentity(Guid guid, int age, uint peStamp, uint pdbStamp)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> size = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(size, bytes.Length);
            hash.AppendData(size); hash.AppendData(bytes);
        }
        Append("sha256-canonical-v1"); Append("portable-pdb-debug-identity-v1");
        foreach (var field in new[] { guid.ToString("D"), age.ToString(CultureInfo.InvariantCulture),
                     peStamp.ToString("x8", CultureInfo.InvariantCulture), pdbStamp.ToString("x8", CultureInfo.InvariantCulture) })
        { hash.AppendData([1]); Append(field); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static EvidenceWriteException Refuse(string message, Exception? parser = null)
    {
        var failure = new EvidenceWriteException("Compiler-line census incomplete: " + message);
        if (parser is not null) failure.Data["CompilerLineMetadataFailure"] = parser;
        return failure;
    }
}
