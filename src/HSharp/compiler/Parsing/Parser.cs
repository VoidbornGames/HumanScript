using HSharp.Lexing;
using HSharp.Syntax;
using Index = HSharp.Syntax.Index;

namespace HSharp.Parsing;

// recursive descent. in tolerant mode (editor) broken statements, blocks
// and call argument lists are salvaged so analysis keeps running mid-edit
public sealed class Parser(List<Token> tokens)
{
    private readonly List<Token> _t = tokens;
    private int _pos;

    public bool Tolerant { get; set; }
    public List<SourceError> Errors { get; } = new();

    public AstProgram Parse()
    {
        var stmts = new List<Stmt>();
        while (!Check(Tok.EOF))
        {
            if (Tolerant)
            {
                int before = _pos;
                Stmt? s = null;
                try
                {
                    s = ParseTopLevel();
                }
                catch (SourceError e)
                {
                    Errors.Add(e);
                }
                if (s != null) stmts.Add(s);
                if (_pos == before)
                {

                    Take();
                    while (!Check(Tok.EOF) && !Check(Tok.Semi) && !Check(Tok.RBrace) && !Check(Tok.LBrace)) Take();
                    Match(Tok.Semi);
                }
            }
            else
            {
                stmts.Add(ParseTopLevel());
            }
        }
        return new AstProgram(stmts);
    }

    private Stmt ParseTopLevel()
    {
        if (Check(Tok.KwImport))
            return ImportStmt();
        if (Check(Tok.KwPublic))
        {
            Take();
            if (Check(Tok.KwEnum)) return EnumDecl(true);
            if (Check(Tok.KwClass) || Check(Tok.KwStruct)) return TypeDecl(true);
            if (LooksLikeFn()) return FnDecl(publicMod: true);
            throw new SourceError(Cur.Line, Cur.Col, "expected a declaration after 'public'");
        }
        if (Check(Tok.KwEnum))
            return EnumDecl();
        if (Check(Tok.KwClass) || Check(Tok.KwStruct))
            return TypeDecl();
        if (LooksLikeFn())
            return FnDecl();
        return Statement();
    }

    private Stmt ImportStmt()
    {
        int line = Cur.Line, col = Cur.Col;
        Take();

        string path;
        if (Check(Tok.Str))
        {
            path = (string)Take().Value!;
        }
        else
        {
            var parts = new List<string> { Expect(Tok.Ident, "imported name").Text };
            while (Match(Tok.Dot))
                parts.Add(Expect(Tok.Ident, "name after '.'").Text);
            path = string.Join(".", parts);
        }

        if (!Match(Tok.Semi))
        {
            if (Tolerant && Cur.Line > line)
                Errors.Add(new SourceError(line, col, "missing ';' after import"));
            else
                Expect(Tok.Semi, "';' after import");
        }
        return new ImportStmt(path, line, col);
    }

    private bool LooksLikeFn()
    {
        if (Check(Tok.KwVoid)) return true;
        if (Check(Tok.Ident)) return Check2(Tok.Ident) && Peek(2).Kind is Tok.LParen or Tok.Lt;
        if (!AtTypeStart()) return false;

        int save = _pos;
        try
        {
            ParseType("type");
            return Check(Tok.Ident) && Check2(Tok.LParen);
        }
        catch (SourceError)
        {
            return false;
        }
        finally
        {
            _pos = save;
        }
    }

    private Token Cur => _t[_pos];
    private Token Peek(int n) => _pos + n < _t.Count ? _t[_pos + n] : _t[^1];
    private bool Check(Tok k) => Cur.Kind == k;
    private bool Check2(Tok k) => Peek(1).Kind == k;
    private Token Take() => _t[_pos++];
    private bool Match(Tok k) { if (Check(k)) { _pos++; return true; } return false; }

    private Token Expect(Tok k, string what)
    {
        if (!Check(k))
            throw new SourceError(Cur.Line, Cur.Col, $"expected {what} but got '{Cur.Text}'");
        return Take();
    }

