using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeliveryForge.Contracts.Serialization;
namespace DeliveryForge.Tool;

public sealed class ToolLifetime : IDisposable,DeliveryForge.Evidence.IOwnedDuplexBudget,DeliveryForge.Evidence.IOwnedBudgetUnknownRetention,DeliveryForge.Evidence.IOwnedFailureCleanup {
    readonly long started=Stopwatch.GetTimestamp(); TimeSpan total,work; readonly CancellationTokenSource linked;
    readonly object gate=new(); long cleanup; bool disposed,retainedUnknown,disposeRequested; ToolFailureAttention? attention;
    public ToolLifetime(TimeSpan total,CancellationToken token=default) {
        if(total<=TimeSpan.Zero||total>TimeSpan.FromSeconds(1200))throw new ArgumentOutOfRangeException(nameof(total));this.total=total;
        work=TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1100).Ticks,total.Ticks-Math.Min(total.Ticks/4,TimeSpan.FromSeconds(60).Ticks)));
        linked=CancellationTokenSource.CreateLinkedTokenSource(token);linked.CancelAfter(work);
    }
    public void Restrict(TimeSpan maximum) { lock(gate) { ObjectDisposedException.ThrowIf(disposed,this);if(maximum<=TimeSpan.Zero||maximum>total)throw new ArgumentOutOfRangeException(nameof(maximum));if(maximum==total)return;total=maximum;var candidate=TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1100).Ticks,maximum.Ticks-Math.Min(maximum.Ticks/4,TimeSpan.FromSeconds(60).Ticks)));if(candidate<work)work=candidate;var left=work-Stopwatch.GetElapsedTime(started);if(left<=TimeSpan.Zero)throw new TimeoutException("Restricted original endpoint already expired.");linked.CancelAfter(left); } }
    public long OriginalStarted=>started;
    public TimeSpan WholeLimit {get {lock(gate)return total;}}
    public TimeSpan WorkLimit {get {lock(gate)return work;}}
    public void CheckWork()=>Check();
    public CancellationToken Token=>linked.Token;
    public TimeSpan Remaining { get { Check();return work-Stopwatch.GetElapsedTime(started); } }
    public void Check() { lock(gate) { ObjectDisposedException.ThrowIf(disposed,this);linked.Token.ThrowIfCancellationRequested();if(cleanup!=0||Stopwatch.GetElapsedTime(started)>=work)throw new TimeoutException("Original tool WORK endpoint expired."); } }
    public TimeSpan CleanupRemaining { get { lock(gate) { ObjectDisposedException.ThrowIf(disposed,this);if(cleanup==0)cleanup=Stopwatch.GetTimestamp();var whole=total-Stopwatch.GetElapsedTime(started);var once=TimeSpan.FromSeconds(60)-Stopwatch.GetElapsedTime(cleanup);var left=whole<once?whole:once;if(left<=TimeSpan.Zero)throw new TimeoutException("SAME original whole/earliest once60 endpoint expired.");return left; } } }
    internal bool HasRetainedUnknown {get {lock(gate)return retainedUnknown;}}
    internal void BindAttention(ToolFailureAttention actual){lock(gate){if(attention is not null)throw new IOException("Original failure attention already bound.");attention=actual;}}
    internal void BeginFailureCleanup(){_=CleanupRemaining;attention?.EmitOnce();}
    void DeliveryForge.Evidence.IOwnedFailureCleanup.BeginFailureCleanup()=>BeginFailureCleanup();
    void DeliveryForge.Evidence.IOwnedBudgetUnknownRetention.PreserveUnknown()=>RetainUnknown();
    internal void RetainUnknown() { lock(gate)retainedUnknown=true; }
    internal void ReleaseSettled() { lock(gate) { retainedUnknown=false;if(disposeRequested&&!disposed){linked.Dispose();disposed=true;} } }
    public void Dispose() { lock(gate) { disposeRequested=true;if(disposed||retainedUnknown)return;linked.Dispose();disposed=true; } }
}
public static class BoundedFiles {
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=48,WriteIndented=true,Converters={new JsonStringEnumConverter(allowIntegerValues:false)} };
    public static string Plain(string path,bool directory=false) {
        if(!Path.IsPathFullyQualified(path)) throw new ToolUsageException("An explicit absolute path is required.");
        var full=Path.GetFullPath(path);var cursor=directory?full:Path.GetDirectoryName(full)!;
        while(cursor is not null) { if(File.Exists(cursor)||Directory.Exists(cursor)) { var a=File.GetAttributes(cursor);if((a&FileAttributes.ReparsePoint)!=0||(a&FileAttributes.Directory)==0) throw new IOException("Non-plain path ancestor."); }cursor=Path.GetDirectoryName(cursor); }
        if(File.Exists(full)||Directory.Exists(full)) { var a=File.GetAttributes(full);if((a&FileAttributes.ReparsePoint)!=0||((a&FileAttributes.Directory)!=0)!=directory) throw new IOException("Unexpected path type/reparse entry."); }
        return full;
    }
    sealed class FileFrame {
        internal required string Path;internal required ToolLifetime Original;internal FileStream? Stream;
        internal bool AcquisitionEntered,AcquisitionReturned,CloseEntered,CloseReturned;
        internal Exception? Primary,CloseFailure,Post;internal byte[]? Bytes;
        internal bool Unknown=>AcquisitionEntered&&!AcquisitionReturned||Stream is not null&&!CloseReturned;
    }
    sealed class FileCredits {internal readonly object Gate=new();internal int Operations;internal long Reads,Writes;}
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolLifetime,FileCredits> Credits=new();
    static readonly List<FileFrame> UnknownFiles=[];
    static void Credit(ToolLifetime life,long bytes,bool write){
        life.Check();var credit=Credits.GetValue(life,_=>new());
        lock(credit.Gate){
            if(credit.Operations>=4096||bytes<0)throw new IOException("Original finite file-operation credit exhausted before effect.");
            var maximum=write?268435456L:1073741824L;var used=write?credit.Writes:credit.Reads;
            if(bytes>maximum-used)throw new IOException("Original cumulative complete file-byte credit exhausted before allocation/effect.");
            credit.Operations++;if(write)credit.Writes+=bytes;else credit.Reads+=bytes;
        }life.Check();
    }
    static void CloseFile(FileFrame frame){
        var life=frame.Original;var cleanup=frame.Primary is not null;
        if(frame.Stream is not null){
            try{
                if(!cleanup){try{life.Check();}catch(Exception admission){frame.CloseFailure=admission;cleanup=true;}}
                if(cleanup){life.BeginFailureCleanup();_=life.CleanupRemaining;}
                frame.CloseEntered=true;frame.Stream.Dispose();frame.CloseReturned=true;
            }catch(Exception error){frame.CloseFailure=frame.CloseFailure is null?error:new AggregateException(frame.CloseFailure,error);}
        }
        try{if(cleanup||frame.CloseFailure is not null){life.BeginFailureCleanup();_=life.CleanupRemaining;}else life.Check();}
        catch(Exception error){frame.Post=error;}
        if(frame.Unknown){life.RetainUnknown();lock(UnknownFiles)UnknownFiles.Add(frame);}
        var failures=new List<Exception>();if(frame.Primary is not null)failures.Add(frame.Primary);if(frame.CloseFailure is not null)failures.Add(frame.CloseFailure);if(frame.Post is not null)failures.Add(frame.Post);
        if(failures.Count>1)throw new AggregateException("Actual complete file operation, first disposition and independent SAME original post.",failures);
        if(failures.Count==1)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
    }
    public static byte[] Read(string path,ToolLifetime life,int maximum=16*1024*1024) {
        life.Check();if(maximum<=0||maximum>268435456)throw new ArgumentOutOfRangeException(nameof(maximum));path=Plain(path);life.Check();
        // Missing input is refused before resource construction. A racing/failed
        // entered constructor still remains explicitly UNKNOWN; no hidden retry.
        var exists=File.Exists(path);life.Check();if(!exists)throw new IOException("Required plain input file is absent.");
        var frame=new FileFrame {Path=path,Original=life};
        try{
            Credit(life,0,false);frame.AcquisitionEntered=true;frame.Stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);frame.AcquisitionReturned=true;life.Check();
            var length=frame.Stream.Length;life.Check();if(length<0||length>maximum)throw new IOException("Input exceeds bounded file profile.");
            Credit(life,length+1,false);frame.Bytes=new byte[(int)length];life.Check();frame.Stream.ReadExactly(frame.Bytes);life.Check();var eof=frame.Stream.ReadByte();life.Check();var after=frame.Stream.Length;life.Check();
            if(eof!=-1||after!=length)throw new IOException("Input changed complete length/EOF.");
        }catch(Exception error){frame.Primary=error;}
        CloseFile(frame);return frame.Bytes??throw new IOException("Complete actual file result missing.");
    }
    public static T ReadJson<T>(string path,ToolLifetime life,int maximum=16*1024*1024) {
        var bytes=Read(path,life,maximum);life.Check();_=CanonicalJson.Canonicalize(bytes);life.Check();var value=JsonSerializer.Deserialize<T>(bytes,Json)??throw new IOException("JSON value is null.");life.Check();return value;
    }
    public static void WriteNew(string path,ReadOnlySpan<byte> bytes,ToolLifetime life) {
        life.Check();path=Plain(path);life.Check();if(bytes.Length>16*1024*1024)throw new IOException("Output exceeds bounded file profile.");
        var exists=File.Exists(path);life.Check();if(exists)throw new IOException("CreateNew target already exists; no construction entered.");
        Credit(life,bytes.Length,true);var frame=new FileFrame {Path=path,Original=life};
        try{
            life.Check();frame.AcquisitionEntered=true;frame.Stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);frame.AcquisitionReturned=true;life.Check();
            frame.Stream.Write(bytes);life.Check();frame.Stream.Flush(true);life.Check();
        }catch(Exception error){frame.Primary=error;}
        // Partial CreateNew outputs stay as failed evidence; never rollback,
        // reopen, truncate or retry an entered actual output construction.
        CloseFile(frame);
    }
    public static string Sha(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static void Separate(string first,string second) {
        var a=Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var b=Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        if(a.StartsWith(b,comparison)||b.StartsWith(a,comparison)) throw new ToolUsageException("Result/replay/worktree destinations overlap.");
    }
}
