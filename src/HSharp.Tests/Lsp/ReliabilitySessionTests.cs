using System.Text;
using System.Text.Json;
using HSharp.Analysis;
using HSharp.Checking;
using HSharp.Lsp;
using HSharp.Syntax;
using static LspSession;

public class ReliabilitySessionTests
{
    [Fact]
    public void BrokenRequestsAnswerWithEmptyResultsNeverErrors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string uri = new Uri(Path.Combine(dir, "app.hs")).AbsoluteUri;

        var msgs = new List<object> { DidOpen(uri, "fn F() { return; }") };
        int id = 2;
        foreach (var method in new[]
        {
            "textDocument/completion", "textDocument/hover", "textDocument/documentSymbol",
            "textDocument/semanticTokens/full", "textDocument/foldingRange", "textDocument/formatting",
            "textDocument/inlayHint", "textDocument/codeAction", "textDocument/references",
            "textDocument/signatureHelp", "textDocument/definition"
        })
        {
            msgs.Add(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0", ["id"] = id++, ["method"] = method,
                ["params"] = new { textDocument = new { uri }, position = new { line = 999, character = 999 } }
            });
        }

        var responses = Run(dir, msgs);
        for (int i = 2; i < id; i++)
        {
            var r = responses.First(x => x.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number && rid.GetInt32() == i);
            Assert.False(r.TryGetProperty("error", out _), $"request {i} answered with an error");
            Assert.True(r.TryGetProperty("result", out _), $"request {i} returned no result");
        }
    }
}

