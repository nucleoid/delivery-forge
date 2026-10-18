namespace DeliveryForge.Tool;
public sealed record ToolFailureNode(string Type,int HResult,string? Message,bool MessageUnavailable,IReadOnlyList<ToolFailureNode> Causes);
public sealed record ToolFailureReport(string Outcome,string Code,string Message,ToolFailureNode Failure,bool DetailsUnavailable) {
    public static ToolFailureReport Create(Exception actual) {
        var count=0;var unavailable=false;
        ToolFailureNode Read(Exception failure,int depth) {
            // Caller admits a node before descending. No collection copy precedes
            // its size admission, and the complete returned tree has at most16 nodes.
            count++;var message=failure.Message;var omitted=message.Length>4096;if(omitted)unavailable=true;
            IReadOnlyList<Exception> children=failure is AggregateException aggregate?aggregate.InnerExceptions:failure.InnerException is Exception inner?new[]{inner}:Array.Empty<Exception>();
            var causes=new List<ToolFailureNode>();
            if(children.Count>16||(depth>=4&&children.Count!=0))unavailable=true;
            else for(var index=0;index<children.Count;index++) {
                if(count>=16){unavailable=true;break;}
                causes.Add(Read(children[index],depth+1));
            }
            return new(failure.GetType().FullName??"Exception",failure.HResult,omitted?null:message,omitted,causes.ToArray());
        }
        var root=Read(actual,0);return new("ERROR","ACTUAL_OPERATION_FAILED","Actual failure retained; this report does not infer runtime closure.",root,unavailable);
    }
}
