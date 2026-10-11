using System.Text.Json;

// Negative process fixture only: never returns Git version/head/object facts or authority.
var metadata = Path.Combine(Directory.GetCurrentDirectory(), ".git");
using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(metadata, "prepare-negative-fixture.json")));
var nonce = document.RootElement.GetProperty("nonce").GetString()!;
var mode = document.RootElement.GetProperty("mode").GetString()!;
if (!Guid.TryParseExact(nonce, "N", out _) || mode is not ("stall" or "pressure")) throw new ArgumentException("Invalid negative fixture binding.");
new Thread(() => { Thread.Sleep(8000); Environment.Exit(0); }) { IsBackground = true }.Start();
var entered = Path.Combine(metadata, "helper-entered-" + nonce);
var staging = entered + ".staging";
using (var writer = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
{ writer.Write(System.Text.Encoding.UTF8.GetBytes(nonce)); writer.Flush(true); }
File.Move(staging, entered, overwrite: false);
if (mode == "pressure")
{
    var data = new string('X', 4096);
    var stderr = Task.Run(() => { for (var index = 0; index < 1024; index++) Console.Error.Write(data); Console.Error.Flush(); });
    for (var index = 0; index < 1024; index++) Console.Out.Write(data);
    Console.Out.Flush(); await stderr;
}
await Task.Delay(TimeSpan.FromSeconds(8));
