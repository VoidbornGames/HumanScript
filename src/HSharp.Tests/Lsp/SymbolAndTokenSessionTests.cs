using System.Text;
using System.Text.Json;
using HSharp.Analysis;
using HSharp.Checking;
using HSharp.Lsp;
using HSharp.Syntax;
using static LspSession;

public class SymbolAndTokenSessionTests
{
    [Fact]
    public void InlayHintsDoNotLeakHandleTypeNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var ls = Http.ListenRaw(80);\nvar n = 5;\n";

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "textDocument/inlayHint",
                ["params"] = new { textDocument = new { uri },
                    range = new { start = new { line = 0, character = 0 }, end = new { line = 5, character = 0 } } }
            }
        });

        var r = responses.First(x => x.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == 2);
        var labels = r.GetProperty("result").EnumerateArray()
            .Select(h => h.GetProperty("label").GetString()!).ToList();
        Assert.DoesNotContain(labels, l => l.Contains("rawhttpl"));

        Assert.Contains(labels, l => l.Contains("int"));
    }

    [Fact]
    public void DocumentSymbolsSkipInjectedBuiltins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "int Double(int x) { return x * 2; }\nprint(Double(1));\n";

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "textDocument/documentSymbol",
                ["params"] = new { textDocument = new { uri } }
            }
        });

        var r = responses.First(x => x.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == 2);
        var symbols = r.GetProperty("result").EnumerateArray().ToList();
        var names = symbols
            .Select(s => (s.GetProperty("name").GetString() ?? "")
                + " @" + s.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32())
            .ToList();
        Assert.Contains(names, n => n.StartsWith("int Double"));
        Assert.DoesNotContain(names, n => n.Contains("CookieOptions") || n.Contains("SameSite"));
        Assert.All(names, n => Assert.DoesNotContain("@-1", n));

        Assert.All(symbols, s => Assert.Equal(JsonValueKind.Array, s.GetProperty("children").ValueKind));
    }

    [Fact]
    public void SemanticTokensStayOnRealWordsDespiteInterpolation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = """
            public class AUser
            {
                string Name;
                int Salary;

                public string Whois()
                {
                    return $"{Name}, with salary {Salary}$";
                }
            }
            """;

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "textDocument/semanticTokens/full",
                ["params"] = new { textDocument = new { uri } }
            }
        });

        var data = responses.First(x => x.TryGetProperty("id", out var i) && i.GetInt32() == 2)
            .GetProperty("result").GetProperty("data").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var lines = src.Replace("\r\n", "\n").Split('\n');
        int pl = 0, pc = 0;
        Assert.True(data.Length > 0, "no semantic tokens at all");
        for (int i = 0; i + 4 < data.Length; i += 5)
        {
            int dl = data[i], dc = data[i + 1], len = data[i + 2];
            int line = pl + dl, col = dl == 0 ? pc + dc : dc;
            pl = line; pc = col;
            string text = lines[line].Length >= col
                ? string.Concat(lines[line].Skip(col).Take(len))
                : "";
            Assert.True(text.Length == len && char.IsLetter(text[0]) && text.All(c => char.IsLetterOrDigit(c) || c == '_'),
                $"semantic token off a word boundary: line {line} col {col} len {len} covers '{text}'");
        }
    }

    [Fact]
    public void DocumentSymbolAnswersWhileTyping()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string full = "public class A { string Name; }\nfn Run() { return; }\n";
        var states = new[] { "", "p", "pub", "public ", "public class A { string Nam", full, "fn F() { ret", "fn F() { return; }" };

        var msgs = new List<object> { DidOpen(uri, states[0]) };
        for (int i = 0; i < states.Length; i++)
        {
            msgs.Add(DidChange(uri, i + 2, states[i]));
            msgs.Add(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0", ["id"] = 100 + i, ["method"] = "textDocument/documentSymbol",
                ["params"] = new { textDocument = new { uri } }
            });
        }

        var responses = Run(dir, msgs);
        for (int i = 0; i < states.Length; i++)
        {
            var r = responses.First(x => x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 100 + i);
            Assert.False(r.TryGetProperty("error", out _), $"documentSymbol failed mid-typing at '{states[i]}'");
            Assert.True(r.TryGetProperty("result", out _), $"documentSymbol returned no result at '{states[i]}'");
        }
    }
}