    private static bool AtTypeStart(Token t) =>
        t.Kind is Tok.TyInt or Tok.TyFloat or Tok.TyBool or Tok.TyString or Tok.TyList or Tok.TyBuffer;
    private bool AtTypeStart() => AtTypeStart(Cur);

    private Ty ParseType(string what)
    {
        Ty ty;
        switch (Cur.Kind)
        {
            case Tok.TyInt: Take(); ty = Ty.Int; break;
            case Tok.TyFloat: Take(); ty = Ty.Float; break;
            case Tok.TyBool: Take(); ty = Ty.Bool; break;
            case Tok.TyString: Take(); ty = Ty.Str; break;
            case Tok.TyList:
                Take();
                Expect(Tok.Lt, "'<' in list type");
                var elem = ParseType("list element type");
                Expect(Tok.Gt, "'>' in list type");
                ty = Ty.List(elem);
                break;
            case Tok.TyBuffer:
                Take();
                ty = Ty.Buffer;
                break;
            case Tok.Ident:
                {

                    var name = Take().Text;
                    if (name == "map")
                    {
                        Expect(Tok.Lt, "'<' in map type");
                        var kt = ParseType("map key type");
                        Expect(Tok.Comma, "',' in map type");
                        var vt = ParseType("map value type");
                        Expect(Tok.Gt, "'>' in map type");
                        ty = Ty.Map(kt, vt);
                    }
                    else if (name == "task")
                    {
                        Expect(Tok.Lt, "'<' in task type");
                        var ret = ParseType("task result type");
                        Expect(Tok.Gt, "'>' in task type");
                        ty = Ty.Task(ret);
                    }
                    else
                    {
                        ty = name switch
                        {
                            "Client" or "listener" or "udp" or "httpl" or "rawhttpl" or "HttpPacket" or "RawHttpPacket" or "StringBuilder"
                                => Ty.Handle(name),
                            _ => Ty.Named(name)
                        };
                    }
                    break;
                }
            default:
                throw new SourceError(Cur.Line, Cur.Col, $"expected {what} but got '{Cur.Text}'");
        }
        if (Match(Tok.Question)) ty = Ty.NullableOf(ty);
        return ty;
    }

    private Stmt EnumDecl(bool pub = false)
    {
        int line = Cur.Line, col = Cur.Col;
        Take();
        var name = Expect(Tok.Ident, "enum name");
        Expect(Tok.LBrace, "'{' after enum name");

        var members = new List<EnumMember>();
        var seen = new HashSet<string>();
        int next = 0;

        while (!Check(Tok.RBrace) && !Check(Tok.EOF))
        {
            var m = Expect(Tok.Ident, "enum member name");
            int value = next;
            if (Match(Tok.Assign))
                value = (int)Expect(Tok.Int, "enum value")?.Value!;
            next = value + 1;

            if (!seen.Add(m.Text))
                throw new SourceError(m.Line, m.Col, $"duplicate enum member '{m.Text}'");
            members.Add(new EnumMember(m.Text, value, m.Line, m.Col));

            if (!Match(Tok.Comma)) break;
        }

        if (Check(Tok.EOF) && Tolerant)
        {
            Errors.Add(new SourceError(Cur.Line, Cur.Col, "missing '}' before end of file"));
            if (members.Count > 0) return new EnumDecl(name.Text, members, line, col, pub);
        }
        Expect(Tok.RBrace, "'}'");

        if (members.Count == 0)
            throw new SourceError(line, col, $"enum {name.Text} has no members");
        return new EnumDecl(name.Text, members, line, col, pub);
    }

