namespace HSharp;

sealed class Sym
{
    public string Name = "";
    public Ty Ty = Ty.Int;
    public bool Owned;       // owns a heap buffer: string/list local or move param
    public bool BorrowParam; // string/list param borrowed from the caller
    public bool Moved;       // definitely moved out
    public bool MaybeMoved;  // moved on some path only
    public bool LoopScoped;  // declared inside the current loop body
}

sealed class WalkResult
{
    // variables this statement read, either directly or through nested blocks
    public HashSet<Sym> Used = new(ReferenceEqualityComparer.Instance);
    public List<string> DropsAfter = new();
}

// types the program and tracks who owns what. rejects anything that could
// double free or use a freed value, then plants Drop statements at the points
// where values die so codegen doesn't have to reason about lifetimes at all
public sealed class Checker
{
    private static readonly HashSet<string> Builtins = new() { "print", "input", "len", "copy", "read", "write", "exists", "delete", "mem" };

    private readonly Dictionary<string, FnDecl> _fns = new();
    private List<Dictionary<string, Sym>> _scopes = new();
    private int _loopDepth, _branchDepth;
    private FnDecl? _fn;

    public void Check(AstProgram program)
    {
        foreach (var s in program.Stmts)
            if (s is FnDecl f)
            {
                if (Builtins.Contains(f.Name) || f.Name.StartsWith("hs_") || f.Name == "main")
                    throw new SourceError(f.Line, f.Col, $"'{f.Name}' is a reserved name");
                if (_fns.ContainsKey(f.Name))
                    throw new SourceError(f.Line, f.Col, $"function '{f.Name}' is already defined");
                _fns[f.Name] = f;
            }

        // signatures first so declaration order and mutual recursion work
        foreach (var s in program.Stmts)
            if (s is FnDecl f)
                CheckFn(f);

        _fn = null;
        _scopes = new List<Dictionary<string, Sym>> { new() };
        WalkStmts(program.Stmts);
    }

    private void CheckFn(FnDecl f)
    {
        _fn = f;
        _scopes = new List<Dictionary<string, Sym>> { new() };

        foreach (var p in f.Params)
        {
            if (p.Move && !p.Type.Owned)
                throw new SourceError(p.Line, p.Col, "'move' is only valid for string and list parameters");
            if (_scopes[0].ContainsKey(p.Name))
                throw new SourceError(p.Line, p.Col, $"duplicate parameter '{p.Name}'");
            _scopes[0][p.Name] = new Sym { Name = p.Name, Ty = p.Type, BorrowParam = p.Type.Owned && !p.Move, Owned = p.Move };
        }

        WalkStmts(f.Body);
    }

