using HSharp.Checking;
using HSharp.Lexing;
using HSharp.Parsing;
using HSharp.Syntax;

namespace HSharp.Analysis;

public sealed record Diag(string File, int Line, int Col, string Message, int Severity = 1);

public sealed class DeclInfo
{
    public string Name = "";
    public string Kind = "";
    public string Signature = "";
    public string? Detail;
    public string File = "";
    public int Line, Col;
    public bool Public;
    public bool OwnFile;
    public string? Container;
    public Ty? RetTy;
    public Ty? Ty;
    public FnDecl? Fn;
}

public sealed record FoldRange(int StartLine, int EndLine);

public sealed class AnalysisDoc
{
    public string EntryPath = "";
    public string Text = "";
    public AstProgram? Program;
    public List<Diag> Diags { get; } = new();
    public List<DeclInfo> Decls { get; } = new();
    public List<Occ> Occs { get; } = new();
    public List<CallSite> Calls { get; } = new();
    public HashSet<string> LoadedFiles { get; } = new();

    public int ParseErrorCount;

    public Dictionary<Occ, int> ScopeEnds { get; } = new();

    public bool HasErrors => Diags.Any(d => d.Severity == 1);
}

public static class BuiltinInfo
{
    public static readonly Dictionary<string, (string Sig, string Doc)> Info = new()    {
        ["print"] = ("void print(value)", "Write a value to stdout."),
        ["input"] = ("string input(string prompt)", "Read a line from stdin."),
        ["len"] = ("int len(string | buffer | list | map x)", "Length of a string, buffer, list or map."),
        ["copy"] = ("string copy(string s)", "Deep-copy a string (required when a borrowed value must escape)."),
        ["read"] = ("string read(string path)", "Read a whole file into a string. Missing files route to catch."),
        ["write"] = ("void write(string path, string content)", "Write a string to a file."),
        ["exists"] = ("bool exists(string path)", "True when the file exists."),
        ["delete"] = ("void delete(string path)", "Delete a file."),
        ["mem"] = ("int mem()", "Number of live heap allocations. A well-formed program returns to 0."),
        ["clock_ms"] = ("int clock_ms()", "Monotonic millisecond clock."),
        ["lastError"] = ("int lastError()", "Non-zero when the nearest builtin failure left the error flag set."),
        ["args"] = ("list<string> args()", "Command-line arguments."),
        ["env"] = ("string? env(string name)", "Environment variable, null when absent."),
        ["exiting"] = ("bool exiting()", "True after Ctrl+C / SIGTERM, for graceful shutdown loops."),
        ["buffer"] = ("buffer buffer(int size | string s)", "Create a byte buffer of size, or from a string."),
        ["unixtime"] = ("int unixtime()", "Seconds since 1970-01-01 UTC."),
        ["fmttime"] = ("string fmttime(int unix, string fmt)", "Format a timestamp with strftime codes like %Y-%m-%d %H:%M:%S."),
        ["format"] = ("string format(float value, int decimals)", "Format a number with a fixed number of decimals.")
    };

    public static readonly string[] Names = Info.Keys.OrderBy(x => x).ToArray();

    public static string ReturnType(string name)
    {
        var sig = Info.TryGetValue(name, out var i) ? i.Sig : null;
        if (sig == null) return "";
        int sp = sig.IndexOf(' ');
        return sp < 0 ? "" : sig[..sp];
    }

    public static List<string> ParamTypes(string name)
    {
        var sig = Info.TryGetValue(name, out var i) ? i.Sig : null;
        var res = new List<string>();
        if (sig == null) return res;
        int lp = sig.IndexOf('(');
        int rp = sig.LastIndexOf(')');
        if (lp < 0 || rp <= lp) return res;
        foreach (var p in sig[(lp + 1)..rp].Split(", "))
        {
            int sp = p.IndexOf(' ');
            res.Add(sp < 0 ? p : p[..sp]);
        }
        return res;
    }
}

public enum CompletionCtxKind
{
    Unknown,
    None,
    MemberAccess,
    ImportPath,
    ForeachIterable,
    LambdaParam,
    CallArg,
    InitializerField,
    LockTarget,
    CatchVar,
    TypePosition,
    DeclName,
    Expression,
    StatementStart
}

public sealed class CompletionContext
{
    public CompletionCtxKind Kind;
    public string? MemberTarget;
    public string? Callee;
    public int CalleeLine, CalleeCol;
    public int ArgIndex;
    public string? AssignTarget;
    public bool AssignThroughIndex;
    public bool MoveValue;
    public string? InitializerType;
    public string? InitializerField;
    public int ReceiverEnd;
    public int Line, Col;
}

