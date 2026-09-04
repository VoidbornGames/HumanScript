using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HSharp.Analysis;
using HSharp.Checking;
using HSharp.Lexing;
using HSharp.Syntax;

[assembly: InternalsVisibleTo("HSharp.Tests")]

namespace HSharp.Lsp;

internal static class Program
{
    private static void Main() => new Server().Run(Console.OpenStandardInput(), Console.OpenStandardOutput());
}

internal sealed partial class Server
{
    private const string Source = "hsharp";
    private readonly Workspace _workspace = new();
    private readonly Dictionary<string, string> _openTexts = new();
    private readonly Dictionary<string, List<string>> _publishedPerDoc = new();
    private Stream? _output;
    private readonly object _writeLock = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _dirty = new();
    private Timer? _debounceTimer;

    private readonly List<string> _workspaceFolders = new();
    private readonly Dictionary<string, (DateTime Mt, List<DeclInfo> Decls)> _fileIndex = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastScan;

    public static Exception? LastError;

    // single-threaded json-rpc loop. requests never fail visibly: a thrown
    // handler answers with the empty shape for that method and logs the
    // cause to stderr, so the editor never pops a request-failed dialog
    public void Run(Stream input, Stream output)
    {
        _output = output;
        _debounceTimer = new Timer(_ => FlushDirty(150), null, 100, 100);
        try
        {
            while (true)
            {
                JsonDocument? msg = ReadMessage(input);
                if (msg == null) break;
                try
                {
                    Handle(msg);
                }
                catch (Exception e)
                {

                    LastError = e;
                    try { Console.Error.WriteLine("hsharp-lsp: " + e); } catch { }

                    try
                    {
                        if (msg.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null
                            && msg.RootElement.TryGetProperty("method", out var mEl))
                        {
                            Send(new
                            {
                                id = JsonSerializer.Deserialize<JsonElement>(idEl.ToString()),
                                jsonrpc = "2.0",
                                result = SafeDefault(mEl.GetString() ?? "")
                            });
                        }
                    }
                    catch { }
                }
            }
        }
        finally
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            FlushDirty(0);
        }
    }

    // re-analyzes documents whose text changed; minAge coalesces keystroke
    // bursts, 0 flushes everything (any incoming request flushes first so
    // answers always see the current buffer)
    private void FlushDirty(int minAgeMs)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var due = _dirty.Where(kv => (now - kv.Value).TotalMilliseconds >= minAgeMs).ToList();
            foreach (var kv in due) _dirty.Remove(kv.Key);
            foreach (var kv in due)
            {
                try { Republish(kv.Key, _openTexts.GetValueOrDefault(kv.Key, "")); }
                catch (Exception ex) { try { Console.Error.WriteLine("hsharp-lsp flush: " + ex.Message); } catch { } }
            }
        }
    }

    private static object? SafeDefault(string method) => method switch
    {
        "textDocument/completion" => new { isIncomplete = false, items = Array.Empty<object>() },
        "textDocument/semanticTokens/full" => new { data = Array.Empty<int>() },
        "textDocument/rename" => new { changes = new Dictionary<string, object>() },
        _ => null
    };

    private static JsonDocument? ReadMessage(Stream input)
    {
        int contentLength = -1;
        while (true)
        {
            var line = new StringBuilder();
            while (true)
            {
                int b = input.ReadByte();
                if (b == -1) return null;
                if (b == '\n') break;
                if (b != '\r') line.Append((char)b);
            }
            if (line.Length == 0) break;

            var header = line.ToString();
            int colon = header.IndexOf(':');
            if (colon > 0 && header[..colon].Trim() == "Content-Length")
                int.TryParse(header[(colon + 1)..].Trim(), out contentLength);
        }

        if (contentLength <= 0) return null;

        var buf = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = input.Read(buf, read, contentLength - read);
            if (n <= 0) return null;
            read += n;
        }
        return JsonDocument.Parse(buf);
    }

    private void Handle(JsonDocument msg)
    {
        var root = msg.RootElement;
        if (!root.TryGetProperty("method", out var methodEl)) return;
        string method = methodEl.GetString() ?? "";
        string? id = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;

        if (id != null && _dirty.Count > 0) FlushDirty(0);
        lock (_gate)
        {

        switch (method)
        {
            case "initialize":
                {
                    var p = root.GetProperty("params");
                    if (p.TryGetProperty("rootUri", out var ru) && ru.ValueKind == JsonValueKind.String)
                        AddWorkspaceFolder(ru.GetString()!);
                    if (p.TryGetProperty("workspaceFolders", out var wfs) && wfs.ValueKind == JsonValueKind.Array)
                        foreach (var wf in wfs.EnumerateArray())
                            if (wf.TryGetProperty("uri", out var wu))
                                AddWorkspaceFolder(wu.GetString()!);
                    Reply(id, new
                {
                    capabilities = new
                    {
                        positionEncoding = "utf-16",

                        textDocumentSync = new { openClose = true, change = 1 },
                        completionProvider = new { triggerCharacters = new[] { ".", "(" }, resolveProvider = false },
                        hoverProvider = true,
                        definitionProvider = true,
                        referencesProvider = true,
                        documentSymbolProvider = true,
                        workspaceSymbolProvider = true,
                        renameProvider = true,
                        documentFormattingProvider = true,
                        codeActionProvider = new { codeActionKinds = new[] { "quickfix" } },
                        signatureHelpProvider = new { triggerCharacters = new[] { "(", "," } },
                        inlayHintProvider = true,
                        foldingRangeProvider = true,
                        semanticTokensProvider = new
                        {
                            legend = new
                            {
                                tokenTypes = new[]
                                {
                                    "namespace", "type", "class", "struct", "enum", "enumMember",
                                    "function", "method", "variable", "property", "event"
                                },
                                tokenModifiers = Array.Empty<string>()
                            },
                            full = true
                        }
                    },
                    serverInfo = new { name = "H# language server", version = "0.7.3" }
                });
                break;
                }

            case "shutdown":
                Reply(id, null);
                break;

            case "exit":
                Environment.Exit(0);
                break;

            case "textDocument/didOpen":
            case "textDocument/didChange":
                {
                    var p = root.GetProperty("params");
                    var td = p.GetProperty("textDocument");
                    string uri = td.GetProperty("uri").GetString()!;
                    string text;
                    if (method.EndsWith("didOpen"))
                        text = td.GetProperty("text").GetString()!;
                    else
                        text = ApplyChanges(_openTexts.GetValueOrDefault(uri, ""), p.GetProperty("contentChanges"));
                    _openTexts[uri] = text;
                    if (method.EndsWith("didOpen"))
                    {

                        lock (_gate) _dirty.Remove(uri);
                        Republish(uri, text);
                    }
                    else
                        lock (_gate) _dirty[uri] = DateTime.UtcNow;
                    break;
                }

            case "textDocument/didClose":
                {
                    string uri = root.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString()!;
                    _openTexts.Remove(uri);
                    lock (_gate) _dirty.Remove(uri);
                    _workspace.Close(UriToPath(uri));
                    _publishedPerDoc.Remove(uri);
                    PublishDiagnostics(uri, Array.Empty<object>());
                    break;
                }

            case "textDocument/completion":
                {
                    var (uri, pos) = Req(root);
                    var items = Completion(uri, pos);
                    Reply(id, new { isIncomplete = false, items });
                    break;
                }

            case "textDocument/hover":
                {
                    var (uri, pos) = Req(root);
                    Reply(id, Hover(uri, pos));
                    break;
                }

            case "textDocument/definition":
                {
                    var (uri, pos) = Req(root);
                    Reply(id, Definition(uri, pos));
                    break;
                }

            case "textDocument/references":
                {
                    var (uri, pos) = Req(root);
                    Reply(id, References(uri, pos));
                    break;
                }

            case "textDocument/rename":
                {
                    var p = root.GetProperty("params");
                    string uri = p.GetProperty("textDocument").GetProperty("uri").GetString()!;
                    var pos = LspPos(p.GetProperty("position"));
                    string newName = p.GetProperty("newName").GetString() ?? "";
                    Reply(id, Rename(uri, pos, newName));
                    break;
                }

            case "textDocument/documentSymbol":
                {
                    var (uri, _) = Req(root);
                    Reply(id, Safe(uri, DocumentSymbols, Array.Empty<object>()));
                    break;
                }

            case "workspace/symbol":
                {
                    string query = root.GetProperty("params").TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
                    Reply(id, WorkspaceSymbols(query));
                    break;
                }

            case "textDocument/semanticTokens/full":
                {
                    var (uri, _) = Req(root);
                    var toks = Safe(uri, SemanticTokens, Array.Empty<int>());
                    Reply(id, new { data = toks });
                    break;
                }

            case "textDocument/inlayHint":
                {
                    var (uri, _) = Req(root);
                    Reply(id, InlayHints(uri));
                    break;
                }

            case "textDocument/signatureHelp":
                {
                    var (uri, pos) = Req(root);
                    Reply(id, SignatureHelp(uri, pos));
                    break;
                }

            case "textDocument/foldingRange":
                {
                    var (uri, _) = Req(root);
                    Reply(id, FoldingRanges(uri));
                    break;
                }

            case "textDocument/formatting":
                {
                    var (uri, _) = Req(root);
                    Reply(id, Format(uri));
                    break;
                }

            case "textDocument/codeAction":
                {
                    var p = root.GetProperty("params");
                    string uri = p.GetProperty("textDocument").GetProperty("uri").GetString()!;
                    var pos = LspPos(p.GetProperty("range").GetProperty("start"));
                    Reply(id, CodeActions(uri, pos));
                    break;
                }
        }
        }
    }

    private static (string Uri, Workspace.Position Pos) Req(JsonElement root)
    {
        var p = root.GetProperty("params");
        string uri = p.GetProperty("textDocument").GetProperty("uri").GetString()!;

        return p.TryGetProperty("position", out var pe)
            ? (uri, LspPos(pe))
            : (uri, new Workspace.Position(1, 1));
    }

    private static Workspace.Position LspPos(JsonElement e) =>
        new(e.GetProperty("line").GetInt32() + 1, e.GetProperty("character").GetInt32() + 1);

    private static (int Line, int Col) ToLsp(Workspace.Position p) => (p.Line - 1, p.Col - 1);

    private static string ApplyChanges(string text, JsonElement changes)
    {
        foreach (var ch in changes.EnumerateArray())
        {
            if (!ch.TryGetProperty("range", out var range))
            {
                text = ch.GetProperty("text").GetString() ?? "";
                continue;
            }
            int start = Offset(text, range.GetProperty("start"));
            int end = Offset(text, range.GetProperty("end"));
            text = text[..start] + (ch.GetProperty("text").GetString() ?? "") + text[end..];
        }
        return text;
    }

    private static int Offset(string text, JsonElement pos)
    {
        int line = pos.GetProperty("line").GetInt32();
        int col = pos.GetProperty("character").GetInt32();
        int i = 0;
        while (line > 0 && i < text.Length)
        {
            if (text[i] == '\n') line--;
            i++;
        }
        i += col;
        return Math.Min(i, text.Length);
    }

    private void Reply(string? id, object? result)
    {
        if (id == null) return;
        Send(new { id = JsonSerializer.Deserialize<JsonElement>(id), result, jsonrpc = "2.0" });
    }

    private void Send(object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
        lock (_writeLock)
        {
            _output!.Write(header);
            _output.Write(json);
            _output.Flush();
        }
    }

    private void PublishDiagnostics(string uri, object[] diagnostics)
    {
        Send(new
        {
            jsonrpc = "2.0",
            method = "textDocument/publishDiagnostics",
            @params = new { uri, diagnostics }
        });
    }

    private static string UriToPath(string uri)
    {
        var u = new Uri(uri);
        if (!u.IsFile) return u.LocalPath;
        string p = Uri.UnescapeDataString(u.AbsolutePath).Replace('/', Path.DirectorySeparatorChar);
        if (p.Length > 3 && p[0] == Path.DirectorySeparatorChar && p[2] == ':') p = p[1..];
        return p;
    }
    private static string PathToUri(string path) => new Uri(path).AbsoluteUri;
}

