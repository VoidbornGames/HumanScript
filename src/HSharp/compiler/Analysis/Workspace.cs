using HSharp.Checking;
using HSharp.Lexing;
using HSharp.Parsing;
using HSharp.Syntax;

namespace HSharp.Analysis;

// analysis over a set of open entry files: tolerant parse, recovering
// check, and every query the editor makes. goes through the real pipeline,
// never a parallel guesser, so completions agree with what hsc accepts
public sealed partial class Workspace
{
    private readonly Dictionary<string, AnalysisDoc> _docs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<AnalysisDoc> Docs => _docs.Values;

    public List<AnalysisDoc> Update(string entryPath, string? text)
    {
        var full = Path.GetFullPath(entryPath);
        if (text == null)
        {
            _docs.Remove(full);
            return new List<AnalysisDoc>();
        }

        var doc = Analyze(full, text);
        _docs[full] = doc;

        var affected = new List<AnalysisDoc> { doc };
        foreach (var other in _docs.Values.ToList())
        {
            if (other.EntryPath == full) continue;
            if (other.LoadedFiles.Contains(full))
            {
                var redone = Analyze(other.EntryPath, other.Text);
                _docs[other.EntryPath] = redone;
                affected.Add(redone);
            }
        }
        return affected;
    }

    public void Close(string entryPath) => _docs.Remove(Path.GetFullPath(entryPath));

    public AnalysisDoc? Doc(string entryPath) => _docs.GetValueOrDefault(Path.GetFullPath(entryPath));

    public AnalysisDoc? DocForFile(string file)
    {
        var full = Path.GetFullPath(file);
        return _docs.Values.FirstOrDefault(d => d.EntryPath == full)
               ?? _docs.Values.FirstOrDefault(d => d.LoadedFiles.Contains(full));
    }

    public static AnalysisDoc Analyze(string entryPath, string text)
    {

        string full;
        try { full = Path.GetFullPath(entryPath); }
        catch (Exception) { full = entryPath; }
        var doc = new AnalysisDoc { EntryPath = full, Text = text };

        AstProgram? program = null;
        try
        {
            program = Imports.LoadTolerant(full, text);
        }
        catch (SourceError e)
        {
            doc.Diags.Add(new Diag(e.File ?? full, e.Line, e.Col, e.Message));
        }
        catch (Exception)
        {

        }

        foreach (var pe in Imports.ParseErrors)
            doc.Diags.Add(new Diag(pe.File ?? full, pe.Line, pe.Col, pe.Message));
        doc.ParseErrorCount = doc.Diags.Count;

        doc.LoadedFiles.UnionWith(LoadedFiles);

        if (program == null) return doc;
        doc.Program = program;

        var checker = new Checker { Recover = true, Record = true };
        try
        {
            checker.Check(program);
        }
        catch (SourceError)
        {

        }
        foreach (var e in checker.Errors)
            doc.Diags.Add(new Diag(e.File ?? full, e.Line, e.Col, e.Message));

        doc.Occs.AddRange(checker.Occs);
        doc.Calls.AddRange(checker.Calls);

        foreach (var o in doc.Occs)
            if (o.File == "") o.File = full;
        foreach (var c in doc.Calls)
            if (c.File == "") c.File = full;
        foreach (var d in doc.Decls)
        {
            if (d.File == "") d.File = full;
            if (d.OwnFile) d.File = full;
        }
        ComputeScopeEnds(doc);
        CollectDecls(doc, program, full);
        return doc;
    }

    private static HashSet<string> LoadedFiles => (HashSet<string>)Imports.LastLoadedFiles;

    private static void CollectDecls(AnalysisDoc doc, AstProgram program, string entryPath)
    {
        foreach (var s in program.Stmts)
        {
            switch (s)
            {
                case FnDecl f:
                    doc.Decls.Add(new DeclInfo
                    {
                        Name = f.Name,
                        Kind = "function",
                        Signature = Sig(f.Ret, f.Name, f.Params, f.TPs),
                        File = f.SourceFile ?? entryPath,
                        Line = f.Line,
                        Col = f.Col,
                        Public = f.Public,
                        OwnFile = f.SourceFile == null,
                        RetTy = f.Ret,
                        Fn = f
                    });
                    break;

                case EnumDecl e:
                    AddEnum(doc, e, entryPath);
                    break;

                case TypeDecl t:
                    AddType(doc, t, entryPath);
                    break;

                case ImportStmt:
                    break;

                default:

                    break;
            }
        }
    }

    private static void AddEnum(AnalysisDoc doc, EnumDecl e, string entryPath)
    {
        doc.Decls.Add(new DeclInfo
        {
            Name = e.Name,
            Kind = "enum",
            Signature = $"enum {e.Name}",
            File = e.SourceFile ?? entryPath,
            Line = e.Line,
            Col = e.Col,
            Public = e.Public,
            OwnFile = e.SourceFile == null
        });
        foreach (var m in e.Members)
            doc.Decls.Add(new DeclInfo
            {
                Name = m.Name,
                Kind = "enummember",
                Signature = $"{e.Name}.{m.Name}",
                Detail = $"enum member of {e.Name} (value {m.Value})",
                File = e.SourceFile ?? entryPath,
                Line = m.Line,
                Col = m.Col,
                Public = e.Public,
                OwnFile = e.SourceFile == null,
                Container = e.Name
            });
    }

