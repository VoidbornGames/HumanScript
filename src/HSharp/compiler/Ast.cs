namespace HSharp;

public sealed class SourceError : Exception
{
    public int Line, Col;
    public SourceError(int line, int col, string msg) : base(msg) { Line = line; Col = col; }
}

// list types are cached, so two list<int> are the same reference and == works
public sealed class Ty
{
    public static readonly Ty Int = new("int", null);
    public static readonly Ty Float = new("float", null);
    public static readonly Ty Bool = new("bool", null);
    public static readonly Ty Str = new("string", null);
    public static readonly Ty Void = new("void", null);

    public string Name { get; }
    public Ty? Elem { get; }
    private Ty(string name, Ty? elem) { Name = name; Elem = elem; }

    private static readonly Dictionary<string, Ty> _lists = new();
    public static Ty List(Ty elem)
    {
        var name = $"list<{elem.Name}>";
        if (!_lists.TryGetValue(name, out var t))
            _lists[name] = t = new Ty(name, elem);
        return t;
    }

    private static readonly Dictionary<string, Ty> _tasks = new();
    // Elem doubles as the task's result type; not owned, the runtime owns the task
    public static Ty Task(Ty ret)
    {
        var name = $"task<{ret.Name}>";
        if (!_tasks.TryGetValue(name, out var t))
            _tasks[name] = t = new Ty(name, ret);
        return t;
    }

    public static bool IsTask(Ty ty) => _tasks.ContainsKey(ty.Name);

    // opaque runtime handles: listeners, clients, requests. plain pointer
    // values, owned by the runtime, freed through their Close/Respond calls
    private static readonly Dictionary<string, Ty> _handles = new();

    public static Ty Handle(string kind)
    {
        if (!_handles.TryGetValue(kind, out var t))
            _handles[kind] = t = new Ty(kind, null);
        return t;
    }

    public static bool IsHandle(Ty ty) => _handles.ContainsKey(ty.Name);

    // strings and lists own heap buffers the language must free; tasks belong
    // to the runtime and die in join
    public bool Owned => this == Str || (Elem != null && !IsTask(this));
    public override string ToString() => Name;
}

public abstract record Expr(int Line, int Col);
public sealed record IntLit(int Value, int Line, int Col) : Expr(Line, Col);
public sealed record FloatLit(double Value, int Line, int Col) : Expr(Line, Col);
public sealed record BoolLit(bool Value, int Line, int Col) : Expr(Line, Col);
public sealed record StrLit(string Value, int Line, int Col) : Expr(Line, Col);
public sealed record InterpLit(List<Expr> Parts, int Line, int Col) : Expr(Line, Col);
public sealed record Ident(string Name, int Line, int Col) : Expr(Line, Col);
public sealed record Bin(string Op, Expr L, Expr R, int Line, int Col) : Expr(Line, Col);
public sealed record Un(string Op, Expr E, int Line, int Col) : Expr(Line, Col);
public sealed record Index(Expr Target, Expr Idx, int Line, int Col) : Expr(Line, Col);
public sealed record Call(string Name, List<Expr> Args, int Line, int Col) : Expr(Line, Col);
public sealed record Method(Expr Target, string Name, List<Expr> Args, int Line, int Col) : Expr(Line, Col);
public sealed record Prop(Expr Target, string Name, int Line, int Col) : Expr(Line, Col);
public sealed record ListLit(Ty ElemTy, List<Expr> Items, int Line, int Col) : Expr(Line, Col);

// (typed params) => body, only valid where a task is expected (Task.Run)
public sealed record LamLit(List<Param> Params, List<Stmt> Body, int Line, int Col) : Expr(Line, Col)
{
    public Ty? RetTy;
}

// await expr, collects a task's result
public sealed record AwaitExpr(Expr Task, int Line, int Col) : Expr(Line, Col);

public sealed record Param(Ty Type, string Name, bool Move, int Line, int Col);

public abstract record Stmt(int Line, int Col);
public sealed record VarDecl(string Name, Ty? Ann, Expr Init, int Line, int Col) : Stmt(Line, Col);
public sealed record Assign(Expr Target, string Op, Expr Value, int Line, int Col) : Stmt(Line, Col);
public sealed record IncDec(Expr Target, bool Inc, int Line, int Col) : Stmt(Line, Col);
public sealed record ExprStmt(Expr E, int Line, int Col) : Stmt(Line, Col);
public sealed record If(Expr Cond, List<Stmt> Then, List<Stmt>? Else, int Line, int Col) : Stmt(Line, Col);
public sealed record While(Expr Cond, List<Stmt> Body, int Line, int Col) : Stmt(Line, Col);
public sealed record For(Stmt? Init, Expr? Cond, Stmt? Step, List<Stmt> Body, int Line, int Col) : Stmt(Line, Col);
public sealed record Foreach(string Var, Expr Iter, List<Stmt> Body, int Line, int Col) : Stmt(Line, Col);
public sealed record Return(Expr? Value, int Line, int Col) : Stmt(Line, Col);
public sealed record Break(int Line, int Col) : Stmt(Line, Col);
public sealed record Continue(int Line, int Col) : Stmt(Line, Col);
public sealed record TryCatch(List<Stmt> Try, List<Stmt> Catch, int Line, int Col) : Stmt(Line, Col);
public sealed record BlockStmt(List<Stmt> Body, int Line, int Col) : Stmt(Line, Col);
public sealed record FnDecl(Ty Ret, string Name, List<Param> Params, List<Stmt> Body, int Line, int Col) : Stmt(Line, Col);

// inserted by the checker; emits guarded drops for the named variables
public sealed record Drop(List<string> Names, int Line, int Col) : Stmt(Line, Col);

public sealed class AstProgram
{
    public List<Stmt> Stmts { get; set; } = new();
    public AstProgram(List<Stmt> stmts) { Stmts = stmts; }
}
