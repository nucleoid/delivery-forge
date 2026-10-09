namespace DeliveryForge.Execution.Tests;

public sealed class TestSupportTests
{
    [Fact]
    public void Temporary_directory_dispose_removes_owned_read_only_files()
    {
        string root;
        using (var directory = new TemporaryDirectory())
        {
            root = directory.Path;
            var nested = Directory.CreateDirectory(System.IO.Path.Combine(root, "objects", "ab"));
            var file = System.IO.Path.Combine(nested.FullName, "object");
            File.WriteAllText(file, "fixture");
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Temporary_directory_dispose_does_not_follow_directory_symlinks()
    {
        using var outside = new TemporaryDirectory();
        var target = Directory.CreateDirectory(System.IO.Path.Combine(outside.Path, "target"));
        var targetFile = System.IO.Path.Combine(target.FullName, "read-only");
        File.WriteAllText(targetFile, "outside");
        File.SetAttributes(targetFile, File.GetAttributes(targetFile) | FileAttributes.ReadOnly);

        using (var directory = new TemporaryDirectory())
            Directory.CreateSymbolicLink(System.IO.Path.Combine(directory.Path, "link"), target.FullName);

        Assert.True(File.Exists(targetFile));
        Assert.True((File.GetAttributes(targetFile) & FileAttributes.ReadOnly) != 0);
    }
}