    private Stmt TypeDecl(bool pub = false)
    {
        int line = Cur.Line, col = Cur.Col;
        var kind = Take().Kind == Tok.KwClass ? UserKind.Class : UserKind.Struct;
        var name = Expect(Tok.Ident, "type name");
        Expect(Tok.LBrace, "'{' after type name");

        var fields = new List<Field>();
        var methods = new List<FnDecl>();
        var seen = new HashSet<string>();

        while (!Check(Tok.RBrace) && !Check(Tok.EOF))
        {
            var mpub = Match(Tok.KwPublic);

            Ty ty;
            if (Check(Tok.KwVoid)) { Take(); ty = Ty.Void; }
            else ty = ParseType("member type");

            var member = Expect(Tok.Ident, "member name");
            if (!seen.Add(member.Text))
                throw new SourceError(member.Line, member.Col, $"duplicate member '{member.Text}' in {name.Text}");

            if (Check(Tok.LParen) || Check(Tok.Lt))
            {
                var tps = TypeParams();
                Expect(Tok.LParen, "'(' after method name");
                var ps = Params();
                var body = Block();
                methods.Add(new FnDecl(ty, member.Text, ps, body, member.Line, member.Col, tps, mpub));
            }
            else
            {
                if (mpub)
                    throw new SourceError(member.Line, member.Col, "fields cannot be public");
                if (ty == Ty.Void)
                    throw new SourceError(member.Line, member.Col, "fields cannot be void");
                Expect(Tok.Semi, "';' after field");
                fields.Add(new Field(ty, member.Text, member.Line, member.Col));
            }
        }

        if (Check(Tok.EOF) && Tolerant)
        {
            Errors.Add(new SourceError(Cur.Line, Cur.Col, "missing '}' before end of file"));
            if (fields.Count > 0 || methods.Count > 0)
                return new TypeDecl(kind, name.Text, fields, methods, line, col, pub);
        }
        Expect(Tok.RBrace, "'}'");

        if (fields.Count == 0 && methods.Count == 0)
            throw new SourceError(line, col, $"{name.Text} has no members");
        return new TypeDecl(kind, name.Text, fields, methods, line, col, pub);
    }

    private FnDecl FnDecl(bool publicMod = false)
    {
        int line = Cur.Line, col = Cur.Col;
        Ty ret;
        if (Check(Tok.KwVoid)) { Take(); ret = Ty.Void; }
        else ret = ParseType("return type");
        if (Match(Tok.Question)) ret = Ty.NullableOf(ret);
        var name = Expect(Tok.Ident, "function name");
        var tps = TypeParams();
        Expect(Tok.LParen, "'(' after function name");
        var ps = Params();
        var body = Block();
        return new FnDecl(ret, name.Text, ps, body, line, col, tps, publicMod);
    }

    private List<string>? TypeParams()
    {
        if (!Match(Tok.Lt)) return null;

        var names = new List<string>();
        do
        {
            var tp = Expect(Tok.Ident, "type parameter name");
            if (names.Contains(tp.Text))
                throw new SourceError(tp.Line, tp.Col, $"duplicate type parameter '{tp.Text}'");
            names.Add(tp.Text);
        } while (Match(Tok.Comma));
        Expect(Tok.Gt, "'>' after type parameters");
        return names;
    }

    private List<Ty>? TryTypeArgs()
    {
        int save = _pos;
        try
        {
            Take();
            var tys = new List<Ty>();
            do { tys.Add(ParseType("type argument")); } while (Match(Tok.Comma));
            if (!Check(Tok.Gt)) { _pos = save; return null; }
            Take();
            if (!Check(Tok.LParen)) { _pos = save; return null; }
            return tys;
        }
        catch (SourceError)
        {
            _pos = save;
            return null;
        }
    }

    private List<Param> Params()
    {
        var ps = new List<Param>();
        if (!Check(Tok.RParen))
        {
            do
            {
                bool mv = Match(Tok.KwMove);
                var pt = ParseType("parameter type");
                var pn = Expect(Tok.Ident, "parameter name");
                ps.Add(new Param(pt, pn.Text, mv, pn.Line, pn.Col));
            } while (Match(Tok.Comma));
        }
        Expect(Tok.RParen, "')' after parameters");
        return ps;
    }

    private List<Stmt> Block()
    {
        Expect(Tok.LBrace, "'{'");
        var stmts = new List<Stmt>();
        while (!Check(Tok.RBrace) && !Check(Tok.EOF))
            stmts.Add(Statement());

        if (Check(Tok.EOF) && Tolerant)
        {
            Errors.Add(new SourceError(Cur.Line, Cur.Col, "missing '}' before end of file"));
            return stmts;
        }

        Expect(Tok.RBrace, "'}'");
        return stmts;
    }

