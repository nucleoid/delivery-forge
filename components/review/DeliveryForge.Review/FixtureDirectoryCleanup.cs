namespace DeliveryForge.Review;

// Called only for newly generated fixture roots owned by the test/isolated host.
internal static class FixtureDirectoryCleanup
{
    internal static void Delete(string path)
    {
        var root = new DirectoryInfo(Path.GetFullPath(path));
        if (!root.Exists) return;
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            root.Delete();
            return;
        }
        var ownedFiles = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false
        };
        foreach (var file in Directory.EnumerateFiles(root.FullName, "*", ownedFiles))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        root.Delete(recursive: true);
    }
}
