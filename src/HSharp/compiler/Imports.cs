using HSharp.Checking;
using HSharp.Lexing;
using HSharp.Parsing;
using HSharp.Syntax;

namespace HSharp;

public static class Imports
{

    public static List<string> SearchDirs { get; } = new();

    public static bool Tolerant { get; private set; }

    public static void ConfigureSearchPaths(IEnumerable<string> extraDirs)
    {
        SearchDirs.Clear();
        SearchDirs.AddRange(extraDirs.Select(Path.GetFullPath).Where(Directory.Exists));

        var env = Environment.GetEnvironmentVariable("HSHARP_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            SearchDirs.AddRange(env.Split(';', ':').Where(s => s.Length > 0 && Directory.Exists(s)).Select(Path.GetFullPath));
    }

    public static IReadOnlyCollection<string> LastLoadedFiles => _lastLoaded;

    private static HashSet<string> _lastLoaded = new();

    public static AstProgram Load(string entryPath)
    {
        return Load(entryPath, null);
    }

    public static AstProgram Load(string entryPath, string? entryContent)
    {
        var entry = Path.GetFullPath(entryPath);
        var loaded = new HashSet<string>();
        var decls = new List<Stmt>();
        LoadFile(entry, decls, loaded, true, entryContent);
        _lastLoaded = loaded;
        return new AstProgram(decls);
    }

    public static List<SourceError> ParseErrors { get; } = new();

    public static AstProgram LoadTolerant(string entryPath, string entryContent)
    {
        ParseErrors.Clear();
        Tolerant = true;
        try
        {
            return Load(entryPath, entryContent);
        }
        finally
        {
            Tolerant = false;
        }
    }

    private static void LoadFile(string path, List<Stmt> decls, HashSet<string> loaded, bool isEntry,
        string? entryContent = null, string? importedBy = null, int stmtLine = 1, int stmtCol = 1)
    {
        var full = Path.GetFullPath(path);
        if (!loaded.Add(full)) return;

        if (!File.Exists(full) && !(isEntry && entryContent != null))
        {

            if (isEntry || !Tolerant || path.EndsWith(".hs", StringComparison.OrdinalIgnoreCase))
                throw new SourceError(stmtLine, stmtCol, $"cannot find file '{path}'").At(importedBy ?? full);
            return;
        }

        var text = isEntry && entryContent != null ? entryContent : File.ReadAllText(full);
        var lexer = new Lexer(text);
        if (Tolerant) lexer.Tolerant = true;
        var toks = lexer.Tokenize();
        if (Tolerant)
            foreach (var le in lexer.Errors)
                ParseErrors.Add(le.At(full));
        var parser = new Parser(toks);
        if (Tolerant) parser.Tolerant = true;
        var program = parser.Parse();
        if (Tolerant)
            foreach (var e in parser.Errors)
                ParseErrors.Add(e.At(full));

        foreach (var s in program.Stmts)
        {
            switch (s)
            {
                case ImportStmt im:

                    try
                    {
                        LoadFile(Resolve(full, im.Path), decls, loaded, false, null, full, im.Line, im.Col);
                    }
                    catch (SourceError ex) when (Tolerant)
                    {
                        ParseErrors.Add(ex.At(ex.File ?? full));
                    }
                    break;

                case FnDecl f:
                    if (!isEntry) f.SourceFile = full;
                    decls.Add(f);
                    break;

                case TypeDecl td:
                    if (!isEntry)
                    {
                        td.SourceFile = full;
                        foreach (var m in td.Methods) m.SourceFile = full;
                    }
                    decls.Add(td);
                    break;

                case EnumDecl ed:
                    if (!isEntry) ed.SourceFile = full;
                    decls.Add(ed);
                    break;

                default:
                    if (!isEntry)
                        throw new SourceError(s.Line, s.Col, "imported files can only contain declarations").At(full);
                    decls.Add(s);
                    break;
            }
        }
    }

    private static string Resolve(string importing, string path)
    {
        var dir = Path.GetDirectoryName(importing) ?? ".";
        var candidate = TryDir(dir, path);
        if (candidate != null) return candidate;

        foreach (var s in SearchDirs)
        {
            candidate = TryDir(s, path);
            if (candidate != null) return candidate;
        }

        return Path.Combine(dir, path);
    }

    private static string? TryDir(string dir, string path)
    {
        var direct = Path.Combine(dir, path);
        if (File.Exists(direct)) return direct;

        if (!path.EndsWith(".hs", StringComparison.OrdinalIgnoreCase) && File.Exists(direct + ".hs"))
            return direct + ".hs";

        var slashed = Path.Combine(dir, path.Replace('.', Path.DirectorySeparatorChar) + ".hs");
        if (File.Exists(slashed)) return slashed;

        return null;
    }
}