    private void EndStmt(int startLine)
    {
        if (Match(Tok.Semi)) return;
        if (Tolerant && (Cur.Line > startLine || Cur.Kind == Tok.EOF))
            Errors.Add(new SourceError(startLine, Cur.Col, "missing ';'"));
        else
            Expect(Tok.Semi, "';'");
    }

    private Stmt Statement()
    {
        int line = Cur.Line, col = Cur.Col;

        if (Check(Tok.KwVar))
        {
            Take();
            var name = Expect(Tok.Ident, "variable name");
            Expect(Tok.Assign, "'=' (declarations need an initializer)");
            var init = Expression();
            EndStmt(line);
            return new VarDecl(name.Text, null, init, line, col, name.Line, name.Col);
        }

        if (AtTypeStart() || Check(Tok.Ident))
        {
            int save = _pos;
            Ty ty;
            try { ty = ParseType("type"); }
            catch (SourceError) { _pos = save; return ExprStatement(); }

            if (Check(Tok.Ident) && Check2(Tok.Assign))
            {
                var name = Take();
                Expect(Tok.Assign, "'=' (declarations need an initializer)");
                var init = Expression();
                EndStmt(line);
                return new VarDecl(name.Text, ty, init, line, col);
            }
            _pos = save;
            if (!Check(Tok.Ident))
                return ExprStatement();
        }

        switch (Cur.Kind)
        {
            case Tok.KwIf: return IfStmt();
            case Tok.KwWhile:
                {
                    Take();
                    Expect(Tok.LParen, "'(' after 'while'");
                    var cond = Expression();
                    Expect(Tok.RParen, "')' after while condition");
                    return new While(cond, Block(), line, col);
                }
            case Tok.KwFor: return ForStmt();
            case Tok.KwForeach:
                {
                    Take();
                    Expect(Tok.LParen, "'(' after 'foreach'");
                    Expect(Tok.KwVar, "'var' in foreach");
                    var name = Expect(Tok.Ident, "loop variable name");
                    Expect(Tok.KwIn, "'in' in foreach");
                    var iter = Expression();
                    Expect(Tok.RParen, "')' after foreach header");
                    return new Foreach(name.Text, iter, Block(), line, col, name.Line, name.Col);
                }
            case Tok.KwReturn:
                {
                    Take();
                    Expr? value = Check(Tok.Semi) ? null : Expression();
                    EndStmt(line);
                    return new Return(value, line, col);
                }
            case Tok.KwBreak:
                {
                    Take();
                    EndStmt(line);
                    return new Break(line, col);
                }
            case Tok.KwContinue:
                {
                    Take();
                    EndStmt(line);
                    return new Continue(line, col);
                }
            case Tok.KwTry:
                {
                    Take();
                    var tryBody = Block();
                    Expect(Tok.KwCatch, "'catch'");

                    string? errName = null;
                    if (Match(Tok.LParen))
                    {
                        errName = Expect(Tok.Ident, "error variable name").Text;
                        Expect(Tok.RParen, "')' after error variable");
                    }
                    var catchBody = Block();
                    return new TryCatch(tryBody, catchBody, line, col, errName);
                }
            case Tok.LBrace:
                return new BlockStmt(Block(), line, col);

            case Tok.KwLock:
                {
                    Take();
                    Expect(Tok.LParen, "'(' after 'lock'");
                    var target = Expression();
                    Expect(Tok.RParen, "')' after lock target");
                    var body = Block();
                    return new Lock(target, body, line, col);
                }
        }

        return ExprStatement();
    }

    private Stmt IfStmt()
    {
        int line = Cur.Line, col = Cur.Col;
        Take();
        Expect(Tok.LParen, "'(' after 'if'");
        var cond = Expression();
        Expect(Tok.RParen, "')' after if condition");
        var then = Block();

        List<Stmt>? els = null;
        if (Match(Tok.KwElse))
        {
            els = Check(Tok.KwIf)
                ? new List<Stmt> { IfStmt() }
                : Block();
        }
        return new If(cond, then, els, line, col);
    }

