using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;
public sealed class BoundedFilesTests {
    [Fact] public void Duplicate_json_and_unknown_config_fields_refuse() {
        var path=Path.Combine(Path.GetTempPath(),"df-config-"+Guid.NewGuid().ToString("N"));try { File.WriteAllText(path,"{\"Repository\":\"one\",\"Repository\":\"two\"}");using var life=new ToolLifetime(TimeSpan.FromSeconds(10));Assert.ThrowsAny<Exception>(()=>BoundedFiles.ReadJson<TrustedConfiguration>(path,life)); }finally{File.Delete(path);}
    }
    [Fact] public void Unknown_config_member_is_not_silently_accepted() {
        var path=Path.Combine(Path.GetTempPath(),"df-unknown-"+Guid.NewGuid().ToString("N"));try { File.WriteAllText(path,"{\"Repository\":\"fixture/repo\",\"RepositoryRoot\":\"/fixture\",\"MutableReference\":\"main\",\"GitExecutable\":\"/git\",\"IssuePrivateCapability\":true}");using var life=new ToolLifetime(TimeSpan.FromSeconds(10));Assert.Throws<System.Text.Json.JsonException>(()=>BoundedFiles.ReadJson<TrustedConfiguration>(path,life)); }finally{File.Delete(path);}
    }
    [Fact] public void Restricting_whole_budget_never_refunds_original_work() {
        using var life=new ToolLifetime(TimeSpan.FromSeconds(1200));var before=life.Remaining;life.Restrict(TimeSpan.FromSeconds(1199));Assert.True(life.Remaining<=before);using var direct=new ToolLifetime(TimeSpan.FromSeconds(1199));Assert.True(direct.Remaining<=TimeSpan.FromSeconds(1100));
    }
    [Fact] public void Read_size_and_create_new_preserve_existing_bytes() { var path=Path.Combine(Path.GetTempPath(),"df-bound-"+Guid.NewGuid().ToString("N"));try { File.WriteAllBytes(path,[1,2,3]);using(var readLife=new ToolLifetime(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken)){
            Assert.Throws<IOException>(()=>BoundedFiles.Read(path,readLife,2));
            // Failure enters cleanup. This SAME original must reject new work,
            // even though its wall/work duration has not naturally expired.
            Assert.Throws<TimeoutException>(readLife.CheckWork);
            Assert.Throws<TimeoutException>(()=>BoundedFiles.WriteNew(path,[4],readLife));
            Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(path));
        }
        // CreateNew is an independent scenario; no retry/refund of readLife.
        using(var createLife=new ToolLifetime(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken)){
            Assert.Throws<IOException>(()=>BoundedFiles.WriteNew(path,[4],createLife));
            Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(path));
        } }finally{File.Delete(path);} }
    [Fact] public void Result_replay_overlap_is_rejected() { var root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"df-results"));Assert.Throws<ToolUsageException>(()=>BoundedFiles.Separate(root,Path.Combine(root,"replay.json")));Assert.Throws<ToolUsageException>(()=>BoundedFiles.Separate(root,root)); }
    [Fact] public void Precancelled_original_refuses_before_file_access() { using var c=new CancellationTokenSource();c.Cancel();using var life=new ToolLifetime(TimeSpan.FromSeconds(10),c.Token);Assert.Throws<OperationCanceledException>(()=>BoundedFiles.Read(Path.Combine(Path.GetTempPath(),"never-read"),life)); }
}
