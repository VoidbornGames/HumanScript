namespace HumanScript;

public abstract class Expr { }

public class NumberExpr : Expr { public int Value; public NumberExpr(int v) { Value = v; } }
public class FloatExpr : Expr { public double Value; public FloatExpr(double v) { Value = v; } }
public class BoolExpr : Expr { public bool Value; public BoolExpr(bool v) { Value = v; } }
public class StringExpr : Expr { public string Value; public StringExpr(string v) { Value = v; } }
public class IdentExpr : Expr { public string Name; public IdentExpr(string n) { Name = n; } }

public class BinaryExpr : Expr
{
    public string Op;
    public Expr Left, Right;
    public BinaryExpr(string op, Expr l, Expr r) { Op = op; Left = l; Right = r; }
}

public class UnaryExpr : Expr
{
    public string Op;
    public Expr Operand;
    public UnaryExpr(string op, Expr operand) { Op = op; Operand = operand; }
}

public class ReadFileExpr : Expr { public string FilePath; public ReadFileExpr(string p) { FilePath = p; } }
public class ExistExpr : Expr { public string FilePath; public ExistExpr(string p) { FilePath = p; } }

public class ListExpr : Expr { public List<Expr> Items = new(); }

public class StartsWithExpr : Expr { public Expr Str; public Expr Prefix; public StartsWithExpr(Expr s, Expr p) { Str = s; Prefix = p; } }
public class EndsWithExpr : Expr { public Expr Str; public Expr Suffix; public EndsWithExpr(Expr s, Expr p) { Str = s; Suffix = p; } }
public class LengthOfExpr : Expr { public Expr Target; public LengthOfExpr(Expr t) { Target = t; } }
public class ListLengthExpr : Expr { public Expr ListExpr; public ListLengthExpr(Expr list) { ListExpr = list; } }
public class ListIndexExpr : Expr { public Expr Target; public Expr Index; public ListIndexExpr(Expr target, Expr index) { Target = target; Index = index; } }


public abstract class Stmt { }

public class DefineStmt : Stmt
{
    public string Name;
    public Expr Value;
    public DefineStmt(string name, Expr value) { Name = name; Value = value; }
}

public class SetStmt : Stmt
{
    public Expr Target;
    public Expr Value;
    public SetStmt(Expr target, Expr value) { Target = target; Value = value; }
}

public class IncreaseStmt : Stmt
{
    public string Name;
    public Expr Amount;
    public IncreaseStmt(string name, Expr amount) { Name = name; Amount = amount; }
}

public class DecreaseStmt : Stmt
{
    public string Name;
    public Expr Amount;
    public DecreaseStmt(string name, Expr amount) { Name = name; Amount = amount; }
}

public class MultiplyStmt : Stmt
{
    public string Name;
    public Expr Amount;
    public MultiplyStmt(string name, Expr amount) { Name = name; Amount = amount; }
}

public class DivideStmt : Stmt
{
    public string Name;
    public Expr Amount;
    public DivideStmt(string name, Expr amount) { Name = name; Amount = amount; }
}

public class SayStmt : Stmt
{
    public Expr Value;
    public SayStmt(Expr value) { Value = value; }
}

public class ShowStmt : Stmt
{
    public Expr Value;
    public ShowStmt(Expr value) { Value = value; }
}

public class AskStmt : Stmt
{
    public Expr Prompt;
    public string VarName;
    public AskStmt(Expr prompt, string varName) { Prompt = prompt; VarName = varName; }
}

public class ReadIntoStmt : Stmt
{
    public string FilePath;
    public string VarName;
    public ReadIntoStmt(string path, string var) { FilePath = path; VarName = var; }
}

public class WriteIntoStmt : Stmt
{
    public Expr Value;
    public string FilePath;
    public WriteIntoStmt(Expr value, string path) { Value = value; FilePath = path; }
}

public class DeleteStmt : Stmt
{
    public string FilePath;
    public DeleteStmt(string path) { FilePath = path; }
}

public class IfStmt : Stmt
{
    public Expr Condition;
    public List<Stmt> ThenBlock;
    public List<Stmt>? ElseBlock;
    public IfStmt(Expr cond, List<Stmt> thenBlock, List<Stmt>? elseBlock)
    { Condition = cond; ThenBlock = thenBlock; ElseBlock = elseBlock; }
}

public class RepeatTimesStmt : Stmt
{
    public Expr Count;
    public List<Stmt> Body;
    public RepeatTimesStmt(Expr count, List<Stmt> body) { Count = count; Body = body; }
}

public class RepeatForeverStmt : Stmt
{
    public List<Stmt> Body;
    public RepeatForeverStmt(List<Stmt> body) { Body = body; }
}

public class WhileStmt : Stmt
{
    public Expr Condition;
    public List<Stmt> Body;
    public WhileStmt(Expr cond, List<Stmt> body) { Condition = cond; Body = body; }
}

public class ForEachStmt : Stmt
{
    public string VarName;
    public Expr Collection;
    public List<Stmt> Body;
    public ForEachStmt(string var, Expr collection, List<Stmt> body)
    { VarName = var; Collection = collection; Body = body; }
}

public class AddToListStmt : Stmt
{
    public Expr Value;
    public string ListName;
    public AddToListStmt(Expr value, string list) { Value = value; ListName = list; }
}

public class RemoveFromListStmt : Stmt
{
    public Expr Value;
    public string ListName;
    public RemoveFromListStmt(Expr value, string list) { Value = value; ListName = list; }
}

public class ClearListStmt : Stmt
{
    public string ListName;
    public ClearListStmt(string list) { ListName = list; }
}

public class FunctionDeclStmt : Stmt
{
    public string Name;
    public List<string> Parameters;
    public List<Stmt> Body;
    public FunctionDeclStmt(string name, List<string> parameters, List<Stmt> body)
    { Name = name; Parameters = parameters; Body = body; }
}

public class CallStmt : Stmt
{
    public string Name;
    public List<Expr> Arguments;
    public CallStmt(string name, List<Expr> args) { Name = name; Arguments = args; }
}

public class CallExpr : Expr
{
    public string Name;
    public List<Expr> Arguments;
    public CallExpr(string name, List<Expr> args) { Name = name; Arguments = args; }
}

public class ReturnStmt : Stmt
{
    public Expr? Value;
    public ReturnStmt(Expr? value) { Value = value; }
}

public class TryCatchStmt : Stmt
{
    public List<Stmt> TryBlock;
    public List<Stmt> CatchBlock;
    public TryCatchStmt(List<Stmt> tryBlock, List<Stmt> catchBlock) { TryBlock = tryBlock; CatchBlock = catchBlock; }
}

public class AstProgram
{
    public List<Stmt> Statements = new();
}