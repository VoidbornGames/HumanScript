using HSharp.Analysis;
using HSharp.Syntax;
using Index = HSharp.Syntax.Index;

namespace HSharp.Checking;

sealed class Sym
{
    public string Name = "";
    public Ty Ty = Ty.Int;
    public Ty? Decl;
    public bool Owned;
    public bool BorrowParam;
    public bool GenericParam;
    public bool Moved;
    public bool MaybeMoved;
    public bool LoopScoped;
    public int DeclLine, DeclCol;
    public string? DeclFile;
}

sealed class WalkResult
{

    public HashSet<Sym> Used = new(ReferenceEqualityComparer.Instance);
    public List<string> DropsAfter = new();

    public List<string> DropsBefore = new();
}

public sealed class Occ
{
    public string File = "";
    public string Name = "";
    public int Line, Col, Len;
    public string Kind = "";
    public Ty? Ty;
    public bool Owned, Borrow, Generic, Moved;
    public string? DeclFile;
    public int DeclLine, DeclCol;
    public string? Container;
    public bool IsDecl;
}

public sealed class CallSite
{
    public string File = "";
    public string Name = "";
    public string? Container;
    public int NameLine, NameCol;
    public FnDecl? Fn;
    public List<int> ArgLines = new();
    public List<int> ArgCols = new();
}

// walks a whole program once, recording every symbol occurrence and call
// site for the editor while enforcing the ownership rules: single owner,
// move semantics, borrow params that may not escape
public sealed class Checker
{
    private static readonly HashSet<string> Builtins = new() { "print", "input", "len", "copy", "read", "write", "exists", "delete", "mem",
        "clock_ms", "lastError", "args", "env", "exiting", "buffer", "unixtime", "fmttime", "format" };

    private readonly Dictionary<string, FnDecl> _fns = new();
    private readonly Dictionary<string, EnumDecl> _enums = new();
    private readonly Dictionary<string, TypeDecl> _types = new();

    private TypeDecl? _fnType;

    private HashSet<string>? _typeParams;
    private Dictionary<string, Ty>? _subst;
    private AstProgram? _program;

    private string? _curFile;

    public List<SourceError> Errors { get; } = new();
    public bool Recover { get; set; }
    public bool Record { get; set; }
    public List<Occ> Occs { get; } = new();
    public List<CallSite> Calls { get; } = new();
    private const int MaxErrors = 500;

    private List<(Dictionary<string, Sym> Scope, string Key, Sym S, bool Moved, bool MaybeMoved, Ty Ty)> SnapAll()
    {
        var l = new List<(Dictionary<string, Sym>, string, Sym, bool, bool, Ty)>();
        foreach (var sc in _scopes)
            foreach (var kv in sc)
                l.Add((sc, kv.Key, kv.Value, kv.Value.Moved, kv.Value.MaybeMoved, kv.Value.Ty));
        return l;
    }

    private void RestoreAll(List<(Dictionary<string, Sym> Scope, string Key, Sym S, bool Moved, bool MaybeMoved, Ty Ty)> snap)
    {
        foreach (var sc in _scopes) sc.Clear();
        foreach (var it in snap) it.Scope[it.Key] = it.S;
        foreach (var it in snap) { it.S.Moved = it.Moved; it.S.MaybeMoved = it.MaybeMoved; it.S.Ty = it.Ty; }
    }

    private void AddError(SourceError e)
    {
        e.File = _curFile;
        Errors.Add(e);
        if (Errors.Count >= MaxErrors) throw e;
    }

    private Occ RecUse(string name, int line, int col, int len, string kind,
        Sym? sym = null, Ty? ty = null, string? container = null, bool isDecl = false)
    {
        var o = new Occ
        {
            File = _curFile ?? "",
            Name = name,
            Line = line,
            Col = col,
            Len = len,
            Kind = kind,
            Ty = ty ?? sym?.Ty,
            Owned = sym?.Owned ?? false,
            Borrow = sym?.BorrowParam ?? false,
            Generic = sym?.GenericParam ?? false,
            Moved = sym?.Moved ?? false,
            DeclFile = sym?.DeclFile,
            DeclLine = sym?.DeclLine ?? 0,
            DeclCol = sym?.DeclCol ?? 0,
            Container = container,
            IsDecl = isDecl
        };
        Occs.Add(o);
        return o;
    }

    private void RecCall(string name, int line, int col, FnDecl? fn, List<Expr> args, string? container = null)
    {
        if (!Record) return;
        var cs = new CallSite
        {
            File = _curFile ?? "",
            Name = name,
            Container = container,
            NameLine = line,
            NameCol = col,
            Fn = fn
        };
        foreach (var a in args) { cs.ArgLines.Add(a.Line); cs.ArgCols.Add(a.Col); }
        Calls.Add(cs);
    }

    private List<Dictionary<string, Sym>> _scopes = new();
    private int _loopDepth, _branchDepth;
    private FnDecl? _fn;

    private readonly List<int> _loopBase = new();

    private readonly List<Ty?> _lamRet = new();
    private readonly List<int> _lamBase = new();

    private readonly List<int> _lamOuterLoop = new();

    private readonly List<bool> _onAcceptCtx = new();

    private Ty Concrete(Ty ty)
    {
        if (_subst == null || _subst.Count == 0) return ty;
        if (ty.Nullable)
        {
            var inner = Concrete(ty.Elem!);
            return inner == ty.Elem ? ty : Ty.NullableOf(inner);
        }
        if (ty.Elem != null && !Ty.IsTask(ty))
        {
            var e2 = Concrete(ty.Elem!);
            return e2 == ty.Elem ? ty : Ty.List(e2);
        }
        if (Ty.IsUser(ty) && ty.Kind == UserKind.None && _subst!.TryGetValue(ty.Name, out var c))
            return c;
        return ty;
    }

    public void Check(AstProgram program)
    {
        _program = program;
        InjectCookiesBuiltins(program);

        Ty.Handle("StringBuilder").OwnsHeap = true;

        foreach (var s in program.Stmts)
        {
            if (s is EnumDecl e)
            {
                if (Recover) { try { CheckTopName(e.Name, e.Line, e.Col); } catch (SourceError ex) { AddError(ex); continue; } }
                else CheckTopName(e.Name, e.Line, e.Col);
                _enums[e.Name] = e;
                Ty.Named(e.Name).Kind = UserKind.Enum;
            }
            else if (s is TypeDecl td)
            {
                if (Recover) { try { CheckTopName(td.Name, td.Line, td.Col); } catch (SourceError ex) { AddError(ex); continue; } }
                else CheckTopName(td.Name, td.Line, td.Col);
                _types[td.Name] = td;
                Ty.Named(td.Name).Kind = td.Kind;
                foreach (var m in td.Methods)
                {
                    var key = $"{td.Name}.{m.Name}";
                    if (_fns.ContainsKey(key))
                    {
                        var dup = new SourceError(m.Line, m.Col, $"'{m.Name}' is already defined in {td.Name}");
                        if (Recover) AddError(dup);
                        else throw dup;
                        continue;
                    }
                    _fns[key] = m;
                }
            }
        }

        foreach (var td in _types.Values)
        {
            try
            {
                var seen = new HashSet<string>();
                _curFile = td.SourceFile;
                foreach (var f in td.Fields)
                {
                    if (!seen.Add(f.Name))
                        throw new SourceError(f.Line, f.Col, $"duplicate field '{f.Name}' in {td.Name}");
                    CheckTy(f.Type, f.Line, f.Col);
                    if (f.Type.Owned && td.Kind == UserKind.Struct && f.Type.Kind == UserKind.Class)
                        throw new SourceError(f.Line, f.Col, $"struct {td.Name} field '{f.Name}' cannot be an owned class; use a class instead");
                }
                CheckNoRecursiveStruct(td.Name, td);
            }
            catch (SourceError ex) when (Recover)
            {
                AddError(ex);
            }
        }
        foreach (var td in _types.Values)
            Ty.Named(td.Name).OwnsHeap = td.Kind == UserKind.Class || td.Fields.Any(f => f.Type.Owned);

        foreach (var s in program.Stmts)
            if (s is FnDecl f)
            {
                if (Recover)
                {
                    try
                    {
                        if (Builtins.Contains(f.Name) || f.Name.StartsWith("hs_") || f.Name == "main")
                            throw new SourceError(f.Line, f.Col, $"'{f.Name}' is a reserved name");
                        if (_fns.ContainsKey(f.Name) || _enums.ContainsKey(f.Name) || _types.ContainsKey(f.Name))
                            throw new SourceError(f.Line, f.Col, $"function '{f.Name}' is already defined");
                        _fns[f.Name] = f;
                    }
                    catch (SourceError ex) { AddError(ex); }
                }
                else
                {
                    if (Builtins.Contains(f.Name) || f.Name.StartsWith("hs_") || f.Name == "main")
                        throw new SourceError(f.Line, f.Col, $"'{f.Name}' is a reserved name");
                    if (_fns.ContainsKey(f.Name) || _enums.ContainsKey(f.Name) || _types.ContainsKey(f.Name))
                        throw new SourceError(f.Line, f.Col, $"function '{f.Name}' is already defined");
                    _fns[f.Name] = f;
                }
            }

        _curFile = null;
        foreach (var s in program.Stmts)
            if (s is FnDecl f)
            {
                _curFile = f.SourceFile;
                if (Recover) { try { CheckFn(f); } catch (SourceError ex) { AddError(ex); } }
                else CheckFn(f);
            }

        foreach (var s in program.Stmts)
            if (s is TypeDecl td)
                foreach (var m in td.Methods)
                {
                    _curFile = m.SourceFile ?? td.SourceFile;
                    if (Recover) { try { CheckMethod(td, m); } catch (SourceError ex) { AddError(ex); } }
                    else CheckMethod(td, m);
                }

        _fn = null;
        _fnType = null;
        _curFile = null;
        _scopes = new List<Dictionary<string, Sym>> { new() };
        try
        {
            WalkStmts(program.Stmts);
        }
        catch (SourceError ex) when (Recover)
        {
            AddError(ex);
        }
    }

