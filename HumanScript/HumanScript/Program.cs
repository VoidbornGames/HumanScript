using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HumanScript
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                var config = new Configuration(args);
                var transpiler = new HumanScriptTranspiler(config);
                transpiler.Transpile();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }
    }

    class Configuration
    {
        public string InputFile { get; }
        public string NimFile { get; }
        public string ExeFile { get; }
        public string NimCompilerPath { get; }
        public string BaseDirectory { get; }

        public Configuration(string[] args)
        {
            BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            string inputFileName = args.Length > 0 ? args[0] : "script.eng";
            InputFile = Path.Combine(BaseDirectory, inputFileName);

            if (!InputFile.EndsWith(".eng"))
            {
                throw new Exception($"File '{inputFileName}' is not a valid '.eng' HumanScript file!");
            }

            if (!File.Exists(InputFile))
            {
                throw new Exception($"File not found at expected location: {InputFile}");
            }

            NimFile = Path.Combine(BaseDirectory, "temp.nim");
            ExeFile = Path.Combine(BaseDirectory, "output.exe");

            NimCompilerPath = Path.Combine(BaseDirectory, @"nim-2.2.6\bin\nim.exe");

            if (!File.Exists(NimCompilerPath))
            {
                throw new Exception($"Nim compiler not found at expected location: {NimCompilerPath}");
            }
        }
    }

    class Token
    {
        public TokenType Type { get; }
        public string Value { get; }
        public int LineNumber { get; }

        public Token(TokenType type, string value, int lineNumber)
        {
            Type = type;
            Value = value;
            LineNumber = lineNumber;
        }
    }

    enum TokenType
    {
        // Literals
        Number,
        String,
        Boolean,
        Identifier,

        // Keywords
        Define,
        As,
        Function,
        Named,
        Set,
        To,
        Add,
        Subtract,
        Multiply,
        Divide,
        By,
        From,
        Print,
        If,
        Else,
        ElseIf,
        Repeat,
        Times,
        Run,
        Wait,
        For,
        Seconds,
        CombinedWith,
        Turn,
        Text,
        Store,
        Console,
        Input,
        In,
        Write,
        Process,
        Is,
        Read,
        File,
        And, 
        It, 

        // Operators
        Equal,
        NotEqual,
        GreaterThan,
        LessThan,
        Plus,
        Minus,
        TimesOp,
        DividedBy,

        // Special
        Colon,
        Period,
        Semicolon,
        Comma,
        LeftBracket,
        RightBracket,
        EndOfLine,
        Unknown
    }

    class Lexer
    {
        private readonly string[] _lines;
        private int _currentLine;
        private int _currentPosition;

        public Lexer(string[] lines)
        {
            _lines = lines;
            _currentLine = 0;
            _currentPosition = 0;
        }

        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();

            while (_currentLine < _lines.Length)
            {
                var line = _lines[_currentLine];

                if (string.IsNullOrWhiteSpace(line) || line.Trim().StartsWith("#"))
                {
                    _currentLine++;
                    continue;
                }

                while (_currentPosition < line.Length)
                {
                    if (char.IsWhiteSpace(line[_currentPosition]))
                    {
                        _currentPosition++;
                        continue;
                    }

                    var token = GetNextToken(line);
                    if (token != null) tokens.Add(token);
                }

                tokens.Add(new Token(TokenType.EndOfLine, "", _currentLine + 1));
                _currentLine++;
                _currentPosition = 0;
            }

            if (tokens.Count > 0 && tokens[tokens.Count - 1].Type != TokenType.EndOfLine)
            {
                tokens.Add(new Token(TokenType.EndOfLine, "", _lines.Length));
            }

            return tokens;
        }

        private Token GetNextToken(string line)
        {
            var remaining = line.Substring(_currentPosition);

            var numberMatch = Regex.Match(remaining, @"^\d+");
            if (numberMatch.Success)
            {
                var value = numberMatch.Value;
                _currentPosition += value.Length;
                return new Token(TokenType.Number, value, _currentLine + 1);
            }

            if (remaining.StartsWith("\""))
            {
                var endIndex = remaining.IndexOf("\"", 1);
                if (endIndex == -1) throw new Exception($"Unterminated string at line {_currentLine + 1}");
                var value = remaining.Substring(0, endIndex + 1);
                _currentPosition += value.Length;
                return new Token(TokenType.String, value, _currentLine + 1);
            }

            if (remaining.StartsWith("true")) { _currentPosition += 4; return new Token(TokenType.Boolean, "true", _currentLine + 1); }
            if (remaining.StartsWith("false")) { _currentPosition += 5; return new Token(TokenType.Boolean, "false", _currentLine + 1); }

            var multiCharPatterns = new Dictionary<string, TokenType>
            {
                {"read", TokenType.Read},{"file", TokenType.File},{"and", TokenType.And},{"it", TokenType.It},
                {"define", TokenType.Define}, {"function", TokenType.Function}, {"named", TokenType.Named}, {"set", TokenType.Set}, {"to", TokenType.To},
                {"add", TokenType.Add}, {"subtract", TokenType.Subtract}, {"multiply", TokenType.Multiply}, {"divide", TokenType.Divide}, {"by", TokenType.By},
                {"from", TokenType.From}, {"print", TokenType.Print}, {"if", TokenType.If}, {"else if", TokenType.ElseIf}, {"else", TokenType.Else},
                {"repeat", TokenType.Repeat}, {"times", TokenType.Times}, {"run", TokenType.Run}, {"wait", TokenType.Wait}, {"for", TokenType.For},
                {"seconds", TokenType.Seconds}, {"combined with", TokenType.CombinedWith}, {"turn", TokenType.Turn}, {"text", TokenType.Text},
                {"as", TokenType.As}, {"store", TokenType.Store}, {"console", TokenType.Console}, {"input", TokenType.Input}, {"in", TokenType.In},
                {"write", TokenType.Write}, {"process", TokenType.Process}, {"equal to", TokenType.Equal}, {"not equal to", TokenType.NotEqual},
                {"greater than", TokenType.GreaterThan}, {"less than", TokenType.LessThan}, {"plus", TokenType.Plus}, {"minus", TokenType.Minus},
                {"divided by", TokenType.DividedBy}, {"is", TokenType.Is}, {":", TokenType.Colon}, {".", TokenType.Period}, {";", TokenType.Semicolon},
                {",", TokenType.Comma}, {"[", TokenType.LeftBracket}, {"]", TokenType.RightBracket}
            };

            foreach (var pattern in multiCharPatterns.OrderByDescending(p => p.Key.Length))
            {
                if (remaining.StartsWith(pattern.Key))
                {
                    _currentPosition += pattern.Key.Length;
                    return new Token(pattern.Value, pattern.Key, _currentLine + 1);
                }
            }

            var identifierMatch = Regex.Match(remaining, @"^[a-zA-Z_][a-zA-Z0-9_]*");
            if (identifierMatch.Success)
            {
                var value = identifierMatch.Value;
                _currentPosition += value.Length;
                return new Token(TokenType.Identifier, value, _currentLine + 1);
            }

            throw new Exception($"Unknown token at line {_currentLine + 1}, position {_currentPosition}: '{remaining[0]}'");
        }
    }


    abstract class AstNode { public int LineNumber { get; } protected AstNode(int lineNumber) { LineNumber = lineNumber; } public abstract T Accept<T>(IAstVisitor<T> visitor); }
    class ProgramNode : AstNode
    {
        public List<VariableDeclarationNode> GlobalVariables { get; }
        public List<FunctionDefinitionNode> Functions { get; }
        public List<StatementNode> MainStatements { get; }
        public ProgramNode(List<VariableDeclarationNode> gv, List<FunctionDefinitionNode> f, List<StatementNode> ms) : base(0) { GlobalVariables = gv; Functions = f; MainStatements = ms; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class VariableDeclarationNode : AstNode
    {
        public string Name, Type, Value;
        public VariableDeclarationNode(string name, string type, string value, int lineNumber) : base(lineNumber) { Name = name; Type = type; Value = value; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class FunctionDefinitionNode : AstNode
    {
        public string Name; public List<StatementNode> Body;
        public FunctionDefinitionNode(string name, List<StatementNode> body, int lineNumber) : base(lineNumber) { Name = name; Body = body; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    abstract class StatementNode : AstNode { protected StatementNode(int lineNumber) : base(lineNumber) { } }
    class BlockNode : StatementNode
    {
        public List<StatementNode> Statements { get; }
        public BlockNode(List<StatementNode> statements, int lineNumber) : base(lineNumber) { Statements = statements; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class AssignmentNode : StatementNode
    {
        public string VariableName; public ExpressionNode Value;
        public AssignmentNode(string varName, ExpressionNode value, int lineNumber) : base(lineNumber) { VariableName = varName; Value = value; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class CompoundAssignmentNode : StatementNode
    {
        public string VariableName; public string Operator; public ExpressionNode Value;
        public CompoundAssignmentNode(string varName, string op, ExpressionNode value, int lineNumber) : base(lineNumber) { VariableName = varName; Operator = op; Value = value; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class PrintNode : StatementNode
    {
        public ExpressionNode Value;
        public PrintNode(ExpressionNode value, int lineNumber) : base(lineNumber) { Value = value; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class IfNode : StatementNode
    {
        public ExpressionNode Condition; public List<StatementNode> ThenStatements; public List<(ExpressionNode, List<StatementNode>)> ElseIfBranches; public List<StatementNode> ElseStatements;
        public IfNode(ExpressionNode cond, List<StatementNode> then, List<(ExpressionNode, List<StatementNode>)> elif, List<StatementNode> elseS, int lineNumber) : base(lineNumber) { Condition = cond; ThenStatements = then; ElseIfBranches = elif; ElseStatements = elseS; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class RepeatNode : StatementNode
    {
        public ExpressionNode Count; public List<StatementNode> Body;
        public RepeatNode(ExpressionNode count, List<StatementNode> body, int lineNumber) : base(lineNumber) { Count = count; Body = body; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class FunctionCallNode : StatementNode, ExpressionNode
    {
        public string FunctionName;
        public FunctionCallNode(string funcName, int lineNumber) : base(lineNumber) { FunctionName = funcName; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class WaitNode : StatementNode
    {
        public ExpressionNode Seconds;
        public WaitNode(ExpressionNode seconds, int lineNumber) : base(lineNumber) { Seconds = seconds; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class InputNode : StatementNode
    {
        public string VariableName;
        public InputNode(string varName, int lineNumber) : base(lineNumber) { VariableName = varName; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class ProcessNode : StatementNode
    {
        public string ProcessPath;
        public ProcessNode(string path, int lineNumber) : base(lineNumber) { ProcessPath = path; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class WriteFileNode : StatementNode
    {
        public ExpressionNode Content; public string FilePath;
        public WriteFileNode(ExpressionNode content, string path, int lineNumber) : base(lineNumber) { Content = content; FilePath = path; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class TypeConversionNode : StatementNode
    {
        public string SourceVariable, TargetVariable;
        public TypeConversionNode(string src, string target, int lineNumber) : base(lineNumber) { SourceVariable = src; TargetVariable = target; }
        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }

    interface ExpressionNode { T Accept<T>(IAstVisitor<T> visitor); }
    class BinaryOperationNode : ExpressionNode
    {
        public ExpressionNode Left, Right; public string Operator; public int LineNumber;
        public BinaryOperationNode(ExpressionNode left, string op, ExpressionNode right, int lineNumber) { Left = left; Operator = op; Right = right; LineNumber = lineNumber; }
        public T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class ConcatenationExpressionNode : ExpressionNode
    {
        public ExpressionNode Left, Right; public int LineNumber;
        public ConcatenationExpressionNode(ExpressionNode left, ExpressionNode right, int lineNumber) { Left = left; Right = right; LineNumber = lineNumber; }
        public T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class VariableNode : ExpressionNode
    {
        public string Name; public int LineNumber;
        public VariableNode(string name, int lineNumber) { Name = name; LineNumber = lineNumber; }
        public T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class LiteralNode : ExpressionNode
    {
        public string Value, Type; public int LineNumber;
        public LiteralNode(string value, string type, int lineNumber) { Value = value; Type = type; LineNumber = lineNumber; }
        public T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }
    class ReadFileNode : StatementNode
    {
        public string FilePath;
        public string VariableName;

        public ReadFileNode(string filePath, string varName, int lineNumber) : base(lineNumber)
        {
            FilePath = filePath;
            VariableName = varName;
        }

        public override T Accept<T>(IAstVisitor<T> visitor) => visitor.Visit(this);
    }

    interface IAstVisitor<T>
    {
        T Visit(ProgramNode node); T Visit(VariableDeclarationNode node); T Visit(FunctionDefinitionNode node);
        T Visit(AssignmentNode node); T Visit(CompoundAssignmentNode node);
        T Visit(PrintNode node); T Visit(IfNode node); T Visit(RepeatNode node);
        T Visit(FunctionCallNode node); T Visit(WaitNode node); T Visit(InputNode node); T Visit(ProcessNode node);
        T Visit(WriteFileNode node); T Visit(TypeConversionNode node); T Visit(BinaryOperationNode node);
        T Visit(ConcatenationExpressionNode node);
        T Visit(VariableNode node); T Visit(LiteralNode node);
        T Visit(BlockNode node);
        T Visit(ReadFileNode node);
    }

    class Parser
    {
        private readonly List<Token> _tokens;
        private int _current;

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
            _current = 0;
        }

        public ProgramNode Parse()
        {
            var gVars = new List<VariableDeclarationNode>();
            var funcs = new List<FunctionDefinitionNode>();
            var mainStmts = new List<StatementNode>();

            while (!IsAtEnd())
            {
                if (Match(TokenType.EndOfLine))
                {
                    continue;
                }

                if (Match(TokenType.Define))
                {
                    if (Match(TokenType.Function))
                    {
                        Consume(TokenType.Named, "Expected 'named'");
                        var name = Consume(TokenType.Identifier, "Expected function name").Value;
                        Consume(TokenType.Colon, "Expected ':' after function signature");
                        Consume(TokenType.EndOfLine, "Expected newline after ':'");

                        var body = ParseBlock();

                        Consume(TokenType.EndOfLine, "Expected EOL after function definition block");

                        funcs.Add(new FunctionDefinitionNode(name, body, Previous().LineNumber));
                    }
                    else
                    {
                        var name = Consume(TokenType.Identifier, "Expected variable name").Value;
                        Consume(TokenType.As, "Expected 'as'");
                        string type = "", value = "";
                        if (Match(TokenType.String)) { type = "string"; value = Previous().Value; }
                        else if (Match(TokenType.Boolean)) { type = "boolean"; value = Previous().Value; }
                        else if (Match(TokenType.Number)) { type = "number"; value = Previous().Value; }
                        else { throw new Exception($"Expected type (string, boolean, number) at line {Peek().LineNumber}"); }
                        Consume(TokenType.Semicolon, "Expected ';'");
                        Consume(TokenType.EndOfLine, "Expected EOL");
                        gVars.Add(new VariableDeclarationNode(name, type, value, Previous().LineNumber));
                    }
                }
                else
                {
                    var stmt = ParseStatement();
                    if (stmt != null) mainStmts.Add(stmt);
                }
            }
            return new ProgramNode(gVars, funcs, mainStmts);
        }

        private List<StatementNode> ParseBlock()
        {
            var stmts = new List<StatementNode>();
            Consume(TokenType.LeftBracket, "Expected '[' to start a block");
            while (!Check(TokenType.RightBracket) && !IsAtEnd())
            {
                var stmt = ParseStatement();
                if (stmt != null) stmts.Add(stmt);
            }
            Consume(TokenType.RightBracket, "Expected ']' to end a block");
            return stmts;
        }

        private StatementNode ParseStatement()
        {
            if (IsAtEnd()) return null;

            if (Match(TokenType.If)) return ParseIf();
            if (Match(TokenType.Repeat)) return ParseRepeat();
            if (Match(TokenType.Read)) return ParseReadFile();
            if (Match(TokenType.Add, TokenType.Subtract, TokenType.Multiply, TokenType.Divide)) return ParseCompoundAssignment();
            if (Match(TokenType.Add, TokenType.Subtract, TokenType.Multiply, TokenType.Divide)) return ParseCompoundAssignment();
            if (Match(TokenType.Set)) return ParseAssignment();
            if (Match(TokenType.Print)) return ParsePrint();
            if (Match(TokenType.Run)) return ParseFunctionCall();
            if (Match(TokenType.Wait)) return ParseWait();
            if (Match(TokenType.Store)) return ParseInput();
            if (Match(TokenType.Write)) return ParseWriteFile();
            if (Match(TokenType.Turn)) return ParseTypeConversion();
            if (Match(TokenType.Process)) return ParseProcess();
            if (Match(TokenType.EndOfLine)) return null;

            throw new Exception($"Unexpected token at line {Peek().LineNumber}: {Peek().Value}");
        }

        private StatementNode ParseReadFile()
        {
            Consume(TokenType.File, "Expected 'file'");
            var path = Consume(TokenType.String, "Expected file path").Value;

            if (path.StartsWith("\"") && path.EndsWith("\""))
            {
                path = path.Substring(1, path.Length - 2);
            }

            Consume(TokenType.And, "Expected 'and'");
            Consume(TokenType.Store, "Expected 'store'");
            Consume(TokenType.It, "Expected 'it'");
            Consume(TokenType.In, "Expected 'in'");

            var varName = Consume(TokenType.Identifier, "Expected variable name").Value;

            Consume(TokenType.Semicolon, "Expected ';'");
            Consume(TokenType.EndOfLine, "Expected EOL");

            return new ReadFileNode(path, varName, Previous().LineNumber);
        }
        private StatementNode ParseCompoundAssignment()
        {
            var operation = Previous().Type;
            string varName, op;
            ExpressionNode value;

            if (operation == TokenType.Add || operation == TokenType.Subtract)
            {
                value = ParseExpression();
                Consume(operation == TokenType.Add ? TokenType.To : TokenType.From, $"Expected '{(operation == TokenType.Add ? "to" : "from")}'");
                varName = Consume(TokenType.Identifier, "Expected variable name").Value;
                op = operation == TokenType.Add ? "+=" : "-=";
            }
            else
            {
                varName = Consume(TokenType.Identifier, "Expected variable name").Value;
                Consume(TokenType.By, "Expected 'by'");
                value = ParseExpression();
                op = operation == TokenType.Multiply ? "*=" : "div=";
            }
            Consume(TokenType.Semicolon, "Expected ';'");
            Consume(TokenType.EndOfLine, "Expected EOL");
            return new CompoundAssignmentNode(varName, op, value, Previous().LineNumber);
        }

        private StatementNode ParseAssignment()
        {
            var varName = Consume(TokenType.Identifier, "Expected variable name").Value;
            Consume(TokenType.To, "Expected 'to'");
            var value = ParseExpression();
            Consume(TokenType.Semicolon, "Expected ';'");
            Consume(TokenType.EndOfLine, "Expected EOL");
            return new AssignmentNode(varName, value, Previous().LineNumber);
        }

        private StatementNode ParsePrint()
        {
            var value = ParseExpression();
            Consume(TokenType.Semicolon, "Expected ';'");
            Consume(TokenType.EndOfLine, "Expected EOL");
            return new PrintNode(value, Previous().LineNumber);
        }

        private StatementNode ParseIf()
        {
            var condition = ParseCondition();
            Consume(TokenType.Colon, "Expected ':' after if condition");
            if (Check(TokenType.EndOfLine)) Advance();

            var thenStatements = ParseBlock();
            if (Check(TokenType.EndOfLine)) Advance();

            var elseIfBranches = new List<(ExpressionNode, List<StatementNode>)>();
            while (Match(TokenType.ElseIf))
            {
                var elifCondition = ParseCondition();
                Consume(TokenType.Colon, "Expected ':' after else if condition");
                if (Check(TokenType.EndOfLine)) Advance();
                var elifStatements = ParseBlock();
                elseIfBranches.Add((elifCondition, elifStatements));
                if (Check(TokenType.EndOfLine)) Advance();
            }

            List<StatementNode> elseStatements = null;
            if (Match(TokenType.Else))
            {
                Consume(TokenType.Colon, "Expected ':' after else");
                if (Check(TokenType.EndOfLine)) Advance();
                elseStatements = ParseBlock();
            }

            Match(TokenType.EndOfLine);
            return new IfNode(condition, thenStatements, elseIfBranches, elseStatements, Previous().LineNumber);
        }

        private StatementNode ParseRepeat()
        {
            var count = ParseExpression();
            Consume(TokenType.Times, "Expected 'times'");
            Consume(TokenType.Colon, "Expected ':' after repeat count");
            if (Check(TokenType.EndOfLine)) Advance();

            var body = ParseBlock();
            Match(TokenType.EndOfLine);
            return new RepeatNode(count, body, Previous().LineNumber);
        }

        private ExpressionNode ParseCondition()
        {
            var left = ParseExpression();
            if (Match(TokenType.Is)) { /* 'is' is optional for user */ }
            if (Match(TokenType.Equal, TokenType.NotEqual, TokenType.GreaterThan, TokenType.LessThan))
            {
                var op = Previous().Value;
                var right = ParseExpression();
                return new BinaryOperationNode(left, op, right, Previous().LineNumber);
            }

            throw new Exception($"Expected comparison operator at line {Peek().LineNumber}");
        }

        private StatementNode ParseFunctionCall()
        {
            Consume(TokenType.Function, "Expected 'function'");
            var name = Consume(TokenType.Identifier, "Expected function name").Value;
            Consume(TokenType.Semicolon, "Expected ';'");
            Consume(TokenType.EndOfLine, "Expected EOL");
            return new FunctionCallNode(name, Previous().LineNumber);
        }

        private StatementNode ParseWait()
        {
            Consume(TokenType.For, "Expected 'for'");
            var seconds = ParseExpression();
            Consume(TokenType.Seconds, "Expected 'seconds'");
            Consume(TokenType.Semicolon, "Expected ';'");
            Consume(TokenType.EndOfLine, "Expected EOL");
            return new WaitNode(seconds, Previous().LineNumber);
        }

        private StatementNode ParseInput()
        {
            Consume(TokenType.Console, "Expected 'console'");
            Consume(TokenType.Input, "Expected 'input'");
            Consume(TokenType.In, "Expected 'in'");
            var name = Consume(TokenType.Identifier, "Expected variable name").Value;
            Consume(TokenType.Semicolon, "Expected ';'");
            Consume(TokenType.EndOfLine, "Expected EOL");
            return new InputNode(name, Previous().LineNumber);
        }

        private StatementNode ParseProcess()
        {
            var path = Consume(TokenType.String, "Expected process path").Value;
            Consume(TokenType.Semicolon, "Expected ';'");
            Consume(TokenType.EndOfLine, "Expected EOL");
            return new ProcessNode(path, Previous().LineNumber);
        }

        private StatementNode ParseWriteFile()
        {
            var content = ParseExpression();
            Consume(TokenType.To, "Expected 'to'");
            var path = Consume(TokenType.String, "Expected file path").Value;

            if (path.StartsWith("\"") && path.EndsWith("\""))
            {
                path = path.Substring(1, path.Length - 2);
            }
            Consume(TokenType.Semicolon, "Expected ';'");
            Consume(TokenType.EndOfLine, "Expected EOL");
            return new WriteFileNode(content, path, Previous().LineNumber);
        }

        private StatementNode ParseTypeConversion()
        {
            var src = Consume(TokenType.Identifier, "Expected source variable name").Value;
            Consume(TokenType.To, "Expected 'to'");
            Consume(TokenType.Text, "Expected 'text'");
            Consume(TokenType.As, "Expected 'as'");
            var target = Consume(TokenType.Identifier, "Expected target variable name").Value;
            Consume(TokenType.Semicolon, "Expected ';'");
            Consume(TokenType.EndOfLine, "Expected EOL");
            return new TypeConversionNode(src, target, Previous().LineNumber);
        }

        private ExpressionNode ParseExpression()
        {
            var left = ParseTerm();

            while (Match(TokenType.Plus, TokenType.Minus))
            {
                var op = Previous().Value;
                var right = ParseTerm();
                left = new BinaryOperationNode(left, op, right, Previous().LineNumber);
            }

            if (Match(TokenType.CombinedWith))
            {
                var right = ParseExpression();
                left = new ConcatenationExpressionNode(left, right, Previous().LineNumber);
            }

            return left;
        }

        private ExpressionNode ParseTerm()
        {
            var left = ParseFactor();

            while (Match(TokenType.TimesOp, TokenType.DividedBy))
            {
                var op = Previous().Value;
                var right = ParseFactor();
                left = new BinaryOperationNode(left, op, right, Previous().LineNumber);
            }

            return left;
        }

        private ExpressionNode ParseFactor()
        {
            if (Match(TokenType.Minus))
            {
                var right = ParseFactor();
                return new BinaryOperationNode(new LiteralNode("0", "number", Previous().LineNumber), "-", right, Previous().LineNumber);
            }

            return ParsePrimary();
        }

        private ExpressionNode ParsePrimary()
        {
            if (Match(TokenType.Number)) return new LiteralNode(Previous().Value, "number", Previous().LineNumber);
            if (Match(TokenType.String)) return new LiteralNode(Previous().Value, "string", Previous().LineNumber);
            if (Match(TokenType.Boolean)) return new LiteralNode(Previous().Value, "boolean", Previous().LineNumber);
            if (Match(TokenType.Identifier)) return new VariableNode(Previous().Value, Previous().LineNumber);
            throw new Exception($"Expected expression (number, string, boolean, or identifier) at line {Peek().LineNumber}");
        }

        private bool Match(params TokenType[] types) { foreach (var t in types) if (Check(t)) { Advance(); return true; } return false; }
        private Token Consume(TokenType type, string message) => Check(type) ? Advance() : throw new Exception(message + $" at line {Peek().LineNumber}");
        private bool Check(TokenType type) => !IsAtEnd() && Peek().Type == type;
        private Token Advance() { if (!IsAtEnd()) _current++; return Previous(); }
        private bool IsAtEnd() => _current >= _tokens.Count;
        private Token Peek() => _tokens[_current];
        private Token Previous() => _tokens[_current - 1];
    }

    class NimCodeGenerator : IAstVisitor<string>
    {
        private readonly List<string> _code = new List<string>();
        private readonly Dictionary<string, string> _variableTypes = new Dictionary<string, string>();
        private int _indentLevel = 0;
        private int _uniqueId = 0;

        public string GenerateCode(ProgramNode program)
        {
            _code.Add("import os, strutils, strformat, streams");
            _code.Add("");
            foreach (var v in program.GlobalVariables) v.Accept(this);
            _code.Add("");
            foreach (var f in program.Functions) f.Accept(this);
            _code.Add("");
            _code.Add("proc main() =");
            _indentLevel++;
            foreach (var s in program.MainStatements) s.Accept(this);
            _indentLevel--;
            _code.Add("main()");
            return string.Join("\n", _code);
        }

        private string Indent() => new string(' ', _indentLevel * 2);

        public string Visit(ProgramNode n) => throw new NotImplementedException();
        public string Visit(VariableDeclarationNode n)
        {
            string nimType = n.Type switch { "string" => "string", "boolean" => "bool", "number" => "int", _ => throw new Exception($"Unknown type: {n.Type}") };
            _variableTypes[n.Name] = nimType;
            _code.Add($"{Indent()}var {n.Name}: {nimType} = {n.Value}");
            return "";
        }
        public string Visit(FunctionDefinitionNode n)
        {
            _code.Add($"{Indent()}proc {n.Name}() =");
            _indentLevel++;
            foreach (var s in n.Body) s.Accept(this);
            _indentLevel--;
            if (n.Body.Count == 0) _code.Add($"{Indent()}  discard");
            _code.Add("");
            return "";
        }
        public string Visit(AssignmentNode n) { _code.Add($"{Indent()}{n.VariableName} = {n.Value.Accept(this)}"); return ""; }
        public string Visit(CompoundAssignmentNode n)
        {
            string value = n.Value.Accept(this);
            if (n.Operator == "div=") _code.Add($"{Indent()}{n.VariableName} = {n.VariableName} div {value}");
            else _code.Add($"{Indent()}{n.VariableName} {n.Operator} {value}");
            return "";
        }
        public string Visit(PrintNode n) { _code.Add($"{Indent()}echo {n.Value.Accept(this)}"); return ""; }
        public string Visit(IfNode n)
        {
            _code.Add($"{Indent()}if {n.Condition.Accept(this)}:");
            _indentLevel++;
            foreach (var s in n.ThenStatements) s.Accept(this);
            _indentLevel--;

            foreach (var (condition, statements) in n.ElseIfBranches)
            {
                _code.Add($"{Indent()}elif {condition.Accept(this)}:");
                _indentLevel++;
                foreach (var s in statements) s.Accept(this);
                _indentLevel--;
            }

            if (n.ElseStatements != null)
            {
                _code.Add($"{Indent()}else:");
                _indentLevel++;
                foreach (var s in n.ElseStatements) s.Accept(this);
                _indentLevel--;
            }
            return "";
        }
        public string Visit(RepeatNode n)
        {
            string count = n.Count.Accept(this);
            string loopVar = $"i{_uniqueId++}";
            _code.Add($"{Indent()}for {loopVar} in 1..{count}:");
            _indentLevel++;
            foreach (var s in n.Body) s.Accept(this);
            _indentLevel--;
            return "";
        }
        public string Visit(FunctionCallNode n) { _code.Add($"{Indent()}{n.FunctionName}()"); return ""; }
        public string Visit(WaitNode n) { _code.Add($"{Indent()}sleep(int({n.Seconds.Accept(this)} * 1000))"); return ""; }
        public string Visit(InputNode n)
        {
            string varType = _variableTypes.GetValueOrDefault(n.VariableName, "string");
            if (varType == "string")
                _code.Add($"{Indent()}{n.VariableName} = readLine(stdin)");
            else
                _code.Add($"{Indent()}{n.VariableName} = parseInt(readLine(stdin))");
            return "";
        }
        public string Visit(ProcessNode n) { _code.Add($"{Indent()}discard execShellCmd({n.ProcessPath})"); return ""; }
        public string Visit(WriteFileNode n)
        {
            string content = n.Content.Accept(this);
            string filePath = n.FilePath.Replace("\\", "\\\\");
            _code.Add($"{Indent()}writeFile(\"{filePath}\", {content})");
            return "";
        }
        public string Visit(TypeConversionNode n) { _code.Add($"{Indent()}{n.TargetVariable} = ${n.SourceVariable}"); return ""; }
        public string Visit(BinaryOperationNode n)
        {
            string left = n.Left.Accept(this);
            string right = n.Right.Accept(this);
            string op = n.Operator switch
            {
                "equal to" => "==",
                "not equal to" => "!=",
                "greater than" => ">",
                "less than" => "<",
                "plus" => "+",
                "minus" => "-",
                "times" => "*",
                "divided by" => "div",
                _ => n.Operator
            };
            return $"{left} {op} {right}";
        }
        public string Visit(ConcatenationExpressionNode n) => $"{n.Left.Accept(this)} & {n.Right.Accept(this)}";
        public string Visit(VariableNode n) => n.Name;
        public string Visit(LiteralNode n) => n.Value;
        public string Visit(BlockNode n)
        {
            foreach (var statement in n.Statements) statement.Accept(this);
            return "";
        }
        public string Visit(ReadFileNode n)
        {
            string filePath = $"\"{n.FilePath}\"";
            _code.Add($"{Indent()}{n.VariableName} = readFile({filePath})");
            return "";
        }
    }

    class NimCompiler
    {
        private readonly Configuration _config;
        public NimCompiler(Configuration config) { _config = config; }
        public void Compile(string nimCode)
        {
            File.WriteAllText(_config.NimFile, nimCode);
            Console.WriteLine("Compiling with Nim...");
            var psi = new ProcessStartInfo();
            psi.FileName = _config.NimCompilerPath;
            psi.WorkingDirectory = _config.BaseDirectory;
            string nimFileName = Path.GetFileName(_config.NimFile);
            psi.Arguments = $"c -d:release --hints:off --nimcache:.nimcache {nimFileName}";
            string gccDir = @"C:\TDM-GCC-64\bin";
            psi.EnvironmentVariables["PATH"] = gccDir + ";" + Path.Combine(gccDir, "x86_64-w64-mingw32", "bin") + ";" + Environment.GetEnvironmentVariable("PATH");
            psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.CreateNoWindow = true;
            var process = new Process { StartInfo = psi };
            process.Start();
            string output = process.StandardOutput.ReadToEnd(); string error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
            if (!string.IsNullOrEmpty(error)) Console.WriteLine(error);
            if (File.Exists(_config.ExeFile)) Console.WriteLine($"Compilation finished: {_config.ExeFile}");
            else if (error.Contains("ERROR")) throw new Exception("Compilation failed.");
        }
    }

    class HumanScriptTranspiler
    {
        private readonly Configuration _config;
        public HumanScriptTranspiler(Configuration config) { _config = config; }
        public void Transpile()
        {
            var lines = File.ReadAllLines(_config.InputFile);
            var lexer = new Lexer(lines);
            var tokens = lexer.Tokenize();
            var parser = new Parser(tokens);
            var ast = parser.Parse();
            var codeGenerator = new NimCodeGenerator();
            var nimCode = codeGenerator.GenerateCode(ast);
            var compiler = new NimCompiler(_config);
            compiler.Compile(nimCode);
        }
    }
}