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
    private int[] SemanticTokens(string uri)
    {
        var (doc, path) = DocFor(uri);
        if (doc == null) return Array.Empty<int>();

        var toks = new List<(int Line, int Col, int Len, int Type)>();
        foreach (var o in doc.Occs.Where(o => o.File == path))
        {
            int ty = o.Kind switch
            {
                "var" or "param" => 8,
                "field" => 9,
                "enummember" => 5,
                _ => -1
            };
            if (ty >= 0) toks.Add((o.Line, o.Col, o.Len, ty));
        }
        foreach (var c in doc.Calls.Where(c => c.File == path))
        {
            bool userCall = c.Fn != null && c.Container == null;
            toks.Add((c.NameLine, c.NameCol, c.Name.Length, userCall ? 6 : 7));
        }

        toks.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Col.CompareTo(b.Col));

        var data = new List<int>();
        int pl = 0, pc = 0;
        foreach (var (l, c, len, ty) in toks)
        {

            int zl = l - 1, zc = c - 1;
            if (zl == pl && zc == pc) continue;
            data.Add(zl - pl);
            data.Add(zl == pl ? zc - pc : zc);
            data.Add(len);
            data.Add(ty);
            data.Add(0);
            pl = zl; pc = zc;
        }
        return data.ToArray();
    }

    private object[] InlayHints(string uri)
    {
        var (doc, path) = DocFor(uri);
        if (doc == null) return Array.Empty<object>();
        var hints = new List<object>();

        if (doc.Program != null)
        {
            foreach (var v in Workspace.InferredVars(doc.Program))
            {
                var occ = doc.Occs.FirstOrDefault(o => o.File == path && o.IsDecl
                    && o.Kind == "var" && o.Line == v.Line && o.Col == v.Col);

                if (occ?.Ty == null || occ.Ty.Name == "?" || Ty.IsHandle(occ.Ty)) continue;
                hints.Add(new
                {
                    position = new { line = v.Line - 1, character = v.Col - 1 + v.Name.Length },
                    label = $": {occ.Ty}",
                    kind = 1
                });
            }
        }

        foreach (var c in doc.Calls.Where(c => c.File == path && c.Fn != null))
        {
            var fn = c.Fn!;
            for (int i = 0; i < c.ArgLines.Count && i < fn.Params.Count; i++)
            {
                var p = fn.Params[i];
                hints.Add(new
                {
                    position = new { line = c.ArgLines[i] - 1, character = c.ArgCols[i] - 1 },
                    label = $"{p.Name}:",
                    kind = 2
                });
            }
        }

        return hints.ToArray();
    }

}

