namespace HSharp.Syntax;

public sealed class SourceError : Exception
{
    public int Line, Col;

    public string? File;
    public SourceError(int line, int col, string msg) : base(msg) { Line = line; Col = col; }
    public SourceError At(string file) { File = file; return this; }
}

public enum UserKind { None, Class, Struct, Enum }

public sealed class Ty
{
    public static readonly Ty Int = new("int", null, false);
    public static readonly Ty Float = new("float", null, false);
    public static readonly Ty Bool = new("bool", null, false);
    public static readonly Ty Str = new("string", null, false);
    public static readonly Ty Void = new("void", null, false);
    public static readonly Ty Null = new("null", null, false);
    public static readonly Ty Buffer = new("buffer", null, false);

    public string Name { get; }
    public Ty? Elem { get; }
    public Ty? KeyTy { get; private set; }
    public bool Nullable { get; }
    public UserKind Kind { get; set; }

    public bool OwnsHeap { get; set; }

    private Ty(string name, Ty? elem, bool nullable) { Name = name; Elem = elem; Nullable = nullable; }

    private static readonly Dictionary<string, Ty> _lists = new();
    public static Ty List(Ty elem)
    {
        var name = $"list<{elem.Name}>";
        if (!_lists.TryGetValue(name, out var t))
            _lists[name] = t = new Ty(name, elem, false);
        return t;
    }

    private static readonly Dictionary<string, Ty> _tasks = new();

    public static Ty Task(Ty ret)
    {
        var name = $"task<{ret.Name}>";
        if (!_tasks.TryGetValue(name, out var t))
            _tasks[name] = t = new Ty(name, ret, false);
        return t;
    }

    public static bool IsTask(Ty ty) => _tasks.ContainsKey(ty.Name);

    private static readonly Dictionary<string, Ty> _handles = new();

    public static Ty Handle(string kind)
    {
        if (!_handles.TryGetValue(kind, out var t))
            _handles[kind] = t = new Ty(kind, null, false);
        return t;
    }

    public static bool IsHandle(Ty ty) => _handles.ContainsKey(ty.Name);

    private static readonly Dictionary<string, Ty> _opts = new();

    public static Ty NullableOf(Ty inner)
    {
        var name = $"{inner.Name}?";
        if (!_opts.TryGetValue(name, out var t))
            _opts[name] = t = new Ty(name, inner, true);
        return t;
    }

    public static bool IsNullable(Ty ty) => ty.Nullable;

    private static readonly Dictionary<string, Ty> _maps = new();

    public static Ty Map(Ty key, Ty val)
    {
        var name = $"map<{key.Name}, {val.Name}>";
        if (!_maps.TryGetValue(name, out var t))
        {
            _maps[name] = t = new Ty(name, val, false);
            t.KeyTy = key;
        }
        return t;
    }

    public static bool IsMap(Ty ty) => _maps.ContainsKey(ty.Name);

    private static readonly Dictionary<string, Ty> _users = new();

    public static Ty Named(string name)
    {
        if (!_users.TryGetValue(name, out var t))
            _users[name] = t = new Ty(name, null, false);
        return t;
    }

    public static bool IsUser(Ty ty) => _users.ContainsKey(ty.Name);

    public bool IsPtrKind => this == Str || this == Buffer || IsHandle(this) || Kind == UserKind.Class || (Elem != null && !Nullable);

    public bool Owned => Nullable ? Elem!.Owned : (OwnsHeap || this == Str || this == Buffer || (Elem != null && !IsTask(this)));
    public override string ToString() => Name;
}

public abstract record Expr(int Line, int Col);
public sealed record IntLit(int Value, int Line, int Col) : Expr(Line, Col);
public sealed record FloatLit(double Value, int Line, int Col) : Expr(Line, Col);
public sealed record BoolLit(bool Value, int Line, int Col) : Expr(Line, Col);
public sealed record StrLit(string Value, int Line, int Col) : Expr(Line, Col);
public sealed record InterpLit(List<Expr> Parts, int Line, int Col) : Expr(Line, Col);
public sealed record Ident(string Name, int Line, int Col) : Expr(Line, Col)
{

    public bool Unwrap;

    public bool ThisField;
    public int ThisIndex = -1;
}
public sealed record Bin(string Op, Expr L, Expr R, int Line, int Col) : Expr(Line, Col);
public sealed record Un(string Op, Expr E, int Line, int Col) : Expr(Line, Col);
public sealed record Index(Expr Target, Expr Idx, int Line, int Col) : Expr(Line, Col);
public sealed record Call(string Name, List<Expr> Args, int Line, int Col, List<Ty>? TypeArgs = null) : Expr(Line, Col)
{

    public List<Ty>? Instantiation;
}
public sealed record Method(Expr Target, string Name, List<Expr> Args, int Line, int Col, bool NullCond = false, List<Ty>? TypeArgs = null) : Expr(Line, Col)
{
    public Ty? ResultTy;
    public List<Ty>? Instantiation;

