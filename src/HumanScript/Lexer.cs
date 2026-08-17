namespace HumanScript;

public enum TokenType
{
    // keywords
    Is, As, A, An, The, Into, From, By, With, Of, In, Times, Forever,
    If, Otherwise, End, Repeat, While, AsLongAs, For, Every,
    Say, Show, Ask, Remember, Set, Increase, Decrease, Multiply, Divide,
    Greater, Less, Least, Most, Above, Below,
    And, Or, Not, Contains, Starts, Ends, Length,
    Yes, No,
    Add, Remove, Clear,
    Read, Write, Delete, Exists,
    Note, That, Catch, Try, Than, Then,
    // funcs
    To,
    Return,
    // identifiers
    Identifier, String, Number, Float,
    // special chars
    NewLine, Indent, Dedent, EOF,
    // operators
    Plus, Minus, Star, Slash, Dot,
    Eq, NotEq, Lt, LtEq, Gt, GtEq,
    Assign,
    LParen, RParen,
    Comma,
    LBracket, RBracket
}

public struct Token
{
    public TokenType Type;
    public string Text;
    public int Line;
    public int Column;
    public Token(TokenType type, string text, int line, int col = 0)
    {
        Type = type; Text = text; Line = line; Column = col;
    }
}

public class Lexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public Lexer(string src)
    {
        _src = src.Replace("\r\n", "\n").Replace("\r", "\n");
        _pos = 0;
        _line = 1;
        _col = 1;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        var indentStack = new List<int> { 0 };

        while (true)
        {
            int currentIndent = 0;
            while (_pos < _src.Length && (_src[_pos] == ' ' || _src[_pos] == '\t'))
            {
                currentIndent += (_src[_pos] == '\t') ? 4 : 1;
                _pos++;
            }

            if (_pos >= _src.Length)
                break;

            if (_src[_pos] == '\n')
            {
                _line++;
                _pos++;
                _col = 1;
                continue;
            }

            if (_src[_pos] == '/' && Peek(1) == '/')
            {
                while (_pos < _src.Length && _src[_pos] != '\n') _pos++;
                if (_pos < _src.Length && _src[_pos] == '\n')
                {
                    _line++;
                    _pos++;
                    _col = 1;
                }
                continue;
            }

            if (AtWord("note"))
            {
                while (_pos < _src.Length && _src[_pos] != '\n') _pos++;
                if (_pos < _src.Length && _src[_pos] == '\n')
                {
                    _line++;
                    _pos++;
                    _col = 1;
                }
                continue;
            }

            if (currentIndent > indentStack[^1])
            {
                indentStack.Add(currentIndent);
                tokens.Add(new Token(TokenType.Indent, "", _line));
            }
            else if (currentIndent < indentStack[^1])
            {
                while (indentStack.Count > 1 && currentIndent < indentStack[^1])
                {
                    indentStack.RemoveAt(indentStack.Count - 1);
                    tokens.Add(new Token(TokenType.Dedent, "", _line));
                }
                if (currentIndent != indentStack[^1])
                    throw new Exception($"Line {_line}: inconsistent indentation");
            }

            bool lineHasTokens = false;
            while (_pos < _src.Length && _src[_pos] != '\n')
            {
                SkipWhitespaceInline();
                if (_pos >= _src.Length || _src[_pos] == '\n') break;
                var tok = ReadToken();
                if (tok == null) break;
                if (tok.Value.Type != TokenType.EOF)
                {
                    tokens.Add(tok.Value);
                    lineHasTokens = true;
                }
            }

            if (lineHasTokens)
                tokens.Add(new Token(TokenType.NewLine, "\n", _line));

            if (_pos < _src.Length && _src[_pos] == '\n')
            {
                _line++;
                _pos++;
                _col = 1;
            }
            else
            {
                break;
            }
        }

        while (indentStack.Count > 1)
        {
            indentStack.RemoveAt(indentStack.Count - 1);
            tokens.Add(new Token(TokenType.Dedent, "", _line));
        }

        tokens.Add(new Token(TokenType.EOF, "", _line));
        return tokens;
    }

    private Token? ReadToken()
    {
        if (_pos + 1 < _src.Length && _src[_pos] == '/' && _src[_pos + 1] == '/')
        {
            while (_pos < _src.Length && _src[_pos] != '\n')
            {
                _pos++;
                _col++;
            }
            return null;
        }

        char c = _src[_pos];

        if (c == '"')
        {
            _pos++; _col++;
            var sb = new System.Text.StringBuilder();
            while (_pos < _src.Length && _src[_pos] != '"')
            {
                if (_src[_pos] == '\\' && _pos + 1 < _src.Length)
                {
                    _pos++; _col++;
                    sb.Append(_src[_pos] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => _src[_pos]
                    });
                }
                else
                {
                    sb.Append(_src[_pos]);
                }
                _pos++; _col++;
            }
            if (_pos >= _src.Length) throw new Exception($"Line {_line}: unterminated string");
            _pos++; _col++;
            return new Token(TokenType.String, sb.ToString(), _line, _col);
        }

        if (char.IsDigit(c))
        {
            int start = _pos;
            int startCol = _col;
            while (_pos < _src.Length && char.IsDigit(_src[_pos])) { _pos++; _col++; }
            bool isFloat = false;
            if (_pos < _src.Length && _src[_pos] == '.' && _pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1]))
            {
                isFloat = true;
                _pos++; _col++;
                while (_pos < _src.Length && char.IsDigit(_src[_pos])) { _pos++; _col++; }
            }
            string text = _src.Substring(start, _pos - start);
            return new Token(isFloat ? TokenType.Float : TokenType.Number, text, _line, startCol);
        }

        if (char.IsLetter(c) || c == '_')
        {
            int start = _pos;
            int startCol = _col;
            while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_'))
            { _pos++; _col++; }
            string word = _src.Substring(start, _pos - start).ToLowerInvariant();

            var (type, consumed) = MatchKeyword(word, start);
            if (consumed > word.Length)
            {
                _pos = start + consumed;
                _col = startCol + consumed;
            }
            return new Token(type, _src.Substring(start, _pos - start), _line, startCol);
        }

        _pos++; _col++;
        return c switch
        {
            '+' => new Token(TokenType.Plus, "+", _line, _col - 1),
            '-' => new Token(TokenType.Minus, "-", _line, _col - 1),
            '*' => new Token(TokenType.Star, "*", _line, _col - 1),
            '/' => new Token(TokenType.Slash, "/", _line, _col - 1),
            '(' => new Token(TokenType.LParen, "(", _line, _col - 1),
            ')' => new Token(TokenType.RParen, ")", _line, _col - 1),
            '[' => new Token(TokenType.LBracket, "[", _line, _col - 1),
            ']' => new Token(TokenType.RBracket, "]", _line, _col - 1),
            ',' => new Token(TokenType.Comma, ",", _line, _col - 1),
            '=' => new Token(TokenType.Assign, "=", _line, _col - 1),
            _ => throw new Exception($"Line {_line}: unexpected character '{c}'")
        };
    }

    private (TokenType type, int consumed) MatchKeyword(string firstWord, int startPos)
    {
        var multiWordChecks = new (string[] words, TokenType type)[]
        {
            (new[] { "as", "long", "as" }, TokenType.AsLongAs),
            (new[] { "at", "least" }, TokenType.Least),
            (new[] { "at", "most" }, TokenType.Most),
        };

        foreach (var (words, type) in multiWordChecks)
        {
            if (firstWord != words[0]) continue;
            bool match = true;
            int pos = startPos + words[0].Length;
            for (int i = 1; i < words.Length; i++)
            {
                while (pos < _src.Length && char.IsWhiteSpace(_src[pos]) && _src[pos] != '\n') pos++;
                int wStart = pos;
                while (pos < _src.Length && (char.IsLetterOrDigit(_src[pos]) || _src[pos] == '_')) pos++;
                string w = _src.Substring(wStart, pos - wStart).ToLowerInvariant();
                if (w != words[i]) { match = false; break; }
            }
            if (match)
            {
                int consumed = pos - startPos;
                return (type, consumed);
            }
        }

        return firstWord switch
        {
            "try" => (TokenType.Try, firstWord.Length),
            "catch" => (TokenType.Catch, firstWord.Length),
            "number" => (TokenType.Number, firstWord.Length),
            "is" => (TokenType.Is, firstWord.Length),
            "as" => (TokenType.As, firstWord.Length),
            "a" => (TokenType.A, firstWord.Length),
            "an" => (TokenType.A, firstWord.Length),
            "the" => (TokenType.The, firstWord.Length),
            "to" => (TokenType.To, firstWord.Length),
            "into" => (TokenType.Into, firstWord.Length),
            "from" => (TokenType.From, firstWord.Length),
            "by" => (TokenType.By, firstWord.Length),
            "with" => (TokenType.With, firstWord.Length),
            "of" => (TokenType.Of, firstWord.Length),
            "in" => (TokenType.In, firstWord.Length),
            "times" => (TokenType.Times, firstWord.Length),
            "forever" => (TokenType.Forever, firstWord.Length),
            "if" => (TokenType.If, firstWord.Length),
            "otherwise" => (TokenType.Otherwise, firstWord.Length),
            "end" => (TokenType.End, firstWord.Length),
            "repeat" => (TokenType.Repeat, firstWord.Length),
            "while" => (TokenType.While, firstWord.Length),
            "for" => (TokenType.For, firstWord.Length),
            "every" => (TokenType.Every, firstWord.Length),
            "say" => (TokenType.Say, firstWord.Length),
            "show" => (TokenType.Show, firstWord.Length),
            "ask" => (TokenType.Ask, firstWord.Length),
            "remember" => (TokenType.Remember, firstWord.Length),
            "set" => (TokenType.Set, firstWord.Length),
            "increase" => (TokenType.Increase, firstWord.Length),
            "decrease" => (TokenType.Decrease, firstWord.Length),
            "multiply" => (TokenType.Multiply, firstWord.Length),
            "divide" => (TokenType.Divide, firstWord.Length),
            "greater" => (TokenType.Greater, firstWord.Length),
            "less" => (TokenType.Less, firstWord.Length),
            "above" => (TokenType.Above, firstWord.Length),
            "below" => (TokenType.Below, firstWord.Length),
            "and" => (TokenType.And, firstWord.Length),
            "or" => (TokenType.Or, firstWord.Length),
            "not" => (TokenType.Not, firstWord.Length),
            "contains" => (TokenType.Contains, firstWord.Length),
            "starts" => (TokenType.Starts, firstWord.Length),
            "ends" => (TokenType.Ends, firstWord.Length),
            "length" => (TokenType.Length, firstWord.Length),
            "yes" => (TokenType.Yes, firstWord.Length),
            "no" => (TokenType.No, firstWord.Length),
            "add" => (TokenType.Add, firstWord.Length),
            "remove" => (TokenType.Remove, firstWord.Length),
            "clear" => (TokenType.Clear, firstWord.Length),
            "read" => (TokenType.Read, firstWord.Length),
            "write" => (TokenType.Write, firstWord.Length),
            "delete" => (TokenType.Delete, firstWord.Length),
            "exists" => (TokenType.Exists, firstWord.Length),
            "note" => (TokenType.Note, firstWord.Length),
            "that" => (TokenType.That, firstWord.Length),
            "return" => (TokenType.Return, firstWord.Length),
            "true" => (TokenType.Yes, firstWord.Length),
            "false" => (TokenType.No, firstWord.Length),
            "then" => (TokenType.Then, firstWord.Length),
            "than" => (TokenType.Than, firstWord.Length),
            _ => (TokenType.Identifier, firstWord.Length)
        };
    }

    private bool AtWord(string word)
    {
        if (_pos + word.Length > _src.Length) return false;
        if (_src.Substring(_pos, word.Length).ToLowerInvariant() != word) return false;
        if (_pos + word.Length < _src.Length && char.IsLetterOrDigit(_src[_pos + word.Length]))
            return false;
        return true;
    }

    private void SkipBlankLines()
    {
        while (_pos < _src.Length)
        {
            while (_pos < _src.Length && (_src[_pos] == ' ' || _src[_pos] == '\t'))
            { _pos++; _col++; }

            if (_pos < _src.Length && _src[_pos] == '\n')
            {
                _line++; _pos++; _col = 1;
                continue;
            }
            if (_pos < _src.Length && _src[_pos] == '/' && Peek(1) == '/')
            {
                while (_pos < _src.Length && _src[_pos] != '\n') _pos++;
                if (_pos < _src.Length && _src[_pos] == '\n') { _line++; _pos++; _col = 1; }
                continue;
            }
            if (AtWord("note"))
            {
                while (_pos < _src.Length && _src[_pos] != '\n') _pos++;
                if (_pos < _src.Length && _src[_pos] == '\n') { _line++; _pos++; _col = 1; }
                continue;
            }
            break;
        }
    }

    private void SkipWhitespaceInline()
    {
        while (_pos < _src.Length && (_src[_pos] == ' ' || _src[_pos] == '\t'))
        {
            _pos++;
            _col++;
        }
    }

    private char Peek(int offset) => _pos + offset < _src.Length ? _src[_pos + offset] : '\0';
}