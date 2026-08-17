namespace HumanScript;

public class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    public Parser(List<Token> tokens) { _tokens = tokens; }

    private Token Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];
    private Token Peek(int offset) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];

    private Token Advance() { var t = Current; _pos++; return t; }
    private bool Check(TokenType type) => Current.Type == type;

    private Token Expect(TokenType type)
    {
        if (!Check(type))
            throw new Exception($"Line {Current.Line}: expected {type} but got {Current.Type} ('{Current.Text}')");
        return Advance();
    }

    public AstProgram Parse()
    {
        var program = new AstProgram();
        while (Current.Type != TokenType.EOF)
        {
            var stmt = ParseStatement();
            if (stmt != null) program.Statements.Add(stmt);
        }
        return program;
    }

    private Stmt? ParseStatement()
    {
        SkipNewLines();
        if (Check(TokenType.EOF)) return null;

        switch (Current.Type)
        {
            case TokenType.Remember: return ParseRemember();
            case TokenType.Set: return ParseSet();
            case TokenType.Increase: return ParseIncrease();
            case TokenType.Decrease: return ParseDecrease();
            case TokenType.Multiply: return ParseMultiply();
            case TokenType.Divide: return ParseDivide();
            case TokenType.Say: return ParseSay();
            case TokenType.Show: return ParseShow();
            case TokenType.Ask: return ParseAsk();
            case TokenType.Read: return ParseReadInto();
            case TokenType.Write: return ParseWriteInto();
            case TokenType.Delete: return ParseDelete();
            case TokenType.Add: return ParseAddToList();
            case TokenType.Remove: return ParseRemoveFromList();
            case TokenType.Clear: return ParseClearList();
            case TokenType.If: return ParseIf();
            case TokenType.Repeat: return ParseRepeat();
            case TokenType.While: return ParseWhile();
            case TokenType.AsLongAs: return ParseAsLongAs();
            case TokenType.For: return ParseForEach();
            case TokenType.To: return ParseFunctionDecl();
            case TokenType.Try: return ParseTryCatch();
            case TokenType.Return: return ParseReturn();
            case TokenType.Identifier:
                return ParseIdentifierStatement();
            case TokenType.NewLine:
                Advance();
                return null;
            case TokenType.Dedent:
                Advance();
                return null;
            case TokenType.Indent:
                Advance();
                return null;
            case TokenType.End:
                Advance();
                return null;
            default:
                throw new Exception($"Line {Current.Line}: unexpected token '{Current.Text}'");
        }
    }

    private Stmt ParseRemember()
    {
        Advance();
        if (Check(TokenType.That)) Advance();
        var name = Expect(TokenType.Identifier).Text;
        Expect(TokenType.Is);

        Expr value;
        if (Check(TokenType.A) || Check(TokenType.An))
        {
            Advance();
            var typeWord = Expect(TokenType.Identifier).Text;
            if (typeWord == "list")
                value = new ListExpr();
            else
                throw new Exception($"Line {Current.Line}: unknown type '{typeWord}'");
        }
        else
        {
            value = ParseExpr();
        }

        ConsumeNewLine();
        return new DefineStmt(name, value);
    }

    private Stmt ParseSet()
    {
        Advance();
        var target = ParseAssignmentTarget();
        Expect(TokenType.To);
        var value = ParseExpr();
        ConsumeNewLine();
        return new SetStmt(target, value);
    }

    private Stmt ParseIncrease()
    {
        Advance();
        var name = Expect(TokenType.Identifier).Text;
        Expect(TokenType.By);
        var amount = ParseExpr();
        ConsumeNewLine();
        return new IncreaseStmt(name, amount);
    }

    private Stmt ParseDecrease()
    {
        Advance();
        var name = Expect(TokenType.Identifier).Text;
        Expect(TokenType.By);
        var amount = ParseExpr();
        ConsumeNewLine();
        return new DecreaseStmt(name, amount);
    }

    private Stmt ParseMultiply()
    {
        Advance();
        var name = Expect(TokenType.Identifier).Text;
        Expect(TokenType.By);
        var amount = ParseExpr();
        ConsumeNewLine();
        return new MultiplyStmt(name, amount);
    }

    private Stmt ParseDivide()
    {
        Advance();
        var name = Expect(TokenType.Identifier).Text;
        Expect(TokenType.By);
        var amount = ParseExpr();
        ConsumeNewLine();
        return new DivideStmt(name, amount);
    }

    private Stmt ParseSay()
    {
        Advance();
        var value = ParseExpr();
        ConsumeNewLine();
        return new SayStmt(value);
    }

    private Stmt ParseShow()
    {
        Advance();
        var value = ParseExpr();
        ConsumeNewLine();
        return new ShowStmt(value);
    }

    private Stmt ParseAsk()
    {
        Advance();
        var prompt = ParseExpr();
        Expect(TokenType.Into);
        var varName = Expect(TokenType.Identifier).Text;
        ConsumeNewLine();
        return new AskStmt(prompt, varName);
    }

    private Stmt ParseReadInto()
    {
        Advance();
        var path = ParseFilePath();
        Expect(TokenType.Into);
        var varName = Expect(TokenType.Identifier).Text;
        ConsumeNewLine();
        return new ReadIntoStmt(path, varName);
    }

    private Stmt ParseWriteInto()
    {
        Advance();
        var value = ParseExpr();
        Expect(TokenType.Into);
        var path = ParseFilePath();
        ConsumeNewLine();
        return new WriteIntoStmt(value, path);
    }

    private Stmt ParseDelete()
    {
        Advance();
        var path = ParseFilePath();
        ConsumeNewLine();
        return new DeleteStmt(path);
    }

    private Stmt ParseAddToList()
    {
        Advance();
        var value = ParseExpr();
        Expect(TokenType.To);
        var listName = Expect(TokenType.Identifier).Text;
        ConsumeNewLine();
        return new AddToListStmt(value, listName);
    }

    private Stmt ParseRemoveFromList()
    {
        Advance();
        var value = ParseExpr();
        Expect(TokenType.From);
        var listName = Expect(TokenType.Identifier).Text;
        ConsumeNewLine();
        return new RemoveFromListStmt(value, listName);
    }

    private Stmt ParseClearList()
    {
        Advance();
        var listName = Expect(TokenType.Identifier).Text;
        ConsumeNewLine();
        return new ClearListStmt(listName);
    }

    private Stmt ParseIf()
    {
        Advance();
        var cond = ParseExpr();
        Expect(TokenType.Then);
        ConsumeNewLine();
        var thenBlock = ParseBlock();

        List<Stmt>? elseBlock = null;
        if (Check(TokenType.Otherwise))
        {
            Advance();
            ConsumeNewLine();
            elseBlock = ParseBlock();
        }

        Expect(TokenType.End);
        ConsumeNewLine();
        return new IfStmt(cond, thenBlock, elseBlock);
    }

    private Stmt ParseRepeat()
    {
        Advance();
        if (Check(TokenType.Forever))
        {
            Advance();
            ConsumeNewLine();
            var body = ParseBlock();
            Expect(TokenType.End);
            ConsumeNewLine();
            return new RepeatForeverStmt(body);
        }
        else
        {
            var count = ParseExpr();
            Expect(TokenType.Times);
            ConsumeNewLine();
            var body = ParseBlock();
            Expect(TokenType.End);
            ConsumeNewLine();
            return new RepeatTimesStmt(count, body);
        }
    }

    private Stmt ParseWhile()
    {
        Advance();
        var cond = ParseExpr();
        ConsumeNewLine();
        var body = ParseBlock();
        Expect(TokenType.End);
        ConsumeNewLine();
        return new WhileStmt(cond, body);
    }

    private Stmt ParseAsLongAs()
    {
        Advance(); 
        var cond = ParseExpr();
        ConsumeNewLine();
        var body = ParseBlock();
        Expect(TokenType.End);
        ConsumeNewLine();
        return new WhileStmt(cond, body);
    }

    private Stmt ParseForEach()
    {
        Advance();
        Expect(TokenType.Every);
        var varName = Expect(TokenType.Identifier).Text;
        Expect(TokenType.In);
        var collection = ParseExpr();
        ConsumeNewLine();
        var body = ParseBlock();
        Expect(TokenType.End);
        ConsumeNewLine();
        return new ForEachStmt(varName, collection, body);
    }

    private Stmt ParseFunctionDecl()
    {
        Advance();
        var name = Expect(TokenType.Identifier).Text;
        var parameters = new List<string>();
        while (Check(TokenType.Identifier))
        {
            parameters.Add(Advance().Text);
        }
        ConsumeNewLine();
        var body = ParseBlock();
        Expect(TokenType.End);
        ConsumeNewLine();
        return new FunctionDeclStmt(name, parameters, body);
    }

    private Stmt ParseTryCatch()
    {
        Advance();
        ConsumeNewLine();
        var tryBlock = ParseBlock();
        Expect(TokenType.Catch);
        ConsumeNewLine();
        var catchBlock = ParseBlock();
        Expect(TokenType.End);
        ConsumeNewLine();
        return new TryCatchStmt(tryBlock, catchBlock);
    }

    private Stmt ParseReturn()
    {
        Advance();
        Expr? value = null;
        if (!Check(TokenType.NewLine) && !Check(TokenType.EOF) && !Check(TokenType.End))
            value = ParseExpr();
        ConsumeNewLine();
        return new ReturnStmt(value);
    }

    private Stmt ParseIdentifierStatement()
    {
        var name = Advance().Text;

        if (Check(TokenType.Is))
        {
            Advance();
            if (Check(TokenType.A) || Check(TokenType.An))
            {
                Advance();
                var typeWord = Expect(TokenType.Identifier).Text;
                ConsumeNewLine();
                if (typeWord == "list")
                    return new DefineStmt(name, new ListExpr());
                throw new Exception($"Line {Current.Line}: unknown type '{typeWord}'");
            }
            var value = ParseExpr();
            ConsumeNewLine();
            return new DefineStmt(name, value);
        }

        var args = new List<Expr>();
        if (!Check(TokenType.NewLine) && !Check(TokenType.EOF) && !Check(TokenType.End) && !Check(TokenType.Otherwise) && !Check(TokenType.Catch))
        {
            args.Add(ParseExpr());
            while (Check(TokenType.Comma))
            {
                Advance();
                args.Add(ParseExpr());
            }
        }
        ConsumeNewLine();
        return new CallStmt(name, args);
    }

    private List<Stmt> ParseBlock()
    {
        var stmts = new List<Stmt>();

        SkipNewLines();

        if (!Check(TokenType.Indent))
            throw new Exception($"Line {Current.Line}: expected indented block");
        Advance();

        while (!Check(TokenType.Dedent) && !Check(TokenType.EOF) && !Check(TokenType.End) && !Check(TokenType.Otherwise) && !Check(TokenType.Catch))
        {
            if (Check(TokenType.NewLine))
            {
                Advance();
                continue;
            }
            var stmt = ParseStatement();
            if (stmt != null) stmts.Add(stmt);
        }

        if (Check(TokenType.Dedent))
            Advance();

        return stmts;
    }

    private Expr ParseExpr() => ParseOr();

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (Check(TokenType.Or))
        {
            Advance();
            left = new BinaryExpr("or", left, ParseAnd());
        }
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseNot();
        while (Check(TokenType.And))
        {
            Advance();
            left = new BinaryExpr("and", left, ParseNot());
        }
        return left;
    }

    private Expr ParseNot()
    {
        if (Check(TokenType.Not))
        {
            Advance();
            return new UnaryExpr("not", ParseNot());
        }
        return ParseCompare();
    }

    private Expr ParseCompare()
    {
        var left = ParseAdd();

        if (Check(TokenType.Is))
        {
            Advance();
            bool negated = Check(TokenType.Not);
            if (negated) Advance();

            string op;
            if (Check(TokenType.Greater))
            {
                Advance(); Expect(TokenType.Than);
                op = ">";
            }
            else if (Check(TokenType.Less))
            {
                Advance(); Expect(TokenType.Than);
                op = "<";
            }
            else if (Check(TokenType.Least))
            {
                Advance();
                op = ">=";
            }
            else if (Check(TokenType.Most))
            {
                Advance();
                op = "<=";
            }
            else if (Check(TokenType.Above))
            {
                Advance();
                op = ">";
            }
            else if (Check(TokenType.Below))
            {
                Advance();
                op = "<";
            }
            else
            {
                op = "==";
            }

            var right = ParseAdd();
            var cmp = new BinaryExpr(op, left, right);
            return negated ? new UnaryExpr("not", cmp) : cmp;
        }

        string? existingOp = null;
        if (Check(TokenType.Eq)) { existingOp = "=="; Advance(); }
        else if (Check(TokenType.NotEq)) { existingOp = "!="; Advance(); }
        else if (Check(TokenType.Lt)) { existingOp = "<"; Advance(); }
        else if (Check(TokenType.LtEq)) { existingOp = "<="; Advance(); }
        else if (Check(TokenType.Gt)) { existingOp = ">"; Advance(); }
        else if (Check(TokenType.GtEq)) { existingOp = ">="; Advance(); }
        else if (Check(TokenType.Contains)) { existingOp = "contains"; Advance(); }
        else if (Check(TokenType.Starts)) { existingOp = "starts with"; Advance(); Expect(TokenType.With); }
        else if (Check(TokenType.Ends)) { existingOp = "ends with"; Advance(); Expect(TokenType.With); }

        if (existingOp != null)
        {
            var right = ParseAdd();
            if (existingOp == "starts with") return new StartsWithExpr(left, right);
            if (existingOp == "ends with") return new EndsWithExpr(left, right);
            return new BinaryExpr(existingOp, left, right);
        }
        return left;
    }

    private Expr ParseAdd()
    {
        var left = ParseMul();
        while (Check(TokenType.Plus) || Check(TokenType.Minus))
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseMul());
        }
        return left;
    }

    private Expr ParseMul()
    {
        var left = ParseUnary();
        while (Check(TokenType.Star) || Check(TokenType.Slash))
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseUnary());
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (Check(TokenType.Minus))
        {
            Advance();
            return new UnaryExpr("-", ParseUnary());
        }
        if (Check(TokenType.Not))
        {
            Advance();
            return new UnaryExpr("not", ParseUnary());
        }
        return ParsePrimary();
    }

    private Expr ParseAssignmentTarget()
    {
        if (!Check(TokenType.Identifier))
            throw new Exception($"Line {Current.Line}: expected assignment target, got '{Current.Text}'");

        var name = Advance().Text;
        if (Check(TokenType.LBracket))
        {
            Advance();
            var index = ParseExpr();
            Expect(TokenType.RBracket);
            return new ListIndexExpr(new IdentExpr(name), index);
        }

        return new IdentExpr(name);
    }

    private Expr ParsePrimary()
    {
        if (Check(TokenType.Length))
        {
            Advance(); Expect(TokenType.Of);
            return new LengthOfExpr(ParsePrimary());
        }

        if (Check(TokenType.Number))
        {
            if (Current.Text == "number")
            {
                Advance(); Expect(TokenType.Of);
                return new ListLengthExpr(ParsePrimary());
            }
            else
            {
                return new NumberExpr(int.Parse(Advance().Text));
            }
        }

        if (Check(TokenType.Exists))
        {
            Advance();
            var path = ParseFilePath();
            return new ExistExpr(path);
        }

        switch (Current.Type)
        {
            case TokenType.Float:
                return new FloatExpr(double.Parse(Advance().Text, System.Globalization.CultureInfo.InvariantCulture));
            case TokenType.String:
                return new StringExpr(Advance().Text);
            case TokenType.Yes:
                Advance(); return new BoolExpr(true);
            case TokenType.No:
                Advance(); return new BoolExpr(false);
            case TokenType.LParen:
                Advance();
                var inner = ParseExpr();
                Expect(TokenType.RParen);
                return inner;
            case TokenType.Identifier:
                var name = Advance().Text;
                if (Check(TokenType.LParen))
                {
                    Advance();
                    var args = new List<Expr>();
                    if (!Check(TokenType.RParen))
                    {
                        args.Add(ParseExpr());
                        while (Check(TokenType.Comma))
                        {
                            Advance();
                            args.Add(ParseExpr());
                        }
                    }
                    Expect(TokenType.RParen);
                    return new CallExpr(name, args);
                }
                if (Check(TokenType.LBracket))
                {
                    Advance();
                    var index = ParseExpr();
                    Expect(TokenType.RBracket);
                    return new ListIndexExpr(new IdentExpr(name), index);
                }
                return new IdentExpr(name);
            default:
                throw new Exception($"Line {Current.Line}: expected expression, got '{Current.Text}'");
        }
    }

    private string ParseFilePath()
    {
        if (Check(TokenType.String))
            return Advance().Text;
        var path = Expect(TokenType.Identifier).Text;
        while (Check(TokenType.Dot))
        {
            Advance();
            path += "." + Expect(TokenType.Identifier).Text;
        }
        return path;
    }

    private void SkipNewLines()
    {
        while (Check(TokenType.NewLine)) Advance();
    }

    private void ConsumeNewLine()
    {
        if (Check(TokenType.NewLine)) Advance();
    }
}