    private Stmt ForStmt()
    {
        int line = Cur.Line, col = Cur.Col;
        Take();
        Expect(Tok.LParen, "'(' after 'for'");

        Stmt? init = null;
        if (!Check(Tok.Semi))
        {
            if (Check(Tok.KwVar))
            {
                var vLine = Cur.Line; var vCol = Cur.Col;
                Take();
                var name = Expect(Tok.Ident, "variable name");
                Expect(Tok.Assign, "'='");
                var initExpr = Expression();
                init = new VarDecl(name.Text, null, initExpr, vLine, vCol);
            }
            else
            {
                init = ExprOrAssignCore();
            }
        }
        Expect(Tok.Semi, "';' in for header");

        Expr? cond = null;
        if (!Check(Tok.Semi)) cond = Expression();
        Expect(Tok.Semi, "';' in for header");

        Stmt? step = null;
        if (!Check(Tok.RParen)) step = ExprOrAssignCore(allowIncDec: true);

        Expect(Tok.RParen, "')' after for header");
        return new For(init, cond, step, Block(), line, col);
    }

    private Stmt ExprStatement()
    {
        int line = Cur.Line, col = Cur.Col;
        var stmt = ExprOrAssignCore(allowIncDec: true);
        EndStmt(line);
        return stmt;
    }

    private Stmt ExprOrAssignCore(bool allowIncDec = false)
    {
        int line = Cur.Line, col = Cur.Col;
        var e = Expression();

        if (Check(Tok.Assign) || Check(Tok.PlusEq) || Check(Tok.MinusEq) || Check(Tok.StarEq) || Check(Tok.SlashEq) || Check(Tok.PercentEq))
        {
            var op = Take();
            if (e is not (Ident or Index or Prop))
                throw new SourceError(line, col, "invalid assignment target");
            var value = Expression();
            return new Assign(e, op.Kind switch
            {
                Tok.Assign => "=", Tok.PlusEq => "+=", Tok.MinusEq => "-=",
                Tok.StarEq => "*=", Tok.SlashEq => "/=", _ => "%="
            }, value, line, col);
        }

        if (allowIncDec && (Check(Tok.PlusPlus) || Check(Tok.MinusMinus)))
        {
            var inc = Take().Kind == Tok.PlusPlus;
            if (e is not (Ident or Index or Prop))
                throw new SourceError(line, col, "invalid '++'/'--' target");
            return new IncDec(e, inc, line, col);
        }

        return new ExprStmt(e, line, col);
    }

    private Expr Expression() => CoalesceExpr();

    private Expr CoalesceExpr()
    {
        var l = TernaryExpr();
        while (Check(Tok.QuestionQuestion))
        {
            int line = Cur.Line, col = Cur.Col;
            Take();
            l = new Coalesce(l, TernaryExpr(), line, col);
        }
        return l;
    }

    private Expr TernaryExpr()
    {
        var cond = OrExpr();
        if (!Check(Tok.Question)) return cond;

        int line = Cur.Line, col = Cur.Col;
        Take();
        var then = Expression();
        Expect(Tok.Colon, "':' in conditional expression");
        var els = CoalesceExpr();
        return new Cond(cond, then, els, line, col);
    }

