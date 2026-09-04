using System.Text;
using HSharp.Lexing;
using HSharp.Syntax;

namespace HSharp.Analysis;

public static class Formatter
{
    private static readonly HashSet<string> ControlKw =
        new() { "if", "while", "for", "foreach", "lock", "catch", "switch" };

    private static readonly HashSet<string> TypeKw =
        new() { "int", "float", "bool", "string", "void", "list", "buffer", "map", "task" };

    private static readonly HashSet<string> AssignOps =
        new() { "=", "+=", "-=", "*=", "/=", "%=" };

    private static readonly HashSet<string> BinOps =
        new() { "==", "!=", "<", "<=", ">", ">=", "&&", "||", "+", "-", "*", "/", "%", "??", "?" };

    private static bool Wordy(Token t) => IdentLike(t);

    private static bool IdentLike(Token t) =>
        t.Kind == Tok.Ident || t.Kind is >= Tok.KwVar and <= Tok.TyBuffer;

    private static string TokenText(Token t)
    {
        if (t.Kind == Tok.Str)
            return "\"" + EscapeStr((string)t.Value!) + "\"";
        if (t.Kind == Tok.Interp)
        {
            var sb = new StringBuilder("$\"");
            foreach (var p in (List<InterpPart>)t.Value!)
            {
                if (p.IsExpr) sb.Append('{').Append(p.Text).Append('}');
                else sb.Append(EscapeStr(p.Text).Replace("{", "{{").Replace("}", "}}"));
            }
            sb.Append('"');
            return sb.ToString();
        }
        return t.Text;
    }

    private static string EscapeStr(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t")
        .Replace("\r", "\\r");

    public static string Format(string src)
    {
        List<Token> toks;
        try
        {
            toks = new Lexer(src).Tokenize();
        }
        catch (SourceError)
        {
            return src;
        }

        var sb = new StringBuilder();
        int indent = 0;
        bool lineStart = true;
        int prevLine = 1;
        Token prev = new(Tok.EOF, "", null, 0, 0);
        Token prev2 = prev;
        int parenDepth = 0;

        var braceInline = new Stack<bool>();

        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind == Tok.EOF) break;

            if (t.Kind == Tok.RBrace)
            {
                bool inlineClose = braceInline.Count > 0 && braceInline.Pop();
                indent = Math.Max(0, indent - 1);
                if (inlineClose)
                {
                    sb.Append(" }");
                    lineStart = false;
                }
                else
                {
                    if (!lineStart) sb.Append('\n');
                    sb.Append(new string(' ', indent * 4));
                    sb.Append('}');

                    sb.Append('\n');
                    lineStart = true;
                }
                prev2 = prev;
                prev = t;
                prevLine = t.Line;
                continue;
            }

            if (t.Kind == Tok.LBrace)
            {
                bool inline = prev.Kind is Tok.LParen or Tok.Comma or Tok.LBrace or Tok.Gt
                    || prev.Text is "=" or "return" or "??"
                    || (IdentLike(prev) && prev2.Kind is not (Tok.KwClass or Tok.KwStruct or Tok.KwEnum));
                braceInline.Push(inline);
                indent++;
                if (!inline && !lineStart)
                {
                    sb.Append('\n');
                }
                if (!inline) sb.Append(new string(' ', (indent - 1) * 4));
                else if (!lineStart) sb.Append(' ');
                sb.Append('{');
                if (!inline)
                {

                    sb.Append('\n');
                    lineStart = true;
                }
                else lineStart = false;
                prev2 = prev;
                prev = t;
                prevLine = t.Line;
                continue;
            }

            if (t.Text is "else" or "catch" && parenDepth == 0 && !lineStart)
            {
                sb.Append('\n');
                lineStart = true;
            }

            if (!lineStart && t.Line > prevLine)
            {
                if (parenDepth > 0)
                    sb.Append(' ');
                else
                {
                    sb.Append('\n');
                    lineStart = true;
                }
            }

            if (lineStart)
            {
                if (prevLine > 0 && t.Line - prevLine > 1 && sb.Length > 0) sb.Append('\n');
                sb.Append(new string(' ', indent * 4));
                lineStart = false;
            }
            else if (SpaceBetween(prev, t, i + 1 < toks.Count ? toks[i + 1] : null,
                i + 2 < toks.Count ? toks[i + 2] : null, parenDepth))
            {
                sb.Append(' ');
            }

            if (t.Kind == Tok.LParen) parenDepth++;
            else if (t.Kind == Tok.RParen) parenDepth = Math.Max(0, parenDepth - 1);

            sb.Append(TokenText(t));

            if (t.Kind == Tok.Semi && parenDepth == 0)
            {
                sb.Append('\n');
                lineStart = true;
            }
            else lineStart = false;

            prev2 = prev;
            prev = t;
            prevLine = t.Line;
        }

        sb.Append('\n');
        return sb.ToString();
    }

    private static bool SpaceBetween(Token prev, Token t, Token? next1, Token? next2, int parenDepth)
    {

        string p = prev.Kind is Tok.Str or Tok.Interp ? "\0s" : prev.Text;
        string c = t.Kind is Tok.Str or Tok.Interp ? "\0s" : t.Text;

        switch (c)
        {
            case ";":
            case ",":
            case ")":
            case "]":
            case ".":
            case "?.":
            case "++":
            case "--":
            case ":":
                return false;
            case "(": return ControlKw.Contains(p);
            case "?":

                if (IdentLike(prev))
                {
                    bool annotation = next1 != null && IdentLike(next1)
                        && (next2 == null || next2.Kind == Tok.Ident
                            || next2.Text is ";" or "," or ")" or "]" or "=" or "." or "[" or "??");
                    return !annotation;
                }
                return true;
        }

        switch (p)
        {
            case "(": return c == "(";
            case "[":
            case ".":
            case "?.":
            case "!":
                return false;
            case "{":

                return c != ";";
            case "=>":
            case ":":
            case ",":
            case ";":
                return true;
        }

        if ((c == "-" || c == "+") && !(IdentLike(prev) || prev.Kind is Tok.Int or Tok.Float
            || prev.Text is ")" or "]" or "++" or "--"))
            return false;

        if (AssignOps.Contains(p) || AssignOps.Contains(c)) return true;
        if (BinOps.Contains(p) || BinOps.Contains(c)) return true;

        if (IdentLike(prev) && (IdentLike(t) || t.Kind is Tok.Str or Tok.Interp)) return true;

        return false;
    }
}

