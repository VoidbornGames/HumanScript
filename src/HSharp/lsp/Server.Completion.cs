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
    private List<object> Completion(string uri, Workspace.Position pos)
    {
        var (doc, path) = DocFor(uri);
        var items = new List<object>();
        if (doc == null) return items;
        string text = _openTexts.GetValueOrDefault(uri, doc.Text);

        var ctx = Workspace.Classify(text, pos);
        string? expected = ExpectedTypeAt(doc, path, ctx, pos);

        return ctx.Kind switch
        {
            CompletionCtxKind.None => items,
            CompletionCtxKind.MemberAccess => MemberCompletion(doc, path, ctx, items, text),
            CompletionCtxKind.ImportPath => ImportCompletions(doc, path, items),
            CompletionCtxKind.TypePosition => TypeCompletion(doc, path, items),
            CompletionCtxKind.LambdaParam => LambdaParamCompletion(doc, path, ctx, items),
            CompletionCtxKind.InitializerField => InitializerCompletion(doc, ctx, items),
            CompletionCtxKind.DeclName or CompletionCtxKind.CatchVar => items,
            CompletionCtxKind.LockTarget => AddLocals(doc, path, pos, items, expected),
            CompletionCtxKind.ForeachIterable => AddIterables(doc, path, pos, items),
            CompletionCtxKind.CallArg => AddValues(doc, path, pos, items, expected, allowLiterals: false),
            CompletionCtxKind.StatementStart => AddStatementStart(doc, path, pos, items),
            _ => AddValues(doc, path, pos, items, expected, allowLiterals: true)
        };
    }

    private static Dictionary<string, object> Item(string label, int kind, string? detail = null,
        string? sort = null, string? docs = null)
    {
        var d = new Dictionary<string, object> { ["label"] = label, ["kind"] = kind };
        if (detail != null) d["detail"] = detail;
        d["sortText"] = sort ?? "2_" + label;
        if (docs != null) d["documentation"] = docs;
        return d;
    }

    private IEnumerable<Occ> LocalsAt(AnalysisDoc doc, string path, Workspace.Position pos)
    {
        var seen = new HashSet<string>();
        foreach (var o in doc.Occs)
        {
            if (o.File != path || !o.IsDecl || o.Kind is not ("var" or "param")) continue;
            if (o.Line > pos.Line || (o.Line == pos.Line && o.Col >= pos.Col)) continue;
            if (doc.ScopeEnds.TryGetValue(o, out var end) && pos.Line > end) continue;
            if (!seen.Add(o.Name))
                continue;
            yield return o;
        }
    }

    private static bool TyMatchesExpected(string? candidateTy, string? expected)
    {
        if (expected == null || candidateTy == null) return false;
        if (candidateTy == expected) return true;

        return expected.TrimEnd('?') == candidateTy || (expected == "float" && candidateTy == "int");
    }

    private List<object> MemberCompletion(AnalysisDoc doc, string path, CompletionContext ctx, List<object> items, string text)
    {
        string? target = ctx.MemberTarget;

        string? tyName = null;
        if (target != null)
        {
            var occ = Workspace.LastOccBefore(doc, path, target, new Workspace.Position(ctx.Line, ctx.Col));
            tyName = occ?.Ty?.Name;
        }
        if (tyName == null && ctx.ReceiverEnd > 0)
            tyName = Workspace.InferTyName(text, ctx.ReceiverEnd, doc, path,
                new Workspace.Position(ctx.Line, ctx.Col));
        tyName = tyName?.TrimEnd('?');

        if (tyName == null && target == null) return items;

        if (tyName != null && tyName.StartsWith("static:"))
        {
            string cls = tyName["static:".Length..];
            foreach (var m in RuntimeApi.StaticMembers[cls])
            {
                var sig = RuntimeApi.StaticSignatures.GetValueOrDefault($"{cls}.{m}");
                items.Add(Item(m, 2, sig is { } s ? s[0].Sig : null, "1_" + m,
                    sig is { } s2 ? s2[0].Doc : null));
            }
            return items;
        }

        if (tyName != null && RuntimeApi.HandleMembers.TryGetValue(tyName, out var hm))
        {
            foreach (var m in hm)
                items.Add(Item(m, 2, RuntimeApi.HandleSignatures.GetValueOrDefault($"{tyName}.{m}") is { } hs ? hs[0] : null, "1_" + m));
            return items;
        }

        if (tyName == "string")
        {
            foreach (var kv in RuntimeApi.StringMembers)
                items.Add(Item(kv.Key, 2, kv.Value[0], "1_" + kv.Key));
            return items;
        }
        if (tyName != null && tyName.StartsWith("list<"))
        {
            bool strElem = tyName == "list<string>";
            foreach (var mem in RuntimeApi.ListMembers)
            {
                if (mem == "Join" && !strElem) continue;
                if ((mem == "Sort" || mem == "Reverse") && !(strElem || tyName.Contains("int") || tyName.Contains("float"))) continue;
                items.Add(Item(mem, mem == "Count" ? 5 : 2, RuntimeApi.ListSignatures[mem], "1_" + mem));
            }
            return items;
        }
        if (tyName != null && tyName.StartsWith("map<"))
        {
            var keyTy = Workspace.MapKeyTy(tyName);
            var valTy = Workspace.MapValueTy(tyName);
            items.Add(Item("Count", 5, "int Count", "1_Count"));
            items.Add(Item("Keys", 5, $"list<{keyTy}> Keys", "1_Keys"));
            items.Add(Item("Values", 5, $"list<{valTy}> Values", "1_Values"));
            items.Add(Item("Contains", 2, "bool Contains(key)", "1_Contains"));
            items.Add(Item("Remove", 2, "void Remove(key)", "1_Remove"));
            items.Add(Item("Clear", 2, "void Clear()", "1_Clear"));
            return items;
        }

        if (tyName != null)
        {

            foreach (var d in doc.Decls.Where(d => d.Container == tyName && d.Kind is "method" or "field"))
            {
                if (d.Kind == "method" && d.File != path && !d.Public) continue;
                items.Add(Item(d.Name, d.Kind == "method" ? 2 : 5, d.Signature, "1_" + d.Name));
            }
            return items;
        }

        if (Workspace.DeclOf(doc, target) is { } td)
        {

            foreach (var d in doc.Decls.Where(d => d.Container == td.Name))
            {
                if (d.Kind == "method" && d.File != path && !d.Public) continue;
                items.Add(Item(d.Name, d.Kind == "method" ? 2 : 5, d.Signature, "1_" + d.Name));
            }
            return items;
        }

        if (RuntimeApi.StaticMembers.TryGetValue(target, out var sm))
        {
            foreach (var m in sm)
            {
                var sig = RuntimeApi.StaticSignatures.GetValueOrDefault($"{target}.{m}");
                items.Add(Item(m, 2, sig is { } s ? s[0].Sig : null, "1_" + m,
                    sig is { } s2 ? s2[0].Doc : null));
            }
            return items;
        }

        if (RuntimeApi.HandleMembers.TryGetValue(target, out var facade))
        {
            foreach (var m in facade)
                items.Add(Item(m, 2, RuntimeApi.HandleSignatures.GetValueOrDefault($"{target}.{m}") is { } fs ? fs[0] : null, "1_" + m));
            return items;
        }

        var enums = doc.Decls.Where(d => d.Kind == "enum" && d.Name == target).ToList();
        if (enums.Count > 0)
        {
            foreach (var d in doc.Decls.Where(d => d.Container == target && d.Kind == "enummember"))
                items.Add(Item(d.Name, 20, d.Signature, "1_" + d.Name, d.Detail));
        }
        return items;
    }

    private List<object> ImportCompletions(AnalysisDoc doc, string path, List<object> items)
    {
        foreach (var rel in WorkspaceFilesRelatively(doc))
            items.Add(Item(rel, 17, "import this file", "1_" + rel));
        return items;
    }

    private List<object> TypeCompletion(AnalysisDoc doc, string path, List<object> items)
    {
        foreach (var t in new[] { "int", "float", "bool", "string", "void", "list", "map", "task", "buffer" })
            items.Add(Item(t, 22, null, "1_" + t));
        foreach (var h in new[] { "Client", "listener", "httpl", "rawhttpl", "udp", "HttpPacket", "RawHttpPacket", "StringBuilder" })
            items.Add(Item(h, 22, "runtime handle", "1_" + h));
        foreach (var d in _workspace.VisibleDecls(path).Where(d => d.Kind is "class" or "struct" or "enum"))
            items.Add(Item(d.Name, d.Kind == "class" ? 7 : d.Kind == "struct" ? 22 : 13, d.Signature, "1_" + d.Name));
        return items;
    }

    private static readonly Dictionary<string, string> OnAcceptParam = new()
    {
        ["rawhttpl"] = "RawHttpPacket",
        ["httpl"] = "HttpPacket",
        ["listener"] = "Client",
    };

    private List<object> InitializerCompletion(AnalysisDoc doc, CompletionContext ctx, List<object> items)
    {
        if (ctx.InitializerType == null) return items;
        foreach (var d in doc.Decls.Where(d => d.Container == ctx.InitializerType && d.Kind == "field"))
            items.Add(Item(d.Name, 5, d.Signature, "0_" + d.Name));
        return items;
    }

    private List<object> LambdaParamCompletion(AnalysisDoc doc, string path, CompletionContext ctx, List<object> items)
    {
        string? preferred = null;
        if (ctx.MemberTarget != null)
        {
            var occ = Workspace.LastOccBefore(doc, path, ctx.MemberTarget, new Workspace.Position(ctx.Line, ctx.Col));
            string? ty = occ?.Ty?.Name;
            if (ty != null && OnAcceptParam.TryGetValue(ty, out var want))
            {
                preferred = want;
                items.Add(Item(want, 22, "lambda parameter type", "0_" + want));
            }
        }

        foreach (var h in new[] { "RawHttpPacket", "HttpPacket", "Client", "StringBuilder" })
        {
            if (h == preferred) continue;
            items.Add(Item(h, 22, "runtime handle", "1_" + h));
        }
        foreach (var d in _workspace.VisibleDecls(path).Where(d => d.Kind is "class" or "struct" or "enum"))
            items.Add(Item(d.Name, d.Kind == "class" ? 7 : d.Kind == "struct" ? 22 : 13, d.Signature, "1_" + d.Name));
        return items;
    }

    private List<object> AddLocals(AnalysisDoc doc, string path, Workspace.Position pos, List<object> items, string? expected)
    {
        foreach (var o in LocalsAt(doc, path, pos))
        {
            string ty = o.Ty?.Name ?? "";
            string sort = (TyMatchesExpected(ty, expected) ? "0_" : "1_") + o.Name;
            items.Add(Item(o.Name, 6, ty, sort));
        }
        return items;
    }

    private List<object> AddIterables(AnalysisDoc doc, string path, Workspace.Position pos, List<object> items)
    {
        foreach (var o in LocalsAt(doc, path, pos))
        {
            if (o.Ty?.Elem == null) continue;
            items.Add(Item(o.Name, 6, o.Ty.Name, "0_" + o.Name));
        }
        foreach (var d in _workspace.VisibleDecls(path).Where(d => d.Kind == "function" && d.RetTy?.Elem != null))
            items.Add(Item(d.Name, 3, d.Signature, "1_" + d.Name));
        foreach (var b in new[] { "args" })
            items.Add(Item(b, 3, BuiltinInfo.Info[b].Sig, "1_" + b));
        return items;
    }

    private List<object> AddValues(AnalysisDoc doc, string path, Workspace.Position pos, List<object> items,
        string? expected, bool allowLiterals)
    {
        foreach (var o in LocalsAt(doc, path, pos))
        {
            string ty = o.Ty?.Name ?? "";
            string sort = (TyMatchesExpected(ty, expected) ? "0_" : "1_") + o.Name;
            items.Add(Item(o.Name, 6, ty, sort));
        }

        foreach (var d in _workspace.VisibleDecls(path).Where(d => d.Kind is "function" or "enum" or "class" or "struct"))
        {
            int kind = d.Kind switch { "function" => 3, "class" => 7, "struct" => 22, "enum" => 13, _ => 9 };
            string sort = (TyMatchesExpected(d.RetTy?.Name, expected) ? "0_" : "1_") + d.Name;
            items.Add(Item(d.Name, kind, d.Signature, sort));
        }

        foreach (var b in BuiltinInfo.Names)
        {
            var info = BuiltinInfo.Info[b];
            string sort = (TyMatchesExpected(BuiltinInfo.ReturnType(b), expected) ? "0_" : "1_") + b;
            items.Add(Item(b, 3, info.Sig, sort, info.Doc));
        }

        foreach (var sc in RuntimeApi.StaticClasses)
            items.Add(Item(sc, 7, "static entry points", "1_" + sc));

        if (allowLiterals)
            foreach (var k in new[] { "null", "true", "false" })
                items.Add(Item(k, 14, null, "3_" + k));

        return items;
    }

    private List<object> AddStatementStart(AnalysisDoc doc, string path, Workspace.Position pos, List<object> items)
    {
        AddLocals(doc, path, pos, items, null);

        foreach (var d in _workspace.VisibleDecls(path).Where(d => d.Kind is "function" or "class" or "struct" or "enum"))
        {
            int kind = d.Kind switch { "function" => 3, "class" => 7, "struct" => 22, _ => 13 };
            items.Add(Item(d.Name, kind, d.Signature, "1_" + d.Name));
        }

        foreach (var b in BuiltinInfo.Names)
            items.Add(Item(b, 3, BuiltinInfo.Info[b].Sig, "1_" + b, BuiltinInfo.Info[b].Doc));

        foreach (var sc in RuntimeApi.StaticClasses)
            items.Add(Item(sc, 7, "static entry points", "1_" + sc));

        foreach (var t in new[] { "int", "float", "bool", "string", "void", "list", "map", "task", "buffer" })
            items.Add(Item(t, 22, null, "2_" + t));

        foreach (var k in new[]
        {
            "var", "if", "else", "while", "for", "foreach", "switch", "return", "try", "catch",
            "break", "continue", "await", "lock", "import", "public", "new"
        })
            items.Add(Item(k, 14, null, "2_" + k));

        void Snippet(string label, string body, string detail) =>
            items.Add(Item(label, 15, detail, "4_" + label, body));

        Snippet("class", "class ${1:Name}\n{\n\t${2:int} ${3:Field};\n\n\t${4:public string Method() { return $0; }}\n}", "class declaration");
        Snippet("struct", "struct ${1:Name}\n{\n\t${2:int} ${3:X};\n}", "struct declaration");
        Snippet("enum", "enum ${1:Name}\n{\n\t${2:A},\n\t${3:B}\n}", "enum declaration");
        Snippet("fn", "${1:void} ${2:Name}(${3})\n{\n\t$0\n}", "function");
        Snippet("if", "if (${1:cond})\n{\n\t$0\n}", "if block");
        Snippet("ifelse", "if (${1:cond})\n{\n\t$0\n}\nelse\n{\n}", "if/else");
        Snippet("while", "while (${1:cond})\n{\n\t$0\n}", "while loop");
        Snippet("for", "for (var ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++)\n{\n\t$0\n}", "for loop");
        Snippet("foreach", "foreach (var ${1:x} in ${2:list})\n{\n\t$0\n}", "foreach loop");
        Snippet("trycatch", "try\n{\n\t$0\n}\ncatch (e)\n{\n\tprint(e);\n}", "try/catch");
        Snippet("var", "var ${1:name} = ${2:value};", "var declaration");

        return items;
    }

    private static string? ExpectedTypeAt(AnalysisDoc doc, string path, CompletionContext ctx, Workspace.Position pos)
    {
        if (ctx.Kind == CompletionCtxKind.CallArg && ctx.Callee != null)
        {

            var cs = doc.Calls.FirstOrDefault(c => c.Name == ctx.Callee && c.NameLine == ctx.CalleeLine);
            FnDecl? fn = cs?.Fn;
            if (fn == null)
            {
                var decl = doc.Decls.FirstOrDefault(d => d.Name == ctx.Callee && d.Fn != null);
                fn = decl?.Fn;
            }
            if (fn != null && ctx.ArgIndex < fn.Params.Count)
                return fn.Params[ctx.ArgIndex].Type.Name;

            var pts = BuiltinInfo.ParamTypes(ctx.Callee);
            if (ctx.ArgIndex < pts.Count) return pts[ctx.ArgIndex];
            return null;
        }

        if (ctx.AssignTarget != null)
        {
            Occ? best = null;
            foreach (var o in doc.Occs)
            {
                if (o.Name != ctx.AssignTarget || o.Ty == null) continue;
                if (o.Line > pos.Line || (o.Line == pos.Line && o.Col > pos.Col)) continue;
                if (best == null || o.Line > best.Line || (o.Line == best.Line && o.Col > best.Col))
                    best = o;
            }
            if (best != null)
            {
                var ty = best.Ty!;
                if (ctx.AssignThroughIndex)
                {
                    if (Ty.IsMap(ty)) return ty.Elem?.Name;
                    return ty.Elem?.Name;
                }
                return ty.Name;
            }
            return null;
        }

        if (ctx.InitializerType != null && ctx.InitializerField != null)
        {
            var f = doc.Decls.FirstOrDefault(d => d.Container == ctx.InitializerType
                && d.Name == ctx.InitializerField && d.Kind == "field");
            return f?.Ty?.Name;
        }

        if (ctx.Kind == CompletionCtxKind.Expression)
        {
            var fns = doc.Decls.Where(d => d.Kind is "function" or "method" && d.File == path)
                .OrderBy(d => d.Line).ToList();
            for (int i = 0; i < fns.Count; i++)
            {
                if (fns[i].Line > pos.Line) break;
                int end = i + 1 < fns.Count ? fns[i + 1].Line : int.MaxValue;
                if (pos.Line >= fns[i].Line && pos.Line < end)
                {
                    var rt = fns[i].RetTy;
                    if (rt != null && rt != Ty.Void) return rt.Name;
                }
            }
        }
        return null;
    }

}

