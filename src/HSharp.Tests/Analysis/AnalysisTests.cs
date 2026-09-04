using System.Text;
using System.Text.Json;
using HSharp.Analysis;
using HSharp.Checking;
using HSharp.Lexing;
using HSharp.Lsp;
using HSharp.Parsing;
using HSharp.Syntax;

public class AnalysisTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "hs-lsp-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static Workspace.Position Pos(int line, int col) => new(line, col);

    [Fact]
    public void MultiErrorRecoveryListsEveryBrokenStatement()
    {
        string src = """
            void A() { var x = ; }
            void B() { print(undefinedThing); }
            void C() { var y = 5; print(y); }
            void D() { var z = "a" + 1z; }
            """;
        var doc = Workspace.Analyze(Path.Combine(TempDir(), "t.hs"), src);
        Assert.True(doc.Diags.Count >= 3, $"expected >=3 diags, got {doc.Diags.Count}: {string.Join("; ", doc.Diags)}");
        Assert.Contains(doc.Diags, d => d.Message.Contains("undefined variable 'undefinedThing'") || d.Message.Contains("undefinedThing"));

        Assert.Contains(doc.Occs, o => o.Name == "y" && o.IsDecl);
    }

    [Fact]
    public void ParseErrorsInImportedFileAreAttributedToIt()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "lib.hs"), "public int Broken( { return 1; }");
        string entry = """
            import lib.hs;
            print(1);
            """;
        var doc = Workspace.Analyze(Path.Combine(dir, "main.hs"), entry);
        Assert.Contains(doc.Diags, d => d.File == Path.Combine(dir, "lib.hs"));
        Assert.DoesNotContain(doc.Diags, d => d.File == Path.Combine(dir, "main.hs") && d.Line == 2);
    }

    [Fact]
    public void CompletionOnlySeesImportedPublicSymbols()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "good.hs"), "public class Greeter { int n; }");
        File.WriteAllText(Path.Combine(dir, "other.hs"), "public class NotImported { int n; }");
        string entry = """
            import good.hs;
            var x = 1;
            """;
        var doc = Workspace.Analyze(Path.Combine(dir, "main.hs"), entry);
        var visible = doc.Decls.Where(d => d.File == doc.EntryPath || d.Public).Select(d => d.Name).ToList();
        Assert.Contains("Greeter", visible);
        Assert.DoesNotContain("NotImported", visible);
    }

    private const string ShadowSrc = """
        void F() { var x = 1; print(x); }
        void G() { var x = 2; print(x); }
        """;

    [Fact]
    public void HoverShowsInferredType()
    {
        var doc = Workspace.Analyze(Path.Combine(TempDir(), "t.hs"), "var x = 5; print(x);");
        var occ = Workspace.OccAt(doc, doc.EntryPath, Pos(1, 5));
        Assert.NotNull(occ);
        Assert.Equal("int", occ!.Ty!.Name);
        Assert.True(occ.IsDecl);
    }

    [Fact]
    public void RenameRespectsShadowing()
    {
        string path = Path.Combine(TempDir(), "t.hs");
        var doc = Workspace.Analyze(path, ShadowSrc);
        var ws = new Workspace();
        ws.Update(path, ShadowSrc);

        var targets = ws.RenameTargets(path, Pos(1, 16), "file:///t.hs");
        Assert.Equal(2, targets.Count);

        var refs = ws.References(path, Pos(1, 16));
        Assert.All(refs, r => Assert.Equal(1, r.Pos.Line));
    }

    [Fact]
    public void DefinitionJumpsToDeclaration()
    {
        string src = """
            int Double(int n) { return n * 2; }
            print(Double(21));
            """;
        string path = Path.Combine(TempDir(), "t.hs");
        var doc = Workspace.Analyze(path, src);
        var call = doc.Calls.FirstOrDefault(c => c.Name == "Double" && c.NameLine == 2);
        Assert.NotNull(call);
        Assert.NotNull(call!.Fn);
        Assert.Equal(1, call.Fn!.Line);
        Assert.Null(call.Fn.SourceFile);
    }

    [Fact]
    public void FormatterNormalizesAndIsIdempotent()
    {
        string messy = "class U{string N;int A;public string G(){return \"hi \"+N;}}var u=U{N:\"b\",A:3};string?s=null;var v=s??\"x\";";
        var once = Formatter.Format(messy);
        Assert.Contains("\n{", once.Replace("\r", ""));
        Assert.Contains("var u = U { N: \"b\", A: 3 };", once);
        Assert.Contains("string? s = null;", once);
        Assert.DoesNotContain("U{", once);

        var twice = Formatter.Format(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void FormatterKeepsBrokenSourceUntouched()
    {
        string broken = "var x = \"unterminated";
        Assert.Equal(broken, Formatter.Format(broken));
    }

    [Fact]
    public void SemanticTokensMarkVariablesAndCalls()
    {
        var doc = Workspace.Analyze(Path.Combine(TempDir(), "t.hs"), "var n = 5; print(n);");
        var occs = doc.Occs.Where(o => o.Kind == "var").ToList();
        Assert.Equal(2, occs.Count);
        Assert.True(occs[0].IsDecl);
        Assert.False(occs[1].IsDecl);
        Assert.Equal("int", occs[1].Ty!.Name);
    }

    [Fact]
    public void InlayHintTypeComesFromChecker()
    {
        string src = "var s = \"hello\"; var n = Double(2); int Double(int x) { return x * 2; }";
        var doc = Workspace.Analyze(Path.Combine(TempDir(), "t.hs"), src);
        var vars = Workspace.InferredVars(doc.Program!);
        Assert.Equal(2, vars.Count);
        var sOcc = doc.Occs.First(o => o.IsDecl && o.Name == "s");
        Assert.Equal("string", sOcc.Ty!.Name);
        var nOcc = doc.Occs.First(o => o.IsDecl && o.Name == "n");
        Assert.Equal("int", nOcc.Ty!.Name);
    }

    [Fact]
    public void CallSiteRecordsArgumentPositions()
    {
        string src = "int Add(int a, int b) { return a + b; }\nprint(Add(1, 2));";
        var doc = Workspace.Analyze(Path.Combine(TempDir(), "t.hs"), src);
        var call = doc.Calls.FirstOrDefault(c => c.Name == "Add");
        Assert.NotNull(call);
        Assert.NotNull(call!.Fn);
        Assert.Equal(2, call.ArgLines.Count);
        Assert.Equal(2, call.ArgCols.Count);
    }

    [Fact]
    public void TypoSuggestsClosestSymbol()
    {
        string src = "var apple = 1;\nprint(applee);";
        string path = Path.Combine(TempDir(), "t.hs");
        var doc = Workspace.Analyze(path, src);
        var ws = new Workspace();
        ws.Update(path, src);
        var diag = doc.Diags.FirstOrDefault(d => d.Message.Contains("undefined variable"));
        Assert.NotNull(diag);
        Assert.Equal("apple", ws.SuggestName("applee", path));
    }

    [Fact]
    public void EditingEntryRepublishesForChangedImport()
    {
        var dir = TempDir();
        string lib = Path.Combine(dir, "lib.hs");
        File.WriteAllText(lib, "public int Val() { return 1; }");
        string entry = "import lib.hs;\nprint(Val());";

        var ws = new Workspace();
        var first = ws.Update(Path.Combine(dir, "main.hs"), entry);
        Assert.DoesNotContain(first[0].Diags, d => d.Message.Contains("not public"));

        File.WriteAllText(lib, "int Val() { return 1; }");
        var second = ws.Update(Path.Combine(dir, "main.hs"), entry);
        Assert.Contains(second[0].Diags, d => d.Message.Contains("not public"));
    }
}

public class JsonRpcServerTests
{
    private static (MemoryStream input, List<byte> output) Send(params object[] messages)
    {
        var input = new MemoryStream();
        foreach (var m in messages)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(m);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
            input.Write(header);
            input.Write(json);
        }
        input.Position = 0;
        return (input, new List<byte>());
    }

    private static List<JsonElement> RunServer(MemoryStream input)
    {
        var output = new MemoryStream();
        new Server().Run(input, output);
        var bytes = output.ToArray();
        var responses = new List<JsonElement>();
        int i = 0;
        while (i < bytes.Length)
        {
            int hdrEnd = bytes.AsSpan(i).IndexOf("\r\n\r\n"u8);
            if (hdrEnd < 0) break;
            var header = Encoding.ASCII.GetString(bytes, i, hdrEnd);
            int len = int.Parse(header.Split(':')[1].Trim());
            i += hdrEnd + 4;
            responses.Add(JsonDocument.Parse(new ArraySegment<byte>(bytes, i, len)).RootElement);
            i += len;
        }
        return responses;
    }

    private static int IdCounter = 0;

    private static object Req(string method, object? @params = null)
    {
        IdCounter++;
        var msg = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = IdCounter, ["method"] = method };
        if (@params != null) msg["params"] = @params;
        return msg;
    }

    [Fact]
    public void FullSessionOverJsonRpc()
    {
        string path = Path.Combine(Path.GetTempPath(), "hs-jsonrpc-" + Path.GetRandomFileName() + ".hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "class Greeter { public string Hi() { return \"yo\"; } }\nvar n = 5;\nprint(n);\nprintg(n);\n";

        var (input, _) = Send(
            Req("initialize", new { capabilities = new { }, rootUri = (string?)null }),

            new { jsonrpc = "2.0", method = "initialized", @params = new { } },
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            {
                textDocument = new { uri, languageId = "hsharp", version = 1, text = src }
            } },
            Req("textDocument/completion", new
            {
                textDocument = new { uri },
                position = new { line = 3, character = 6 }
            }),
            Req("textDocument/hover", new
            {
                textDocument = new { uri },
                position = new { line = 1, character = 4 }
            }),
            Req("textDocument/definition", new
            {
                textDocument = new { uri },
                position = new { line = 3, character = 0 }
            }),
            Req("textDocument/foldingRange", new { textDocument = new { uri } }),
            Req("textDocument/formatting", new { textDocument = new { uri }, options = new { tabSize = 4 } }),
            Req("shutdown")

        );

        var responses = RunServer(input);
        var byMethodOrId = new Dictionary<int, JsonElement>();
        foreach (var r in responses)
            if (r.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                byMethodOrId[idEl.GetInt32()] = r;

        var init = byMethodOrId[1];
        var caps = init.GetProperty("result").GetProperty("capabilities");
        Assert.True(caps.TryGetProperty("renameProvider", out _));
        Assert.True(caps.TryGetProperty("semanticTokensProvider", out _));
        Assert.True(caps.TryGetProperty("inlayHintProvider", out _));
        Assert.True(caps.TryGetProperty("signatureHelpProvider", out _));
        Assert.True(caps.TryGetProperty("foldingRangeProvider", out _));
        Assert.True(caps.TryGetProperty("documentFormattingProvider", out _));

        var diags = responses.FirstOrDefault(r => r.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics");
        Assert.True(diags.ValueKind != JsonValueKind.Undefined, "no diagnostics published");

        var completion = byMethodOrId[2];
        var items = completion.GetProperty("result").GetProperty("items");
        var labels = items.EnumerateArray().Select(i => i.GetProperty("label").GetString()).ToList();
        Assert.Contains("n", labels);
        Assert.Contains("Greeter", labels);
        Assert.Contains("print", labels);
        Assert.Contains("var", labels);

        var hover = byMethodOrId[3];
        string hoverText = hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString() ?? "";
        Assert.Contains("int", hoverText);

        var format = byMethodOrId[6];
        string newText = format.GetProperty("result")[0].GetProperty("newText").GetString() ?? "";
        Assert.Contains("print(n);", newText);
        Assert.Contains("\n", newText);
    }

    [Fact]
    public void MalformedRequestDoesNotKillServer()
    {
        string path = Path.Combine(Path.GetTempPath(), "hs-jsonrpc-" + Path.GetRandomFileName() + ".hs");
        string uri = new Uri(path).AbsoluteUri;
        File.WriteAllText(path, "print(1);");

        var (input, _) = Send(
            Req("textDocument/hover", new { bogus = true }),
            Req("shutdown")

        );
        var responses = RunServer(input);
        Assert.Contains(responses, r => r.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void EveryNonCallbackHandleMemberHasASignature()
    {
        foreach (var (handle, members) in RuntimeApi.HandleMembers)
        {
            foreach (var m in members)
            {

                if (m == "OnAccept" || (handle == "HttpPacket" && m == "Cookies")) continue;
                var sigs = RuntimeApi.HandleSignatures.GetValueOrDefault($"{handle}.{m}");
                Assert.True(sigs is { Length: > 0 }, $"{handle}.{m} has no signature; chains like x.{m}(). lose completion");
                Assert.Equal(m, sigs[0].Split(' ')[1].Split('(')[0]);
            }
        }
    }
}

