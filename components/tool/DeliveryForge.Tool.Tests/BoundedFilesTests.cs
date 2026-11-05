using DeliveryForge.Tool;
using System.Runtime.InteropServices;
using System.Text;
namespace DeliveryForge.Tool.Tests;

public sealed class BoundedFilesTests
{
    [Fact]
    public void Duplicate_json_and_unknown_config_fields_refuse()
    {
        var path = Path.Combine(Path.GetTempPath(), "df-config-" + Guid.NewGuid().ToString("N")); try { File.WriteAllText(path, "{\"Repository\":\"one\",\"Repository\":\"two\"}"); using var life = new ToolLifetime(TimeSpan.FromSeconds(10)); Assert.ThrowsAny<Exception>(() => BoundedFiles.ReadJson<TrustedConfiguration>(path, life)); } finally { File.Delete(path); }
    }
    [Fact]
    public void Unknown_config_member_is_not_silently_accepted()
    {
        var path = Path.Combine(Path.GetTempPath(), "df-unknown-" + Guid.NewGuid().ToString("N")); try { File.WriteAllText(path, "{\"Repository\":\"fixture/repo\",\"RepositoryRoot\":\"/fixture\",\"MutableReference\":\"main\",\"GitExecutable\":\"/git\",\"IssuePrivateCapability\":true}"); using var life = new ToolLifetime(TimeSpan.FromSeconds(10)); Assert.Throws<System.Text.Json.JsonException>(() => BoundedFiles.ReadJson<TrustedConfiguration>(path, life)); } finally { File.Delete(path); }
    }
    [Fact]
    public void Restricting_whole_budget_never_refunds_original_work()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(1200)); var before = life.Remaining; life.Restrict(TimeSpan.FromSeconds(1199)); Assert.True(life.Remaining <= before); using var direct = new ToolLifetime(TimeSpan.FromSeconds(1199)); Assert.True(direct.Remaining <= TimeSpan.FromSeconds(1100));
    }
    [Fact]
    public void Read_size_and_create_new_preserve_existing_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), "df-bound-" + Guid.NewGuid().ToString("N")); try
        {
            File.WriteAllBytes(path, [1, 2, 3]); using (var readLife = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
            {
                Assert.Throws<IOException>(() => BoundedFiles.Read(path, readLife, 2));
                // Failure enters cleanup. This SAME original must reject new work,
                // even though its wall/work duration has not naturally expired.
                Assert.Throws<TimeoutException>(readLife.CheckWork);
                Assert.Throws<TimeoutException>(() => BoundedFiles.WriteNew(path, [4], readLife));
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            }
            // CreateNew is an independent scenario; no retry/refund of readLife.
            using (var createLife = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
            {
                Assert.Throws<IOException>(() => BoundedFiles.WriteNew(path, [4], createLife));
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            }
        }
        finally { File.Delete(path); }
    }
    [Fact] public void Result_replay_overlap_is_rejected() { var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "df-results")); Assert.Throws<ToolUsageException>(() => BoundedFiles.Separate(root, Path.Combine(root, "replay.json"))); Assert.Throws<ToolUsageException>(() => BoundedFiles.Separate(root, root)); }
    [Fact] public void Precancelled_original_refuses_before_file_access() { using var c = new CancellationTokenSource(); c.Cancel(); using var life = new ToolLifetime(TimeSpan.FromSeconds(10), c.Token); Assert.Throws<OperationCanceledException>(() => BoundedFiles.Read(Path.Combine(Path.GetTempPath(), "never-read"), life)); }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_extended_spelling_cannot_hide_existing_file_or_absent_output_overlap(bool existingFile)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows extended-path identity.");
        var root = Directory.CreateTempSubdirectory("df-separation-extended-").FullName;
        TestContext.Current.TestOutputHelper?.WriteLine("Retained separation fixture: " + root);
        var parent = Path.Combine(root, "protected-parent-with-a-long-name"); Directory.CreateDirectory(parent);
        var file = Path.Combine(parent, "protected-long-file-name.json"); File.WriteAllText(file, "preserved");
        var protectedPath = existingFile ? file : parent;
        var extended = ExtendedWindowsPath(protectedPath);
        var output = existingFile ? extended : Path.Combine(extended, "absent-output", "plan.json");
        Assert.Throws<ToolUsageException>(() => BoundedFiles.Separate(protectedPath, output));
        Assert.Throws<ToolUsageException>(() => BoundedFiles.Separate(output, protectedPath));
        Assert.Equal("preserved", File.ReadAllText(file)); Assert.False(Directory.Exists(Path.Combine(parent, "absent-output")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_actual_short_alias_preserves_the_same_overlap_refusal(bool existingFile)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows short-name identity.");
        var root = Directory.CreateTempSubdirectory("df-separation-short-").FullName;
        TestContext.Current.TestOutputHelper?.WriteLine("Retained separation fixture: " + root);
        var parent = Path.Combine(root, "protected-parent-with-a-long-name"); Directory.CreateDirectory(parent);
        var file = Path.Combine(parent, "protected-long-file-name.json"); File.WriteAllText(file, "preserved");
        var protectedPath = existingFile ? file : parent; var alias = TryGetShortPath(protectedPath);
        if (alias is null || string.Equals(Path.GetFileName(alias), Path.GetFileName(protectedPath), StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Selected Windows volume did not supply a distinct actual8.3 entry alias.");
        var output = existingFile ? alias! : Path.Combine(alias!, "absent-output", "plan.json");
        Assert.Throws<ToolUsageException>(() => BoundedFiles.Separate(protectedPath, output));
        Assert.Throws<ToolUsageException>(() => BoundedFiles.Separate(output, protectedPath));
        Assert.Equal("preserved", File.ReadAllText(file)); Assert.False(Directory.Exists(Path.Combine(parent, "absent-output")));
    }
    [Fact]
    public void Separate_sibling_output_with_nonexisting_suffix_stays_absent()
    {
        var root = Directory.CreateTempSubdirectory("df-separation-sibling-").FullName;
        TestContext.Current.TestOutputHelper?.WriteLine("Retained separation fixture: " + root);
        var parent = Path.Combine(root, "parent"); Directory.CreateDirectory(parent);
        var output = Path.Combine(root, "parent-sibling", "absent", "plan.json");
        BoundedFiles.Separate(parent, output); BoundedFiles.Separate(output, parent);
        Assert.False(Directory.Exists(Path.Combine(root, "parent-sibling")));
    }
    [Theory]
    [InlineData("ambiguous.")]
    [InlineData("ambiguous ")]
    public void Windows_unresolved_ambiguous_output_component_is_refused(string component)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows unresolved component normalization.");
        var root = Directory.CreateTempSubdirectory("df-separation-component-").FullName;
        TestContext.Current.TestOutputHelper?.WriteLine("Retained separation fixture: " + root);
        Assert.Throws<ToolUsageException>(() => BoundedFiles.Separate(Path.Combine(root, "parent"), Path.Combine(ExtendedWindowsPath(root), component, "plan.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
    [Fact]
    public void Windows_separation_still_refuses_a_reparse_ancestor()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows reparse identity refusal.");
        var root = Directory.CreateTempSubdirectory("df-separation-reparse-").FullName;
        TestContext.Current.TestOutputHelper?.WriteLine("Retained separation fixture: " + root);
        var target = Path.Combine(root, "target"); Directory.CreateDirectory(target); var link = Path.Combine(root, "link");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        { Assert.Skip("Selected Windows host cannot create the required disposable reparse entry."); }
        Assert.Throws<IOException>(() => BoundedFiles.Separate(Path.Combine(root, "outside"), Path.Combine(link, "absent", "plan.json")));
        Assert.False(Directory.Exists(Path.Combine(target, "absent")));
    }
    static string ExtendedWindowsPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    // Same GetShortPathName test-helper pattern as Execution.Tests/TestSupport;
    // no fsutil/cmd/service query, and missing aliases are explicit skipped proof.
    static string? TryGetShortPath(string path)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity); var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
            if (length == 0) return null;
            if (length < buffer.Capacity) return buffer.ToString();
            capacity = checked((int)length + 1);
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint shortPathLength);

}
