using System.Text;
using System.Text.Json;
using HSharp.Analysis;
using HSharp.Checking;
using HSharp.Lexing;
using HSharp.Lsp;
using HSharp.Parsing;
using HSharp.Syntax;

public class CompletionContextTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "hs-ctx-" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static Workspace.Position Pos(int line, int col) => new(line, col);

    [Theory]
    [InlineData("var x = 1;", 10, CompletionCtxKind.Expression)]
    [InlineData("var x = 1;", 5, CompletionCtxKind.DeclName)]
    [InlineData("print(1);", 1, CompletionCtxKind.StatementStart)]
    [InlineData("if (true) { }", 5, CompletionCtxKind.Expression)]
    [InlineData("import util.hs;", 8, CompletionCtxKind.ImportPath)]
    [InlineData("lock (x) { }", 7, CompletionCtxKind.LockTarget)]
    [InlineData("try { } catch (e) { }", 16, CompletionCtxKind.CatchVar)]
    [InlineData("list<int> a = list<int>();", 6, CompletionCtxKind.TypePosition)]
    [InlineData("string name = \"x\";", 8, CompletionCtxKind.DeclName)]
    [InlineData("// note", 5, CompletionCtxKind.None)]
    public void ContextDependsOnCursorPosition(string line, int col, CompletionCtxKind want)
    {
        var ctx = Workspace.Classify(line, Pos(1, col));
        Assert.Equal(want, ctx.Kind);
    }

    [Fact]
    public void ForeachIterableAfterIn()
    {
        var ctx = Workspace.Classify("foreach (var it in items) { }", Pos(1, 22));
        Assert.Equal(CompletionCtxKind.ForeachIterable, ctx.Kind);
    }

    [Fact]
    public void CallArgKnowsCalleeAndIndex()
    {

        var ctx = Workspace.Classify("F(a, b, c);", Pos(1, 6));
        Assert.Equal(CompletionCtxKind.CallArg, ctx.Kind);
        Assert.Equal("F", ctx.Callee);
        Assert.Equal(1, ctx.ArgIndex);
    }

    [Fact]
    public void MemberAccessExtractsTarget()
    {
        var ctx = Workspace.Classify("client.Close();", Pos(1, 9));
        Assert.Equal(CompletionCtxKind.MemberAccess, ctx.Kind);
        Assert.Equal("client", ctx.MemberTarget);
    }

    private const string ScopeSrc = """
        void A()
        {
            var first = 1;
            print(first);
        }
        void B()
        {
            var second = 2;
        }
        var top = 3;
        """;

    [Fact]
    public void LocalsAreScopeAware()
    {
        string path = Path.Combine(TempDir(), "t.hs");
        var doc = Workspace.Analyze(path, ScopeSrc);

        var ws = new Workspace();
        ws.Update(path, ScopeSrc);
        var server = new CompletionProbe();
        var items = server.ProbeLocals(doc, path, Pos(4, 5));
        Assert.Contains("first", items);
        Assert.DoesNotContain("second", items);

        var items2 = server.ProbeLocals(doc, path, Pos(8, 18));
        Assert.Contains("second", items2);
        Assert.DoesNotContain("first", items2);

        var items3 = server.ProbeLocals(doc, path, Pos(10, 13));
        Assert.Contains("top", items3);
        Assert.DoesNotContain("first", items3);
        Assert.DoesNotContain("second", items3);
    }

    private sealed class CompletionProbe
    {
        public List<string> ProbeLocals(AnalysisDoc doc, string path, Workspace.Position pos)
        {
            var result = new List<string>();
            foreach (var o in EnumerateLocals(doc, path, pos)) result.Add(o);
            return result;
        }

        private static IEnumerable<string> EnumerateLocals(AnalysisDoc doc, string path, Workspace.Position pos)
        {
            var seen = new HashSet<string>();
            foreach (var o in doc.Occs)
            {
                if (o.File != path || !o.IsDecl || o.Kind is not ("var" or "param")) continue;
                if (o.Line > pos.Line || (o.Line == pos.Line && o.Col >= pos.Col)) continue;
                if (doc.ScopeEnds.TryGetValue(o, out var end) && pos.Line > end) continue;
                if (seen.Add(o.Name)) yield return o.Name;
            }
        }
    }

    [Fact]
    public void CookieOptionsRequiresNewButUserClassesDoNot()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "t.hs");
        var ok = Workspace.Analyze(path, "var o = new CookieOptions { Secure: true, SameSite: SameSite.Lax };\nprint(o.Secure);\nprint(o.SameSite == SameSite.Lax);\n");
        Assert.DoesNotContain(ok.Diags, d => d.Severity == 1);

        var bare = Workspace.Analyze(Path.Combine(dir, "b.hs"), "var o = CookieOptions { };\nprint(o.HttpOnly);\n");
        Assert.Contains(bare.Diags, d => d.Message.Contains("'CookieOptions' must be created with 'new'"));

        var sugar = Workspace.Analyze(Path.Combine(dir, "s.hs"), "class P { int X; }\nvar p = new P { X: 1 };\nprint(p.X);\n");
        Assert.DoesNotContain(sugar.Diags, d => d.Severity == 1);
    }

    [Fact]
    public void UnusedVariablesWarnAndUsedOnesDoNot()
    {
        string src = "var unused = 5;\nvar used = 6;\nprint(used);";
        var doc = Workspace.Analyze(Path.Combine(TempDir(), "t.hs"), src);
        var warns = Workspace.UnusedWarnings(doc).ToList();
        Assert.Contains(warns, w => w.Message.Contains("'unused' is declared but never used"));
        Assert.DoesNotContain(warns, w => w.Message.Contains("'used'"));
        Assert.All(warns, w => Assert.Equal(2, w.Severity));
    }

    [Fact]
    public void UnterminatedBlockStillYieldsItsDeclarations()
    {
        string src = "void Broken() {\nvar x = 1;\nprint(x);";
        string path = Path.Combine(TempDir(), "t.hs");
        var doc = Workspace.Analyze(path, src);

        Assert.NotNull(doc.Program);
        Assert.Contains(doc.Program.Stmts, s => s is FnDecl f && f.Name == "Broken");
        Assert.Contains(doc.Diags, d => d.Message.Contains("missing '}'"));

        Assert.Contains(doc.Occs, o => o.Name == "x" && o.IsDecl);
    }

    [Fact]
    public void UnterminatedClassStillYieldsItsMembers()
    {
        string src = "class Thing {\nint n;\npublic string Go() { return \"x\"; }";
        var doc = Workspace.Analyze(Path.Combine(TempDir(), "t.hs"), src);
        Assert.NotNull(doc.Program);
        Assert.Contains(doc.Program.Stmts, s => s is TypeDecl t && t.Name == "Thing");
        Assert.Contains(doc.Decls, d => d.Name == "Go" && d.Kind == "method");
    }
}

