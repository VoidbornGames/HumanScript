namespace HSharp;

// recursive descent. statements dispatch on the first token, expressions climb
// the usual precedence chain
public sealed class Parser(List<Token> tokens)
{
    private readonly List<Token> _t = tokens;
    private int _pos;

    public AstProgram Parse()
    {
        var stmts = new List<Stmt>();
        while (!Check(Tok.EOF))
        {
            if (AtTypeStart() || Check(Tok.KwVoid))
                stmts.Add(FnDecl());
            else
                stmts.Add(Statement());
        }
        return new AstProgram(stmts);
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
        t.Kind is Tok.TyInt or Tok.TyFloat or Tok.TyBool or Tok.TyString or Tok.TyList;
    private bool AtTypeStart() => AtTypeStart(Cur);

    private Ty ParseType(string what)
    {
        switch (Cur.Kind)
        {
            case Tok.TyInt: Take(); return Ty.Int;
            case Tok.TyFloat: Take(); return Ty.Float;
            case Tok.TyBool: Take(); return Ty.Bool;
            case Tok.TyString: Take(); return Ty.Str;
            case Tok.TyList:
                Take();
                Expect(Tok.Lt, "'<' in list type");
                var elem = ParseType("list element type");
                Expect(Tok.Gt, "'>' in list type");
                return Ty.List(elem);
            default:
                throw new SourceError(Cur.Line, Cur.Col, $"expected {what} but got '{Cur.Text}'");
        }
    }

    private FnDecl FnDecl()
    {
        int line = Cur.Line, col = Cur.Col;
        Ty ret;
        if (Check(Tok.KwVoid)) { Take(); ret = Ty.Void; }
        else ret = ParseType("return type");
        var name = Expect(Tok.Ident, "function name");
        Expect(Tok.LParen, "'(' after function name");

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
        var body = Block();
        return new FnDecl(ret, name.Text, ps, body, line, col);
    }

    private List<Stmt> Block()
    {
        Expect(Tok.LBrace, "'{'");
        var stmts = new List<Stmt>();
        while (!Check(Tok.RBrace) && !Check(Tok.EOF))
            stmts.Add(Statement());
        Expect(Tok.RBrace, "'}'");
        return stmts;
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
            Expect(Tok.Semi, "';'");
            return new VarDecl(name.Text, null, init, line, col);
        }

        if (AtTypeStart())
        {
            int save = _pos;
            var ty = ParseType("type");
            if (Check(Tok.Ident))
            {
                var name = Take();
                Expect(Tok.Assign, "'=' (declarations need an initializer)");
                var init = Expression();
                Expect(Tok.Semi, "';'");
                return new VarDecl(name.Text, ty, init, line, col);
            }
            if (Check(Tok.LParen) || Check(Tok.LBrace))
            {
                _pos = save;
                var e = Expression();
                Expect(Tok.Semi, "';'");
                return new ExprStmt(e, line, col);
            }
            throw new SourceError(Cur.Line, Cur.Col, $"expected variable name after type '{ty}'");
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
                    return new Foreach(name.Text, iter, Block(), line, col);
                }
            case Tok.KwReturn:
                {
                    Take();
                    Expr? value = Check(Tok.Semi) ? null : Expression();
                    Expect(Tok.Semi, "';'");
                    return new Return(value, line, col);
                }
            case Tok.KwTry:
                {
                    Take();
                    var tryBody = Block();
                    Expect(Tok.KwCatch, "'catch'");
                    var catchBody = Block();
                    return new TryCatch(tryBody, catchBody, line, col);
                }
            case Tok.LBrace:
                return new BlockStmt(Block(), line, col);
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
        Expect(Tok.Semi, "';'");
        return stmt;
    }

    private Stmt ExprOrAssignCore(bool allowIncDec = false)
    {
        int line = Cur.Line, col = Cur.Col;
        var e = Expression();

        if (Check(Tok.Assign) || Check(Tok.PlusEq) || Check(Tok.MinusEq) || Check(Tok.StarEq) || Check(Tok.SlashEq) || Check(Tok.PercentEq))
        {
            var op = Take();
            if (e is not (Ident or Index))
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
            if (e is not (Ident or Index))
                throw new SourceError(line, col, "invalid '++'/'--' target");
            return new IncDec(e, inc, line, col);
        }

        return new ExprStmt(e, line, col);
    }

    private Expr Expression() => OrExpr();

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
        return Postfix();
    }

    private Expr Postfix()
    {
        var e = Primary();
        while (true)
        {
            if (Check(Tok.LParen))
            {
                if (e is not Ident id)
                {
                    int line = Cur.Line, col = Cur.Col;
                    throw new SourceError(line, col, "only named functions can be called");
                }
                e = new Call(id.Name, Args(), id.Line, id.Col);
            }
            else if (Check(Tok.LBracket))
            {
                int line = Cur.Line, col = Cur.Col;
                Take();
                var idx = Expression();
                Expect(Tok.RBracket, "']'");
                e = new Index(e, idx, line, col);
            }
            else if (Check(Tok.Dot))
            {
                int line = Cur.Line, col = Cur.Col;
                Take();
                var name = Expect(Tok.Ident, "member name after '.'");
                e = Check(Tok.LParen)
                    ? new Method(e, name.Text, Args(), line, col)
                    : new Prop(e, name.Text, line, col);
            }
            else break;
        }
        return e;
    }

    private List<Expr> Args()
    {
        Expect(Tok.LParen, "'('");
        var args = new List<Expr>();
        if (!Check(Tok.RParen))
        {
            do { args.Add(Expression()); } while (Match(Tok.Comma));
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
            case Tok.Str: return new StrLit((string)Take().Value!, line, col);
            case Tok.Interp:
                {
                    var parts = (List<InterpPart>)Take().Value!;
                    var exprs = new List<Expr>();
                    foreach (var p in parts)
                    {
                        if (p.IsExpr)
                        {
                            var sub = new Lexer(p.Text).Tokenize();
                            if (sub.Count != 2 || sub[0].Kind == Tok.EOF)
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
            case Tok.Ident: return new Ident(Take().Text, line, col);
            case Tok.LParen:
                {
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