    // switch (v) { case a: x, case b: y, default: z } desugars to a nested
    // conditional chain, so the checker and codegen need nothing new. the
    // scrutinee expression is evaluated once per case, keep it side-effect
    // free
    private Expr SwitchExpr()
    {
        int line = Cur.Line, col = Cur.Col;
        Take();
        Expect(Tok.LParen, "'(' after 'switch'");
        var scrut = Expression();
        Expect(Tok.RParen, "')' after switch value");
        Expect(Tok.LBrace, "'{' before switch cases");

        var cases = new List<(Expr Test, Expr Value)>();
        Expr? dflt = null;
        while (!Check(Tok.RBrace) && !Check(Tok.EOF))
        {
            Expr? test;
            if (Match(Tok.KwCase)) test = Expression();
            else if (Match(Tok.KwDefault)) test = null;
            else throw new SourceError(Cur.Line, Cur.Col, "expected 'case' or 'default' in switch");
            Expect(Tok.Colon, "':' after the case label");
            var value = Expression();
            if (test == null)
            {
                if (dflt != null)
                    throw new SourceError(Cur.Line, Cur.Col, "switch already has a 'default' case");
                dflt = value;
            }
            else cases.Add((test, value));
            if (!Match(Tok.Comma)) break;
        }
        Expect(Tok.RBrace, "'}' after switch cases");
        if (dflt == null)
            throw new SourceError(line, col, "switch needs a 'default' case");

        Expr result = dflt;
        for (int i = cases.Count - 1; i >= 0; i--)
            result = new Cond(new Bin("==", scrut, cases[i].Test, line, col), cases[i].Value, result, line, col);
        return result;
    }

    private Expr OrExpr()
    {
        var l = AndExpr();
        while (Check(Tok.OrOr))
        {
            int line = Cur.Line, col = Cur.Col;
            Take();
            l = new Bin("||", l, AndExpr(), line, col);
        }
        return l;
    }

    private Expr AndExpr()
    {
        var l = EqExpr();
        while (Check(Tok.AndAnd))
        {
            int line = Cur.Line, col = Cur.Col;
            Take();
            l = new Bin("&&", l, EqExpr(), line, col);
        }
        return l;
    }

    private Expr EqExpr()
    {
        var l = RelExpr();
        while (Check(Tok.Eq) || Check(Tok.NotEq))
        {
            int line = Cur.Line, col = Cur.Col;
            string op = Take().Kind == Tok.Eq ? "==" : "!=";
            l = new Bin(op, l, RelExpr(), line, col);
        }
        return l;
    }

    private Expr RelExpr()
    {
        var l = AddExpr();
        while (Check(Tok.Lt) || Check(Tok.LtEq) || Check(Tok.Gt) || Check(Tok.GtEq))
        {
            int line = Cur.Line, col = Cur.Col;
            string op = Take().Kind switch { Tok.Lt => "<", Tok.LtEq => "<=", Tok.Gt => ">", _ => ">=" };
            l = new Bin(op, l, AddExpr(), line, col);
        }
        return l;
    }

    private Expr AddExpr()
    {
        var l = MulExpr();
        while (Check(Tok.Plus) || Check(Tok.Minus))
        {
            int line = Cur.Line, col = Cur.Col;
            string op = Take().Kind == Tok.Plus ? "+" : "-";
            l = new Bin(op, l, MulExpr(), line, col);
        }
        return l;
    }

    private Expr MulExpr()
    {
        var l = Unary();
        while (Check(Tok.Star) || Check(Tok.Slash) || Check(Tok.Percent))
        {
            int line = Cur.Line, col = Cur.Col;
            string op = Take().Kind switch { Tok.Star => "*", Tok.Slash => "/", _ => "%" };
            l = new Bin(op, l, Unary(), line, col);
        }
        return l;
    }

    private Expr Unary()
    {
        if (Check(Tok.Not) || Check(Tok.Minus))
        {
            int line = Cur.Line, col = Cur.Col;
            string op = Take().Kind == Tok.Not ? "!" : "-";
            return new Un(op, Unary(), line, col);
        }
        if (Check(Tok.KwAwait))
        {
            int line = Cur.Line, col = Cur.Col;
            Take();
            return new AwaitExpr(Unary(), line, col);
        }
        return Postfix();
    }

