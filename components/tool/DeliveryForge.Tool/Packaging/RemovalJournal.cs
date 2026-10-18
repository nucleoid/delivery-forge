using System.Text.Json;
namespace DeliveryForge.Tool.Packaging;

public sealed record RemovalReceipt(InstallationReceipt Installation,string JournalDirectory,string PlanSha256);
public sealed record RemovalPlan(string Kind,InstallationReceipt Installation,PackageManifest Manifest,IReadOnlyList<string> Steps);
// The journal is ordinary recovery DATA. It cannot recreate a live process or
// authorize retry of an entered removal whose actual return was not recorded.
public static partial class PackageLifecycle {
    public static RemovalReceipt BeginRemoval(InstallationReceipt installation,ToolLifetime life) {
        var manifest=Verify(installation,life);
        var steps=manifest.Files.Where(x=>x.Path!=ClaimName).Select(x=>"file:"+x.Path)
            .Concat(manifest.Directories.OrderByDescending(x=>x.Count(c=>c=='/')).ThenByDescending(x=>x.Length).Select(x=>"directory:"+x))
            .Concat(new[]{"file:"+ManifestName,"file:"+ClaimName,"root:"}).ToArray();
        if(steps.Length>MaximumEntries*2+3)throw new IOException("Finite removal plan exceeded.");
        var journal=BoundedFiles.Plain(installation.Prefix+".removal-"+Guid.NewGuid().ToString("N"),true);
        life.Check();if(Directory.Exists(journal)||File.Exists(journal))throw new IOException("Removal journal already exists.");Directory.CreateDirectory(journal);life.Check();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new RemovalPlan("DeliveryForgeRemoval-v1",installation,manifest,steps),BoundedFiles.Json);
        BoundedFiles.WriteNew(Path.Combine(journal,"plan.json"),bytes,life);return new(installation,journal,BoundedFiles.Sha(bytes));
    }
    public static void ContinueRemoval(RemovalReceipt receipt,ToolLifetime life) {
        var journal=BoundedFiles.Plain(receipt.JournalDirectory,true);BoundedFiles.Separate(journal,receipt.Installation.Prefix);
        var bytes=BoundedFiles.Read(Path.Combine(journal,"plan.json"),life,1048576);
        if(BoundedFiles.Sha(bytes)!=receipt.PlanSha256)throw new IOException("Removal plan pin changed.");
        var plan=BoundedFiles.ReadJson<RemovalPlan>(Path.Combine(journal,"plan.json"),life,1048576);
        if(plan.Kind!="DeliveryForgeRemoval-v1"||plan.Installation!=receipt.Installation||plan.Steps.Count>MaximumEntries*2+3)throw new IOException("Removal plan identity differs.");
        var expectedSteps=plan.Manifest.Files.Where(x=>x.Path!=ClaimName).Select(x=>"file:"+x.Path)
            .Concat(plan.Manifest.Directories.OrderByDescending(x=>x.Count(c=>c=='/')).ThenByDescending(x=>x.Length).Select(x=>"directory:"+x))
            .Concat(new[]{"file:"+ManifestName,"file:"+ClaimName,"root:"}).ToArray();
        if(!expectedSteps.SequenceEqual(plan.Steps,StringComparer.Ordinal))throw new IOException("Removal plan roles differ.");
        var known=new HashSet<string>(StringComparer.Ordinal){"plan.json"};var completed=0;var pendingSeen=false;
        for(var i=0;i<plan.Steps.Count;i++) {
            life.Check();var intent=i.ToString("D4",System.Globalization.CultureInfo.InvariantCulture)+".entered.json";var returned=i.ToString("D4",System.Globalization.CultureInfo.InvariantCulture)+".returned.json";
            var enteredPath=Path.Combine(journal,intent);var returnedPath=Path.Combine(journal,returned);
            if(File.Exists(enteredPath)) {
                known.Add(intent);if(pendingSeen)throw new IOException("Removal journal is not a contiguous prefix.");
                var entered=BoundedFiles.ReadJson<RemovalStep>(enteredPath,life,4096);if(entered.Index!=i||entered.Step!=plan.Steps[i]||entered.State!="Entered")throw new IOException("Removal intent differs.");
                if(!File.Exists(returnedPath))throw new IOException("Removal entered but actual return unknown; no retry or inferred deletion.");
                known.Add(returned);var done=BoundedFiles.ReadJson<RemovalStep>(returnedPath,life,4096);if(done.Index!=i||done.Step!=plan.Steps[i]||done.State!="Returned")throw new IOException("Removal return differs.");completed++;
            }else { pendingSeen=true;if(File.Exists(returnedPath))throw new IOException("Removal return has no actual intent."); }
        }
        var entries=Directory.EnumerateFileSystemEntries(journal).Take(MaximumEntries*4+9).ToArray();life.Check();if(entries.Length!=known.Count||entries.Any(x=>!known.Contains(Path.GetFileName(x))))throw new IOException("Foreign removal journal entry.");
        // Revalidate complete remaining topology before any resumed deletion. An
        // absent original is accepted only for an exact previously returned step.
        var removed=plan.Steps.Take(completed).ToHashSet(StringComparer.Ordinal);
        if(!removed.Contains("root:")) {
            var prefix=BoundedFiles.Plain(receipt.Installation.Prefix,true);
            var expectedFiles=plan.Manifest.Files.Select(x=>x.Path).Append(ManifestName).Where(x=>!removed.Contains("file:"+x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedDirs=plan.Manifest.Directories.Where(x=>!removed.Contains("directory:"+x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actualFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var actualDirs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var stack=new Stack<string>();stack.Push(prefix);var count=0;
            while(stack.Count!=0)foreach(var path in Directory.EnumerateFileSystemEntries(stack.Pop())) {
                life.Check();if(++count>MaximumEntries*2)throw new IOException("Removal topology exceeds finite profile.");var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Removal reparse path.");var relative=Relative(Path.GetRelativePath(prefix,path).Replace('\\','/'));
                if((attributes&FileAttributes.Directory)!=0){if(!actualDirs.Add(relative))throw new IOException("Removal directory case collision before any resumed deletion.");stack.Push(path);}else if(!actualFiles.Add(relative))throw new IOException("Removal file case collision before any resumed deletion.");
            }
            if(!actualFiles.SetEquals(expectedFiles)||!actualDirs.SetEquals(expectedDirs))throw new IOException("Remaining removal topology changed.");
            // New bound executable metadata is checked for EVERY remaining
            // owned role before the first resumed deletion, not just at its row.
            foreach(var row in plan.Manifest.Files.Where(row=>!removed.Contains("file:"+row.Path)))RequireOwnedMode(Inside(prefix,row.Path),row,life);
        }else if(Directory.Exists(receipt.Installation.Prefix)||File.Exists(receipt.Installation.Prefix))throw new IOException("Removed prefix was recreated; never adopt it.");
        for(var i=completed;i<plan.Steps.Count;i++) {
            life.Check();var step=plan.Steps[i];var split=step.IndexOf(':');var kind=step[..split];var relative=step[(split+1)..];
            var target=kind=="root"?BoundedFiles.Plain(receipt.Installation.Prefix,true):Inside(receipt.Installation.Prefix,relative,kind=="directory");
            if(kind=="file") {
                var actual=BoundedFiles.Read(target,life);var pin=relative==ManifestName?new InstalledFile(relative,actual.Length,receipt.Installation.ManifestSha256):plan.Manifest.Files.Single(x=>x.Path==relative);
                if(actual.LongLength!=pin.Bytes||BoundedFiles.Sha(actual)!=pin.Sha256)throw new IOException("Owned removal input changed.");
                RequireOwnedMode(target,pin,life);
            }
            BoundedFiles.WriteNew(Path.Combine(journal,i.ToString("D4",System.Globalization.CultureInfo.InvariantCulture)+".entered.json"),JsonSerializer.SerializeToUtf8Bytes(new RemovalStep(i,step,"Entered")),life);
            life.Check();if(kind=="file")File.Delete(target);else Directory.Delete(target,false);
            // If this post or publication fails, the entered-only record remains
            // UNKNOWN. Recovery refuses it; it never silently retries the effect.
            life.Check();BoundedFiles.WriteNew(Path.Combine(journal,i.ToString("D4",System.Globalization.CultureInfo.InvariantCulture)+".returned.json"),JsonSerializer.SerializeToUtf8Bytes(new RemovalStep(i,step,"Returned")),life);
        }
    }
    sealed record RemovalStep(int Index,string Step,string State);
}