public class ContextJsonRpcTests
{
    private static int IdCounter = 0;

    private static object Req(string method, object? @params = null)
    {
        IdCounter++;
        var msg = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = IdCounter, ["method"] = method };
        if (@params != null) msg["params"] = @params;
        return msg;
    }

    private static List<JsonElement> RunServer(MemoryStream input, out MemoryStream output)
    {
        output = new MemoryStream();
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

    private static (MemoryStream, List<JsonElement>) Session(string rootPath, Func<object[]> extraRequests)
    {
        IdCounter = 0;
        var input = new MemoryStream();
        var all = new List<object>
        {
            Req("initialize", new { capabilities = new { }, rootUri = new Uri(rootPath).AbsoluteUri })
        };
        all.AddRange(extraRequests());
        foreach (var m in all)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(m);
            input.Write(Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n"));
            input.Write(json);
        }
        input.Position = 0;
        var responses = RunServer(input, out _);
        return (input, responses);
    }

    private static List<string> CompletionLabels(List<JsonElement> responses, int id)
    {
        var r = responses.FirstOrDefault(x => x.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id);
        if (r.ValueKind == JsonValueKind.Undefined)
            throw new Exception("no response for id " + id + "; responses: " + string.Join(" | ",
                responses.Select(x => x.GetRawText().Substring(0, Math.Min(120, x.GetRawText().Length)))));
        if (!r.TryGetProperty("result", out _))
            throw new Exception("no result in response for id " + id + ": " + r.GetRawText());

        return r.GetProperty("result").GetProperty("items")
            .EnumerateArray()
            .Select(i => (Label: i.GetProperty("label").GetString()!, Sort: i.GetProperty("sortText").GetString()!))
            .OrderBy(x => x.Sort, StringComparer.Ordinal)
            .Select(x => x.Label)
            .ToList();
    }

    [Fact]
    public void ExpressionPositionHasNoStatementKeywords()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-ctx-ws-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "main.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var n = 5;\nvar m = n + \n";

        var (_, responses) = Session(dir, () => new object[]
        {
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            { textDocument = new { uri, languageId = "hsharp", version = 1, text = src } } },
            Req("textDocument/completion", new { textDocument = new { uri }, position = new { line = 1, character = 10 } })
        });

        var labels = CompletionLabels(responses, IdCounter);
        Assert.Contains("n", labels);

        Assert.DoesNotContain("if", labels);
        Assert.DoesNotContain("while", labels);
        Assert.DoesNotContain("return", labels);
        Assert.DoesNotContain("var", labels);
        Assert.DoesNotContain("lock", labels);
    }

    [Fact]
    public void StatementStartHasStatementsAndDeclarations()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-ctx-ws-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "main.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var n = 5;\n\n";

        var (_, responses) = Session(dir, () => new object[]
        {
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            { textDocument = new { uri, languageId = "hsharp", version = 1, text = src } } },
            Req("textDocument/completion", new { textDocument = new { uri }, position = new { line = 1, character = 0 } })
        });

        var labels = CompletionLabels(responses, IdCounter);
        Assert.Contains("if", labels);
        Assert.Contains("while", labels);
        Assert.Contains("var", labels);
        Assert.Contains("n", labels);
    }

    [Fact]
    public void ExpectedTypeSortsCallArguments()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-ctx-ws-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "main.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "void F(int a, string b) { print(b); }\nvar num = 1;\nvar txt = \"x\";\nF(num, \n";

        var (_, responses) = Session(dir, () => new object[]
        {
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            { textDocument = new { uri, languageId = "hsharp", version = 1, text = src } } },
            Req("textDocument/completion", new { textDocument = new { uri }, position = new { line = 3, character = 6 } })
        });

        var labels = CompletionLabels(responses, IdCounter);

        Assert.True(labels.IndexOf("txt") < labels.IndexOf("num"),
            $"expected txt before num, got: {string.Join(", ", labels)}");
    }

    [Fact]
    public void NullableStringCompletesStringMethods()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-ctx-ws-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "main.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var mystery = env(\"X\");\nmystery.\n";

        var (_, responses) = Session(dir, () => new object[]
        {
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            { textDocument = new { uri, languageId = "hsharp", version = 1, text = src } } },
            Req("textDocument/completion", new { textDocument = new { uri }, position = new { line = 1, character = 9 } })
        });

        var labels = CompletionLabels(responses, IdCounter);
        Assert.Contains("Contains", labels);
        Assert.Contains("ToUpper", labels);
    }

    [Fact]
    public void UnknownIdentifierStillGetsNoMemberList()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-ctx-ws-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "main.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var mystery = env(\"X\");\nNoSuchThing.\n";

        var (_, responses) = Session(dir, () => new object[]
        {
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            { textDocument = new { uri, languageId = "hsharp", version = 1, text = src } } },
            Req("textDocument/completion", new { textDocument = new { uri }, position = new { line = 1, character = 12 } })
        });

        var labels = CompletionLabels(responses, IdCounter);
        Assert.Empty(labels);
    }

    [Fact]
    public void AutoImportFixOffersWorkspaceImport()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-ctx-ws-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "helper.hs"), "public class Helper { int n; }");
        string path = Path.Combine(dir, "main.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var h = Helper { n: 1 };\n";

        var (_, responses) = Session(dir, () => new object[]
        {
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            { textDocument = new { uri, languageId = "hsharp", version = 1, text = src } } },
            Req("textDocument/codeAction", new
            {
                textDocument = new { uri },
                range = new { start = new { line = 0, character = 8 }, end = new { line = 0, character = 9 } },
                context = new { diagnostics = Array.Empty<object>() }
            })
        });

        var r = responses.First(x => x.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == IdCounter);
        var actions = r.GetProperty("result");
        bool found = false;
        foreach (var a in actions.EnumerateArray())
        {
            string title = a.GetProperty("title").GetString() ?? "";
            if (title.Contains("import helper.hs"))
            {
                found = true;
                string newText = a.GetProperty("edit").GetProperty("changes").EnumerateObject().First().Value.EnumerateArray().First()
                    .GetProperty("newText").GetString();
                Assert.Equal("import helper.hs;\n", newText);
            }
        }
        Assert.True(found, "no auto-import action offered");
    }

    [Fact]
    public void ImportCompletionListsWorkspaceFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-ctx-ws-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "helper.hs"), "public class Helper { int n; }");
        string path = Path.Combine(dir, "main.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "import \n";

        var (_, responses) = Session(dir, () => new object[]
        {
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            { textDocument = new { uri, languageId = "hsharp", version = 1, text = src } } },
            Req("textDocument/completion", new { textDocument = new { uri }, position = new { line = 0, character = 8 } })
        });

        var labels = CompletionLabels(responses, IdCounter);
        Assert.Contains("helper.hs", labels);
    }

    [Fact]
    public void UnusedWarningsArriveAsSeverityTwo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-ctx-ws-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "main.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var unusedThing = 5;\nprint(\"hi\");\n";

        var (_, responses) = Session(dir, () => new object[]
        {
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            { textDocument = new { uri, languageId = "hsharp", version = 1, text = src } } }
        });

        var diags = responses.Where(r => r.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics")
            .SelectMany(r => r.GetProperty("params").GetProperty("diagnostics").EnumerateArray())
            .ToList();
        Assert.Contains(diags, dd => dd.GetProperty("severity").GetInt32() == 2
            && (dd.GetProperty("message").GetString() ?? "").Contains("'unusedThing'"));
    }

    [Fact]
    public void DiagnosticRangeCoversWholeToken()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-ctx-ws-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "main.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "undefinedFunctionHere(1);\n";

        var (_, responses) = Session(dir, () => new object[]
        {
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
            { textDocument = new { uri, languageId = "hsharp", version = 1, text = src } } }
        });

        var diags = responses.Where(r => r.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics")
            .SelectMany(r => r.GetProperty("params").GetProperty("diagnostics").EnumerateArray())
            .Where(dd => dd.GetProperty("severity").GetInt32() == 1)
            .ToList();

        var range = diags.Select(dd => dd.GetProperty("range")).First();
        int start = range.GetProperty("start").GetProperty("character").GetInt32();
        int end = range.GetProperty("end").GetProperty("character").GetInt32();
        Assert.True(end - start >= "undefinedFunctionHere".Length,
            $"expected squiggle over the whole token, got {end - start}");
    }
}

