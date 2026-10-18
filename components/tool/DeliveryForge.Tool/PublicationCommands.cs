using DeliveryForge.Coordination;
using DeliveryForge.Publication;
namespace DeliveryForge.Tool;
// Only a parent embedding can supply actual registered objects; JSON/config cannot construct these.
public sealed class ParentPublicationBinding(PublicationService service,PublicationRun run,ICoordinatedEffectAdmission? coordination=null) {
    public string RunIdentity=>run.Definition.RunIdentity;
    public string PlanIdentity=>run.Definition.Patch.PlanIdentity;
    public string PolicyIdentity=>run.Definition.Policy.Identity;
    public async Task<ToolResult> ExecuteAsync(ToolMode mode,bool dryRun,CancellationToken cancellationToken) {
        if(mode is not (ToolMode.Pr or ToolMode.Merge))throw new ToolUsageException("Explicit pr/merge required.");
        if(coordination is not null&&!service.UsesCoordinationAdmission(coordination))throw new ToolUsageException("Actual publication mutation boundary must bind the SAME original parent admission.");
        var selected=mode==ToolMode.Pr?PublicationMode.Pr:PublicationMode.Merge;
        var key=BoundedFiles.Sha(System.Text.Encoding.UTF8.GetBytes(run.Definition.RunIdentity+"\0"+run.Definition.RevisionIdentity+"\0"+selected));
        var result=await (coordination is null||dryRun?service.ExecuteAsync(run,new(selected,key,dryRun),cancellationToken):
            coordination.EnterAsync(run.Definition.RunIdentity,()=>service.ExecuteAsync(run,new(selected,key,dryRun),cancellationToken))).ConfigureAwait(false);
        return new(result.Outcome==PublicationOutcome.DryRun?"DRY_RUN":result.Outcome is PublicationOutcome.PublishedFixture or PublicationOutcome.ReconciledFixture?"COMPLETE":"BLOCKED","PARENT_PUBLICATION",result.Reason,result);
    }
}
