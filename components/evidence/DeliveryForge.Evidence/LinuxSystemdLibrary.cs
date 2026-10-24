using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
namespace DeliveryForge.Evidence;

// Selection is DATA, never a scope or native-owner constructor. Runtime checks
// pin actual opened images; actual user-manager/kernel/resource proof follows.
public sealed record LinuxScopeImage(string Path, long Bytes, string Sha256);
public sealed record LinuxScopeSelection(LinuxScopeImage SystemdRun, LinuxScopeImage SystemdLibrary, string ManagerVersion, string KernelRelease, LinuxScopeImage BirthGate, IReadOnlyList<LinuxScopeImage> BirthGateClosure, LinuxScopeImage GateBuildReceipt, string? RestrictiveChildPolicy = null);
[SupportedOSPlatform("linux")]
internal sealed class LinuxSystemdLibrary
{
    internal readonly IOwnedDuplexBudget Original; readonly LinuxScopeImage selected;
    FileStream? image; IntPtr actualModule; readonly List<Operation> operations = []; readonly List<object> retained = []; readonly List<Resource> auxiliary = []; bool loadEntered, loadReturned, freeEntered, freed, imageCloseEntered, imageClosed; long readBytes;
    sealed class Resource(IDisposable actual) { internal readonly IDisposable Actual = actual; internal bool CloseEntered, Closed; }
    Resource Track(IDisposable actual) { var row = new Resource(actual); auxiliary.Add(row); retained.Add(row); return row; }
    internal sealed class Operation { internal required string Kind; internal bool Entered, Returned; internal Exception? Primary, Post; }
    internal IReadOnlyList<Operation> Operations => operations.AsReadOnly();
    internal bool Unresolved => operations.Any(x => x.Entered && !x.Returned) || freeEntered && !freed || imageCloseEntered && !imageClosed || auxiliary.Any(x => !x.Closed);
    internal LinuxSystemdLibrary(IOwnedDuplexBudget original, LinuxScopeImage selected) { Original = original; this.selected = selected; retained.Add(original); retained.Add(this); }
    void Check(bool cleanup) { if (cleanup) _ = Original.CleanupRemaining; else Original.CheckWork(); }
    T Invoke<T>(string kind, Func<T> action, bool cleanup = false)
    {
        Check(cleanup); if (operations.Count >= 2048) throw new IOException("Original library IO/native operation ledger exhausted."); var row = new Operation { Kind = kind }; operations.Add(row); T result = default!;
        try { Check(cleanup); row.Entered = true; result = action(); row.Returned = true; } catch (Exception error) { row.Primary = error; retained.Add(error); }
        try { Check(cleanup); } catch (Exception error) { row.Post = error; retained.Add(error); }
        if (row.Primary is not null && row.Post is not null) throw new AggregateException("Actual library operation and original independent post.", row.Primary, row.Post); if (row.Primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(row.Primary).Throw(); if (row.Post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(row.Post).Throw(); return result;
    }
    void Effect(string kind, Action action, bool cleanup = false) => Invoke(kind, () => { action(); return true; }, cleanup);
    static void Selection(LinuxScopeImage pin) { if (pin is null || pin.Path is null || pin.Sha256 is null || pin.Path.Length > 4096 || !System.IO.Path.IsPathFullyQualified(pin.Path) || pin.Path.Contains('\0') || pin.Bytes is <= 0 or > 16777216 || pin.Sha256.Length != 64 || pin.Sha256.Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c))) throw new IOException("Finite exact lowercase selected Linux image tuple required."); }
    void HashOpened(FileStream stream, LinuxScopeImage pin)
    {
        Selection(pin); var length = Invoke("image-length", () => stream.Length); if (length != pin.Bytes) throw new IOException("Actual selected library length changed."); Effect("image-rewind", () => stream.Position = 0);
        IncrementalHash? hash = null; Resource? hashOwner = null; Exception? primary = null; var posts = new List<Exception>(); bool hashCloseEntered = false, hashClosed = false;
        try
        {
            Effect("sha256-create", () => { hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); retained.Add(hash); hashOwner = Track(hash); }); var buffer = new byte[65536]; retained.Add(buffer); long total = 0;
            while (total < length) { if (readBytes > 67108864 - Math.Min(buffer.Length, length - total)) throw new IOException("Original image read-credit bound exceeded before read."); var count = (int)Math.Min(buffer.Length, length - total); readBytes += count; var n = Invoke("complete-image-read", () => stream.Read(buffer, 0, count)); if (n <= 0) throw new IOException("Original selected image ended before length."); Effect("sha256-append", () => hash!.AppendData(buffer, 0, n)); total += n; }
            if (Invoke("image-EOF", stream.ReadByte) != -1 || Invoke("image-second-length", () => stream.Length) != length) throw new IOException("Complete selected image EOF/length differs."); var digest = Invoke("sha256-final", () => Convert.ToHexString(hash!.GetHashAndReset()).ToLowerInvariant()); if (digest != pin.Sha256) throw new IOException("Actual selected Linux image bytes differ.");
        }
        catch (Exception error) { primary = error; }
        if (hash is not null) { try { Effect("sha256-first-close", () => { hashCloseEntered = true; hashOwner!.CloseEntered = true; hash.Dispose(); hashClosed = true; hashOwner.Closed = true; }, primary is not null); } catch (Exception error) { posts.Add(error); } }
        if (hash is not null && (!hashCloseEntered || !hashClosed)) posts.Add(new IOException("Original image hash disposition remains unknown."));
        if (primary is not null && posts.Count > 0) throw new AggregateException("Actual image hash and original first-close.", new[] { primary }.Concat(posts)); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (posts.Count > 0) throw new AggregateException(posts);
    }
    internal void VerifyImage(LinuxScopeImage pin)
    {
        Selection(pin); FileStream? stream = null; Resource? streamOwner = null; Exception? primary = null, post = null; bool closeEntered = false, closed = false;
        try { Effect("profile-image-open", () => { stream = new FileStream(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read); retained.Add(stream); streamOwner = Track(stream); }); HashOpened(stream!, pin); } catch (Exception error) { primary = error; }
        if (stream is not null) { try { Effect("profile-image-first-close", () => { closeEntered = true; streamOwner!.CloseEntered = true; stream.Dispose(); closed = true; streamOwner.Closed = true; }, primary is not null); } catch (Exception error) { post = error; } }
        if (stream is not null && (!closeEntered || !closed)) post ??= new IOException("Actual profile image disposition unknown."); if (primary is not null && post is not null) throw new AggregateException(primary, post); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
    }
    internal void Load()
    {
        if (loadEntered) throw new IOException("Original library load already entered; no replay."); Selection(selected); Effect("selected-image-open", () => { image = new FileStream(selected.Path, FileMode.Open, FileAccess.Read, FileShare.Read); retained.Add(image); }); HashOpened(image!, selected);
        // Load precisely the actual still-open hashed inode, not a second lookup
        // of its potentially replaceable pathname. Trusted loader dependencies
        // remain an explicitly selected installed-platform prerequisite.
        Effect("actual-selected-library-load", () => { loadEntered = true; actualModule = NativeLibrary.Load("/proc/self/fd/" + image!.SafeFileHandle.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)); loadReturned = true; });
        if (actualModule == IntPtr.Zero) throw new IOException("Actual native library handle unavailable."); HashOpened(image!, selected);
        openUser = Export<OpenUser>("sd_bus_open_user"); unrefBus = Export<Unref>("sd_bus_unref"); newMessage = Export<NewMessage>("sd_bus_message_new_method_call"); append = Export<Append>("sd_bus_message_append_basic"); call = Export<Call>("sd_bus_call"); readBasic = Export<ReadBasic>("sd_bus_message_read_basic"); readArray = Export<ReadArray>("sd_bus_message_read_array"); enter = Export<Enter>("sd_bus_message_enter_container"); exit = Export<OneMessage>("sd_bus_message_exit_container"); atEnd = Export<AtEnd>("sd_bus_message_at_end"); unrefMessage = Export<Unref>("sd_bus_message_unref");
    }
    T Export<T>(string name) where T : Delegate => Invoke("bind-fixed-export:" + name, () => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(actualModule, name)));
    internal bool KnownDisposed => (!loadEntered || freed) && (image is null || imageClosed) && auxiliary.All(x => x.Closed);
    internal void DisposeKnown(bool busAndScopeClosed, bool cleanup)
    {
        Check(cleanup); if (!busAndScopeClosed || Unresolved) throw new IOException("Actual dependent bus/scope resources remain unknown; retain original library."); Exception? primary = null; var posts = new List<Exception>();
        if (loadEntered) { if (!loadReturned) throw new IOException("Original native load return unknown; no guessed free."); if (freeEntered) throw new IOException("Original module first-free already entered."); try { Effect("native-library-first-free", () => { freeEntered = true; NativeLibrary.Free(actualModule); freed = true; }, cleanup); } catch (Exception error) { primary = error; } }
        if (loadEntered && !freed) throw primary ?? new IOException("Actual original module free remains unknown; retain dependent image.");
        if (image is not null) { try { if (imageCloseEntered) throw new IOException("Original image first-close already entered."); Effect("image-first-close", () => { imageCloseEntered = true; image.Dispose(); imageClosed = true; }, cleanup || primary is not null); } catch (Exception error) { posts.Add(error); } }
        if (primary is not null && posts.Count > 0) throw new AggregateException(new[] { primary }.Concat(posts)); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (posts.Count > 0) throw new AggregateException(posts);
    }
    internal OpenUser openUser = null!; internal Unref unrefBus = null!, unrefMessage = null!; internal NewMessage newMessage = null!; internal Append append = null!; internal Call call = null!; internal ReadBasic readBasic = null!; internal ReadArray readArray = null!; internal Enter enter = null!; internal OneMessage exit = null!; internal AtEnd atEnd = null!;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int OpenUser(out IntPtr bus);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr Unref(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int NewMessage(IntPtr bus, out IntPtr message, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, [MarshalAs(UnmanagedType.LPUTF8Str)] string member);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Append(IntPtr message, byte type, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Call(IntPtr bus, IntPtr message, ulong timeout, IntPtr error, out IntPtr reply);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReadBasic(IntPtr message, byte type, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReadArray(IntPtr message, byte type, out IntPtr value, out nuint bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Enter(IntPtr message, byte type, [MarshalAs(UnmanagedType.LPUTF8Str)] string contents);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int OneMessage(IntPtr message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AtEnd(IntPtr message, int complete);
}