    private void RequireVisible(bool pub, string? sourceFile, int line, int col, string what)
    {
        if (pub || sourceFile == null || sourceFile == _curFile) return;
        Err(line, col, $"{what} is not public in '{Path.GetFileName(sourceFile)}'");
    }

    private static void InjectCookiesBuiltins(AstProgram program)
    {
        bool named(string n) => program.Stmts.Any(s => s switch
        {
            TypeDecl t => t.Name == n,
            EnumDecl e => e.Name == n,
            FnDecl f => f.Name == n,
            _ => false
        });

        if (!named("SameSite"))
            program.Stmts.Insert(0, new EnumDecl("SameSite", new List<EnumMember>
            {
                new("None", 0, 0, 0), new("Lax", 1, 0, 0), new("Strict", 2, 0, 0)
            }, 0, 0, true));

        if (!named("CookieOptions"))
            program.Stmts.Insert(0, new TypeDecl(UserKind.Class, "CookieOptions", new List<Field>
            {
                new(Ty.Bool, "Secure", 0, 0),
                new(Ty.Bool, "HttpOnly", 0, 0),
                new(Ty.Named("SameSite"), "SameSite", 0, 0),
                new(Ty.Str, "Path", 0, 0),
                new(Ty.Str, "Domain", 0, 0),
                new(Ty.Int, "MaxAge", 0, 0)
            }, new List<FnDecl>(), 0, 0, true) { BuiltIn = true });
    }

    private void CheckTopName(string name, int line, int col)
    {
        if (Builtins.Contains(name) || name.StartsWith("hs_") || name == "main")
            throw new SourceError(line, col, $"'{name}' is a reserved name");
        if (_fns.ContainsKey(name) || _enums.ContainsKey(name) || _types.ContainsKey(name))
            throw new SourceError(line, col, $"'{name}' is already defined");
    }

    private void CheckNoRecursiveStruct(string root, TypeDecl td)
    {
        var onPath = new HashSet<string>();
        Visit(td);

        void Visit(TypeDecl t)
        {
            onPath.Add(t.Name);
            foreach (var f in t.Fields)
            {
                if (f.Type.Kind != UserKind.Struct || !_types.TryGetValue(f.Type.Name, out var next))
                    continue;
                if (onPath.Contains(f.Type.Name))
                    throw new SourceError(f.Line, f.Col, $"struct {t.Name} contains itself through field '{f.Name}'");
                Visit(next);
            }
            onPath.Remove(t.Name);
        }
    }

    private void CheckFn(FnDecl f)
    {
        _fn = f;
        _fnType = null;
        _scopes = new List<Dictionary<string, Sym>> { new() };

        var savedTp = _typeParams;
        _typeParams = f.TPs.Count > 0 ? new HashSet<string>(f.TPs) : null;
        CheckTy(f.Ret, f.Line, f.Col);
        AddParams(f.Params);
        _typeParams = savedTp;

        if (f.TPs.Count == 0 || _subst != null)
            WalkStmts(f.Body);
    }

    private void CheckMethod(TypeDecl td, FnDecl m)
    {
        _fn = m;
        _fnType = td;
        _scopes = new List<Dictionary<string, Sym>> { new() };

        var savedTp = _typeParams;
        _typeParams = m.TPs.Count > 0 ? new HashSet<string>(m.TPs) : null;
        CheckTy(m.Ret, m.Line, m.Col);

        var thisTy = Ty.Named(td.Name);
        _scopes[0]["this"] = new Sym
        {
            Name = "this",
            Ty = thisTy,
            Decl = thisTy,
            BorrowParam = td.Kind == UserKind.Class,
            Owned = false
        };

        AddParams(m.Params);
        _typeParams = savedTp;

        if (m.TPs.Count == 0 || _subst != null)
            WalkStmts(m.Body);
        _fnType = null;
    }

    private void CheckGenericBody(FnDecl f, TypeDecl? owner, Dictionary<string, Ty> subst)
    {
        var savedSubst = _subst;
        var savedTp = _typeParams;
        var savedFn = _fn;
        var savedFnType = _fnType;
        var savedFile = _curFile;
        var savedScopes = _scopes;
        var savedLoopDepth = _loopDepth;
        var savedBranchDepth = _branchDepth;
        var savedLoopBase = _loopBase.ToList();
        var savedLamRet = _lamRet.ToList();
        var savedLamBase = _lamBase.ToList();
        var savedLamOuter = _lamOuterLoop.ToList();

        try
        {
            _subst = subst;
            _typeParams = null;

            _curFile = f.SourceFile ?? owner?.SourceFile;

            if (owner != null) CheckMethod(owner, f);
            else CheckFn(f);
        }
        finally
        {
            _subst = savedSubst;
            _typeParams = savedTp;
            _fn = savedFn;
            _fnType = savedFnType;
            _curFile = savedFile;
            _scopes = savedScopes;
            _loopDepth = savedLoopDepth;
            _branchDepth = savedBranchDepth;
            _loopBase.Clear();
            _loopBase.AddRange(savedLoopBase);
            _lamRet.Clear();
            _lamRet.AddRange(savedLamRet);
            _lamBase.Clear();
            _lamBase.AddRange(savedLamBase);
            _lamOuterLoop.Clear();
            _lamOuterLoop.AddRange(savedLamOuter);
        }
    }

    private void AddParams(List<Param> ps)
    {
        foreach (var p in ps)
        {
            var ty = Concrete(p.Type);

            if (p.Move && ty.Kind == UserKind.Struct)
                throw new SourceError(p.Line, p.Col, "'move' has no effect on structs, they copy");
            if (p.Move && !ty.Owned)
                throw new SourceError(p.Line, p.Col, "'move' is only valid for owned parameters");
            if (_scopes[0].ContainsKey(p.Name))
                throw new SourceError(p.Line, p.Col, $"duplicate parameter '{p.Name}'");
            CheckTy(ty, p.Line, p.Col);

            bool isStruct = ty.Kind == UserKind.Struct;
            bool bareGeneric = Ty.IsUser(p.Type) && p.Type.Kind == UserKind.None;
            _scopes[0][p.Name] = new Sym
            {
                Name = p.Name,
                Ty = ty,
                Decl = ty,
                BorrowParam = !isStruct && ty.Owned && !p.Move,
                GenericParam = bareGeneric,
                Owned = p.Move || (isStruct && ty.Owned),
                DeclLine = p.Line,
                DeclCol = p.Col,
                DeclFile = _curFile
            };
            if (Record) RecUse(p.Name, p.Line, p.Col, p.Name.Length, "param", _scopes[0][p.Name], isDecl: true);
        }
    }

    private void CheckTy(Ty ty, int line, int col)
    {
        if (_types.TryGetValue(ty.Name, out var pvt))
            RequireVisible(pvt.Public, pvt.SourceFile, line, col, $"type '{ty.Name}'");
        if (_enums.TryGetValue(ty.Name, out var pve))
            RequireVisible(pve.Public, pve.SourceFile, line, col, $"enum '{ty.Name}'");

        if (Ty.IsUser(ty) && ty.Kind == UserKind.None
            && _typeParams?.Contains(ty.Name) != true
            && _subst?.ContainsKey(ty.Name) != true)
            Err(line, col, $"unknown type '{ty.Name}'");
        if (ty.Elem is { Nullable: true })
            Err(line, col, "lists cannot hold nullable values");
        if (!ty.Nullable) return;

        var inner = ty.Elem!;
        if (inner.Nullable) Err(line, col, "a nullable type cannot be made nullable again");
        if (inner == Ty.Void || Ty.IsTask(inner))
            Err(line, col, $"{inner} cannot be made nullable");
        CheckTy(inner, line, col);
    }