    private Expr Postfix()
    {
        var e = Primary();
        List<Ty>? typeArgs = null;

        while (true)
        {
            if (Check(Tok.LParen))
            {
                if (e is not Ident id)
                {
                    int line = Cur.Line, col = Cur.Col;
                    throw new SourceError(line, col, "only named functions can be called");
                }
                e = new Call(id.Name, Args(), id.Line, id.Col, typeArgs);
                typeArgs = null;
            }
            else if (Check(Tok.Lt) && e is Ident)
            {
                var tas = TryTypeArgs();
                if (tas == null) break;
                typeArgs = tas;
            }
            else if (Check(Tok.LBracket))
            {
                int line = Cur.Line, col = Cur.Col;
                Take();
                var idx = Expression();
                Expect(Tok.RBracket, "']'");
                e = new Index(e, idx, line, col);
            }
            else if (Check(Tok.Dot) || Check(Tok.QuestionDot))
            {
                int line = Cur.Line, col = Cur.Col;
                bool nullCond = Take().Kind == Tok.QuestionDot;

                if (!Check(Tok.Ident))
                {
                    if (Tolerant)
                    {
                        Errors.Add(new SourceError(line, col, "expected member name after '.'"));
                        break;
                    }
                    Expect(Tok.Ident, "member name");
                }

                var name = Take();
                var mty = Check(Tok.Lt) ? TryTypeArgs() : null;
                if (Check(Tok.LParen))
                {
                    var me = new Method(e, name.Text, Args(), line, col, nullCond, mty);
                    me.NameLine = name.Line; me.NameCol = name.Col;
                    e = me;
                }
                else
                {
                    var pr = new Prop(e, name.Text, line, col, nullCond);
                    pr.NameLine = name.Line; pr.NameCol = name.Col;
                    e = pr;
                }
            }
            else break;
        }
        return e;
    }

    private Expr Lambda(int line, int col)
    {
        var ps = new List<Param>();
        Expect(Tok.LParen, "'('");
        if (!Check(Tok.RParen))
        {
            do
            {
                bool mv = Match(Tok.KwMove);
                var pt = ParseType("lambda parameter type");
                var pn = Expect(Tok.Ident, "lambda parameter name");
                ps.Add(new Param(pt, pn.Text, mv, pn.Line, pn.Col));
            } while (Match(Tok.Comma));
        }
        Expect(Tok.RParen, "')'");
        Expect(Tok.FatArrow, "'=>' after lambda parameters");

        var body = new List<Stmt>();
        if (Check(Tok.LBrace))
        {
            body = Block();
        }
        else
        {
            var e = Expression();
            body.Add(new Return(e, e.Line, e.Col));
        }
        return new LamLit(ps, body, line, col);
    }

    private Expr NewInit(string name, int line, int col, bool usesNew)
    {
        var inits = new List<FieldInit>();
        if (Match(Tok.LParen))
        {
            Expect(Tok.RParen, "')' after 'new'");
            return new NewLit(name, inits, line, col, usesNew);
        }
        Expect(Tok.LBrace, "'{' or '(' after type name");
        if (!Check(Tok.RBrace))
        {
            do
            {
                var f = Expect(Tok.Ident, "field name");
                Expect(Tok.Colon, "':' after field name");
                var v = Expression();
                inits.Add(new FieldInit(f.Text, v, f.Line, f.Col));
            } while (Match(Tok.Comma));
        }
        Expect(Tok.RBrace, "'}'");
        return new NewLit(name, inits, line, col, usesNew);
    }

    private List<Expr> Args()
    {
        Expect(Tok.LParen, "'('");
        var args = new List<Expr>();
        if (!Check(Tok.RParen))
        {
            do { args.Add(Expression()); } while (Match(Tok.Comma));
        }

        if (!Check(Tok.RParen) && Tolerant && Cur.Kind == Tok.EOF)
        {
            Errors.Add(new SourceError(Cur.Line, Cur.Col, "missing ')' before end of file"));
            return args;
        }
        Expect(Tok.RParen, "')'");
        return args;
    }

