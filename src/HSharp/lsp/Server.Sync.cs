using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HSharp.Analysis;
using HSharp.Checking;
using HSharp.Lexing;
using HSharp.Syntax;

[assembly: InternalsVisibleTo("HSharp.Tests")]

namespace HSharp.Lsp;

internal sealed partial class Server
{
    private void Republish(string uri, string text)
    {
        string path = UriToPath(uri);
        foreach (var doc in _workspace.Update(path, text))
            PublishDoc(doc);
    }

    private void PublishDoc(AnalysisDoc doc)
    {

        var merged = new Dictionary<string, List<object>>();

        void Add(string file, List<Diag> diags)
        {
            if (diags.Count == 0) return;

            if (file != doc.EntryPath && !File.Exists(file)) return;
            var lens = TokenLengths(file, file == doc.EntryPath ? doc.Text : null);
            var list = merged.GetValueOrDefault(file) ?? new List<object>();
            foreach (var d in diags)
            {
                int len = lens.GetValueOrDefault((d.Line, d.Col), 1);
                list.Add(new
                {
                    range = Range(d.Line, d.Col, d.Col + len),
                    severity = d.Severity,
                    source = Source,
                    message = d.Message
                });
            }
            merged[file] = list;
        }

        foreach (var g in doc.Diags.GroupBy(d => d.File))
            Add(g.Key, g.ToList());

        if (doc.ParseErrorCount == 0)
            foreach (var w in Workspace.UnusedWarnings(doc))
                Add(w.File, new List<Diag> { w });

        foreach (var kv in merged)
            PublishDiagnostics(PathToUri(kv.Key), kv.Value.ToArray());

        string entryUri = PathToUri(doc.EntryPath);

        if (!merged.ContainsKey(entryUri))
            PublishDiagnostics(entryUri, Array.Empty<object>());

        var fresh = merged.Keys.Append(entryUri).Distinct().ToList();

        if (_publishedPerDoc.TryGetValue(entryUri, out var old))
            foreach (var u in old.Where(u => !fresh.Contains(u)))
                PublishDiagnostics(u, Array.Empty<object>());
        _publishedPerDoc[entryUri] = fresh;
    }

    private static object Range(int l1, int c1, int c2) => new
    {
        start = new { line = Math.Max(0, l1 - 1), character = Math.Max(0, c1 - 1) },
        end = new { line = Math.Max(0, l1 - 1), character = Math.Max(0, c2) }
    };

    private (AnalysisDoc?, string) DocFor(string uri)
    {
        string path = UriToPath(uri);
        var doc = _workspace.DocForFile(path);
        string text = _openTexts.GetValueOrDefault(uri) ?? doc?.Text ?? "";
        if (doc == null && text.Length > 0)
            doc = Workspace.Analyze(path, text);
        return (doc, path);
    }

    private T Safe<T>(string uri, Func<string, T> f, T empty)
    {
        try { return f(uri); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"H# lsp: {f.Method.Name} failed for {uri}: {ex.Message}");
            return empty;
        }
    }

    private void AddWorkspaceFolder(string uri)
    {
        try
        {
            string path = UriToPath(uri);
            if (Directory.Exists(path) && !_workspaceFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
                _workspaceFolders.Add(path);
        }
        catch { }
    }

    private static readonly string[] ScanSkipDirs = { "bin", "obj", ".git", "node_modules", ".vs", ".vscode" };

    private List<string> _scannedFiles = new();
    private DateTime _scanAt = DateTime.MinValue;

    private List<string> ScanWorkspaceFiles()
    {
        if (DateTime.UtcNow - _scanAt < TimeSpan.FromSeconds(3)) return _scannedFiles;

        var files = new List<string>();
        foreach (var folder in _workspaceFolders)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*.hs", SearchOption.AllDirectories)
                    .Where(f => !ScanSkipDirs.Any(s => f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(s)))
                    .Take(2000));
            }
            catch { }
        }
        _scannedFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _scanAt = DateTime.UtcNow;
        return _scannedFiles;
    }

    private List<(string File, DeclInfo Decl)> PublicIndex(bool force = false)
    {
        if (!force && _lastScan != default && DateTime.UtcNow - _lastScan < TimeSpan.FromSeconds(2))
            return _fileIndex.SelectMany(kv => kv.Value.Decls.Where(d => d.Public), (kv, d) => (kv.Key, d)).ToList();

        var mt = ScanWorkspaceFiles().Select(f => File.GetLastWriteTimeUtc(f)).DefaultIfEmpty().Max();
        _lastScan = DateTime.UtcNow;

        foreach (var f in ScanWorkspaceFiles())
        {
            try
            {
                var m = File.GetLastWriteTimeUtc(f);
                if (_fileIndex.TryGetValue(f, out var cached) && cached.Mt == m) continue;
                _fileIndex[f] = (m, Workspace.ScanDecls(f, File.ReadAllText(f)));
            }
            catch { }
        }

        var live = ScanWorkspaceFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _fileIndex.Keys.Where(k => !live.Contains(k)).ToList())
            _fileIndex.Remove(gone);

        return _fileIndex.SelectMany(kv => kv.Value.Decls.Where(d => d.Public), (kv, d) => (kv.Key, d)).ToList();
    }

    private List<string> WorkspaceFilesRelatively(AnalysisDoc doc)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string dir = Path.GetDirectoryName(doc.EntryPath) ?? ".";

        void Add(string file)
        {
            string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (rel.StartsWith("..")) rel = file.Replace('\\', '/');
            if (seen.Add(rel)) results.Add(rel);
        }

        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.hs")) Add(f);
        }
        catch { }

        foreach (var f in ScanWorkspaceFiles()) Add(f);
        return results;
    }

    private static Dictionary<(int, int), int> TokenLengths(string file, string? liveText = null)
    {
        var map = new Dictionary<(int, int), int>();
        try
        {
            string text = liveText ?? (File.Exists(file) ? File.ReadAllText(file) : null);
            if (text == null) return map;
            foreach (var t in new Lexer(text).Tokenize())
                if (t.Kind != Tok.EOF)
                    map[(t.Line, t.Col)] = Math.Max(1, t.Text.Length);
        }
        catch { }
        return map;
    }

}

