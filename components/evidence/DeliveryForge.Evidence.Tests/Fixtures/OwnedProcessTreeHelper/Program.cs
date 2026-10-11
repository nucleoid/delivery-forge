using System.Diagnostics;
using System.Reflection;
using System.Text;

if (args.Length != 3 || !Path.IsPathFullyQualified(args[1]) || !Guid.TryParseExact(args[2], "N", out _))
    throw new ArgumentException("Owned helper requires mode, absolute owned directory and fresh nonce.");
var mode = args[0]; var directory = args[1]; var nonce = args[2];
for (DirectoryInfo? ancestor = new(directory); ancestor is not null; ancestor = ancestor.Parent)
    if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Helper path is redirected.");
var started = Stopwatch.GetTimestamp();
if (mode == "child")
{
    Console.Out.WriteLine("actual-child-stdout:" + nonce); Console.Out.Flush();
    Console.Error.WriteLine("actual-child-stderr:" + nonce); Console.Error.Flush();
    using (var entered = new FileStream(Path.Combine(directory, "child-entered-" + nonce), FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        entered.Write(Encoding.UTF8.GetBytes(nonce)); entered.Flush(true);
    }
    while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(8)) await Task.Delay(20);
    return;
}
if (mode is not ("root-exit" or "root-hold")) throw new ArgumentException("Unknown owned helper mode.");
var host = Environment.ProcessPath ?? throw new InvalidOperationException("Actual host path unavailable.");
// .NET Windows StartWithCreateProcess sets STARTF_USESTDHANDLES when any stream is redirected,
// binding the unredirected stdout/stderr to the root's actual standard handles (no proxy output).
var childInfo = new ProcessStartInfo(host) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
childInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
childInfo.ArgumentList.Add("child"); childInfo.ArgumentList.Add(directory); childInfo.ArgumentList.Add(nonce);
// Child inherits the root's actual redirected standard handles; no fixture-written stdout/TRX/results.
using var child = Process.Start(childInfo) ?? throw new InvalidOperationException("Real descendant did not start.");
child.StandardInput.Close();
var entry = Path.Combine(directory, "child-entered-" + nonce);
while (!File.Exists(entry))
{
    if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(3)) throw new TimeoutException("Actual child entry never appeared.");
    await Task.Delay(10);
}
Console.Out.WriteLine("actual-root-entered:" + nonce); Console.Out.Flush();
if (mode == "root-exit") return;
while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(8)) await Task.Delay(20);
