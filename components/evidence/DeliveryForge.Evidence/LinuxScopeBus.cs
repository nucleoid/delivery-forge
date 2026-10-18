using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
namespace DeliveryForge.Evidence;

// UNSELECTED SOURCE WIP. Actual fixed library/version/profile admission and the
// creation-owned systemd-run/user scope birth must be wired before production use.
// This object owns its actual USER bus connection and exact response messages;
// no caller JSON, PID, name lookup or state string can construct a native owner.
[SupportedOSPlatform("linux")]
internal sealed class LinuxScopeBus {
    readonly IOwnedDuplexBudget original;readonly LinuxSystemdLibrary library;readonly object serial=new();readonly List<Call> calls=[];readonly List<Message> messages=[];
    IntPtr bus;bool openEntered,openReturned,closeEntered,closed;int callCount;
    internal sealed class Call {internal required string Kind;internal bool Entered,Returned;internal int Result;internal IntPtr Actual;internal Exception? Primary,Post;}
    sealed class Message {internal IntPtr Actual;internal bool CloseEntered,Closed;}
    readonly List<Exception> failures=[];
    static readonly List<LinuxScopeBus> Retained=[];
    const string Destination="org.freedesktop.systemd1",ManagerPath="/org/freedesktop/systemd1",Manager="org.freedesktop.systemd1.Manager";
    LinuxScopeBus(IOwnedDuplexBudget original,LinuxSystemdLibrary library){if(!ReferenceEquals(original,library.Original))throw new IOException("Actual same-original library required.");this.original=original;this.library=library;lock(Retained)Retained.Add(this);}
    void Check(bool cleanup){if(cleanup)_=original.CleanupRemaining;else original.CheckWork();}
    int Invoke(string kind,Func<int> effect,bool cleanup,Action<Call>? capture=null) {
        Check(cleanup);if(calls.Count>=4096)throw new IOException("Finite original user-bus call ledger exceeded before invocation.");var call=new Call{Kind=kind};calls.Add(call);int result=0;
        try{Check(cleanup);call.Entered=true;result=effect();call.Result=result;call.Returned=true;capture?.Invoke(call);if(result<0)throw new Win32Exception(-result,"Original user-bus operation refused: "+kind);}catch(Exception error){call.Primary=error;failures.Add(error);}
        try{Check(cleanup);}catch(Exception error){call.Post=error;failures.Add(error);}
        if(call.Primary is not null&&call.Post is not null)throw new AggregateException("Original user-bus invocation and independent endpoint post.",call.Primary,call.Post);
        if(call.Primary is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(call.Primary).Throw();if(call.Post is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(call.Post).Throw();return result;
    }
    internal static LinuxScopeBus Create(IOwnedDuplexBudget original,LinuxSystemdLibrary library) {
        original.CheckWork();if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException();return new LinuxScopeBus(original,library);
    }
    internal void Open() {
        // The containing birth frame retains this object BEFORE native entry.
        lock(serial){if(openEntered)throw new IOException("Actual original bus open cannot be repeated.");Invoke("sd_bus_open_user",()=>{openEntered=true;var result=library.openUser(out bus);openReturned=true;return result;},false,c=>c.Actual=bus);if(bus==IntPtr.Zero)throw new IOException("Actual original user-bus connection unavailable.");}
    }
    Message New(string path,string iface,string member,bool cleanup) {
        Check(cleanup);if(messages.Count>=512)throw new IOException("Finite user-bus actual message ledger exceeded.");var row=new Message();messages.Add(row);
        Invoke("new-message:"+member,()=>library.newMessage(bus,out row.Actual,Destination,path,iface,member),cleanup,c=>c.Actual=row.Actual);
        if(row.Actual==IntPtr.Zero)throw new IOException("Original message constructor returned no resource.");return row;
    }
    void Append(Message message,string value,bool cleanup) {
        Check(cleanup);if(value.Length>4096||value.Contains('\0'))throw new IOException("Bounded original D-Bus scalar required.");
        // Native library copies this supplied UTF8 scalar into its retained message.
        // Managed pinning requires no separately allocated unmanaged string owner.
        Invoke("append-string",()=>library.append(message.Actual,(byte)'s',value),cleanup);
    }
    Message Send(Message request,bool cleanup) {
        Check(cleanup);if(++callCount>128)throw new IOException("Finite fixed user-bus requests exhausted.");
        var left=cleanup?original.CleanupRemaining:original.WorkLimit-Stopwatch.GetElapsedTime(original.OriginalStarted);if(left<=TimeSpan.Zero)throw new TimeoutException("Original bus-call allowance expired.");
        var micros=checked((ulong)Math.Floor(left.TotalMilliseconds*1000));if(micros==0)throw new TimeoutException("No original bus-call microsecond remaining.");
        var reply=new Message();messages.Add(reply);
        // No fresh timeout/grant: the actual bus library call receives only the
        // SAME original remaining time. Error strings are not adopted as state.
        Invoke("sd_bus_call",()=>library.call(bus,request.Actual,micros,IntPtr.Zero,out reply.Actual),cleanup,c=>c.Actual=reply.Actual);
        if(reply.Actual==IntPtr.Zero)throw new IOException("Actual reply resource unavailable.");return reply;
    }
    string ReadString(Message message,byte type,bool cleanup) {
        IntPtr value=IntPtr.Zero;var n=Invoke("read-complete-scalar",()=>library.readBasic(message.Actual,type,out value),cleanup);if(n!=1||value==IntPtr.Zero)throw new IOException("One exact complete typed bus scalar required.");
        // Admission precedes allocation/copy; source memory stays borrowed from
        // the exact retained reply until all parsing and post checks complete.
        var length=0;while(length<=4096){Check(cleanup);if(Marshal.ReadByte(value,length)==0)break;length++;}if(length>4096)throw new IOException("Bus scalar exceeded4096 UTF8 bytes before copying.");var data=new byte[length];Marshal.Copy(value,data,0,length);var text=new UTF8Encoding(false,true).GetString(data);Check(cleanup);return text;
    }
    void End(Message message,bool cleanup){if(Invoke("reply-complete-EOF",()=>library.atEnd(message.Actual,1),cleanup)!=1)throw new IOException("Unexpected extra actual D-Bus reply fields.");}
    void Close(Message message,bool cleanup) {
        if(message.Actual==IntPtr.Zero)return;Check(cleanup);if(message.CloseEntered)throw new IOException("Actual message first-close already entered; no retry.");message.CloseEntered=true;
        Invoke("message-first-unref",()=>{var left=library.unrefMessage(message.Actual);message.Closed=left==IntPtr.Zero;return message.Closed?0:-5;},cleanup);
    }
    T Request<T>(string path,string iface,string member,string[] args,Func<Message,T> read,bool cleanup) {
        lock(serial){Check(cleanup);if(!openReturned||bus==IntPtr.Zero||closeEntered)throw new IOException("Actual original bus is not available.");Message? request=null,reply=null;T value=default!;Exception? primary=null;var posts=new List<Exception>();
            try{request=New(path,iface,member,cleanup);foreach(var arg in args)Append(request,arg,cleanup);reply=Send(request,cleanup);value=read(reply);End(reply,cleanup);}catch(Exception error){primary=error;}
            // Preserve both actual resource disposals independently; no dependent
            // retry is permitted if an earlier endpoint is already exhausted.
            var closeCleanup=cleanup||primary is not null;
            foreach(var resource in new[]{reply,request})if(resource is not null){try{Close(resource,closeCleanup);}catch(Exception error){posts.Add(error);}}
            try{Check(closeCleanup);}catch(Exception error){posts.Add(error);}
            if(primary is not null&&posts.Count!=0)throw new AggregateException("Original user-bus request and independent first-dispositions.",new[]{primary}.Concat(posts));if(primary is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();if(posts.Count!=0)throw new AggregateException(posts);return value;
        }
    }
    internal string ManagerVersion()=>StringProperty(ManagerPath,Manager,"Version",false);
    internal string GetUnit(string privateUnit)=>Request(ManagerPath,Manager,"GetUnit",[privateUnit],m=>ReadString(m,(byte)'o',false),false);
    internal void RefUnit(string privateUnit)=>Request(ManagerPath,Manager,"RefUnit",[privateUnit],_=>0,false);
    internal void UnrefUnit(string privateUnit,bool cleanup)=>Request(ManagerPath,Manager,"UnrefUnit",[privateUnit],_=>0,cleanup);
    internal string StringProperty(string actualUnitPath,string iface,string property,bool cleanup) =>Request(actualUnitPath,"org.freedesktop.DBus.Properties","Get",[iface,property],message=>{
        if(Invoke("enter-exact-string-variant",()=>library.enter(message.Actual,(byte)'v',"s"),cleanup)!=1)throw new IOException("Exact string variant required.");var text=ReadString(message,(byte)'s',cleanup);if(Invoke("variant-complete-EOF",()=>library.atEnd(message.Actual,0),cleanup)!=1)throw new IOException("Extra variant values.");Invoke("leave-variant",()=>library.exit(message.Actual),cleanup);return text;
    },cleanup);
    internal byte[] InvocationId(string actualUnitPath,bool cleanup) =>Request(actualUnitPath,"org.freedesktop.DBus.Properties","Get",["org.freedesktop.systemd1.Unit","InvocationID"],message=>{
        if(Invoke("enter-exact-byte-array-variant",()=>library.enter(message.Actual,(byte)'v',"ay"),cleanup)!=1)throw new IOException("Exact InvocationID byte-array variant required.");
        IntPtr bytes=IntPtr.Zero;nuint length=0;
        if(Invoke("read-exact-invocation-id",()=>library.readArray(message.Actual,(byte)'y',out bytes,out length),cleanup)!=1||length!=16||bytes==IntPtr.Zero)throw new IOException("Complete actual128-bit InvocationID required before copy.");
        var id=new byte[16];Marshal.Copy(bytes,id,0,16);Check(cleanup);if(id.All(x=>x==0))throw new IOException("Actual scope InvocationID was empty.");
        if(Invoke("invocation-variant-EOF",()=>library.atEnd(message.Actual,0),cleanup)!=1)throw new IOException("Unexpected InvocationID fields.");Invoke("leave-invocation-variant",()=>library.exit(message.Actual),cleanup);return id;
    },cleanup);
    internal bool KnownClosed=>closed;
    internal void CloseKnown(bool originalScopeEmpty,bool cleanup) {
        lock(serial){Check(cleanup);if(!originalScopeEmpty||messages.Any(x=>x.Actual!=IntPtr.Zero&&!x.Closed)||calls.Any(x=>x.Entered&&!x.Returned))throw new IOException("Original user-bus observations/dispositions unresolved; retain all actual resources.");if(closeEntered)throw new IOException("Original bus first-close entered already.");closeEntered=true;Invoke("bus-first-unref",()=>{closed=library.unrefBus(bus)==IntPtr.Zero;return closed?0:-5;},cleanup);if(closed)lock(Retained)Retained.Remove(this);}
    }
}