    public int NameLine, NameCol;
}
public sealed record Prop(Expr Target, string Name, int Line, int Col, bool NullCond = false) : Expr(Line, Col)
{
    public Ty? ResultTy;

    public int? EnumValue;

    public int FieldIndex = -1;
    public int NameLine, NameCol;

    public bool CookiesFacade;
}
public sealed record ListLit(Ty ElemTy, List<Expr> Items, int Line, int Col) : Expr(Line, Col);

public sealed record LamLit(List<Param> Params, List<Stmt> Body, int Line, int Col) : Expr(Line, Col)
{
    public Ty? RetTy;
}

public sealed record AwaitExpr(Expr Task, int Line, int Col) : Expr(Line, Col);

public sealed record Cast(Ty Type, Expr Value, int Line, int Col) : Expr(Line, Col);

public sealed record NullLit(int Line, int Col) : Expr(Line, Col);

public sealed record Coalesce(Expr L, Expr R, int Line, int Col) : Expr(Line, Col)
{
    public Ty? Ty;
}

public sealed record Cond(Expr CondExpr, Expr Then, Expr Else, int Line, int Col) : Expr(Line, Col)
{
    public Ty? Ty;
}

public sealed record Param(Ty Type, string Name, bool Move, int Line, int Col);

public sealed record EnumMember(string Name, int Value, int Line, int Col);
public sealed record EnumDecl(string Name, List<EnumMember> Members, int Line, int Col, bool Public = false) : Stmt(Line, Col)
{
    public string? SourceFile;
}

public sealed record Field(Ty Type, string Name, int Line, int Col);
public sealed record TypeDecl(UserKind Kind, string Name, List<Field> Fields, List<FnDecl> Methods, int Line, int Col, bool Public = false) : Stmt(Line, Col)
{
    public string? SourceFile;

    public bool BuiltIn;
}

public sealed record FieldInit(string Name, Expr Value, int Line, int Col);
public sealed record NewLit(string TypeName, List<FieldInit> Fields, int Line, int Col, bool UsesNew = false) : Expr(Line, Col)
{

    public TypeDecl? Decl;
}

public sealed record MapPair(Expr Key, Expr Value, int Line, int Col);
public sealed record MapLit(Ty KeyTy, Ty ValTy, List<MapPair> Pairs, int Line, int Col) : Expr(Line, Col);

public abstract record Stmt(int Line, int Col);
public sealed record VarDecl(string Name, Ty? Ann, Expr Init, int Line, int Col, int NameLine = 0, int NameCol = 0) : Stmt(Line, Col);
public sealed record Assign(Expr Target, string Op, Expr Value, int Line, int Col) : Stmt(Line, Col);
public sealed record IncDec(Expr Target, bool Inc, int Line, int Col) : Stmt(Line, Col);
public sealed record ExprStmt(Expr E, int Line, int Col) : Stmt(Line, Col);
public sealed record If(Expr Cond, List<Stmt> Then, List<Stmt>? Else, int Line, int Col) : Stmt(Line, Col);
public sealed record While(Expr Cond, List<Stmt> Body, int Line, int Col) : Stmt(Line, Col);
public sealed record For(Stmt? Init, Expr? Cond, Stmt? Step, List<Stmt> Body, int Line, int Col) : Stmt(Line, Col);
public sealed record Foreach(string Var, Expr Iter, List<Stmt> Body, int Line, int Col, int NameLine = 0, int NameCol = 0) : Stmt(Line, Col);
public sealed record Return(Expr? Value, int Line, int Col) : Stmt(Line, Col);
public sealed record Break(int Line, int Col) : Stmt(Line, Col);
public sealed record Continue(int Line, int Col) : Stmt(Line, Col);
public sealed record TryCatch(List<Stmt> Try, List<Stmt> Catch, int Line, int Col, string? ErrName = null) : Stmt(Line, Col);
public sealed record BlockStmt(List<Stmt> Body, int Line, int Col) : Stmt(Line, Col);
public sealed record FnDecl(Ty Ret, string Name, List<Param> Params, List<Stmt> Body, int Line, int Col, List<string>? TypeParams = null, bool Public = false) : Stmt(Line, Col)
{
    public List<string> TPs => TypeParams ?? new List<string>();
    public string? SourceFile;
}

public sealed record ImportStmt(string Path, int Line, int Col) : Stmt(Line, Col);

public sealed record Lock(Expr Target, List<Stmt> Body, int Line, int Col) : Stmt(Line, Col);

public sealed record Drop(List<string> Names, int Line, int Col) : Stmt(Line, Col);

public sealed class AstProgram
{
    public List<Stmt> Stmts { get; set; } = new();
    public AstProgram(List<Stmt> stmts) { Stmts = stmts; }

    public Dictionary<FnDecl, List<List<Ty>>> Instantiations { get; } = new(ReferenceEqualityComparer.Instance);
}

