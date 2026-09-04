using HSharp.Checking;
using HSharp.Lexing;
using HSharp.Parsing;
using HSharp.Syntax;

namespace HSharp.Analysis;

public sealed partial class Workspace
{
    public static void ComputeScopeEnds(AnalysisDoc doc)
    {
        doc.ScopeEnds.Clear();
        List<Token> toks;
        try
        {
            toks = new Lexer(doc.Text).Tokenize();
        }
        catch (SourceError)
        {
            return;
        }

        var st = new Stack<(int Line, int Col)>();
        var pairs = new List<((int Line, int Col) Open, int CloseLine)>();
        foreach (var t in toks)
        {
            if (t.Kind == Tok.LBrace) st.Push((t.Line, t.Col));
            else if (t.Kind == Tok.RBrace && st.Count > 0)
                pairs.Add((st.Pop(), t.Line));
        }

        foreach (var o in doc.Occs)
        {
            if (!o.IsDecl || o.Kind is not ("var" or "param") || o.File != doc.EntryPath)
                continue;

            int? bestEnd = null;
            foreach (var (openPos, closeLine) in pairs)
            {
                bool before = openPos.Line < o.Line || (openPos.Line == o.Line && openPos.Col < o.Col);
                bool after = closeLine > o.Line || (closeLine == o.Line && o.Col <= closeLine);
                if (before && after && (bestEnd == null || closeLine < bestEnd))
                    bestEnd = closeLine;
            }

            doc.ScopeEnds[o] = bestEnd ?? int.MaxValue;
        }
    }

    // resolves the type of the expression ending at tokenEnd by walking
    // tokens backward: calls through signature tables, indexers through
    // element types, member chains through recursion. returns type NAME
    // strings, not Ty, because only the name is needed for completion
    public static string? InferTyName(string text, int tokenEnd, AnalysisDoc doc, string path, Position pos)
    {
        List<Token> toks;
        try
        {
            toks = new Lexer(text) { Tolerant = true }.Tokenize();
        }
        catch (SourceError)
        {
            return null;
        }
        if (tokenEnd <= 0 || tokenEnd > toks.Count) return null;
        return InferTy(toks, 0, tokenEnd, doc, path, pos, 0);
    }

    private static string? InferTy(List<Token> toks, int start, int end, AnalysisDoc doc, string path, Position pos, int depth)
    {
        if (depth > 16 || end <= start) return null;
        var last = toks[end - 1];

        if (last.Kind == Tok.RParen)
        {
            int open = MatchBack(toks, end - 1, Tok.RParen, Tok.LParen);
            if (open < 0 || open - 1 < start) return null;

            if (toks[open - 1].Kind == Tok.Ident)
            {
                string name = toks[open - 1].Text;
                string? recv = null;
                if (open - 2 >= start && toks[open - 2].Kind is Tok.Dot or Tok.QuestionDot)
                    recv = InferTy(toks, start, open - 2, doc, path, pos, depth + 1);
                return CallReturn(name, recv, doc);
            }

            return InferTy(toks, open + 1, end - 1, doc, path, pos, depth + 1);
        }

        if (last.Kind == Tok.RBracket)
        {
            int open = MatchBack(toks, end - 1, Tok.RBracket, Tok.LBracket);
            if (open < 0 || open - 1 < start) return null;
            var baseTy = InferTy(toks, start, open, doc, path, pos, depth + 1);
            if (baseTy == null) return null;
            if (baseTy.StartsWith("list<")) return ElemOf(baseTy);
            if (baseTy == "buffer") return "int";
            return null;
        }

        if (last.Kind == Tok.Ident)
        {
            string name = last.Text;
            if (end - 2 >= start && toks[end - 2].Kind is Tok.Dot or Tok.QuestionDot)
            {
                var recv = InferTy(toks, start, end - 2, doc, path, pos, depth + 1);
                return MemberTy(recv, name, doc);
            }
            var occ = LastOccBefore(doc, path, name, pos);
            if (occ?.Ty != null) return occ.Ty.Name;
            if (RuntimeApi.IsStaticClass(name)) return "static:" + name;
            return null;
        }
        return null;
    }