    private Expr Primary()
    {
        int line = Cur.Line, col = Cur.Col;
        switch (Cur.Kind)
        {
            case Tok.Int: return new IntLit((int)Take().Value!, line, col);
            case Tok.Float: return new FloatLit((double)Take().Value!, line, col);
            case Tok.KwTrue: Take(); return new BoolLit(true, line, col);
            case Tok.KwFalse: Take(); return new BoolLit(false, line, col);
            case Tok.KwNull: Take(); return new NullLit(line, col);
            case Tok.Str: return new StrLit((string)Take().Value!, line, col);
            case Tok.Interp:
                {
                    var parts = (List<InterpPart>)Take().Value!;
                    var exprs = new List<Expr>();
                    foreach (var p in parts)
                    {
                        if (p.IsExpr)
                        {

                            var sub = new Lexer(p.Text) { StartLine = p.Line, StartCol = p.Col }.Tokenize();
                            if (sub[0].Kind == Tok.EOF)
                                throw new SourceError(line, col, "empty '{}' in interpolated string");
                            if (sub[^1].Kind != Tok.EOF)
                                throw new SourceError(sub[^1].Line, sub[^1].Col, "unexpected tokens in interpolated expression");
                            exprs.Add(new Parser(sub).Expression());
                        }
                        else if (p.Text.Length > 0)
                        {
                            exprs.Add(new StrLit(p.Text, line, col));
                        }
                    }
                    return new InterpLit(exprs, line, col);
                }
            case Tok.KwSwitch when Check2(Tok.LParen):
                return SwitchExpr();
            case Tok.Ident when Cur.Text == "map" && Check2(Tok.Lt):
                {

                    Take();
                    Take();
                    var kt = ParseType("map key type");
                    Expect(Tok.Comma, "',' in map type");
                    var vt = ParseType("map value type");
                    Expect(Tok.Gt, "'>' in map type");
                    Expect(Tok.LBrace, "'{' after map type");

                    var pairs = new List<MapPair>();
                    if (!Check(Tok.RBrace))
                    {
                        do
                        {
                            var k = Expression();
                            Expect(Tok.Colon, "':' after map key");
                            var v = Expression();
                            pairs.Add(new MapPair(k, v, k.Line, k.Col));
                        } while (Match(Tok.Comma));
                    }
                    Expect(Tok.RBrace, "'}'");
                    return new MapLit(kt, vt, pairs, line, col);
                }
            case Tok.KwNew:
                {
                    Take();
                    var tn = Expect(Tok.Ident, "type name after 'new'");
                    return NewInit(tn.Text, tn.Line, tn.Col, usesNew: true);
                }
            case Tok.Ident when Check2(Tok.LBrace):
                {

                    var name = Take();
                    return NewInit(name.Text, name.Line, name.Col, usesNew: false);
                }
            case Tok.Ident: return new Ident(Take().Text, line, col);
            case Tok.TyInt or Tok.TyFloat or Tok.TyString or Tok.TyBuffer when Check2(Tok.LParen):
                {

                    var ty = ParseType("type");
                    Expect(Tok.LParen, "'('");
                    var e = Expression();
                    Expect(Tok.RParen, "')'");
                    return new Cast(ty, e, line, col);
                }
            case Tok.LParen:
                {

                    if (AtTypeStart(Peek(1)) || Check2(Tok.RParen)
                        || (Peek(1).Kind == Tok.Ident && Peek(2).Kind == Tok.Ident)
                        || Peek(1).Kind == Tok.KwMove)
                        return Lambda(line, col);

                    Take();
                    var e = Expression();
                    Expect(Tok.RParen, "')'");
                    return e;
                }
            case Tok.TyList:
                {
                    Take();
                    Expect(Tok.Lt, "'<' after 'list'");
                    var elem = ParseType("list element type");
                    Expect(Tok.Gt, "'>' after list element type");
                    if (Match(Tok.LParen))
                    {
                        Expect(Tok.RParen, "')'");
                        return new ListLit(elem, new List<Expr>(), line, col);
                    }
                    Expect(Tok.LBrace, "'{' or '(' after list type");
                    var items = new List<Expr>();
                    if (!Check(Tok.RBrace))
                    {
                        do { items.Add(Expression()); } while (Match(Tok.Comma));
                    }
                    Expect(Tok.RBrace, "'}'");
                    return new ListLit(elem, items, line, col);
                }
            default:
                throw new SourceError(line, col, $"unexpected '{Cur.Text}' in expression");
        }
    }
}

