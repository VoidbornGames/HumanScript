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
    private object? Hover(string uri, Workspace.Position pos)
    {
        var (doc, path) = DocFor(uri);
        if (doc == null) return null;
        string text = _openTexts.GetValueOrDefault(uri, doc.Text);
        var word = Workspace.WordAt(text, pos);
        string name = word?.Word ?? "";
        if (name.Length == 0) return null;

        var occ = Workspace.OccAt(doc, path, pos);
        if (occ != null)
        {
            string sig = occ.Kind switch
            {
                "var" => $"{occ.Ty} {occ.Name}" + (occ.Owned ? "  (owned)" : occ.Borrow ? "  (borrowed)" : ""),
                "param" => $"{occ.Ty} {occ.Name}" + (occ.Borrow ? "  (borrowed param)" : occ.Generic ? "  (generic)" : ""),
                "field" => $"{occ.Container}.{occ.Name}: {occ.Ty}",
                "enummember" => $"{occ.Container}.{occ.Name}",
                _ => $"{occ.Ty} {occ.Name}"
            };
            return Md($"```hsharp\n{sig}\n```");
        }

        var call = doc.Calls.FirstOrDefault(c => c.Name == name && c.NameLine == pos.Line
            && pos.Col >= c.NameCol && pos.Col < c.NameCol + name.Length);
        if (call != null)
        {
            if (call.Fn != null)
                return Md($"```hsharp\n{Workspace.SigPublic(call.Fn)}\n```");
            if (call.Container != null && RuntimeApi.StaticSignatures.TryGetValue($"{call.Container}.{name}", out var ss))
                return Md($"```hsharp\n{ss[0].Sig}\n```\n{ss[0].Doc}");
        }

        var decl = _workspace.VisibleDecls(path).LastOrDefault(d => d.Name == name);
        if (decl != null)
            return Md($"```hsharp\n{decl.Signature}\n```\n{decl.Detail}".Replace("\n\n", "\n"));

        if (BuiltinInfo.Info.TryGetValue(name, out var bi))
            return Md($"```hsharp\n{bi.Sig}\n```\n{bi.Doc}");

        if (RuntimeApi.IsStaticClass(name))
            return Md($"```hsharp\n{name}\n```\nStatic entry points: {string.Join(", ", RuntimeApi.StaticMembers[name])}");

        return null;
    }

    private static object Md(string value) => new { contents = new { kind = "markdown", value } };

    private object? Definition(string uri, Workspace.Position pos)
    {
        var (doc, path) = DocFor(uri);
        if (doc == null) return null;
        string text = _openTexts.GetValueOrDefault(uri, doc.Text);

        var occ = Workspace.OccAt(doc, path, pos);
        if (occ != null && occ.DeclLine > 0)
            return Loc(occ.DeclFile ?? occ.File, occ.DeclLine, occ.DeclCol, occ.Name.Length);

        var word = Workspace.WordAt(text, pos);
        if (word == null) return null;
        string name = word.Value.Word;

        var call = doc.Calls.FirstOrDefault(c => c.Name == name && c.NameLine == pos.Line
            && pos.Col >= c.NameCol && pos.Col < c.NameCol + name.Length);
        if (call?.Fn != null)
            return Loc(call.Fn.SourceFile ?? doc.EntryPath, call.Fn.Line, call.Fn.Col, name.Length);

        var decl = _workspace.VisibleDecls(path).LastOrDefault(d => d.Name == name);
        if (decl != null)
            return Loc(decl.File, decl.Line, decl.Col, name.Length);

        return null;
    }

    private static object Loc(string file, Workspace.Position p, int len) => new
    {
        uri = PathToUri(file),
        range = new
        {
            start = new { line = Math.Max(0, p.Line - 1), character = Math.Max(0, p.Col - 1) },
            end = new { line = Math.Max(0, p.Line - 1), character = Math.Max(0, p.Col - 1 + Math.Max(1, len)) }
        }
    };

    private static object Loc(string file, int line, int col, int len) =>
        Loc(file, new Workspace.Position(line, col), len);

    private object[] References(string uri, Workspace.Position pos)
    {
        var (doc, path) = DocFor(uri);
        if (doc == null) return Array.Empty<object>();
        return _workspace.References(path, pos)
            .Select(r => Loc(r.File, r.Pos, 1))
            .Distinct()
            .ToArray();
    }

    private object? Rename(string uri, Workspace.Position pos, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || !newName.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return null;
        var (doc, path) = DocFor(uri);
        if (doc == null) return null;

        var targets = _workspace.RenameTargets(path, pos, uri);
        if (targets.Count == 0) return null;

        var changes = new Dictionary<string, List<object>>();
        foreach (var t in targets)
        {
            var occ = doc.Occs.FirstOrDefault(o => o.Line == t.Line && o.Col == t.Col);
            string file = PathToUri(occ?.File ?? doc.EntryPath);
            if (!changes.TryGetValue(file, out var list))
                changes[file] = list = new List<object>();
            list.Add(new
            {
                range = new
                {
                    start = new { line = t.Line - 1, character = t.Col - 1 },
                    end = new { line = t.Line - 1, character = t.Col - 1 + WordLenAt(doc, t) }
                },
                newText = newName
            });
        }

        return new { changes };
    }

    private int WordLenAt(AnalysisDoc doc, Workspace.Position t)
    {
        var occ = doc.Occs.FirstOrDefault(o => o.Line == t.Line && o.Col == t.Col);
        if (occ != null) return occ.Len;
        var lines = doc.Text.Replace("\r\n", "\n").Split('\n');
        if (t.Line - 1 < 0 || t.Line - 1 >= lines.Length) return 1;
        var l = lines[t.Line - 1];
        int i = Math.Min(t.Col - 1, l.Length);
        int n = 0;
        while (i + n < l.Length && (char.IsLetterOrDigit(l[i + n]) || l[i + n] == '_')) n++;
        return Math.Max(1, n);
    }

    private object[] DocumentSymbols(string uri)
    {
        var (doc, path) = DocFor(uri);
        if (doc == null) return Array.Empty<object>();

        var top = new List<object>();
        foreach (var d in doc.Decls.Where(d => d.File == path && d.Container == null && d.Line > 0))
        {
            var children = doc.Decls.Where(c => c.Container == d.Name && c.File == path && c.Line > 0)
                .Select(c => SymInfo(c, null)).ToList();
            top.Add(SymInfo(d, children.Count > 0 ? children : null));
        }
        return top.ToArray();
    }

    private object SymInfo(DeclInfo d, List<object>? children) => new
    {
        name = d.Kind == "field" || d.Kind == "enummember" ? d.Name : d.Signature,
        kind = d.Kind switch
        {
            "function" or "method" => 12,
            "class" => 5,
            "struct" => 23,
            "enum" => 10,
            "enummember" => 22,
            "field" => 8,
            _ => 13
        },
        range = Range(d.Line, d.Col, d.Col + d.Name.Length),
        selectionRange = Range(d.Line, d.Col, d.Col + d.Name.Length),
        detail = d.Detail,

        children = children ?? (object)Array.Empty<object>()
    };

    private object[] WorkspaceSymbols(string query)
    {

        var open = _workspace.AllDecls
            .Where(d => d.Line > 0 && d.Kind != "field" && (query.Length == 0 || d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(200)
            .ToList();

        var seen = open.Select(d => (d.File, d.Name, d.Line)).ToHashSet();
        var disk = PublicIndex()
            .Where(t => t.Decl.Kind is "function" or "class" or "struct" or "enum"
                && (query.Length == 0 || t.Decl.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Where(t => !seen.Contains((t.File, t.Decl.Name, t.Decl.Line)))
            .Select(t => t.Decl)
            .Take(200 - open.Count);

        return open.Concat(disk)
            .Select(d => new
            {
                name = d.Name,
                kind = d.Kind switch
                {
                    "function" or "method" => 12,
                    "class" => 5,
                    "struct" => 23,
                    "enum" => 10,
                    "enummember" => 22,
                    _ => 13
                },
                location = new
                {
                    uri = PathToUri(d.File),
                    range = Range(d.Line, d.Col, d.Col + d.Name.Length)
                },
                containerName = d.Container,
                detail = d.Signature
            })
            .ToArray();
    }

    private object? SignatureHelp(string uri, Workspace.Position pos)
    {
        var (doc, path) = DocFor(uri);
        if (doc == null) return null;

        var call = Workspace.CallAt(doc, path, pos);
        if (call == null) return null;

        string sig;
        List<string> labels;
        if (call.Fn != null)
        {
            var fn = call.Fn;
            sig = Workspace.SigPublic(fn);
            labels = fn.Params.Select(p => $"{p.Type} {p.Name}" + (p.Move ? " [move]" : "")).ToList();
        }
        else if (call.Container != null && RuntimeApi.StaticSignatures.TryGetValue($"{call.Container}.{call.Name}", out var ss))
        {
            sig = ss[0].Sig;
            labels = ParenParams(sig);
        }
        else if (BuiltinInfo.Info.TryGetValue(call.Name, out var bi))
        {
            sig = bi.Sig;
            labels = ParenParams(sig);
        }
        else return null;

        int lp = sig.IndexOf('(');
        int active = 0;
        for (int i = 0; i < call.ArgLines.Count; i++)
        {
            int al = call.ArgLines[i], ac = call.ArgCols[i];
            if (al < pos.Line || (al == pos.Line && ac <= pos.Col)) active = i;
        }

        return new
        {
            signatures = new[] { new { label = sig, parameters = labels.Select(l => new { label = new[] { lp + 1, lp + 1 + l.Length } }) } },
            activeSignature = 0,
            activeParameter = active
        };
    }

    private static List<string> ParenParams(string sig)
    {
        int lp = sig.IndexOf('(');
        int rp = sig.LastIndexOf(')');
        if (lp < 0 || rp <= lp) return new List<string>();
        return sig[(lp + 1)..rp].Split(", ").ToList();
    }

    private object[] FoldingRanges(string uri)
    {
        var (doc, _) = DocFor(uri);
        if (doc == null) return Array.Empty<object>();

        List<Token> toks;
        try
        {
            toks = new Lexer(doc.Text).Tokenize();
        }
        catch (SourceError)
        {
            return Array.Empty<object>();
        }

        var ranges = new List<object>();
        var stack = new Stack<int>();
        foreach (var t in toks)
        {
            if (t.Kind == Tok.LBrace) stack.Push(t.Line);
            else if (t.Kind == Tok.RBrace && stack.Count > 0)
            {
                int start = stack.Pop();
                if (t.Line > start)
                    ranges.Add(new { startLine = start - 1, endLine = t.Line - 1, kind = "region" });
            }
        }
        return ranges.ToArray();
    }

    private object? Format(string uri)
    {
        var (doc, _) = DocFor(uri);
        if (doc == null) return null;
        string formatted = Formatter.Format(_openTexts.GetValueOrDefault(uri, doc.Text));
        return new[]
        {
            new
            {
                range = new { start = new { line = 0, character = 0 }, end = new { line = int.MaxValue, character = int.MaxValue } },
                newText = formatted
            }
        };
    }

    private object[] CodeActions(string uri, Workspace.Position pos)
    {
        var (doc, path) = DocFor(uri);
        if (doc == null) return Array.Empty<object>();
        var actions = new List<object>();

        foreach (var d in doc.Diags.Where(d => d.File == path
            && Math.Abs(d.Line - pos.Line) <= 0))
        {
            var m = System.Text.RegularExpressions.Regex.Match(d.Message, @"undefined variable '(\w+)'");
            if (m.Success)
            {
                var sug = _workspace.SuggestName(m.Groups[1].Value, path);
                if (sug != null && sug != m.Groups[1].Value)
                {
                    actions.Add(new
                    {
                        title = $"Did you mean '{sug}'?",
                        kind = "quickfix",
                        diagnostics = new[] { new { message = d.Message, range = Range(d.Line, d.Col, d.Col + m.Groups[1].Value.Length), source = Source } },
                        edit = new
                        {
                            changes = new Dictionary<string, object>
                            {
                                [PathToUri(path)] = new[]
                                {
                                    new
                                    {
                                        range = Range(d.Line, d.Col, d.Col + m.Groups[1].Value.Length),
                                        newText = sug
                                    }
                                }
                            }
                        }
                    });
                }
            }

            var np = System.Text.RegularExpressions.Regex.Match(d.Message, @"'(\w+)' is not public in '(.+)'");
            if (np.Success)
            {
                var name = np.Groups[1].Value;
                var decl = doc.Decls.FirstOrDefault(x => x.Name == name && !x.Public && x.Line == d.Line)
                    ?? doc.Decls.FirstOrDefault(x => x.Name == name && !x.Public);
                if (decl != null)
                {
                    actions.Add(new
                    {
                        title = $"Make '{name}' public",
                        kind = "quickfix",
                        diagnostics = new[] { new { message = d.Message, range = Range(d.Line, d.Col, d.Col + name.Length), source = Source } },
                        edit = new
                        {
                            changes = new Dictionary<string, object>
                            {
                                [PathToUri(decl.File)] = new[]
                                {
                                    new
                                    {
                                        range = Range(decl.Line, decl.Col, decl.Col),
                                        newText = "public "
                                    }
                                }
                            }
                        }
                    });
                }
            }

            var unknown = System.Text.RegularExpressions.Regex.Match(d.Message, @"(?:undefined variable|unknown function|unknown type) '(\w+)'");
            if (unknown.Success)
            {
                var name = unknown.Groups[1].Value;
                var hit = PublicIndex().FirstOrDefault(t => t.Decl.Name == name
                    && !doc.LoadedFiles.Contains(t.File) && t.File != doc.EntryPath);
                if (hit.File != null)
                {
                    string dir = Path.GetDirectoryName(doc.EntryPath) ?? ".";
                    string rel = Path.GetRelativePath(dir, hit.File).Replace('\\', '/');
                    if (rel.StartsWith("..")) rel = hit.File.Replace('\\', '/');
                    string importText = rel.EndsWith(".hs", StringComparison.OrdinalIgnoreCase) ? rel : rel + ".hs";

                    int insertLine = 1;
                    if (doc.Program != null)
                    {
                        foreach (var s in doc.Program.Stmts)
                            if (s is ImportStmt im)
                                insertLine = Math.Max(insertLine, im.Line + 1);
                    }

                    actions.Add(new
                    {
                        title = $"import {importText};",
                        kind = "quickfix",
                        diagnostics = new[] { new { message = d.Message, range = Range(d.Line, d.Col, d.Col + name.Length), source = Source } },
                        edit = new
                        {
                            changes = new Dictionary<string, object>
                            {
                                [PathToUri(doc.EntryPath)] = new[]
                                {
                                    new
                                    {
                                        range = Range(insertLine, 1, 1),
                                        newText = $"import {importText};\n"
                                    }
                                }
                            }
                        }
                    });
                }
            }
        }

        return actions.ToArray();
    }

}

