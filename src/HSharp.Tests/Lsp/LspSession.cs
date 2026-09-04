using System.Text;
using System.Text.Json;
using HSharp.Analysis;
using HSharp.Checking;
using HSharp.Lsp;
using HSharp.Syntax;

public static class LspSession
{
    internal static List<JsonElement> Run(string rootPath, List<object> messages)
    {
        var init = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new
            {
                capabilities = new
                {
                    textDocument = new
                    {
                        completion = new
                        {
                            completionItem = new
                            {
                                snippetSupport = true,
                                documentationFormat = new[] { "markdown" }
                            },
                            contextSupport = true
                        },
                        publishDiagnostics = new { relatedInformation = true, versionSupport = true }
                    },
                    workspace = new { configuration = true, workspaceFolders = true }
                },
                rootUri = new Uri(rootPath).AbsoluteUri,
                workspaceFolders = new[] { new { uri = new Uri(rootPath).AbsoluteUri, name = "test" } }
            }
        };
        var input = new MemoryStream();
        foreach (var m in new object[] { init }.Concat(messages))
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

    internal static object DidOpen(string uri, string text) =>
        new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new
        {
            textDocument = new { uri, languageId = "hsharp", version = 1, text }
        } };

    internal static object DidChange(string uri, int version, string text) =>
        new { jsonrpc = "2.0", method = "textDocument/didChange", @params = new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { new { text } }
        } };

    internal static object Completion(int id, string uri, int line, int ch) =>
        new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "textDocument/completion",
            ["params"] = new { textDocument = new { uri }, position = new { line, character = ch } }
        };

    internal static List<string> Labels(List<JsonElement> responses, int id)
    {
        var r = responses.First(x => x.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id);
        return r.GetProperty("result").GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("label").GetString()!).ToList();
    }

    internal static List<string> Ranked(List<JsonElement> responses, int id)
    {
        var r = responses.First(x => x.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id);
        return r.GetProperty("result").GetProperty("items")
            .EnumerateArray()
            .Select(i => (Label: i.GetProperty("label").GetString()!, Sort: i.GetProperty("sortText").GetString()!))
            .OrderBy(x => x.Sort, StringComparer.Ordinal)
            .Select(x => x.Label)
            .ToList();
    }

}