    private Sym Find(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var s)) return s;
        return null!;
    }

    private static void Err(int line, int col, string msg) => throw new SourceError(line, col, msg);

    // walks a block, remembers each local's last use, then afterwards inserts
    // Drop statements after those uses plus one at the scope end. uses picked
    // up inside nested blocks count as uses of the containing statement, so a
    // free never lands inside a branch or loop body for an outer variable
    private HashSet<Sym> WalkStmts(List<Stmt> block, Sym? preDecl = null)
    {
        var scope = new Dictionary<string, Sym>();
        var declared = new List<Sym>();
        var lastUse = new Dictionary<Sym, int>(ReferenceEqualityComparer.Instance);
        var usedOuter = new HashSet<Sym>(ReferenceEqualityComparer.Instance);
        var pendings = new List<(int index, string name)>();

        _scopes.Add(scope);

        if (preDecl != null)
        {
            scope[preDecl.Name] = preDecl;
            declared.Add(preDecl);
        }

        for (int i = 0; i < block.Count; i++)
        {
            var r = WalkStmt(block[i]);

            foreach (var s in _scopes[^1].Values)
                if (!declared.Contains(s)) declared.Add(s);

            foreach (var s in r.Used)
            {
                if (declared.Contains(s)) lastUse[s] = i;
                else usedOuter.Add(s);
            }

            foreach (var name in r.DropsAfter)
                pendings.Add((i + 1, name));
        }

        foreach (var g in lastUse)
            if (g.Key.Owned)
                pendings.Add((g.Value + 1, g.Key.Name));

        var scopeDrops = declared.Where(s => s.Owned).Select(s => s.Name).Reverse().ToList();
        _scopes.RemoveAt(_scopes.Count - 1);

        // descending so earlier insertions don't shift later indexes
        foreach (var g in pendings.GroupBy(p => p.index).OrderByDescending(g => g.Key))
            block.Insert(g.Key, new Drop(g.Select(p => p.name).ToList(), 0, 0));

        if (scopeDrops.Count > 0)
            block.Add(new Drop(scopeDrops, 0, 0));

        return usedOuter;
    }

    private WalkResult WalkStmt(Stmt stmt)
    {
        var r = new WalkResult();

        switch (stmt)
        {
            case VarDecl d:
                {
                    var ty = WalkExpr(d.Init, r.Used);
                    if (ty == Ty.Void) Err(d.Line, d.Col, "cannot assign a void value");
                    if (_scopes[^1].ContainsKey(d.Name))
                        Err(d.Line, d.Col, $"'{d.Name}' is already declared in this scope");
                    if (d.Ann != null && !TyMatches(d.Ann, ty))
                        Err(d.Line, d.Col, $"cannot initialize '{d.Name}' of type {d.Ann} with a {ty} value");

                    ConsumeOwned(d.Init);
                    CheckListCopy(d.Init, d.Line, d.Col);

                    var sym = new Sym { Name = d.Name, Ty = d.Ann ?? ty, Owned = (d.Ann ?? ty).Owned, LoopScoped = _loopDepth > 0 };
                    _scopes[^1][d.Name] = sym;
                    break;
                }

            case Assign a:
                {
                    var valTy = WalkExpr(a.Value, r.Used);
                    if (valTy == Ty.Void) Err(a.Line, a.Col, "cannot assign a void value");

                    if (a.Target is Ident id)
                    {
                        var sym = Find(id.Name) ?? throw new SourceError(id.Line, id.Col, $"undefined variable '{id.Name}'");
                        if (!TyMatches(sym.Ty, valTy))
                            Err(a.Line, a.Col, $"cannot assign a {valTy} value to '{id.Name}' of type {sym.Ty}");

                        if (a.Op != "=")
                        {
                            if (sym.Ty == Ty.Str)
                            {
                                if (a.Op != "+=" || valTy != Ty.Str)
                                    Err(a.Line, a.Col, $"operator '{a.Op}' is not supported on strings");
                            }
                            else if (sym.Ty.Owned)
                                Err(a.Line, a.Col, $"operator '{a.Op}' is not supported on {sym.Ty}");
                        }

                        // += only reads its source, plain = takes it over
                        if (a.Op == "=")
                        {
                            ConsumeOwned(a.Value);
                            CheckListCopy(a.Value, a.Line, a.Col);
                        }

                        // assignment revives a moved variable
                        sym.Moved = false;
                        sym.MaybeMoved = false;
                        break;
                    }

                    if (a.Target is Index ix)
                    {
                        var listTy = WalkExpr(ix.Target, r.Used);
                        var idxTy = WalkExpr(ix.Idx, r.Used);

                        if (listTy.Elem == null) Err(ix.Line, ix.Col, "only lists can be indexed");
                        if (idxTy != Ty.Int) Err(ix.Line, ix.Col, "list index must be an int");
                        if (!TyMatches(listTy.Elem!, valTy))
                            Err(a.Line, a.Col, $"cannot store a {valTy} in a {listTy}");
                        break;
                    }

                    Err(a.Line, a.Col, "invalid assignment target");
                    break;
                }

            case IncDec inc:
                {
                    var ty = WalkExpr(inc.Target, r.Used);
                    if (ty != Ty.Int) Err(inc.Line, inc.Col, "'++'/'--' requires an int");
                    break;
                }

            case ExprStmt e:
                WalkExpr(e.E, r.Used);
                break;

            case If s:
                {
                    var condTy = WalkExpr(s.Cond, r.Used);
                    if (condTy != Ty.Bool) Err(s.Line, s.Col, "if condition must be a bool");

                    var snap = Snapshot();
                    _branchDepth++;

                    var usedThen = WalkStmts(s.Then);
                    var then = Snapshot();
                    Restore(snap);

                    var usedElse = s.Else != null
                        ? WalkStmts(s.Else)
                        : new HashSet<Sym>(ReferenceEqualityComparer.Instance);
                    MergeBranch(then, Snapshot());
                    _branchDepth--;

                    r.Used.UnionWith(usedThen);
                    r.Used.UnionWith(usedElse);
                    break;
                }

            case While w:
                {
                    var condTy = WalkExpr(w.Cond, r.Used);
                    if (condTy != Ty.Bool) Err(w.Line, w.Col, "while condition must be a bool");

                    // body state is thrown away afterwards, the loop may not run at all
                    var snap = Snapshot();
                    _loopDepth++;
                    var used = WalkStmts(w.Body);
                    _loopDepth--;
                    Restore(snap);

                    r.Used.UnionWith(used);
                    break;
                }

            case For f:
                {
                    // the loop variable outlives the body but not the statement
                    var forScope = new Dictionary<string, Sym>();
                    _scopes.Add(forScope);

                    if (f.Init != null) r.Used.UnionWith(WalkStmt(f.Init).Used);

                    if (f.Cond != null)
                    {
                        var ty = WalkExpr(f.Cond, r.Used);
                        if (ty != Ty.Bool) Err(f.Line, f.Col, "for condition must be a bool");
                    }

                    var snap = Snapshot();
                    _loopDepth++;
                    var used = WalkStmts(f.Body);
                    _loopDepth--;
                    Restore(snap);

                    r.Used.UnionWith(used);

                    if (f.Step != null) r.Used.UnionWith(WalkStmt(f.Step).Used);

                    _scopes.RemoveAt(_scopes.Count - 1);
                    r.DropsAfter.AddRange(forScope.Values.Where(x => x.Owned).Select(x => x.Name).Reverse());
                    break;
                }

            case Foreach fe:
                {
                    var itTy = WalkExpr(fe.Iter, r.Used);
                    if (itTy.Elem == null) Err(fe.Line, fe.Col, "foreach requires a list");
                    if (itTy.Elem != Ty.Int && itTy.Elem != Ty.Str)
                        Err(fe.Line, fe.Col, $"foreach over {itTy} is not supported yet");

                    // fresh copy of the element every iteration, freed with the body
                    var loopVar = new Sym { Name = fe.Var, Ty = itTy.Elem!, Owned = itTy.Elem!.Owned, LoopScoped = true };
                    var snap = Snapshot();
                    _loopDepth++;
                    var used = WalkStmts(fe.Body, loopVar);
                    _loopDepth--;
                    Restore(snap);

                    r.Used.UnionWith(used);
                    break;
                }

            case Return ret:
                {
                    if (_fn == null) Err(ret.Line, ret.Col, "'return' outside of a function");

                    if (ret.Value == null)
                    {
                        if (_fn!.Ret != Ty.Void) Err(ret.Line, ret.Col, "function must return a value");
                        break;
                    }

                    var ty = WalkExpr(ret.Value, r.Used);
                    if (_fn!.Ret == Ty.Void) Err(ret.Line, ret.Col, "void function cannot return a value");
                    if (!TyMatches(_fn.Ret, ty)) Err(ret.Line, ret.Col, $"cannot return a {ty} from a function returning {_fn.Ret}");

                    if (ret.Value is Ident rid)
                    {
                        var sym = Find(rid.Name) ?? throw new SourceError(rid.Line, rid.Col, $"undefined variable '{rid.Name}'");
                        if (sym.BorrowParam) Err(ret.Line, ret.Col, $"cannot return borrowed value '{rid.Name}'; use copy()");
                        if (sym.Owned)
                        {
                            if (sym.Moved || sym.MaybeMoved) Err(ret.Line, ret.Col, $"use of moved value '{rid.Name}'");
                            sym.Moved = true;
                        }
                    }
                    break;
                }

            case TryCatch tc:
                {
                    var snap = Snapshot();
                    _branchDepth++;
                    var usedTry = WalkStmts(tc.Try);
                    var mid = Snapshot();
                    Restore(snap);

                    // a runtime error can jump to catch from any point in the try
                    // body, so a value moved anywhere in it looks maybe-moved there
                    foreach (var (s, m, mm) in mid)
                    {
                        var before = snap.First(x => ReferenceEquals(x.Item1, s));
                        s.Moved = before.Item2;
                        s.MaybeMoved = before.Item3 || m || mm;
                    }
                    _branchDepth--;

                    var usedCatch = WalkStmts(tc.Catch);

                    r.Used.UnionWith(usedTry);
                    r.Used.UnionWith(usedCatch);
                    break;
                }

            case BlockStmt b:
                r.Used.UnionWith(WalkStmts(b.Body));
                break;
        }

        return r;
    }

    private static bool TyMatches(Ty target, Ty value) =>
        target == value || (target == Ty.Float && value == Ty.Int);

    private List<(Sym, bool, bool)> Snapshot() =>
        _scopes.SelectMany(s => s.Values).Select(s => (s, s.Moved, s.MaybeMoved)).ToList();

    private void Restore(List<(Sym, bool, bool)> snap)
    {
        foreach (var (s, m, mm) in snap) { s.Moved = m; s.MaybeMoved = mm; }
    }

    // after an if, a variable counts as moved only if both branches moved it.
    // one branch is enough to make later uses "possibly moved" and rejected
    private void MergeBranch(List<(Sym, bool, bool)> a, List<(Sym, bool, bool)> b)
    {
        foreach (var (s, am, amm) in a)
        {
            var match = b.First(x => ReferenceEquals(x.Item1, s));
            s.Moved = am && match.Item2;
            s.MaybeMoved = am != match.Item2 || amm || match.Item3;
        }
    }

    // storing a value into a variable takes over its source: a move when the
    // source is an owned variable, an implicit copy otherwise
    private void ConsumeOwned(Expr e)
    {
        if (e is not Ident id) return;

        var sym = Find(id.Name);
        if (sym == null || !sym.Owned) return;

        if (sym.Moved || sym.MaybeMoved)
            throw new SourceError(id.Line, id.Col, $"use of moved value '{id.Name}'");

        // next iteration would read a freed buffer
        if (_loopDepth > 0 && !sym.LoopScoped)
            throw new SourceError(id.Line, id.Col, $"cannot move '{id.Name}' inside a loop");

        sym.Moved = true;
        if (_branchDepth > 0) sym.MaybeMoved = true;
    }

    // strings copy fine at ownership boundaries, lists have no deep copy yet
    private void CheckListCopy(Expr e, int line, int col)
    {
        if (e is not Ident id) return;

        var sym = Find(id.Name);
        if (sym != null && sym.BorrowParam && sym.Ty.Elem != null)
            Err(line, col, $"cannot copy borrowed list '{id.Name}'");
    }

    private Ty WalkExpr(Expr e, HashSet<Sym> uses)
    {
        switch (e)
        {
            case IntLit: return Ty.Int;
            case FloatLit: return Ty.Float;
            case BoolLit: return Ty.Bool;
            case StrLit: return Ty.Str;

            case InterpLit it:
                {
                    foreach (var p in it.Parts)
                    {
                        var ty = WalkExpr(p, uses);
                        if (ty.Elem != null) Err(it.Line, it.Col, "cannot embed a list in an interpolated string");
                        if (ty == Ty.Void) Err(it.Line, it.Col, "cannot embed a void value");
                    }
                    return Ty.Str;
                }

            case Ident id:
                {
                    var sym = Find(id.Name);
                    if (sym == null) Err(id.Line, id.Col, $"undefined variable '{id.Name}'");
                    if (sym.Moved || sym.MaybeMoved)
                        Err(id.Line, id.Col, $"use of moved value '{id.Name}'");
                    uses.Add(sym);
                    return sym.Ty;
                }

            case Un u:
                {
                    var ty = WalkExpr(u.E, uses);
                    if (u.Op == "!")
                    {
                        if (ty != Ty.Bool) Err(u.Line, u.Col, "'!' requires a bool");
                        return Ty.Bool;
                    }
                    if (ty != Ty.Int && ty != Ty.Float) Err(u.Line, u.Col, "unary '-' requires a number");
                    return ty;
                }

            case Bin b:
                return WalkBin(b, uses);

            case Index ix:
                {
                    var target = WalkExpr(ix.Target, uses);
                    var idx = WalkExpr(ix.Idx, uses);

                    if (target.Elem == null) Err(ix.Line, ix.Col, "only lists can be indexed");
                    if (idx != Ty.Int) Err(ix.Line, ix.Col, "list index must be an int");
                    return target.Elem!;
                }

            case Call c:
                return WalkCall(c, uses);

            case Method m:
                return WalkMethod(m, uses);

            case Prop p:
                {
                    var target = WalkExpr(p.Target, uses);
                    if (target.Elem == null) Err(p.Line, p.Col, $"'{p.Name}' is only available on lists");
                    if (p.Name == "Count") return Ty.Int;
                    Err(p.Line, p.Col, $"unknown member '{p.Name}'");
                    return Ty.Int;
                }

            case ListLit ll:
                {
                    if (ll.ElemTy != Ty.Int && ll.ElemTy != Ty.Str)
                        Err(ll.Line, ll.Col, $"{ll.ElemTy} lists are not supported yet");

                    foreach (var item in ll.Items)
                    {
                        var ty = WalkExpr(item, uses);
                        if (!TyMatches(ll.ElemTy, ty))
                            Err(ll.Line, ll.Col, $"{ll.ElemTy} list cannot hold a {ty} value");
                    }
                    return Ty.List(ll.ElemTy);
                }

            default:
                Err(e.Line, e.Col, "unsupported expression");
                return Ty.Int;
        }
    }

    private Ty WalkBin(Bin b, HashSet<Sym> uses)
    {
        var lt = WalkExpr(b.L, uses);
        var rt = WalkExpr(b.R, uses);

        switch (b.Op)
        {
            case "&&":
            case "||":
                if (lt != Ty.Bool || rt != Ty.Bool) Err(b.Line, b.Col, $"'{b.Op}' requires bool operands");
                return Ty.Bool;

            case "==":
            case "!=":
                if (lt.Elem != null || rt.Elem != null) Err(b.Line, b.Col, "lists cannot be compared");
                if (lt == Ty.Str && rt == Ty.Str) return Ty.Bool;
                if (lt == Ty.Str || rt == Ty.Str) Err(b.Line, b.Col, "cannot compare a string with a non-string");
                if (lt == Ty.Bool && rt == Ty.Bool) return Ty.Bool;
                if (lt == Ty.Bool || rt == Ty.Bool) Err(b.Line, b.Col, "cannot compare a bool with a non-bool");
                if (lt != Ty.Int && lt != Ty.Float) Err(b.Line, b.Col, $"cannot compare {lt} and {rt}");
                return Ty.Bool;

            case "<":
            case "<=":
            case ">":
            case ">=":
                if (lt == Ty.Str || rt == Ty.Str) Err(b.Line, b.Col, "strings only support '==' and '!='");
                if (lt == Ty.Bool || rt == Ty.Bool || lt.Elem != null || rt.Elem != null)
                    Err(b.Line, b.Col, $"'{b.Op}' requires numbers");
                if (lt != Ty.Int && lt != Ty.Float) Err(b.Line, b.Col, $"'{b.Op}' requires numbers");
                return Ty.Bool;

            case "+":
                if (lt == Ty.Str || rt == Ty.Str)
                {
                    if (lt == Ty.Bool || rt == Ty.Bool || lt.Elem != null || rt.Elem != null)
                        Err(b.Line, b.Col, "cannot concatenate these types");
                    return Ty.Str;
                }
                goto Numeric;

            case "-":
            case "*":
            case "/":
            case "%":
                if (lt == Ty.Str || rt == Ty.Str) Err(b.Line, b.Col, $"'{b.Op}' is not supported on strings");
                if (lt == Ty.Bool || rt == Ty.Bool) Err(b.Line, b.Col, $"'{b.Op}' is not supported on bools");
                if (lt.Elem != null || rt.Elem != null) Err(b.Line, b.Col, $"'{b.Op}' is not supported on lists");
                goto Numeric;

            Numeric:
                if (lt != Ty.Int && lt != Ty.Float) Err(b.Line, b.Col, $"'{b.Op}' requires numbers, got {lt}");
                if (rt != Ty.Int && rt != Ty.Float) Err(b.Line, b.Col, $"'{b.Op}' requires numbers, got {rt}");
                return lt == Ty.Float || rt == Ty.Float ? Ty.Float : Ty.Int;

            default:
                Err(b.Line, b.Col, $"unknown operator '{b.Op}'");
                return Ty.Int;
        }
    }

    private Ty WalkCall(Call c, HashSet<Sym> uses)
    {
        switch (c.Name)
        {
            case "print":
                if (c.Args.Count != 1) Err(c.Line, c.Col, "print takes one argument");
                {
                    var ty = WalkExpr(c.Args[0], uses);
                    if (ty == Ty.Void) Err(c.Line, c.Col, "cannot print a void value");
                    if (ty.Elem != null) Err(c.Line, c.Col, "cannot print a list");
                }
                return Ty.Void;

            case "input": StrArgs(c, uses, 1); return Ty.Str;

            case "len":
                if (c.Args.Count != 1) Err(c.Line, c.Col, "len takes one argument");
                {
                    var ty = WalkExpr(c.Args[0], uses);
                    if (ty != Ty.Str && ty.Elem == null) Err(c.Line, c.Col, "len requires a string or a list");
                }
                return Ty.Int;

            case "copy": StrArgs(c, uses, 1); return Ty.Str;
            case "read": StrArgs(c, uses, 1); return Ty.Str;
            case "write": StrArgs(c, uses, 2); return Ty.Void;
            case "exists": StrArgs(c, uses, 1); return Ty.Bool;
            case "delete": StrArgs(c, uses, 1); return Ty.Void;

            case "mem":
                if (c.Args.Count != 0) Err(c.Line, c.Col, "mem takes no arguments");
                return Ty.Int;
        }

        return WalkUserCall(c, uses);
    }

    private Ty WalkUserCall(Call c, HashSet<Sym> uses)
    {
        if (!_fns.TryGetValue(c.Name, out var fn))
            Err(c.Line, c.Col, $"unknown function '{c.Name}'");
        if (c.Args.Count != fn!.Params.Count)
            Err(c.Line, c.Col, $"'{c.Name}' takes {fn.Params.Count} argument(s), got {c.Args.Count}");

        for (int i = 0; i < c.Args.Count; i++)
        {
            var argTy = WalkExpr(c.Args[i], uses);
            var p = fn.Params[i];

            if (!TyMatches(p.Type, argTy))
                Err(c.Line, c.Col, $"argument {i + 1} of '{c.Name}': expected {p.Type}, got {argTy}");

            if (!p.Move) continue;

            ConsumeOwned(c.Args[i]);

            // a move param needs a real owner to take from: literals are static
            // and borrows still belong to someone else
            if (c.Args[i] is Ident aid)
            {
                var sym = Find(aid.Name);
                if (sym.BorrowParam)
                    Err(aid.Line, aid.Col, $"cannot move borrowed value '{aid.Name}' into '{c.Name}'");
            }
            else if (c.Args[i] is not (Call or InterpLit))
                Err(c.Line, c.Col, $"argument {i + 1} of '{c.Name}' must be an owned value");
        }

        return fn.Ret;
    }

    private void StrArgs(Call c, HashSet<Sym> uses, int want)
    {
        if (c.Args.Count != want) Err(c.Line, c.Col, $"{c.Name} takes {want} argument(s)");

        foreach (var a in c.Args)
        {
            var ty = WalkExpr(a, uses);
            if (ty != Ty.Str) Err(c.Line, c.Col, $"{c.Name} requires string arguments");
        }
    }

    private Ty WalkMethod(Method m, HashSet<Sym> uses)
    {
        var target = WalkExpr(m.Target, uses);
        if (target.Elem == null)
            Err(m.Line, m.Col, $"'{m.Name}' is only available on lists");

        switch (m.Name)
        {
            case "Add":
                {
                    if (m.Args.Count != 1) Err(m.Line, m.Col, "Add takes one argument");
                    var ty = WalkExpr(m.Args[0], uses);
                    if (!TyMatches(target.Elem!, ty))
                        Err(m.Line, m.Col, $"{target} cannot hold a {ty} value");
                    return Ty.Void;
                }

            case "Remove":
                {
                    if (m.Args.Count != 1) Err(m.Line, m.Col, "Remove takes one argument");
                    var ty = WalkExpr(m.Args[0], uses);
                    if (!TyMatches(target.Elem!, ty))
                        Err(m.Line, m.Col, $"Remove expects a {target.Elem} value");
                    return Ty.Void;
                }

            case "Clear":
                if (m.Args.Count != 0) Err(m.Line, m.Col, "Clear takes no arguments");
                return Ty.Void;

            default:
                Err(m.Line, m.Col, $"unknown list member '{m.Name}'");
                return Ty.Void;
        }
    }
}