    private static int MatchBack(List<Token> toks, int close, Tok closeKind, Tok openKind)
    {
        int depth = 0;
        for (int i = close; i >= 0; i--)
        {
            if (toks[i].Kind == closeKind) depth++;
            else if (toks[i].Kind == openKind)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static string ElemOf(string listTy)
    {
        int a = listTy.IndexOf('<');
        int b = listTy.LastIndexOf('>');
        return a >= 0 && b > a ? listTy[(a + 1)..b] : "";
    }

    private static string? CallReturn(string name, string? recv, AnalysisDoc doc)
    {
        if (recv == null)
        {
            var call = doc.Calls.LastOrDefault(c => c.Name == name && c.Fn != null);
            if (call?.Fn != null)
            {
                var decl = doc.Decls.FirstOrDefault(d => ReferenceEquals(d.Fn, call.Fn));
                if (decl?.RetTy != null) return decl.RetTy.Name;
            }
            var rt = BuiltinInfo.ReturnType(name);
            return rt.Length > 0 ? rt : null;
        }
        if (recv.StartsWith("static:"))
        {
            var cls = recv["static:".Length..];
            return SigHead(RuntimeApi.StaticSignatures.GetValueOrDefault($"{cls}.{name}") is { } s0 ? s0[0].Sig : null);
        }
        if (RuntimeApi.HandleMembers.ContainsKey(recv))
            return SigHead(RuntimeApi.HandleSignatures.GetValueOrDefault($"{recv}.{name}") is { } h0 ? h0[0] : null);
        return MemberTy(recv, name, doc);
    }

    private static string? MemberTy(string? recv, string name, AnalysisDoc doc)
    {
        if (recv == null) return null;
        if (recv == "string")
            return RuntimeApi.StringMembers.TryGetValue(name, out var sa) ? SigHead(sa[0]) : null;
        if (RuntimeApi.HandleMembers.ContainsKey(recv))
        {
            if (name == "Cookies" && recv == "HttpPacket") return "Cookies";
            return SigHead(RuntimeApi.HandleSignatures.GetValueOrDefault($"{recv}.{name}") is { } hs ? hs[0] : null);
        }
        if (recv.StartsWith("list<"))
        {
            return name switch
            {
                "Count" => "int",
                "Contains" => "bool",
                "IndexOf" => "int",
                "Sort" or "Reverse" => "void",
                "Join" when recv == "list<string>" => "string",
                _ => null
            };
        }
        if (recv.StartsWith("map<"))
        {
            return name switch
            {
                "Count" => "int",
                "Keys" => $"list<{MapKeyTy(recv)}>",
                "Values" => $"list<{MapValueTy(recv)}>",
                _ => null
            };
        }

        var d = doc.Decls.FirstOrDefault(x => x.Container == recv && x.Name == name);
        if (d != null) return (d.Ty ?? d.RetTy)?.Name;
        return null;
    }

    // "map<string, int>" -> "string"
    public static string MapKeyTy(string mapTy)
    {
        int a = mapTy.IndexOf('<'), comma = mapTy.IndexOf(',');
        return a >= 0 && comma > a ? mapTy[(a + 1)..comma].Trim() : "";
    }

    public static string MapValueTy(string mapTy)
    {
        int a = mapTy.IndexOf('<'), comma = mapTy.IndexOf(','), b = mapTy.LastIndexOf('>');
        return a >= 0 && comma > a && b > comma ? mapTy[(comma + 1)..b].Trim() : "";
    }

    private static string? SigHead(string? sig)
    {
        if (sig == null) return null;
        int sp = sig.IndexOf(' ');
        return sp < 0 ? "" : sig[..sp];
    }

    public static List<DeclInfo> ScanDecls(string path, string text)    {
        var doc = new AnalysisDoc { EntryPath = path, Text = text };
        try
        {
            var program = new Parser(new Lexer(text).Tokenize()).Parse();
            CollectDecls(doc, program, path);
        }
        catch (SourceError)
        {
            return new List<DeclInfo>();
        }
        return doc.Decls;
    }

    public static List<Diag> UnusedWarnings(AnalysisDoc doc)
    {
        var result = new List<Diag>();
        foreach (var decl in doc.Occs.Where(o => o.IsDecl && o.Kind is "var" or "param"))
        {
            if (decl.Name == "_") continue;
            bool used = doc.Occs.Any(o => !o.IsDecl && o.Name == decl.Name
                && o.DeclLine == decl.DeclLine && o.DeclCol == decl.DeclCol);
            if (used) continue;
            string what = decl.Kind == "param" ? "parameter" : "variable";
            result.Add(new Diag(decl.File, decl.Line, decl.Col,
                $"{what} '{decl.Name}' is declared but never used", 2));
        }
        return result;
    }

    private static int EditDistance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3) return 99;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    public string? SuggestName(string misspelled, string file)
    {
        string? best = null;
        int bestDist = 3;
        foreach (var d in VisibleDecls(file).Where(d => d.Kind != "field"))
        {
            int dist = EditDistance(misspelled, d.Name);
            if (dist < bestDist) { bestDist = dist; best = d.Name; }
        }
        if (best != null) return best;

        var doc = DocForFile(file);
        if (doc == null) return null;
        foreach (var o in doc.Occs.Where(o => o.Kind is "var" or "param").Select(o => o.Name).Distinct())
        {
            int dist = EditDistance(misspelled, o);
            if (dist < bestDist) { bestDist = dist; best = o; }
        }
        return best;
    }
}