    private static void AddType(AnalysisDoc doc, TypeDecl t, string entryPath)
    {
        doc.Decls.Add(new DeclInfo
        {
            Name = t.Name,
            Kind = t.Kind == UserKind.Struct ? "struct" : "class",
            Signature = $"{t.Kind.ToString().ToLower()} {t.Name}",
            File = t.SourceFile ?? entryPath,
            Line = t.Line,
            Col = t.Col,
            Public = t.Public,
            OwnFile = t.SourceFile == null
        });
        foreach (var f in t.Fields)
            doc.Decls.Add(new DeclInfo
            {
                Name = f.Name,
                Kind = "field",
                Signature = $"{f.Type} {f.Name}",
                Ty = f.Type,
                Detail = $"field of {t.Name}",
                File = t.SourceFile ?? entryPath,
                Line = f.Line,
                Col = f.Col,
                Public = false,
                OwnFile = t.SourceFile == null,
                Container = t.Name
            });
        foreach (var m in t.Methods)
            doc.Decls.Add(new DeclInfo
            {
                Name = m.Name,
                Kind = "method",
                Signature = Sig(m.Ret, m.Name, m.Params, m.TPs),
                File = m.SourceFile ?? t.SourceFile ?? entryPath,
                Line = m.Line,
                Col = m.Col,
                Public = m.Public,
                OwnFile = t.SourceFile == null,
                Container = t.Name,
                RetTy = m.Ret,
                Fn = m
            });
    }

    private static string Sig(Ty ret, string name, List<Param> ps, List<string> tps)
    {
        var gen = tps.Count > 0 ? $"<{string.Join(", ", tps)}>" : "";
        var args = string.Join(", ", ps.Select(p => $"{p.Type} {p.Name}" + (p.Move ? " [move]" : "")));
        return $"{ret} {name}{gen}({args})";
    }

    public static string SigPublic(FnDecl f) => Sig(f.Ret, f.Name, f.Params, f.TPs);

    public record struct Position(int Line, int Col);

    public static Occ? OccAt(AnalysisDoc doc, string file, Position pos)
    {
        return doc.Occs.FirstOrDefault(o => o.File == file
            && o.Line == pos.Line && pos.Col >= o.Col && pos.Col < o.Col + o.Len);
    }

    public static CallSite? CallAt(AnalysisDoc doc, string file, Position pos)
    {

        return doc.Calls.LastOrDefault(c => c.File == file
            && (c.NameLine < pos.Line || (c.NameLine == pos.Line && c.NameCol <= pos.Col))
            && ArgsReach(c, pos));
    }

    private static bool ArgsReach(CallSite c, Position pos)
    {
        if (c.ArgLines.Count == 0)
            return pos.Line == c.NameLine;
        int last = c.ArgLines.Count - 1;
        int endLine = c.ArgLines[last];
        int endCol = c.ArgCols[last] + 1;
        return pos.Line < endLine || (pos.Line == endLine && pos.Col <= endCol + 200);
    }

