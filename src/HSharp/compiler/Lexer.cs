namespace HSharp;

public enum Tok
{
    EOF, Ident, Int, Float, Str, Interp,
    KwVar, KwIf, KwElse, KwWhile, KwFor, KwForeach, KwIn, KwReturn, KwTry, KwCatch,
    KwBreak, KwContinue, KwAwait,
    KwTrue, KwFalse, KwMove, KwVoid,
    TyInt, TyFloat, TyBool, TyString, TyList,
    LBrace, RBrace, LParen, RParen, LBracket, RBracket, Comma, Semi, Dot,
    Assign, Plus, Minus, Star, Slash, Percent, FatArrow,
    Eq, NotEq, Lt, LtEq, Gt, GtEq, AndAnd, OrOr, Not,
    PlusPlus, MinusMinus, PlusEq, MinusEq, StarEq, SlashEq, PercentEq
}

public sealed record InterpPart(bool IsExpr, string Text);
public sealed record Token(Tok Kind, string Text, object? Value, int Line, int Col);

// plain token stream, one token per call. positions are 1-based line/col
public sealed class Lexer(string src)
{
    private static readonly Dictionary<string, Tok> Keywords = new()
    {
        ["var"] = Tok.KwVar, ["if"] = Tok.KwIf, ["else"] = Tok.KwElse, ["while"] = Tok.KwWhile,
        ["for"] = Tok.KwFor, ["foreach"] = Tok.KwForeach, ["in"] = Tok.KwIn, ["return"] = Tok.KwReturn,
        ["try"] = Tok.KwTry, ["catch"] = Tok.KwCatch, ["true"] = Tok.KwTrue, ["false"] = Tok.KwFalse,
        ["break"] = Tok.KwBreak, ["continue"] = Tok.KwContinue, ["await"] = Tok.KwAwait,
        ["move"] = Tok.KwMove, ["void"] = Tok.KwVoid,
        ["int"] = Tok.TyInt, ["float"] = Tok.TyFloat, ["bool"] = Tok.TyBool,
        ["string"] = Tok.TyString, ["list"] = Tok.TyList
    };

    private readonly string _s = src;
    private int _i, _line = 1, _col = 1;

    public List<Token> Tokenize()
    {
        var toks = new List<Token>();
        while (true)
        {
            var t = Next();
            toks.Add(t);
            if (t.Kind == Tok.EOF) return toks;
        }
    }

    private char Cur => _i < _s.Length ? _s[_i] : '\0';
    private char Peek(int n) => _i + n < _s.Length ? _s[_i + n] : '\0';

    private char Take()
    {
        char c = _s[_i++];
        if (c == '\n') { _line++; _col = 1; } else _col++;
        return c;
    }

    private Token Make(Tok kind, string text, object? value, int line, int col) => new(kind, text, value, line, col);

    private void Err(int line, int col, string msg) => throw new SourceError(line, col, msg);

    private Token Next()
    {
        SkipWs();
        int line = _line, col = _col;
        if (_i >= _s.Length) return Make(Tok.EOF, "", null, line, col);

        char c = Cur;
        if (char.IsLetter(c) || c == '_') return Word(line, col);
        if (char.IsDigit(c)) return Number(line, col);
        if (c == '"') return Str(line, col);
        if (c == '$' && Peek(1) == '"') return Interp(line, col);
        return Punct(line, col);
    }

    private void SkipWs()
    {
        while (_i < _s.Length)
        {
            char c = Cur;
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { Take(); continue; }
            if (c == '/' && Peek(1) == '/')
            {
                while (_i < _s.Length && Cur != '\n') Take();
                continue;
            }
            if (c == '/' && Peek(1) == '*')
            {
                int line = _line, col = _col;
                Take(); Take();
                while (_i < _s.Length && !(Cur == '*' && Peek(1) == '/')) Take();
                if (_i >= _s.Length) Err(line, col, "unterminated block comment");
                Take(); Take();
                continue;
            }
            break;
        }
    }

    private Token Word(int line, int col)
    {
        int start = _i;
        while (char.IsLetterOrDigit(Cur) || Cur == '_') Take();
        string word = _s[start.._i];
        return Keywords.TryGetValue(word, out var kw)
            ? Make(kw, word, null, line, col)
            : Make(Tok.Ident, word, null, line, col);
    }

    private Token Number(int line, int col)
    {
        int start = _i;
        while (char.IsDigit(Cur)) Take();
        bool isFloat = false;
        if (Cur == '.' && char.IsDigit(Peek(1)))
        {
            isFloat = true;
            Take();
            while (char.IsDigit(Cur)) Take();
        }
        string text = _s[start.._i];
        return isFloat
            ? Make(Tok.Float, text, double.Parse(text, System.Globalization.CultureInfo.InvariantCulture), line, col)
            : Make(Tok.Int, text, int.Parse(text), line, col);
    }

