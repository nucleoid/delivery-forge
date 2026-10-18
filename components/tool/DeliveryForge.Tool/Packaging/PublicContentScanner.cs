using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
namespace DeliveryForge.Tool.Packaging;
public static class PublicContentScanner {
    public static void RequireSafe(ReadOnlySpan<byte> bytes) {
        if(bytes.Length>16*1024*1024)throw new IOException("Public scan byte bound exceeded.");
        var text=Encoding.Latin1.GetString(bytes).Replace("\0","",StringComparison.Ordinal);
        RequireTextSafe(text);
    }
    public static void RequireJsonSafe(ReadOnlySpan<byte> bytes) {
        RequireSafe(bytes);var reader=new Utf8JsonReader(bytes,new JsonReaderOptions{MaxDepth=32,CommentHandling=JsonCommentHandling.Disallow,AllowTrailingCommas=false});var containers=new Stack<HashSet<string>?>();var nodes=0;
        while(reader.Read()) {
            if(++nodes>65536)throw new IOException("Public JSON node bound exceeded.");
            if(reader.TokenType==JsonTokenType.StartObject)containers.Push(new HashSet<string>(StringComparer.Ordinal));
            else if(reader.TokenType==JsonTokenType.StartArray)containers.Push(null);
            else if(reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)containers.Pop();
            else if(reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String) {
                if(reader.ValueSpan.Length>65536)throw new IOException("Public JSON complete string bound exceeded before decode.");var value=reader.GetString()??throw new IOException("Public JSON null string.");RequireTextSafe(value);
                if(reader.TokenType==JsonTokenType.PropertyName&&(containers.Count==0||containers.Peek() is not HashSet<string> properties||!properties.Add(value)))throw new IOException("Public JSON duplicate/foreign property.");
            }
        }
        if(nodes==0||containers.Count!=0||reader.BytesConsumed!=bytes.Length)throw new IOException("Public JSON incomplete document.");
    }
    static void RequireTextSafe(string text) {
        if(Regex.IsMatch(text,@"(?i)(?:[a-z]:[\\/]Users[\\/]|/home/[A-Za-z0-9_.-]+/|/Users/[A-Za-z0-9_.-]+/)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))throw new IOException("Private host path in package content.");
        if(Regex.IsMatch(text,@"(?:ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))throw new IOException("Credential/private key in public content.");
    }
}
