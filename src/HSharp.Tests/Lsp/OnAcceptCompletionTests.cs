using System.Text;
using System.Text.Json;
using HSharp.Analysis;
using HSharp.Checking;
using HSharp.Lsp;
using HSharp.Syntax;

public class OnAcceptCompletionTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "hs-onacc-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static Workspace.Position Pos(int line, int col) => new(line, col);

    [Fact]
    public void LambdaParamListClassifiesAsTypes()
    {
        var ctx = Workspace.Classify("ls.OnAccept((", Pos(1, 14));
        Assert.Equal(CompletionCtxKind.LambdaParam, ctx.Kind);
        Assert.Equal("OnAccept", ctx.Callee);

        var ctx2 = Workspace.Classify("ls.OnAccept((RawHttpPacket ", Pos(1, 27));
        Assert.Equal(CompletionCtxKind.LambdaParam, ctx2.Kind);
    }

    [Fact]
    public void LambdaBodyIsNotLambdaParam()
    {

        var ctx = Workspace.Classify("ls.OnAccept((RawHttpPacket p) => { ", Pos(1, 34));
        Assert.NotEqual(CompletionCtxKind.LambdaParam, ctx.Kind);
    }

    private static List<JsonElement> Run(string rootPath, params object[] messages)
    {
        var input = new MemoryStream();
        var all = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new { capabilities = new { }, rootUri = new Uri(rootPath).AbsoluteUri }
            }
        };
        all.AddRange(messages);
        foreach (var m in all)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(m);
            input.Write(Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n"));
            input.Write(json);
        }
        input.Position = 0;
        var output = new MemoryStream();
        new Server().Run(input, output);
        var bytes = output.ToArray();
        var responses = new List<JsonElement>();
        int i = 0;
        while (i < bytes.Length)
        {
            int hdrEnd = bytes.AsSpan(i).IndexOf("\r\n\r\n"u8);
            if (hdrEnd < 0) break;
            int len = int.Parse(Encoding.ASCII.GetString(bytes, i, hdrEnd).Split(':')[1].Trim());
            i += hdrEnd + 4;
            responses.Add(JsonDocument.Parse(new ArraySegment<byte>(bytes, i, len)).RootElement);
            i += len;
        }
        return responses;
    }

    private static object DidOpen(string uri, string text) =>
        new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
        {
            textDocument = new { uri, languageId = "hsharp", version = 1, text }
        } };

    private static object Completion(int id, string uri, int line, int ch) =>
        new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "textDocument/completion",
            ["params"] = new { textDocument = new { uri }, position = new { line, character = ch } }
        };

    private static List<string> Labels(List<JsonElement> responses, int id)
    {
        var r = responses.First(x => x.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id);
        return r.GetProperty("result").GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("label").GetString()!).ToList();
    }

    private const string ServerSrc = """
        import models.hs

        var ls = Http.ListenRaw(15600);
        ls.OnAccept((RawHttpPacket packet) =>
        {
            packet.Forward("127.0.0.1", 80);
            var req = packet.ToHttpPacket();
            p
            packet.
        });
        """;

    [Fact]
    public void RawHttpPacketMembersCompleteAfterDot()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "models.hs"), "public class User { string Name; }");
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        var lines = ServerSrc.Split("\n");
        int dotLine = Array.IndexOf(lines, "    packet.");

        var responses = Run(dir,
            DidOpen(uri, ServerSrc),
            Completion(2, uri, dotLine, 11));
        var labels = Labels(responses, 2);

        Assert.Contains("Source", labels);
        Assert.Contains("Dest", labels);
        Assert.Contains("ToHttpPacket", labels);
        Assert.Contains("Forward", labels);
        Assert.Contains("Close", labels);
        Assert.DoesNotContain("print", labels);
    }

    [Fact]
    public void LambdaParamSuggestsHandleTypesNotValues()
    {
        var dir = TempDir();
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var ls = Http.ListenRaw(80);\nls.OnAccept((\n";

        var responses = Run(dir,
            DidOpen(uri, src),
            Completion(2, uri, 1, 13));
        var labels = Labels(responses, 2);

        Assert.Equal("RawHttpPacket", labels[0]);
        Assert.Contains("HttpPacket", labels);

        Assert.DoesNotContain("ls", labels);
        Assert.DoesNotContain("print", labels);
        Assert.DoesNotContain("null", labels);
    }

    [Fact]
    public void PacketSuggestedInsideLambdaBody()
    {
        var dir = TempDir();
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var ls = Http.ListenRaw(80);\nls.OnAccept((RawHttpPacket packet) =>\n{\n    p\n});\n";
        var lines = src.Split("\n");

        var responses = Run(dir,
            DidOpen(uri, src),
            Completion(2, uri, 3, 5));
        var labels = Labels(responses, 2);

        Assert.Contains("packet", labels);
        Assert.Contains("ls", labels);
    }

    [Fact]
    public void ImportedClassCompletesAtValuePosition()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "models.hs"),
            "public class User { string Name; int Salary; public string Whois() { return Name; } }");
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "import models.hs\nvar usr = U\n";

        var responses = Run(dir,
            DidOpen(uri, src),
            Completion(2, uri, 1, 11));
        var labels = Labels(responses, 2);

        Assert.Contains("User", labels);
    }

    [Fact]
    public void ImportedClassMembersCompleteAfterDot()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "models.hs"),
            "public class User { string Name; int Salary; public string Whois() { return Name; } }");
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "import models.hs\nvar usr = User { Name: \"a\", Salary: 1 }\nusr.\n";
        var lines = src.Split("\n");

        var responses = Run(dir,
            DidOpen(uri, src),
            Completion(2, uri, 2, 4));
        var labels = Labels(responses, 2);

        Assert.Contains("Name", labels);
        Assert.Contains("Salary", labels);
        Assert.Contains("Whois", labels);
        Assert.DoesNotContain("print", labels);
    }

    [Fact]
    public void NonPublicImportedMethodsAreHidden()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "models.hs"),
            "public class User { public string Ok() { return \"a\"; } string Hidden() { return \"b\"; } }");
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "import models.hs\nvar usr = User { }\nusr.\n";

        var responses = Run(dir,
            DidOpen(uri, src),
            Completion(2, uri, 2, 4));
        var labels = Labels(responses, 2);

        Assert.Contains("Ok", labels);
        Assert.DoesNotContain("Hidden", labels);
    }
}

