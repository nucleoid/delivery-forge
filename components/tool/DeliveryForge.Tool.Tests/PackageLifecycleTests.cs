using System.IO.Compression;
using System.Text;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Packaging;
namespace DeliveryForge.Tool.Tests;

public sealed class PackageLifecycleTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();
    sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "delivery-forge-package-fixture-" + Guid.NewGuid().ToString("N"));
        public string Archive => Path.Combine(Root, "fixture.nupkg"); public string Prefix => Path.Combine(Root, "prefix with spaces");
        public Fixture() { Directory.CreateDirectory(Root); }
        public void Package(string? extra = null, string json = "{}") { using var file = new FileStream(Archive, FileMode.CreateNew); using var zip = new ZipArchive(file, ZipArchiveMode.Create); foreach (var name in new[] { "tools/net10.0/any/DeliveryForge.Tool.dll", "tools/net10.0/any/DeliveryForge.Tool.runtimeconfig.json", "tools/net10.0/any/content/SKILL.md" }.Concat(extra is null ? [] : new[] { extra })) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(name.EndsWith(".json", StringComparison.Ordinal) ? json : "contract fixture only"); } }
        public FilePin Host() { var p = Path.Combine(Root, "explicit-runtime-host"); File.WriteAllText(p, "DATA host fixture, never invoked"); var b = File.ReadAllBytes(p); return new(p, b.Length, BoundedFiles.Sha(b)); }
        public void Dispose() { var expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "delivery-forge-package-fixture-"; if (!Path.GetFullPath(Root).StartsWith(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Fixture cleanup target escaped owned temporary root."); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    [Fact(Skip = "Unix execute-mode control requires Linux.", SkipUnless = nameof(IsLinux))]
    public void Genuine_owned_gate_install_records_execute_mode_and_refuses_drift_before_resume_deletion()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Actual Unix execute-mode control requires Linux.");
        using var f = new Fixture(); f.Package("tools/net10.0/any/runtime/linux-x64/gate/DeliveryForge.LinuxScopeBirthGate"); using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var installed = PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life); var manifest = PackageLifecycle.Verify(installed, life); var gate = manifest.Files.Single(row => row.Path == "payload/runtime/linux-x64/gate/DeliveryForge.LinuxScopeBirthGate"); Assert.Equal((int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), gate.UnixMode);
        var removal = PackageLifecycle.BeginRemoval(installed, life); var before = Directory.GetFiles(f.Prefix, "*", SearchOption.AllDirectories).ToDictionary(path => path, path => BoundedFiles.Sha(File.ReadAllBytes(path)), StringComparer.Ordinal);
        File.SetUnixFileMode(Path.Combine(f.Prefix, gate.Path), UnixFileMode.UserRead);
        Assert.Throws<IOException>(() => PackageLifecycle.ContinueRemoval(removal, life));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(f.Prefix, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)); foreach (var row in before) Assert.Equal(row.Value, BoundedFiles.Sha(File.ReadAllBytes(row.Key)));
        // Actual BCL ZIP/permissions/removal positive and drift refusal only;
        // inert fixture bytes are never treated as a compiled executable.
    }
    [Fact]
    public void Genuine_archive_install_verify_reinstall_uninstall_preserves_outside_user_file()
    {
        using var f = new Fixture(); f.Package(); var host = f.Host(); var user = Path.Combine(f.Root, "user-config.txt"); File.WriteAllText(user, "preserve"); using var life = new ToolLifetime(TimeSpan.FromSeconds(30));
        var first = PackageLifecycle.Install(f.Archive, f.Prefix, host, life); Assert.Equal("DeliveryForgeDisposableInstall-v1", PackageLifecycle.Verify(first, life).Kind);
        var next = PackageLifecycle.Reinstall(f.Archive, first, host, life); Assert.Equal(first.PayloadDigest, next.PayloadDigest); Assert.NotEqual(first.Nonce, next.Nonce);
        PackageLifecycle.Uninstall(next, life); Assert.False(Directory.Exists(f.Prefix)); Assert.Equal("preserve", File.ReadAllText(user));
        // Actual BCL ZIP/filesystem positive; fixture DLL/host were NEVER executed or certified.
    }
    [Fact]
    public void Changed_owned_payload_refuses_before_any_delete()
    {
        using var f = new Fixture(); f.Package(); using var life = new ToolLifetime(TimeSpan.FromSeconds(30)); var installed = PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life); var target = Path.Combine(f.Prefix, "payload", "DeliveryForge.Tool.dll"); File.AppendAllText(target, "changed");
        Assert.Throws<IOException>(() => PackageLifecycle.Uninstall(installed, life)); Assert.True(File.Exists(Path.Combine(f.Prefix, ".delivery-forge-install.json"))); Assert.True(File.Exists(target));
    }
    [Fact]
    public void Actual_post_BeginRemoval_later_file_drift_refuses_before_any_resumed_delete()
    {
        using var f = new Fixture();
        f.Package();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var installed = PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life);
        var removal = PackageLifecycle.BeginRemoval(installed, life);
        var plan = BoundedFiles.ReadJson<RemovalPlan>(Path.Combine(removal.JournalDirectory, "plan.json"), life);
        const string laterRelative = "payload/content/SKILL.md";
        var laterStep = "file:" + laterRelative;
        var firstStep = plan.Steps.First(step => step.StartsWith("file:", StringComparison.Ordinal));
        Assert.NotEqual(firstStep, laterStep);
        Assert.True(plan.Steps.ToList().IndexOf(firstStep) < plan.Steps.ToList().IndexOf(laterStep));
        var earlierPath = Path.Combine(f.Prefix, firstStep["file:".Length..]);
        var earlierBytes = File.ReadAllBytes(earlierPath);
        var changedPath = Path.Combine(f.Prefix, laterRelative);
        File.AppendAllText(changedPath, " changed after BeginRemoval");
        var expected = Directory.GetFiles(f.Prefix, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

        Assert.Throws<IOException>(() => PackageLifecycle.ContinueRemoval(removal, life));

        Assert.Equal(earlierBytes, File.ReadAllBytes(earlierPath));
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(f.Prefix, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var row in expected) Assert.Equal(row.Value, File.ReadAllBytes(row.Key));
        Assert.Empty(Directory.GetFiles(removal.JournalDirectory, "*.entered.json"));
        Assert.Empty(Directory.GetFiles(removal.JournalDirectory, "*.returned.json"));
    }
    [Fact]
    public void Unknown_file_or_empty_directory_refuses_preserving_user_data()
    {
        using var f = new Fixture(); f.Package(); using var life = new ToolLifetime(TimeSpan.FromSeconds(30)); var installed = PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life); var added = Path.Combine(f.Prefix, "foreign.txt"); File.WriteAllText(added, "user");
        Assert.Throws<IOException>(() => PackageLifecycle.Uninstall(installed, life)); Assert.Equal("user", File.ReadAllText(added)); File.Delete(added); Directory.CreateDirectory(Path.Combine(f.Prefix, "foreign-empty")); Assert.Throws<IOException>(() => PackageLifecycle.Uninstall(installed, life));
    }
    [Fact]
    public void Genuine_removal_plan_then_entered_only_refuses_retry_and_preserves_payload()
    {
        using var f = new Fixture(); f.Package(); using var life = new ToolLifetime(TimeSpan.FromSeconds(30)); var installed = PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life);
        var removal = PackageLifecycle.BeginRemoval(installed, life); var plan = BoundedFiles.ReadJson<RemovalPlan>(Path.Combine(removal.JournalDirectory, "plan.json"), life);
        File.WriteAllText(Path.Combine(removal.JournalDirectory, "0000.entered.json"), System.Text.Json.JsonSerializer.Serialize(new { Index = 0, Step = plan.Steps[0], State = "Entered" }));
        var error = Assert.Throws<IOException>(() => PackageLifecycle.ContinueRemovalRetainingReceipt(removal, life)); Assert.Contains("actual return unknown", error.Message); Assert.Same(removal, error.Data["ActualRemovalReceipt"]); Assert.True(File.Exists(Path.Combine(f.Prefix, "payload", "DeliveryForge.Tool.dll")));
    }
    [Fact]
    public void Genuine_removal_journal_completed_reobservation_never_adopts_recreated_prefix()
    {
        using var f = new Fixture(); f.Package(); using var life = new ToolLifetime(TimeSpan.FromSeconds(30)); var installed = PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life);
        var removal = PackageLifecycle.BeginRemoval(installed, life); PackageLifecycle.ContinueRemoval(removal, life); Assert.False(Directory.Exists(f.Prefix)); PackageLifecycle.ContinueRemoval(removal, life);
        Directory.CreateDirectory(f.Prefix); File.WriteAllText(Path.Combine(f.Prefix, "user.txt"), "preserve"); Assert.Throws<IOException>(() => PackageLifecycle.ContinueRemoval(removal, life)); Assert.Equal("preserve", File.ReadAllText(Path.Combine(f.Prefix, "user.txt")));
    }
    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    public void Actual_post_BeginRemoval_case_collision_refuses_before_any_resumed_delete(string kind)
    {
        using var f = new Fixture(); f.Package(); using var life = new ToolLifetime(TimeSpan.FromSeconds(30)); var installed = PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life); var removal = PackageLifecycle.BeginRemoval(installed, life);
        // Windows positive exercises the ordinary complete removal. The distinct
        // case-alias filesystem regression is genuinely executed on Linux only.
        if (!OperatingSystem.IsLinux()) { PackageLifecycle.ContinueRemoval(removal, life); Assert.False(Directory.Exists(f.Prefix)); return; }
        var originals = Directory.GetFiles(f.Prefix, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes, StringComparer.Ordinal);
        var alias = kind == "file" ? Path.Combine(f.Prefix, "payload", "deliveryforge.tool.dll") : Path.Combine(f.Prefix, "PAYLOAD");
        if (kind == "file") File.WriteAllText(alias, "FOREIGN user file"); else Directory.CreateDirectory(alias);
        Assert.Throws<IOException>(() => PackageLifecycle.ContinueRemoval(removal, life));
        foreach (var original in originals) { Assert.True(File.Exists(original.Key)); Assert.Equal(original.Value, File.ReadAllBytes(original.Key)); }
        Assert.Empty(Directory.GetFiles(removal.JournalDirectory, "*.entered.json")); Assert.Empty(Directory.GetFiles(removal.JournalDirectory, "*.returned.json"));
        if (kind == "file") Assert.Equal("FOREIGN user file", File.ReadAllText(alias)); else Assert.True(Directory.Exists(alias));
    }
    [Fact]
    public void Serviced_external_runtime_host_blocks_launch_trust_but_not_exact_owned_removal()
    {
        using var f = new Fixture(); f.Package(); var host = f.Host();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var receipt = PackageLifecycle.Install(f.Archive, f.Prefix, host, life);
        File.AppendAllText(host.Path, " serviced outside owned prefix"); var serviced = File.ReadAllBytes(host.Path);
        Assert.Throws<IOException>(() => PackageLifecycle.Verify(receipt, life));
        PackageLifecycle.Uninstall(receipt, life);
        Assert.False(Directory.Exists(f.Prefix)); Assert.Equal(serviced, File.ReadAllBytes(host.Path));
    }
    [Fact]
    public void Actual_removal_failure_retains_its_exact_receipt_without_retrying_or_inventing_a_return()
    {
        using var f = new Fixture(); f.Package();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var installed = PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life);
        var removal = PackageLifecycle.BeginRemoval(installed, life);
        var path = Path.Combine(f.Prefix, "payload", "DeliveryForge.Tool.dll"); File.AppendAllText(path, " actual post-plan drift");
        var actual = File.ReadAllBytes(path);
        var primary = Assert.Throws<IOException>(() => PackageLifecycle.ContinueRemovalRetainingReceipt(removal, life));
        Assert.Same(removal, primary.Data["ActualRemovalReceipt"]);
        Assert.Equal(removal.PlanSha256, BoundedFiles.Sha(File.ReadAllBytes(Path.Combine(removal.JournalDirectory, "plan.json"))));
        Assert.Equal(actual, File.ReadAllBytes(path)); Assert.Empty(Directory.GetFiles(removal.JournalDirectory, "*.entered.json"));
        Assert.Throws<IOException>(() => PackageLifecycle.ContinueRemoval(removal, life));
        Assert.Equal(actual, File.ReadAllBytes(path));
        // Exact receipt is recovery DATA, not permission to retry entered effects.
    }
    [Theory]
    [InlineData("file-parent")]
    [InlineData("directory-role")]
    public void Complete_payload_topology_is_validated_before_new_prefix_birth(string shape)
    {
        using var f = new Fixture();
        using (var file = new FileStream(f.Archive, FileMode.CreateNew))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "tools/net10.0/any/DeliveryForge.Tool.dll", "tools/net10.0/any/DeliveryForge.Tool.runtimeconfig.json", "tools/net10.0/any/content/SKILL.md", "tools/net10.0/any/blocked" })
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(name.EndsWith(".json", StringComparison.Ordinal) ? "{}" : "inert archive DATA"); }
            using var last = new StreamWriter(zip.CreateEntry(shape == "file-parent" ? "tools/net10.0/any/blocked/child" : "tools/net10.0/any/content").Open(), new UTF8Encoding(false)); last.Write("inert late conflict");
        }
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(() => PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life));
        Assert.False(Directory.Exists(f.Prefix));
    }
    [Fact] public void Existing_prefix_is_never_adopted() { using var f = new Fixture(); f.Package(); var host = f.Host(); Directory.CreateDirectory(f.Prefix); using var life = new ToolLifetime(TimeSpan.FromSeconds(30)); Assert.Throws<IOException>(() => PackageLifecycle.Install(f.Archive, f.Prefix, host, life)); }
    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("tools/net10.0/any/../escape.dll")]
    [InlineData("TOOLS/net10.0/any/DeliveryForge.Tool.dll")]
    public void Archive_traversal_or_case_alias_refuses(string extra) { using var f = new Fixture(); f.Package(extra); using var life = new ToolLifetime(TimeSpan.FromSeconds(30)); Assert.Throws<IOException>(() => PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life)); Assert.False(Directory.Exists(f.Prefix)); }
    [Fact] public void Manifest_pin_substitution_refuses_without_delete() { using var f = new Fixture(); f.Package(); using var life = new ToolLifetime(TimeSpan.FromSeconds(30)); var receipt = PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life); Assert.Throws<IOException>(() => PackageLifecycle.Uninstall(receipt with { ManifestSha256 = new string('0', 64) }, life)); Assert.True(Directory.Exists(f.Prefix)); }
    [Theory]
    [InlineData("C:\\Users\\fixture-person\\secret.txt")]
    [InlineData("/home/fixture-person/secret.txt")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    public void Public_scan_refuses_private_paths_and_keys(string value) => Assert.Throws<IOException>(() => PublicContentScanner.RequireSafe(Encoding.UTF8.GetBytes(value)));
    [Fact]
    public void Genuine_archive_JSON_escaped_private_path_is_refused_before_public_install_acceptance()
    {
        using var f = new Fixture(); f.Package(json: System.Text.Json.JsonSerializer.Serialize(new { path = @"C:\Users\fixture-person\secret.txt" })); using var life = new ToolLifetime(TimeSpan.FromSeconds(30)); Assert.Throws<IOException>(() => PackageLifecycle.Install(f.Archive, f.Prefix, f.Host(), life)); Assert.False(File.Exists(Path.Combine(f.Prefix, ".delivery-forge-install.json"))); Assert.False(Directory.Exists(f.Prefix));
    }
    [Theory]
    [InlineData("{\"path\":\"C:\\\\Users\\\\fixture-person\\\\secret.txt\"}")]
    [InlineData("{\"path\":\"C:\\u005cUsers\\u005cfixture-person\\u005csecret.txt\"}")]
    [InlineData("{\"C:\\\\Users\\\\fixture-person\\\\secret.txt\":true}")]
    public void Complete_JSON_strings_and_property_names_are_scanned_after_strict_decode(string value) => Assert.Throws<IOException>(() => PublicContentScanner.RequireJsonSafe(Encoding.UTF8.GetBytes(value)));
    [Fact]
    public void Public_JSON_positive_then_duplicate_invalid_UTF8_and_complete_scalar_limit_refuse()
    {
        PublicContentScanner.RequireJsonSafe(Encoding.UTF8.GetBytes("{\"text\":\"Portable \\u03bb\",\"items\":[1,true,null]}")); Assert.Throws<IOException>(() => PublicContentScanner.RequireJsonSafe(Encoding.UTF8.GetBytes("{\"same\":1,\"same\":2}"))); Assert.ThrowsAny<Exception>(() => PublicContentScanner.RequireJsonSafe(new byte[] { (byte)'"', 255, (byte)'"' })); Assert.Throws<IOException>(() => PublicContentScanner.RequireJsonSafe(Encoding.UTF8.GetBytes("{\"text\":\"" + new string('a', 65537) + "\"}")));
    }
    [Fact] public void Public_scan_positive_plain_content_and_unicode() => PublicContentScanner.RequireSafe(Encoding.UTF8.GetBytes("Portable contract fixture: ÃŽÂ», no private context."));
}