    private int Escape(int line, int col)
    {
        Take();
        char c = Cur;
        switch (c)
        {
            case 'n': Take(); return '\n';
            case 't': Take(); return '\t';
            case 'r': Take(); return '\r';
            case '0': Take(); return '\0';
            case '\\': Take(); return '\\';
            case '"': Take(); return '"';
            case '{': Take(); return '{';
            case '}': Take(); return '}';
            default: Err(line, col, $"unknown escape '\\{c}'"); return 0;
        }
    }

    private Token Str(int line, int col)
    {
        Take();
        var sb = new System.Text.StringBuilder();
        while (Cur != '"')
        {
            if (Cur == '\0' || Cur == '\n') Err(line, col, "unterminated string");
            sb.Append(Cur == '\\' ? (char)Escape(line, col) : Take());
        }
        Take();
        return Make(Tok.Str, sb.ToString(), sb.ToString(), line, col);
    }

    // $"..." -> parts of literal text and raw expression source, the parser
    // re-lexes the expression parts on its own
    private Token Interp(int line, int col)
    {
        Take(); Take();
        var parts = new List<InterpPart>();
        var sb = new System.Text.StringBuilder();

        while (true)
        {
            char c = Cur;
            if (c == '\0' || c == '\n') Err(line, col, "unterminated interpolated string");

            if (c == '"') { Take(); break; }
            if (c == '\\') { sb.Append((char)Escape(line, col)); continue; }
            if (c == '{' && Peek(1) == '{') { Take(); Take(); sb.Append('{'); continue; }
            if (c == '}' && Peek(1) == '}') { Take(); Take(); sb.Append('}'); continue; }
            if (c == '}') Err(line, col, "unexpected '}' in interpolated string; use '}}'");

            if (c == '{')
            {
                if (sb.Length > 0) { parts.Add(new InterpPart(false, sb.ToString())); sb.Clear(); }
                Take();
                parts.Add(new InterpPart(true, CaptureExpr(line, col)));
                continue;
            }

            sb.Append(Take());
        }

        if (sb.Length > 0) parts.Add(new InterpPart(false, sb.ToString()));
        if (parts.Count == 0) parts.Add(new InterpPart(false, ""));
        return Make(Tok.Interp, "", parts, line, col);
    }

    // consumes an expression inside { }, aware of nested braces and strings
    private string CaptureExpr(int line, int col)
    {
        int depth = 1;
        int start = _i;
        while (true)
        {
            char c = Cur;
            if (c == '\0' || c == '\n') Err(line, col, "unterminated '{' in interpolated string");
            if (c == '"')
            {
                Take();
                while (Cur != '"' && Cur != '\0' && Cur != '\n') Take();
                if (Cur != '"') Err(line, col, "unterminated string inside interpolated expression");
                Take();
                continue;
            }
            Take();
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return _s[start..(_i - 1)];
            }
        }
    }

    private Token Punct(int line, int col)
    {
        int start = _i;
        char c = Take();
        Tok kind;
        switch (c)
        {
            case '{': kind = Tok.LBrace; break;
            case '}': kind = Tok.RBrace; break;
            case '(': kind = Tok.LParen; break;
            case ')': kind = Tok.RParen; break;
            case '[': kind = Tok.LBracket; break;
            case ']': kind = Tok.RBracket; break;
            case ',': kind = Tok.Comma; break;
            case ';': kind = Tok.Semi; break;
            case '.': kind = Tok.Dot; break;
            case '+': kind = Match('=') ? Tok.PlusEq : Match('+') ? Tok.PlusPlus : Tok.Plus; break;
            case '-': kind = Match('=') ? Tok.MinusEq : Match('-') ? Tok.MinusMinus : Tok.Minus; break;
            case '*': kind = Match('=') ? Tok.StarEq : Tok.Star; break;
            case '/': kind = Match('=') ? Tok.SlashEq : Tok.Slash; break;
            case '%': kind = Match('=') ? Tok.PercentEq : Tok.Percent; break;
            case '=': kind = Match('=') ? Tok.Eq : Match('>') ? Tok.FatArrow : Tok.Assign; break;
            case '!': kind = Match('=') ? Tok.NotEq : Tok.Not; break;
            case '<': kind = Match('=') ? Tok.LtEq : Tok.Lt; break;
            case '>': kind = Match('=') ? Tok.GtEq : Tok.Gt; break;
            case '&': if (!Match('&')) Err(line, col, "unexpected '&'; did you mean '&&'?"); kind = Tok.AndAnd; break;
            case '|': if (!Match('|')) Err(line, col, "unexpected '|'; did you mean '||'?"); kind = Tok.OrOr; break;
            default: Err(line, col, $"unexpected character '{c}'"); kind = Tok.EOF; break;
        }
        return Make(kind, _s[start.._i], null, line, col);
    }

    private bool Match(char c)
    {
        if (Cur != c) return false;
        Take();
        return true;
    }
}