    private Sym Find(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var s)) return s;
        return null!;
    }

    private int OwnerDepth(Sym sym)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].ContainsValue(sym)) return i;
        return -1;
    }

    private static void Err(int line, int col, string msg) => throw new SourceError(line, col, msg);

    private void NarrowBy(Expr cond, bool thenBranch)
    {
        if (cond is not Bin { Op: "==" or "!=" } b) return;
        if (b.L is not NullLit && b.R is not NullLit) return;

        var id = b.L as Ident ?? b.R as Ident;
        if (id == null) return;

        var sym = Find(id.Name);
        if (sym == null || !sym.Ty.Nullable) return;
        if ((b.Op == "!=") == thenBranch) sym.Ty = sym.Ty.Elem!;
    }

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

        try
        {
            for (int i = 0; i < block.Count; i++)
            {
                var snap = Recover ? SnapAll() : null;
                WalkResult r;
                try
                {
                    r = WalkStmt(block[i]);
                }
                catch (SourceError e) when (Recover)
                {

                    AddError(e);
                    if (snap != null) RestoreAll(snap);
                    continue;
                }

                foreach (var s in _scopes[^1].Values)
                    if (!declared.Contains(s)) declared.Add(s);

                foreach (var s in r.Used)
                {
                    if (declared.Contains(s)) lastUse[s] = i;
                    else usedOuter.Add(s);
                }

                foreach (var name in r.DropsAfter)
                    pendings.Add((i + 1, name));

                foreach (var name in r.DropsBefore)
                    pendings.Add((i, name));
            }

            foreach (var g in lastUse)
                if (g.Key.Owned)
                    pendings.Add((g.Value + 1, g.Key.Name));

            var scopeDrops = declared.Where(s => s.Owned).Select(s => s.Name).Reverse().ToList();

            foreach (var g in pendings.GroupBy(p => p.index).OrderByDescending(g => g.Key))
                block.Insert(g.Key, new Drop(g.Select(p => p.name).ToList(), 0, 0));

            if (scopeDrops.Count > 0)
                block.Add(new Drop(scopeDrops, 0, 0));
        }
        finally
        {
            _scopes.RemoveAt(_scopes.Count - 1);
        }

        return usedOuter;
    }

    private WalkResult WalkStmt(Stmt stmt)
    {
        var r = new WalkResult();

        switch (stmt)
        {
            case VarDecl d:
                {
                    if (_scopes[^1].ContainsKey(d.Name))
                        Err(d.Line, d.Col, $"'{d.Name}' is already declared in this scope");
                    var ann = d.Ann == null ? null : Concrete(d.Ann);
                    if (ann != null) CheckTy(ann, d.Line, d.Col);

                    var ty = WalkExpr(d.Init, r.Used);
                    if (ty == Ty.Void) Err(d.Line, d.Col, "cannot assign a void value");
                    if (ann != null && !TyMatches(ann, ty))
                    {
                        if (ty.Nullable && !ann.Nullable)
                            Err(d.Line, d.Col, $"cannot initialize '{d.Name}' of type {ann} with a possibly null {ty}; check it against null first");
                        Err(d.Line, d.Col, $"cannot initialize '{d.Name}' of type {ann} with a {ty} value");
                    }
                    if (ty == Ty.Null && ann == null)
                        Err(d.Line, d.Col, "cannot infer a type from 'null'; annotate the variable");

                    if (d.Init is Ident src && InitFromBorrow(src))
                        Err(d.Line, d.Col, $"cannot move out of borrowed parameter '{src.Name}', use copy({src.Name})");

                    ConsumeOwned(d.Init);
                    CheckListCopy(d.Init, d.Line, d.Col);

                    var declTy = ann ?? ty;
                    var sym = new Sym
                    {
                        Name = d.Name,
                        Ty = declTy,
                        Decl = declTy,
                        Owned = declTy.Owned,
                        LoopScoped = _loopDepth > 0,
                        DeclLine = d.NameLine > 0 ? d.NameLine : d.Line,
                        DeclCol = d.NameLine > 0 ? d.NameCol : d.Col,
                        DeclFile = _curFile
                    };
                    _scopes[^1][d.Name] = sym;
                    if (Record) RecUse(d.Name, sym.DeclLine, sym.DeclCol, d.Name.Length, "var", sym, isDecl: true);
                    break;
                }

            case Assign a:
                {
                    var valTy = WalkExpr(a.Value, r.Used);
                    if (valTy == Ty.Void) Err(a.Line, a.Col, "cannot assign a void value");

                    if (a.Target is Ident bareId && Find(bareId.Name) == null
                        && _fnType != null && FieldIndex(_fnType, bareId.Name) is >= 0 and var bfi)
                    {
                        bareId.ThisField = true;
                        bareId.ThisIndex = bfi;
                        CheckFieldStore(a, _fnType.Fields[bfi].Type, bareId.Name, valTy);
                        break;
                    }

                    if (a.Target is Ident id)
                    {

                        if (id.Name == "_" && Find(id.Name) == null)
                        {
                            WalkExpr(a.Value, r.Used);
                            break;
                        }

                        var sym = Find(id.Name) ?? throw new SourceError(id.Line, id.Col, $"undefined variable '{id.Name}'");

                        if (_lamBase.Count > 0 && OwnerDepth(sym) is >= 0 and var od && od < _lamBase[^1]
                            && !(_fn == null && od < _lamBase[0]))
                            Err(a.Line, a.Col, $"cannot assign to '{id.Name}' from inside a lambda");
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

                        if (a.Op == "=")
                        {
                            if (a.Value is Ident src && InitFromBorrow(src))
                                Err(a.Line, a.Col, $"cannot move out of borrowed parameter '{src.Name}', use copy({src.Name})");

                            ConsumeOwned(a.Value);
                            CheckListCopy(a.Value, a.Line, a.Col);
                        }

                        sym.Moved = false;
                        sym.MaybeMoved = false;
                        break;
                    }

                    if (a.Target is Index ix)
                    {
                        var listTy = WalkExpr(ix.Target, r.Used);
                        var idxTy = WalkExpr(ix.Idx, r.Used);

                        if (Ty.IsMap(listTy))
                        {
                            if (a.Op != "=") Err(a.Line, a.Col, "only '=' works on map entries");
                            if (!TyMatches(listTy.KeyTy!, idxTy))
                                Err(ix.Line, ix.Col, $"map key expects a {listTy.KeyTy}, got {idxTy}");
                            if (!TyMatches(listTy.Elem!, valTy))
                                Err(a.Line, a.Col, $"cannot store a {valTy} in a {listTy}");
                            break;
                        }
                        if (listTy == Ty.Buffer)
                        {
                            if (idxTy != Ty.Int) Err(ix.Line, ix.Col, "buffer index must be an int");
                            if (valTy != Ty.Int) Err(a.Line, a.Col, "buffer elements are bytes (int)");
                            break;
                        }
                        if (listTy.Nullable) Err(ix.Line, ix.Col, "cannot index a possibly null list; check it against null first");
                        if (listTy.Elem == null) Err(ix.Line, ix.Col, "only lists or buffers can be indexed");
                        if (idxTy != Ty.Int) Err(ix.Line, ix.Col, "list index must be an int");
                        if (!TyMatches(listTy.Elem!, valTy))
                            Err(a.Line, a.Col, $"cannot store a {valTy} in a {listTy}");
                        break;
                    }

                    if (a.Target is Prop pr)
                    {
                        CheckFieldStore(a, WalkExpr(pr, r.Used), pr.Name, valTy);
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

                    NarrowBy(s.Cond, true);
                    var usedThen = WalkStmts(s.Then);
                    var then = Snapshot();
                    Restore(snap);

                    NarrowBy(s.Cond, false);
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

                    var snap = Snapshot();
                    _loopDepth++;
                    _loopBase.Add(_scopes.Count);
                    var used = WalkStmts(w.Body);
                    _loopBase.RemoveAt(_loopBase.Count - 1);
                    _loopDepth--;
                    Restore(snap);

                    r.Used.UnionWith(used);
                    break;
                }

                case For f:
                    {

                        var forScope = new Dictionary<string, Sym>();
                        _loopBase.Add(_scopes.Count);
                        _scopes.Add(forScope);

                    try
                    {
                        if (f.Init != null) r.Used.UnionWith(WalkStmt(f.Init).Used);

                        if (f.Cond != null)
                        {
                            var ty = WalkExpr(f.Cond, r.Used);
                            if (ty != Ty.Bool) Err(f.Line, f.Col, "for condition must be a bool");
                        }

                        var snap = Snapshot();
                        _loopDepth++;
                        try
                        {
                            var used = WalkStmts(f.Body);
                            r.Used.UnionWith(used);
                        }
                        finally
                        {
                            _loopDepth--;
                            Restore(snap);
                        }

                        if (f.Step != null) r.Used.UnionWith(WalkStmt(f.Step).Used);
                    }
                    finally
                    {
                        _scopes.RemoveAt(_scopes.Count - 1);
                        _loopBase.RemoveAt(_loopBase.Count - 1);
                    }
                    r.DropsAfter.AddRange(forScope.Values.Where(x => x.Owned).Select(x => x.Name).Reverse());
                    break;
                }

            case Foreach fe:
                {
                    var itTy = WalkExpr(fe.Iter, r.Used);
                    if (itTy.Nullable) Err(fe.Line, fe.Col, "cannot iterate a possibly null list; check it against null first");
                    if (Ty.IsMap(itTy))
                        Err(fe.Line, fe.Col, "cannot iterate a map with foreach yet");
                    if (itTy.Elem == null) Err(fe.Line, fe.Col, "foreach requires a list");
                    if (itTy.Elem != Ty.Int && itTy.Elem != Ty.Str)
                        Err(fe.Line, fe.Col, $"foreach over {itTy} is not supported yet");

                    var loopVar = new Sym { Name = fe.Var, Ty = itTy.Elem!, Decl = itTy.Elem!, Owned = itTy.Elem!.Owned, LoopScoped = true };
                    var snap = Snapshot();
                    _loopDepth++;
                    _loopBase.Add(_scopes.Count);
                    var used = WalkStmts(fe.Body, loopVar);
                    _loopBase.RemoveAt(_loopBase.Count - 1);
                    _loopDepth--;
                    Restore(snap);

                    r.Used.UnionWith(used);
                    break;
                }

            case Return ret:
                {

                    if (_lamRet.Count > 0)
                    {
                        if (ret.Value == null)
                        {
                            if (_lamRet[^1] != null && _lamRet[^1] != Ty.Void)
                                Err(ret.Line, ret.Col, "lambda must return a value");
                            _lamRet[^1] ??= Ty.Void;
                        }
                        else
                        {
                            var rt = WalkExpr(ret.Value, r.Used);
                            if (rt == Ty.Void) Err(ret.Line, ret.Col, "cannot return a void value");

                            if (_lamRet[^1] == null) _lamRet[^1] = rt;
                            else if (!TyMatches(_lamRet[^1]!, rt))
                                Err(ret.Line, ret.Col, $"lambda returns both {_lamRet[^1]} and {rt}");
                        }
                        break;
                    }

                    if (_fn == null) Err(ret.Line, ret.Col, "'return' outside of a function");

                    var wantRet = Concrete(_fn!.Ret);
                    if (ret.Value == null)
                    {
                        if (wantRet != Ty.Void) Err(ret.Line, ret.Col, "function must return a value");
                        break;
                    }

                    var ty = WalkExpr(ret.Value, r.Used);
                    if (wantRet == Ty.Void) Err(ret.Line, ret.Col, "void function cannot return a value");
                    if (!TyMatches(wantRet, ty)) Err(ret.Line, ret.Col, $"cannot return a {ty} from a function returning {wantRet}");

                    if (ret.Value is Ident rid)
                    {
                        if (rid.ThisField)
                            Err(ret.Line, ret.Col, $"cannot return borrowed field '{rid.Name}'; use copy()");
                        var sym = Find(rid.Name) ?? throw new SourceError(rid.Line, rid.Col, $"undefined variable '{rid.Name}'");
                        if (sym.BorrowParam && !sym.GenericParam)
                            Err(ret.Line, ret.Col, $"cannot return borrowed value '{rid.Name}'; use copy()");
                        if (sym.Owned)
                        {
                            if (sym.Moved || sym.MaybeMoved) Err(ret.Line, ret.Col, $"use of moved value '{rid.Name}'");
                            sym.Moved = true;
                        }
                    }
                    break;
                }

            case Break br:
                {
                    if (_loopBase.Count == 0)
                        Err(br.Line, br.Col, "'break' outside of a loop");

                    for (int si = _loopBase[^1]; si < _scopes.Count; si++)
                        foreach (var s in _scopes[si].Values)
                            if (s.Owned) r.DropsBefore.Add(s.Name);
                    break;
                }

            case Continue co:
                {
                    if (_loopBase.Count == 0)
                        Err(co.Line, co.Col, "'continue' outside of a loop");

                    for (int si = _loopBase[^1]; si < _scopes.Count; si++)
                        foreach (var s in _scopes[si].Values)
                            if (s.Owned) r.DropsBefore.Add(s.Name);
                    break;
                }

            case TryCatch tc:
                {
                    var snap = Snapshot();
                    _branchDepth++;
                    var usedTry = WalkStmts(tc.Try);
                    var mid = Snapshot();
                    Restore(snap);

                    foreach (var (s, m, mm, _) in mid)
                    {
                        var before = snap.First(x => ReferenceEquals(x.Item1, s));
                        s.Moved = before.Item2;
                        s.MaybeMoved = before.Item3 || m || mm;
                    }
                    _branchDepth--;

                    Sym? errSym = null;
                    if (tc.ErrName != null)
                    {
                        errSym = new Sym
                        {
                            Name = tc.ErrName,
                            Ty = Ty.Str,
                            Decl = Ty.Str,
                            Owned = true,
                            LoopScoped = _loopDepth > 0,
                            DeclLine = tc.Line,
                            DeclCol = tc.Col,
                            DeclFile = _curFile
                        };
                    }
                    var usedCatch = WalkStmts(tc.Catch, errSym);

                    r.Used.UnionWith(usedTry);
                    r.Used.UnionWith(usedCatch);
                    break;
                }

            case BlockStmt b:
                r.Used.UnionWith(WalkStmts(b.Body));
                break;

            case Lock lk:
                {
                    if (lk.Target is not Ident lid || Find(lid.Name) == null)
                        Err(lk.Line, lk.Col, "lock takes a variable");
                    if (ContainsJump(lk.Body))
                        Err(lk.Line, lk.Col, "cannot return, break or continue out of a lock");

                    var used = WalkStmts(lk.Body);
                    r.Used.UnionWith(used);
                    break;
                }
        }

        return r;
    }

    private static bool TyMatches(Ty target, Ty value) =>
        target == value
        || (target.Nullable && (value == Ty.Null || value == target.Elem))
        || (target.Nullable && target.Elem == Ty.Float && value == Ty.Int)
        || (target == Ty.Float && value == Ty.Int);

    private List<(Sym, bool, bool, Ty)> Snapshot() =>
        _scopes.SelectMany(s => s.Values).Select(s => (s, s.Moved, s.MaybeMoved, s.Ty)).ToList();

    private void Restore(List<(Sym, bool, bool, Ty)> snap)
    {
        foreach (var (s, m, mm, ty) in snap) { s.Moved = m; s.MaybeMoved = mm; s.Ty = ty; }
    }

    private void MergeBranch(List<(Sym, bool, bool, Ty)> a, List<(Sym, bool, bool, Ty)> b)
    {
        foreach (var (s, am, amm, aty) in a)
        {
            var match = b.First(x => ReferenceEquals(x.Item1, s));
            s.Moved = am && match.Item2;
            s.MaybeMoved = am != match.Item2 || amm || match.Item3;
            s.Ty = aty == match.Item4 ? aty : (aty.Nullable ? aty : match.Item4);
        }
    }

    private void CheckFieldStore(Assign a, Ty fieldTy, string name, Ty valTy)
    {
        if (!TyMatches(fieldTy, valTy))
            Err(a.Line, a.Col, $"cannot assign a {valTy} value to field '{name}' of type {fieldTy}");
        if (a.Op != "=" && (fieldTy == Ty.Str || fieldTy.Owned))
            Err(a.Line, a.Col, $"operator '{a.Op}' is not supported on {fieldTy} fields");

        if (a.Op == "=")
        {
            if (a.Value is Ident src && InitFromBorrow(src))
                Err(a.Line, a.Col, $"cannot move out of borrowed parameter '{src.Name}', use copy({src.Name})");
            ConsumeOwned(a.Value);
        }
    }

    private static int FieldIndex(TypeDecl td, string name)
    {
        for (int i = 0; i < td.Fields.Count; i++)
            if (td.Fields[i].Name == name) return i;
        return -1;
    }

    private void ConsumeOwned(Expr e)
    {
        if (e is not Ident id) return;

        var sym = Find(id.Name);
        if (sym == null || !sym.Owned) return;
        if (sym.Ty.Kind == UserKind.Struct) return;

        if (sym.Moved || sym.MaybeMoved)
            throw new SourceError(id.Line, id.Col, $"use of moved value '{id.Name}'");

        if (_loopDepth > 0 && !sym.LoopScoped)
            throw new SourceError(id.Line, id.Col, $"cannot move '{id.Name}' inside a loop");

        sym.Moved = true;
        if (_branchDepth > 0) sym.MaybeMoved = true;
    }

    private void CheckListCopy(Expr e, int line, int col)
    {
        if (e is not Ident id) return;

        var sym = Find(id.Name);
        if (sym != null && sym.BorrowParam && sym.Ty.Elem != null)
            Err(line, col, $"cannot copy borrowed list '{id.Name}'");
    }

    private bool InitFromBorrow(Ident id)
    {
        var sym = Find(id.Name);
        return sym != null && sym.BorrowParam && !sym.GenericParam && sym.Ty.Owned;
    }

    private Ty WalkExpr(Expr e, HashSet<Sym> uses)
    {
        switch (e)
        {
            case IntLit: return Ty.Int;
            case FloatLit: return Ty.Float;
            case BoolLit: return Ty.Bool;
            case StrLit: return Ty.Str;
            case NullLit: return Ty.Null;

            case InterpLit it:
                {
                    foreach (var p in it.Parts)
                    {
                        var ty = WalkExpr(p, uses);
                        if (ty.Elem != null) Err(it.Line, it.Col, "cannot embed a list in an interpolated string");
                        if (ty.Nullable) Err(it.Line, it.Col, "cannot embed a nullable value; check it against null first");
                        if (Ty.IsUser(ty) && ty.Kind != UserKind.Enum) Err(it.Line, it.Col, "cannot embed a class or struct");
                        if (Ty.IsMap(ty)) Err(it.Line, it.Col, "cannot embed a map in an interpolated string");
                        if (ty == Ty.Void) Err(it.Line, it.Col, "cannot embed a void value");
                    }
                    return Ty.Str;
                }

            case Ident id:
                {
                    var sym = Find(id.Name);

                    if (sym == null && _fnType != null && FieldIndex(_fnType, id.Name) is >= 0)
                    {
                        if (_lamBase.Count > 0)
                            Err(id.Line, id.Col, $"cannot capture field '{id.Name}' inside a lambda");
                        id.ThisField = true;
                        id.ThisIndex = FieldIndex(_fnType, id.Name);
                        if (Record)
                        {
                            var fd = _fnType.Fields[id.ThisIndex];
                            RecUse(id.Name, id.Line, id.Col, id.Name.Length, "field", null, fd.Type,
                                container: _fnType.Name);
                            var fo = Occs[^1];
                            fo.DeclLine = fd.Line; fo.DeclCol = fd.Col; fo.DeclFile = _fnType.SourceFile;
                        }
                        return _fnType.Fields[id.ThisIndex].Type;
                    }

                    if (sym == null) Err(id.Line, id.Col, $"undefined variable '{id.Name}'");
                    if (sym.Moved || sym.MaybeMoved)
                        Err(id.Line, id.Col, $"use of moved value '{id.Name}'");

                    if (_lamBase.Count > 0)
                    {
                        var owner = OwnerDepth(sym);

                        if (_onAcceptCtx.Count > 0 && _onAcceptCtx[^1]
                            && _fn == null && owner is >= 0 and <= 1 && owner < _lamBase[^1])
                        {
                            uses.Add(sym);
                            return sym.Ty;
                        }

                        if (owner >= 0 && owner < _lamBase[^1])
                        {
                            if (sym.BorrowParam)
                                Err(id.Line, id.Col, $"cannot capture borrowed value '{id.Name}'; use copy()");
                            if (sym.Ty.Owned && sym.Ty.Kind != UserKind.Struct)
                            {
                                if (_lamOuterLoop[^1] > 0 && !sym.LoopScoped)
                                    Err(id.Line, id.Col, $"cannot capture '{id.Name}' inside a loop");
                                sym.Moved = true;
                                if (_branchDepth > 0) sym.MaybeMoved = true;
                            }
                        }
                    }

                    if (sym.Decl != null && sym.Decl.Nullable && !sym.Ty.Nullable)
                        id.Unwrap = true;

                    uses.Add(sym);
                    if (Record)
                        RecUse(id.Name, id.Line, id.Col, id.Name.Length, "var", sym);
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

            case Cond cd:
                {
                    var ct = WalkExpr(cd.CondExpr, uses);
                    if (ct != Ty.Bool) Err(cd.Line, cd.Col, "the condition of '?:' must be a bool");
                    var tt = WalkExpr(cd.Then, uses);
                    var et = WalkExpr(cd.Else, uses);
                    if (!TyMatches(tt, et) && !TyMatches(et, tt))
                        Err(cd.Line, cd.Col, $"branches of '?:' have different types: {tt} and {et}");
                    var res = TyMatches(tt, et) ? tt : et;
                    cd.Ty = res;
                    return res;
                }

            case Coalesce co:
                {
                    var lt = WalkExpr(co.L, uses);
                    var rt = WalkExpr(co.R, uses);
                    if (!lt.Nullable)
                        Err(co.Line, co.Col, "'??' requires a nullable left side");
                    if (rt.Nullable)
                    {
                        if (rt != lt)
                            Err(co.Line, co.Col, $"cannot coalesce {lt} with {rt}");
                        co.Ty = rt;
                    }
                    else
                    {
                        if (!TyMatches(lt.Elem!, rt))
                            Err(co.Line, co.Col, $"cannot coalesce {lt} with a {rt} value");
                        co.Ty = lt.Elem!;
                    }
                    return co.Ty;
                }

            case Index ix:
                {
                    var target = WalkExpr(ix.Target, uses);
                    var idx = WalkExpr(ix.Idx, uses);

                    if (Ty.IsMap(target))
                    {
                        if (!TyMatches(target.KeyTy!, idx))
                            Err(ix.Line, ix.Col, $"map key expects a {target.KeyTy}, got {idx}");
                        return Ty.NullableOf(target.Elem!);
                    }
                    if (target == Ty.Buffer)
                    {
                        if (idx != Ty.Int) Err(ix.Line, ix.Col, "buffer index must be an int");
                        return Ty.Int;
                    }
                    if (target.Nullable) Err(ix.Line, ix.Col, "cannot index a possibly null list; check it against null first");
                    if (target.Elem == null) Err(ix.Line, ix.Col, "only lists, maps or buffers can be indexed");
                    if (idx != Ty.Int) Err(ix.Line, ix.Col, "list index must be an int");
                    return target.Elem!;
                }

            case LamLit:
                Err(e.Line, e.Col, "lambdas are only allowed as the argument to Task.Run");
                return Ty.Void;

            case AwaitExpr aw:
                {
                    var ty = WalkExpr(aw.Task, uses);
                    if (!Ty.IsTask(ty))
                        Err(aw.Line, aw.Col, "'await' requires a task");
                    return ty.Elem!;
                }

            case Cast c:
                {
                    var src = WalkExpr(c.Value, uses);
                    if (src.Elem != null)
                        Err(c.Line, c.Col, $"cannot convert a {src} value");
                    if (src == c.Type) return src;

                    bool ok = (c.Type, src) switch
                    {
                        (var t, var s) when t == Ty.Int && s == Ty.Float => true,
                        (var t, var s) when t == Ty.Int && s == Ty.Str => true,
                        (var t, var s) when t == Ty.Int && s.Kind == UserKind.Enum => true,
                        (var t, var s) when t == Ty.Float && s == Ty.Int => true,
                        (var t, var s) when t == Ty.Float && s == Ty.Str => true,
                        (var t, var s) when t == Ty.Str && (s == Ty.Int || s == Ty.Float) => true,
                        (var t, var s) when t == Ty.Str && s == Ty.Buffer => true,
                        (var t, var s) when t == Ty.Buffer && (s == Ty.Str || s == Ty.Int) => true,
                        _ => false
                    };
                    if (!ok)
                        Err(c.Line, c.Col, $"cannot convert a {src} value to {c.Type}");
                    return c.Type;
                }

            case Call c:
                return WalkCall(c, uses);

            case Method m:
                return WalkMethod(m, uses);

            case Prop p:
                {

                    if (p.Target is Ident tid && Find(tid.Name) == null && _enums.TryGetValue(tid.Name, out var ed))
                    {
                        RequireVisible(ed.Public, ed.SourceFile, p.Line, p.Col, $"enum {ed.Name}");
                        var m = ed.Members.FirstOrDefault(x => x.Name == p.Name);
                        if (m == null) Err(p.Line, p.Col, $"'{p.Name}' is not a member of enum {ed.Name}");
                        var ety = Ty.Named(ed.Name);
                        p.EnumValue = m!.Value;
                        p.ResultTy = ety;
                        if (Record)
                        {
                            RecUse(p.Name, p.NameLine, p.NameCol, p.Name.Length, "enummember", null, ety, container: ed.Name);
                            var eo = Occs[^1];
                            eo.DeclLine = m.Line; eo.DeclCol = m.Col; eo.DeclFile = ed.SourceFile;
                        }
                        return ety;
                    }

                    var target = WalkExpr(p.Target, uses);

                    if (target.Nullable && !p.NullCond)
                        Err(p.Line, p.Col, $"'{p.Name}' on a possibly null value; use '?.' or check it against null");

                    var inner = target.Nullable ? target.Elem! : target;

                    if (Ty.IsHandle(inner) && inner.Name == "HttpPacket" && p.Name == "Cookies")
                    {
                        p.CookiesFacade = true;
                        p.ResultTy = Ty.Handle("Cookies");
                        return p.ResultTy;
                    }

                    if (inner.Kind is UserKind.Class or UserKind.Struct)
                    {
                        var td = _types[inner.Name];
                        var idx = FieldIndex(td, p.Name);
                        if (idx < 0) Err(p.Line, p.Col, $"'{inner.Name}' has no field '{p.Name}'");
                        var res = td.Fields[idx].Type;
                        if (p.NullCond) res = Ty.NullableOf(res);
                        p.FieldIndex = idx;
                        p.ResultTy = res;
                        if (Record)
                        {
                            RecUse(p.Name, p.NameLine, p.NameCol, p.Name.Length, "field", null, res, container: td.Name);
                            var fo = Occs[^1];
                            fo.DeclLine = td.Fields[idx].Line;
                            fo.DeclCol = td.Fields[idx].Col;
                            fo.DeclFile = td.SourceFile;
                        }
                        return res;
                    }

                    if (Ty.IsMap(inner))
                    {
                        switch (p.Name)
                        {
                            case "Count": return Ty.Int;
                            case "Keys": return Ty.List(inner.KeyTy!);
                            case "Values": return Ty.List(inner.Elem!);
                            default:
                                Err(p.Line, p.Col, $"unknown member '{p.Name}' on a map; maps support Count, Keys and Values");
                                return Ty.Void;
                        }
                    }

                    if (inner.Elem == null || Ty.IsTask(inner)) Err(p.Line, p.Col, $"'{p.Name}' is only available on lists");
                    if (p.Name != "Count") Err(p.Line, p.Col, $"unknown member '{p.Name}'");

                    var lres = p.NullCond ? Ty.NullableOf(Ty.Int) : Ty.Int;
                    p.ResultTy = lres;
                    return lres;
                }

            case NewLit nl:
                {
                    if (!_types.TryGetValue(nl.TypeName, out var td))
                    {
                        if (_enums.ContainsKey(nl.TypeName))
                            Err(nl.Line, nl.Col, $"enum {nl.TypeName} has no initializer; use {nl.TypeName}.Member");
                        Err(nl.Line, nl.Col, $"unknown type '{nl.TypeName}'");
                    }

                    RequireVisible(td!.Public, td.SourceFile, nl.Line, nl.Col, $"type '{td.Name}'");
                    if (td.BuiltIn && !nl.UsesNew)
                        Err(nl.Line, nl.Col, $"'{td.Name}' must be created with 'new'");

                    var given = new HashSet<string>();
                    foreach (var fi in nl.Fields)
                    {
                        var idx = FieldIndex(td!, fi.Name);
                        if (idx < 0) Err(fi.Line, fi.Col, $"'{td!.Name}' has no field '{fi.Name}'");
                        if (!given.Add(fi.Name)) Err(fi.Line, fi.Col, $"duplicate field '{fi.Name}' in the initializer");

                        var vTy = WalkExpr(fi.Value, uses);
                        if (!TyMatches(td!.Fields[idx].Type, vTy))
                            Err(fi.Line, fi.Col, $"field '{fi.Name}' expects a {td.Fields[idx].Type} value, got {vTy}");

                        ConsumeOwned(fi.Value);
                    }

                    foreach (var f in td!.Fields)
                        if (!given.Contains(f.Name) && !td.BuiltIn)
                            Err(nl.Line, nl.Col, $"'{td.Name}' initializer is missing field '{f.Name}'");

                    nl.Decl = td;
                    return Ty.Named(td.Name);
                }

            case MapLit ml:
                {
                    if (ml.KeyTy != Ty.Int && ml.KeyTy != Ty.Str)
                        Err(ml.Line, ml.Col, $"map keys must be string or int, not {ml.KeyTy}");
                    if (ml.ValTy != Ty.Int && ml.ValTy != Ty.Str)
                        Err(ml.Line, ml.Col, $"map values must be string or int, not {ml.ValTy}");

                    foreach (var p in ml.Pairs)
                    {
                        var kt = WalkExpr(p.Key, uses);
                        if (!TyMatches(ml.KeyTy, kt))
                            Err(p.Line, p.Col, $"map key expects a {ml.KeyTy}, got {kt}");
                        var vt = WalkExpr(p.Value, uses);
                        if (!TyMatches(ml.ValTy, vt))
                            Err(p.Line, p.Col, $"map value expects a {ml.ValTy}, got {vt}");
                    }
                    return Ty.Map(ml.KeyTy, ml.ValTy);
                }

            case ListLit ll:
                {
                    if (ll.ElemTy != Ty.Int && ll.ElemTy != Ty.Str
                        && !(Ty.IsTask(ll.ElemTy) && ll.ElemTy!.Elem != Ty.Void))
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
                if (lt.Kind == UserKind.Enum && lt == rt) return Ty.Bool;
                if (lt == Ty.Null || rt == Ty.Null)
                {
                    if (lt == Ty.Null && rt == Ty.Null)
                        Err(b.Line, b.Col, "cannot compare two nulls");
                    var other = lt == Ty.Null ? rt : lt;
                    if (!other.Nullable)
                        Err(b.Line, b.Col, "only nullable values can be compared against null");
                    return Ty.Bool;
                }
                if (lt.Nullable || rt.Nullable)
                    Err(b.Line, b.Col, "nullable values can only be compared against null");
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
                if (lt.Kind == UserKind.Enum && lt == rt) return Ty.Bool;
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
                    if (ty.Nullable) Err(c.Line, c.Col, "cannot print a nullable value; check it against null first");
                    if (Ty.IsUser(ty) && ty.Kind != UserKind.Enum) Err(c.Line, c.Col, "cannot print a class or struct");
                    if (Ty.IsMap(ty)) Err(c.Line, c.Col, "cannot print a map");
                    if (ty.Elem != null) Err(c.Line, c.Col, "cannot print a list or a task");
                }
                return Ty.Void;

            case "input": StrArgs(c, uses, 1); return Ty.Str;

            case "len":
                if (c.Args.Count != 1) Err(c.Line, c.Col, "len takes one argument");
                {
                    var ty = WalkExpr(c.Args[0], uses);
                    if (ty != Ty.Str && ty != Ty.Buffer && !Ty.IsMap(ty) && !IsListy(ty)) Err(c.Line, c.Col, "len requires a string, a buffer, a map or a list");
                }
                return Ty.Int;

            case "copy":
                if (c.Args.Count != 1) Err(c.Line, c.Col, "copy takes one argument");
                {
                    var ty = WalkExpr(c.Args[0], uses);
                    if (ty != Ty.Str && !(ty.Nullable && ty.Elem == Ty.Str))
                        Err(c.Line, c.Col, "copy requires a string");
                    return ty;
                }
            case "read": StrArgs(c, uses, 1); return Ty.Str;
            case "write": StrArgs(c, uses, 2); return Ty.Void;
            case "exists": StrArgs(c, uses, 1); return Ty.Bool;
            case "delete": StrArgs(c, uses, 1); return Ty.Void;

            case "mem":
                if (c.Args.Count != 0) Err(c.Line, c.Col, "mem takes no arguments");
                return Ty.Int;

            case "buffer":
                {
                    if (c.Args.Count != 1) Err(c.Line, c.Col, "buffer takes a size or a string");
                    var ty = WalkExpr(c.Args[0], uses);
                    if (ty != Ty.Int && ty != Ty.Str)
                        Err(c.Line, c.Col, "buffer requires an int size or a string");
                    return Ty.Buffer;
                }

            case "clock_ms":
                if (c.Args.Count != 0) Err(c.Line, c.Col, "clock_ms takes no arguments");
                return Ty.Int;

            case "unixtime":
                if (c.Args.Count != 0) Err(c.Line, c.Col, "unixtime takes no arguments");
                return Ty.Int;

            case "fmttime":
                if (c.Args.Count != 2) Err(c.Line, c.Col, "fmttime takes (unix seconds, format)");
                if (WalkExpr(c.Args[0], uses) != Ty.Int) Err(c.Line, c.Col, "fmttime requires an int timestamp");
                if (WalkExpr(c.Args[1], uses) != Ty.Str) Err(c.Line, c.Col, "fmttime requires a string format");
                return Ty.Str;

            case "format":
                if (c.Args.Count != 2) Err(c.Line, c.Col, "format takes (float value, decimals)");
                var fv = WalkExpr(c.Args[0], uses);
                if (fv != Ty.Float && fv != Ty.Int) Err(c.Line, c.Col, "format requires a number");
                if (WalkExpr(c.Args[1], uses) != Ty.Int) Err(c.Line, c.Col, "format requires an int decimal count");
                return Ty.Str;

            case "lastError":
                if (c.Args.Count != 0) Err(c.Line, c.Col, "lastError takes no arguments");
                return Ty.Int;

            case "exiting":
                if (c.Args.Count != 0) Err(c.Line, c.Col, "exiting takes no arguments");
                return Ty.Bool;

            case "args":
                if (c.Args.Count != 0) Err(c.Line, c.Col, "args takes no arguments");
                return Ty.List(Ty.Str);

            case "env":
                StrArgs(c, uses, 1);
                return Ty.NullableOf(Ty.Str);
        }

        return WalkUserCall(c, uses);
    }

    private Ty WalkUserCall(Call c, HashSet<Sym> uses)
    {
        if (!_fns.TryGetValue(c.Name, out var fn))
            Err(c.Line, c.Col, $"unknown function '{c.Name}'");

        RequireVisible(fn!.Public, fn.SourceFile, c.Line, c.Col, $"function '{fn.Name}'");
        if (Record) RecCall(fn.Name, c.Line, c.Col, fn, c.Args);

        if (fn.TPs.Count > 0)
            return CheckGenericCall(fn, c.Name, c.TypeArgs, c.Args, c.Line, c.Col, null, uses, inst => c.Instantiation = inst);

        CheckCallArgs(fn, c.Name, c.Args, c.Line, c.Col, uses);
        return fn.Ret;
    }

    private Ty CheckGenericCall(FnDecl fn, string display, List<Ty>? typeArgs, List<Expr> args,
        int line, int col, TypeDecl? owner, HashSet<Sym> uses, Action<List<Ty>> record)
    {
        if (Record) RecCall(display, line, col, fn, args, owner?.Name);
        if (args.Count != fn.Params.Count)
            Err(line, col, $"'{display}' takes {fn.Params.Count} argument(s), got {args.Count}");
        if (typeArgs != null && typeArgs.Count > fn.TPs.Count)
            Err(line, col, $"'{display}' takes at most {fn.TPs.Count} type argument(s), got {typeArgs.Count}");

        foreach (var ta in typeArgs ?? new List<Ty>())
            if (Ty.IsUser(ta) && ta.Kind == UserKind.None)
                Err(line, col, $"unknown type '{ta.Name}'");

        var argTys = new List<Ty>();
        foreach (var a in args)
            argTys.Add(WalkExpr(a, uses));

        var subst = new Dictionary<string, Ty>();
        for (int i = 0; i < typeArgs?.Count; i++)
            subst[fn.TPs[i]] = typeArgs[i];

        for (int i = 0; i < args.Count && subst.Count < fn.TPs.Count; i++)
        {
            var pty = fn.Params[i].Type;
            if (!Ty.IsUser(pty) || pty.Kind != UserKind.None || !fn.TPs.Contains(pty.Name) || subst.ContainsKey(pty.Name))
                continue;

            var at = argTys[i];
            if (subst.TryGetValue(pty.Name, out var prev))
            {
                if (prev != at && !(prev == Ty.Float && at == Ty.Int))
                    Err(line, col, $"cannot infer '{pty.Name}' for '{display}': saw both {prev} and {at}");
            }
            else if (at != Ty.Void && at != Ty.Null)
            {
                subst[pty.Name] = at;
            }
        }

        foreach (var tp in fn.TPs)
            if (!subst.ContainsKey(tp))
                Err(line, col, $"cannot infer '{tp}' for '{display}'; pass it explicitly, like {display}<{tp}>");

        for (int i = 0; i < args.Count; i++)
        {
            var p = fn.Params[i];
            var pty = Concrete2(p.Type, subst);
            if (!TyMatches(pty, argTys[i]))
                Err(line, col, $"argument {i + 1} of '{display}': expected {pty}, got {argTys[i]}");

            if (!p.Move) continue;
            ConsumeOwned(args[i]);
            if (args[i] is Ident aid)
            {
                var sym = Find(aid.Name);
                if (sym != null && sym.BorrowParam)
                    Err(aid.Line, aid.Col, $"cannot move borrowed value '{aid.Name}' into '{display}'");
            }
            else if (args[i] is not (Call or InterpLit or NewLit))
                Err(line, col, $"argument {i + 1} of '{display}' must be an owned value");
        }

        var resolved = fn.TPs.Select(tp => subst[tp]).ToList();
        record(resolved);

        var insts = _program!.Instantiations;
        if (!insts.TryGetValue(fn, out var list))
            insts[fn] = list = new List<List<Ty>>();

        if (!list.Any(xs => xs.SequenceEqual(resolved)))
        {
            list.Add(resolved);
            CheckGenericBody(fn, owner, subst);
        }

        return Concrete2(fn.Ret, subst);
    }

    private static Ty Concrete2(Ty ty, Dictionary<string, Ty> subst)
    {
        if (ty.Nullable)
        {
            var inner = Concrete2(ty.Elem!, subst);
            return inner == ty.Elem ? ty : Ty.NullableOf(inner);
        }
        if (ty.Elem != null && !Ty.IsTask(ty))
        {
            var e2 = Concrete2(ty.Elem!, subst);
            return e2 == ty.Elem ? ty : Ty.List(e2);
        }
        if (Ty.IsUser(ty) && ty.Kind == UserKind.None && subst.TryGetValue(ty.Name, out var c))
            return c;
        return ty;
    }

    private void CheckCallArgs(FnDecl fn, string display, List<Expr> args, int line, int col, HashSet<Sym> uses)
    {
        if (args.Count != fn.Params.Count)
            Err(line, col, $"'{display}' takes {fn.Params.Count} argument(s), got {args.Count}");

        for (int i = 0; i < args.Count; i++)
        {
            var argTy = WalkExpr(args[i], uses);
            var p = fn.Params[i];

            if (!TyMatches(p.Type, argTy))
                Err(line, col, $"argument {i + 1} of '{display}': expected {p.Type}, got {argTy}");

            if (!p.Move) continue;

            ConsumeOwned(args[i]);

            if (args[i] is Ident aid)
            {
                var sym = Find(aid.Name);
                if (sym != null && sym.BorrowParam)
                    Err(aid.Line, aid.Col, $"cannot move borrowed value '{aid.Name}' into '{display}'");
            }
            else if (args[i] is not (Call or InterpLit or NewLit))
                Err(line, col, $"argument {i + 1} of '{display}' must be an owned value");
        }
    }

    private Ty WalkHandleMethod(Ty target, Method m, HashSet<Sym> uses)
    {
        string kind = target.Name;

        if (m.Name == "OnAccept" && kind is "listener" or "httpl" or "rawhttpl" or "udp")
            return CheckOnAccept(m, kind switch
            {
                "listener" => Ty.Handle("Client"),
                "httpl" => Ty.Handle("HttpPacket"),
                "rawhttpl" => Ty.Handle("RawHttpPacket"),
                _ => Ty.Str
            }, uses);
        if (kind == "StringBuilder" && m.Name == "Add")
        {
            if (m.Args.Count != 1) Err(m.Line, m.Col, "Add takes one value");
            var at = WalkExpr(m.Args[0], uses);
            if (at != Ty.Str && at != Ty.Int && at != Ty.Float && at != Ty.Buffer)
                Err(m.Line, m.Col, "Add requires a string, int, float or buffer");
            return Ty.Void;
        }

        return CheckRegistryCall(kind, m, RuntimeApi.Lookup(kind, m.Name), uses,
            $"'{m.Name}' is not available on a {kind}");
    }

    private Ty CheckRegistryCall(string kind, Method m, List<RtMethod> ms, HashSet<Sym> uses, string notFound)
    {
        if (ms.Count == 0)
        {
            Err(m.Line, m.Col, notFound);
            return Ty.Void;
        }

        var got = new Ty[m.Args.Count];
        for (int i = 0; i < got.Length; i++) got[i] = WalkExpr(m.Args[i], uses);

        foreach (var rt in ms)
        {
            if (rt.Params.Length != got.Length) continue;
            var ok = true;
            for (int i = 0; i < got.Length; i++)
                if (got[i] != RuntimeApi.TyOf(RuntimeApi.ParamTy(rt.Params[i]))) { ok = false; break; }
            if (ok) return RuntimeApi.TyOf(rt.Ret);
        }

        string want = string.Join(" or ", ms.Select(rt => $"({string.Join(", ", rt.Params)})"));
        Err(m.Line, m.Col, $"{kind}.{m.Name} expects {want}");
        return RuntimeApi.TyOf(ms[0].Ret);
    }

    private static bool IsStaticClass(string name) =>
        name is "Task" or "Tcp" or "Udp" or "Http" or "StringBuilder";

    private Ty CheckOnAccept(Method m, Ty packetTy, HashSet<Sym> uses)
    {
        var lam = m.Args.Count == 1 ? m.Args[0] as LamLit : null;
        if (lam == null)
            Err(m.Line, m.Col, "OnAccept takes a lambda: OnAccept((packet) => { ... })");
        if (lam!.Params.Count != 1)
            Err(m.Line, m.Col, "the OnAccept lambda takes exactly one parameter");
        if (lam.Params.Count > 0 && lam.Params[0].Type != packetTy)
            Err(lam.Params[0].Line, lam.Params[0].Col, $"the OnAccept lambda parameter must be a {packetTy}");

        _onAcceptCtx.Add(true);
        WalkLambda(lam, uses);
        _onAcceptCtx.RemoveAt(_onAcceptCtx.Count - 1);

        if (lam.RetTy != null && lam.RetTy != Ty.Void)
            Err(m.Line, m.Col, "the OnAccept lambda must not return a value");
        return Ty.Void;
    }

    private Ty WalkStaticCall(string cls, Method m, HashSet<Sym> uses)
    {

        if (cls == "Task" && m.Name == "Run")
        {
            var lam = m.Args.Count == 1 ? m.Args[0] as LamLit : null;
            if (lam == null)
                Err(m.Line, m.Col, "Task.Run takes a lambda: Task.Run((params) => body)");
            if (lam!.Params.Count != 0)
                Err(m.Line, m.Col, "a Task.Run lambda takes no parameters, capture values instead");

            return Ty.Task(WalkLambda(lam, uses));
        }

        if (cls == "Task" && m.Name == "WhenAll")
        {
            if (m.Args.Count != 1) Err(m.Line, m.Col, "WhenAll takes a list of tasks");
            var ty = WalkExpr(m.Args[0], uses);
            if (ty.Elem == null || !Ty.IsTask(ty.Elem!))
                Err(m.Line, m.Col, "WhenAll requires a list<task<T>>");
            var ret = ty.Elem!.Elem!;
            if (ret == Ty.Void)
                Err(m.Line, m.Col, "WhenAll needs task<T> with a result type");
            m.ResultTy = Ty.List(ret);
            return Ty.List(ret);
        }

        return CheckRegistryCall(cls, m, RuntimeApi.LookupStatic(cls, m.Name), uses,
            $"'{cls}.{m.Name}' is not available yet");
    }

    private Ty WalkLambda(LamLit lam, HashSet<Sym> uses)
    {
        var savedFn = _fn;
        var outerLoopDepth = _loopDepth;

        _fn = null;
        _loopDepth = 0;
        _lamRet.Add(null);
        _lamBase.Add(_scopes.Count);
        _lamOuterLoop.Add(outerLoopDepth);

        _scopes.Add(new Dictionary<string, Sym>());

        try
        {

            foreach (var p in lam.Params)
            {
                var ty = Concrete(p.Type);
                if (p.Move && ty.Kind == UserKind.Struct)
                    throw new SourceError(p.Line, p.Col, "'move' has no effect on structs, they copy");
                if (p.Move && !ty.Owned)
                    throw new SourceError(p.Line, p.Col, "'move' is only valid for owned parameters");
                if (_scopes[^1].ContainsKey(p.Name))
                    throw new SourceError(p.Line, p.Col, $"duplicate parameter '{p.Name}'");
                CheckTy(ty, p.Line, p.Col);

                bool isStruct = ty.Kind == UserKind.Struct;
                bool bareGeneric = Ty.IsUser(p.Type) && p.Type.Kind == UserKind.None;
                _scopes[^1][p.Name] = new Sym
                {
                    Name = p.Name,
                    Ty = ty,
                    Decl = ty,
                    BorrowParam = !isStruct && ty.Owned && !p.Move,
                    GenericParam = bareGeneric,
                    Owned = p.Move || (isStruct && ty.Owned),
                    DeclLine = p.Line,
                    DeclCol = p.Col,
                    DeclFile = _curFile
                };
                if (Record) RecUse(p.Name, p.Line, p.Col, p.Name.Length, "param", _scopes[^1][p.Name], isDecl: true);
            }

            WalkStmts(lam.Body);

            var ret = _lamRet[^1] ?? Ty.Void;
            lam.RetTy = ret;
            return ret;
        }
        finally
        {
            _scopes.RemoveAt(_scopes.Count - 1);
            _lamOuterLoop.RemoveAt(_lamOuterLoop.Count - 1);
            _lamBase.RemoveAt(_lamBase.Count - 1);
            _lamRet.RemoveAt(_lamRet.Count - 1);
            _loopDepth = outerLoopDepth;
            _fn = savedFn;
        }
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

    private static bool IsListy(Ty ty) => ty.Elem != null && !Ty.IsTask(ty) && !ty.Nullable;

    private static bool ContainsJump(List<Stmt> body)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case Return or Break or Continue:
                    return true;
                case If f:
                    if (ContainsJump(f.Then) || (f.Else != null && ContainsJump(f.Else))) return true;
                    break;
                case While w:
                    if (ContainsJump(w.Body)) return true;
                    break;
                case BlockStmt b:
                    if (ContainsJump(b.Body)) return true;
                    break;
            }
        }
        return false;
    }

    private Ty WalkMethod(Method m, HashSet<Sym> uses)
    {

        if (m.Target is Ident ns && IsStaticClass(ns.Name))
            return WalkStaticCall(ns.Name, m, uses);

        var target = WalkExpr(m.Target, uses);

        if (target.Nullable && !m.NullCond)
            Err(m.Line, m.Col, $"'{m.Name}' on a possibly null value; use '?.' or check it against null");
        if (target.Nullable) target = target.Elem!;

        if (Ty.IsMap(target))
        {
            switch (m.Name)
            {
                case "Contains":
                    if (m.Args.Count != 1) Err(m.Line, m.Col, "Contains takes a key");
                    if (!TyMatches(target.KeyTy!, WalkExpr(m.Args[0], uses)))
                        Err(m.Line, m.Col, $"Contains expects a {target.KeyTy} key");
                    return Ty.Bool;
                case "Remove":
                    if (m.Args.Count != 1) Err(m.Line, m.Col, "Remove takes a key");
                    if (!TyMatches(target.KeyTy!, WalkExpr(m.Args[0], uses)))
                        Err(m.Line, m.Col, $"Remove expects a {target.KeyTy} key");
                    return Ty.Void;
                case "Clear":
                    if (m.Args.Count != 0) Err(m.Line, m.Col, "Clear takes no arguments");
                    return Ty.Void;
                default:
                    Err(m.Line, m.Col, $"'{m.Name}' is not available on a {target}; maps support Contains, Remove and Clear");
                    return Ty.Void;
            }
        }

        if (target.Kind is UserKind.Class or UserKind.Struct)
        {
            var key = $"{target.Name}.{m.Name}";
            if (!_fns.TryGetValue(key, out var fn))
                Err(m.Line, m.Col, $"'{target.Name}' has no method '{m.Name}'");

            RequireVisible(fn!.Public, fn.SourceFile, m.Line, m.Col, $"'{key}'");
            if (Record) RecCall(m.Name, m.NameLine, m.NameCol, fn, m.Args, target.Name);

            Ty res;
            if (fn!.TPs.Count > 0)
            {
                res = CheckGenericCall(fn, key, m.TypeArgs, m.Args, m.Line, m.Col, _types[target.Name], uses,
                    inst => m.Instantiation = inst);
            }
            else
            {
                CheckCallArgs(fn, key, m.Args, m.Line, m.Col, uses);
                res = fn.Ret;
            }

            if (m.NullCond && res != Ty.Void) res = Ty.NullableOf(res);
            m.ResultTy = res;
            return res;
        }

        if (Ty.IsHandle(target))
        {
            var res = WalkHandleMethod(target, m, uses);
            m.ResultTy = res;
            return res;
        }

        if (target == Ty.Str)
        {
            var sres = WalkStringMethod(m, uses);
            if (m.NullCond && sres != Ty.Void) sres = Ty.NullableOf(sres);
            m.ResultTy = sres;
            return sres;
        }

        if (target.Elem == null || Ty.IsTask(target))
            Err(m.Line, m.Col, $"'{m.Name}' is only available on lists");

        var r = WalkListMethod(target, m, uses);        if (m.NullCond && r != Ty.Void) r = Ty.NullableOf(r);
        m.ResultTy = r;
        return r;
    }

    private Ty WalkStringMethod(Method m, HashSet<Sym> uses) =>
        CheckRegistryCall("string", m, RuntimeApi.Lookup("string", m.Name), uses,
            $"'{m.Name}' is not a string method; strings support Split, Contains, StartsWith, IndexOf, Substring, Replace, Trim, ToLower, ToUpper and ToInt");

    private Ty WalkListMethod(Ty target, Method m, HashSet<Sym> uses)
    {
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

            case "Join":
                if (target.Elem != Ty.Str)
                    Err(m.Line, m.Col, "Join works on a list of strings");
                if (m.Args.Count != 1) Err(m.Line, m.Col, "Join takes a separator");
                if (WalkExpr(m.Args[0], uses) != Ty.Str) Err(m.Line, m.Col, "Join requires a string separator");
                return Ty.Str;

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

            case "Contains":
            case "IndexOf":
                {
                    if (m.Args.Count != 1) Err(m.Line, m.Col, $"{m.Name} takes one argument");
                    var ty = WalkExpr(m.Args[0], uses);
                    if (!TyMatches(target.Elem!, ty))
                        Err(m.Line, m.Col, $"{m.Name} expects a {target.Elem} value");
                    return m.Name == "Contains" ? Ty.Bool : Ty.Int;
                }

            case "Sort":
            case "Reverse":
                if (m.Args.Count != 0) Err(m.Line, m.Col, $"{m.Name} takes no arguments");
                if (m.Name == "Sort" && target.Elem != Ty.Str && target.Elem != Ty.Int && target.Elem != Ty.Float)
                    Err(m.Line, m.Col, $"Sort works on lists of strings, ints or floats, not {target.Elem}");
                return Ty.Void;

            default:
                Err(m.Line, m.Col, $"unknown list member '{m.Name}'");
                return Ty.Void;
        }
    }
}