    public static (string Word, Position Start)? WordAt(string text, Position pos)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (pos.Line - 1 < 0 || pos.Line - 1 >= lines.Length) return null;
        var l = lines[pos.Line - 1];
        int end = Math.Min(pos.Col, l.Length);
        int start = end;
        while (start > 0 && (char.IsLetterOrDigit(l[start - 1]) || l[start - 1] == '_')) start--;
        while (end < l.Length && (char.IsLetterOrDigit(l[end]) || l[end] == '_')) end++;
        if (start == end) return null;
        return (l[start..end], new Position(pos.Line, start + 1));
    }

    public static string? MemberTargetBefore(string text, Position pos)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (pos.Line - 1 < 0 || pos.Line - 1 >= lines.Length) return null;
        var l = lines[pos.Line - 1];
        int end = Math.Min(pos.Col - 1, l.Length);
        var before = l[..end];
        var m = System.Text.RegularExpressions.Regex.Match(before, @"([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static Occ? LastOccBefore(AnalysisDoc doc, string file, string name, Position pos)
    {
        Occ? best = null;
        foreach (var o in doc.Occs)
        {
            if (o.Name != name || o.Ty == null) continue;
            if (o.Line > pos.Line || (o.Line == pos.Line && o.Col > pos.Col)) continue;
            if (best == null || o.Line > best.Line || (o.Line == best.Line && o.Col > best.Col))
                best = o;
        }
        return best;
    }

    public static DeclInfo? DeclOf(AnalysisDoc doc, string name) =>
        doc.Decls.LastOrDefault(d => d.Name == name && d.Kind is "class" or "struct");

    public IEnumerable<DeclInfo> AllDecls => _docs.Values.SelectMany(d => d.Decls);

    public IEnumerable<DeclInfo> VisibleDecls(string file)
    {
        var full = Path.GetFullPath(file);
        return AllDecls.Where(d => d.File == full || d.Public).Distinct();
    }

    public List<Position> RenameTargets(string file, Position pos, string uri)
    {
        var results = new List<Position>();
        var doc = DocForFile(file);
        if (doc == null) return results;

        var occ = OccAt(doc, file, pos);
        if (occ != null)
        {
            foreach (var o in doc.Occs)
                if (SameAnchor(o, occ))
                    results.Add(new Position(o.Line, o.Col));
            return results;
        }

        var word = WordAt(doc.Text, pos);
        if (word == null) return results;
        string name = word.Value.Word;

        var call = doc.Calls.FirstOrDefault(c => c.Name == name && c.NameLine == pos.Line
            && pos.Col >= c.NameCol && pos.Col < c.NameCol + name.Length);
        if (call?.Fn != null)
        {
            var fn = call.Fn;
            results.Add(new Position(fn.Line, fn.Col));
            foreach (var c in doc.Calls.Where(c => ReferenceEquals(c.Fn, fn)))
                results.Add(new Position(c.NameLine, c.NameCol));
            foreach (var other in _docs.Values)
                foreach (var c in other.Calls.Where(c => ReferenceEquals(c.Fn, fn)))
                    if (!results.Contains(new Position(c.NameLine, c.NameCol)))
                        results.Add(new Position(c.NameLine, c.NameCol));
            return results;
        }

        var decl = doc.Decls.FirstOrDefault(d => d.Name == name && d.Kind is "class" or "struct" or "enum"
            && d.Line == pos.Line);
        if (decl != null)
        {
            results.Add(new Position(decl.Line, decl.Col));
            return results;
        }

        return results;
    }

    private static bool SameAnchor(Occ a, Occ b)
    {
        if (a.IsDecl || b.IsDecl)
            return a.DeclLine == b.DeclLine && a.DeclCol == b.DeclCol && a.Name == b.Name
                && a.DeclFile == b.DeclFile;
        return a.DeclLine == b.DeclLine && a.DeclCol == b.DeclCol && a.Name == b.Name
            && a.DeclFile == b.DeclFile && a.Kind == b.Kind;
    }

    public List<(string File, Position Pos)> References(string file, Position pos)
    {
        var results = new List<(string, Position)>();
        var doc = DocForFile(file);
        if (doc == null) return results;

        var occ = OccAt(doc, file, pos);
        if (occ != null)
        {
            foreach (var o in doc.Occs)
                if (SameAnchor(o, occ))
                    results.Add((o.File, new Position(o.Line, o.Col)));
            return results;
        }

        var word = WordAt(doc.Text, pos);
        if (word == null) return results;
        string name = word.Value.Word;

        var call = doc.Calls.FirstOrDefault(c => c.Name == name && c.NameLine == pos.Line
            && pos.Col >= c.NameCol && pos.Col < c.NameCol + name.Length);
        if (call?.Fn != null)
        {
            var fn = call.Fn;
            results.Add((fn.SourceFile ?? doc.EntryPath, new Position(fn.Line, fn.Col)));
            foreach (var other in _docs.Values)
                foreach (var c in other.Calls.Where(c => ReferenceEquals(c.Fn, fn)))
                    results.Add((c.File, new Position(c.NameLine, c.NameCol)));
        }

        return results;
    }

    public static List<Occ> InferredVars(AstProgram program)
    {
        var list = new List<Occ>();
        void WalkStmt(Stmt s)
        {
            switch (s)
            {
                case VarDecl d:
                    if (d.Ann == null) list.Add(new Occ { Name = d.Name, Line = d.NameLine > 0 ? d.NameLine : d.Line, Col = d.NameLine > 0 ? d.NameCol : d.Col });
                    break;
                case If f:
                    foreach (var x in f.Then) WalkStmt(x);
                    if (f.Else != null) foreach (var x in f.Else) WalkStmt(x);
                    break;
                case While w:
                    foreach (var x in w.Body) WalkStmt(x);
                    break;
                case For f:
                    if (f.Init != null) WalkStmt(f.Init);
                    foreach (var x in f.Body) WalkStmt(x);
                    break;
                case Foreach fe:
                    list.Add(new Occ { Name = fe.Var, Line = fe.NameLine > 0 ? fe.NameLine : fe.Line, Col = fe.NameLine > 0 ? fe.NameCol : fe.Col });
                    foreach (var x in fe.Body) WalkStmt(x);
                    break;
                case BlockStmt b:
                    foreach (var x in b.Body) WalkStmt(x);
                    break;
                case Lock lk:
                    foreach (var x in lk.Body) WalkStmt(x);
                    break;
                case TryCatch tc:
                    foreach (var x in tc.Try) WalkStmt(x);
                    foreach (var x in tc.Catch) WalkStmt(x);
                    break;
                case FnDecl fd:
                    foreach (var x in fd.Body) WalkStmt(x);
                    break;
                case TypeDecl td:
                    foreach (var m in td.Methods)
                        foreach (var x in m.Body) WalkStmt(x);
                    break;
            }
        }
        foreach (var s in program.Stmts) WalkStmt(s);
        return list;
    }
}

