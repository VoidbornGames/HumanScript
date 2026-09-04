using HSharp.Checking;
using HSharp.Lexing;
using HSharp.Parsing;
using HSharp.Syntax;

namespace HSharp.Analysis;

public sealed partial class Workspace
{
    private static int TokenSpan(Token t) => t.Kind switch
    {
        Tok.Str => ((string)t.Value!).Length + 2,
        Tok.Interp => 3 + ((List<InterpPart>)t.Value!).Sum(p => p.Text.Length + (p.IsExpr ? 2 : 0)),
        _ => Math.Max(1, t.Text.Length)
    };

    private static readonly HashSet<string> TypeOpensGeneric = new() { "list", "task", "map" };

    private static readonly HashSet<string> ControlKws =
        new() { "if", "else", "while", "for", "foreach", "in", "return", "try", "catch", "break", "continue", "await", "lock" };

    // token-stream walk with paren/brace/angle stacks that decides what the
    // cursor could be sitting at: a member access, a call argument, a type
    // position, a statement start and so on. completion quality lives here
    public static CompletionContext Classify(string text, Position pos)
    {
        var ctx = new CompletionContext { Line = pos.Line, Col = pos.Col };

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (pos.Line - 1 >= 0 && pos.Line - 1 < lines.Length)
        {
            var prefix = lines[pos.Line - 1][..Math.Min(pos.Col - 1, lines[pos.Line - 1].Length)];
            if (prefix.Contains("//"))
            {
                ctx.Kind = CompletionCtxKind.None;
                return ctx;
            }
        }

        List<Token> toks;
        try
        {
            var lx = new Lexer(text) { Tolerant = true };
            toks = lx.Tokenize();
        }
        catch (SourceError)
        {
            return ctx;
        }

        foreach (var t in toks)
        {
            if (t.Line > pos.Line) break;
            if (t.Kind == Tok.Str)
            {
                if (t.Line == pos.Line && pos.Col > t.Col && pos.Col < t.Col + TokenSpan(t))
                {
                    ctx.Kind = CompletionCtxKind.None;
                    return ctx;
                }
            }
            else if (t.Kind == Tok.Interp)
            {
                foreach (var p in (List<InterpPart>)t.Value!)
                {
                    bool afterStart = pos.Line > p.Line || (pos.Line == p.Line && pos.Col >= p.Col);
                    bool beforeEnd = pos.Line < p.EndLine || (pos.Line == p.EndLine && pos.Col < p.EndCol);
                    if (afterStart && beforeEnd)
                    {
                        if (p.IsExpr)
                        {
                            ctx.Kind = CompletionCtxKind.Expression;
                            return ctx;
                        }
                        ctx.Kind = CompletionCtxKind.None;
                        return ctx;
                    }
                }
            }
        }

        int cut = 0;
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind == Tok.EOF) break;
            if (t.Line > pos.Line || (t.Line == pos.Line && t.Col > pos.Col)) break;
            if (t.Line == pos.Line && pos.Col <= t.Col) break;
            bool wordy = t.Kind is Tok.Ident or Tok.Int or Tok.Float or Tok.Str or Tok.Interp;
            if (wordy && t.Line == pos.Line && pos.Col <= t.Col + TokenSpan(t)) break;
            cut = i + 1;
        }

        var paren = new Stack<(string Owner, int Line, int Col, int Commas, int ParenIdx)>();
        var angles = new Stack<bool>();

        var brace = new Stack<(string PrevText, Tok PrevKind, string Prev2Text, Tok Prev2Kind, int BraceIdx)>();
        Token none = new(Tok.EOF, "", null, 0, 0);
        Token prev = none;
        Token prev2 = none;

        int prevIdx = -1;
        for (int i = 0; i < cut; i++)
        {
            var t = toks[i];
            switch (t.Kind)
            {
                case Tok.LParen:
                    paren.Push((prev != none ? prev.Text : "", prev.Line, prev.Col, 0, i));
                    break;
                case Tok.RParen:
                    if (paren.Count > 0) paren.Pop();
                    angles.Clear();
                    break;
                case Tok.LBrace:
                    brace.Push((prev.Text, prev.Kind, prev2.Text, prev2.Kind, i));
                    break;
                case Tok.RBrace:
                    if (brace.Count > 0) brace.Pop();
                    break;
                case Tok.Lt:
                    angles.Push(prev != none && (TypeOpensGeneric.Contains(prev.Text) || angles.Count > 0));
                    break;
                case Tok.Gt:
                    if (angles.Count > 0) angles.Pop();
                    break;
                case Tok.Comma:
                    if (paren.Count > 0 && angles.Count == 0)
                    {
                        var top = paren.Pop();
                        paren.Push((top.Owner, top.Line, top.Col, top.Commas + 1, top.ParenIdx));
                    }
                    break;
            }
            prev2 = prev;
            prev = t;
            prevIdx = i;
        }

        ctx.Kind = CompletionCtxKind.Expression;

        if (angles.Count > 0 && angles.Peek())
        {
            ctx.Kind = CompletionCtxKind.TypePosition;
            return ctx;
        }

        if (prev != none && prev.Kind is Tok.Dot or Tok.QuestionDot)
        {
            ctx.Kind = CompletionCtxKind.MemberAccess;
            ctx.MemberTarget = prev2 != none && prev2.Kind == Tok.Ident ? prev2.Text : null;
            ctx.ReceiverEnd = prevIdx;
            return ctx;
        }

        if (prev != none && prev.Kind == Tok.KwImport)
        {
            ctx.Kind = CompletionCtxKind.ImportPath;
            return ctx;
        }

        if (prev != none && prev.Kind == Tok.KwMove)
        {
            ctx.Kind = CompletionCtxKind.Expression;
            ctx.MoveValue = true;
            return ctx;
        }

        if (paren.Count > 0)
        {
            var top = paren.Peek();

            if (paren.Count >= 2 && top.Owner == "(" && !ArrowSince(toks, top.ParenIdx, cut))
            {

                var outer = paren.ElementAt(1);
                if (outer.Owner == "OnAccept")
                {
                    ctx.Kind = CompletionCtxKind.LambdaParam;
                    ctx.Callee = "OnAccept";

                    int oi = outer.ParenIdx;
                    if (oi >= 3 && toks[oi - 1].Kind == Tok.Ident && toks[oi - 2].Kind == Tok.Dot
                        && toks[oi - 3].Kind == Tok.Ident)
                        ctx.MemberTarget = toks[oi - 3].Text;
                    return ctx;
                }
            }

            if (top.Owner == "foreach")
            {

                if (prev != none && prev.Kind == Tok.KwVar)
                {
                    ctx.Kind = CompletionCtxKind.DeclName;
                    return ctx;
                }

                bool sawIn = false;
                for (int i = 0; i < cut; i++)
                    if (toks[i].Kind == Tok.KwIn
                        && (toks[i].Line > top.Line || (toks[i].Line == top.Line && toks[i].Col > top.Col)))
                        sawIn = true;
                ctx.Kind = sawIn ? CompletionCtxKind.ForeachIterable : CompletionCtxKind.None;
                return ctx;
            }

            if (top.Owner == "catch")
            {
                ctx.Kind = CompletionCtxKind.CatchVar;
                return ctx;
            }

            if (top.Owner == "lock")
            {
                ctx.Kind = CompletionCtxKind.LockTarget;
                return ctx;
            }

            if (top.Owner == "" || ControlKws.Contains(top.Owner))
            {
                ctx.Kind = CompletionCtxKind.Expression;
                return ctx;
            }

            ctx.Kind = CompletionCtxKind.CallArg;
            ctx.Callee = top.Owner;
            ctx.CalleeLine = top.Line;
            ctx.CalleeCol = top.Col;
            ctx.ArgIndex = top.Commas;
            return ctx;
        }

        if (brace.Count > 0)
        {
            var b = brace.Peek();
            bool initializer = b.PrevKind == Tok.Ident
                && !(b.Prev2Kind is Tok.KwClass or Tok.KwStruct or Tok.KwEnum);
            bool literal = b.PrevKind is Tok.Gt or Tok.LBrace;

            if (initializer)
            {

                int lastSepKind = 1;
                string? field = null;
                for (int i = b.BraceIdx + 1; i < cut; i++)
                {
                    if (toks[i].Kind is Tok.LBrace or Tok.Comma) { lastSepKind = 1; field = null; }
                    else if (toks[i].Kind == Tok.Colon) lastSepKind = 2;
                    else if (lastSepKind == 1 && toks[i].Kind == Tok.Ident) field = toks[i].Text;
                }

                if (lastSepKind == 2)
                {
                    ctx.Kind = CompletionCtxKind.Expression;
                    ctx.InitializerType = b.PrevText;
                    ctx.InitializerField = field;
                    return ctx;
                }
                ctx.Kind = CompletionCtxKind.InitializerField;
                ctx.InitializerType = b.PrevText;
                return ctx;
            }
            if (literal)
            {
                ctx.Kind = CompletionCtxKind.Expression;
                return ctx;
            }
        }

        if (prev != none && prev.Kind is Tok.Assign or Tok.PlusEq or Tok.MinusEq)
        {
            ctx.Kind = CompletionCtxKind.Expression;
            int back = cut - 2;
            if (back >= 0 && toks[back].Kind == Tok.Ident)
            {
                ctx.AssignTarget = toks[back].Text;
            }
            else if (back >= 1 && toks[back].Kind == Tok.RBracket)
            {

                int depth = 0;
                for (int b = back; b >= 0; b--)
                {
                    if (toks[b].Kind == Tok.RBracket) depth++;
                    else if (toks[b].Kind == Tok.LBracket)
                    {
                        depth--;
                        if (depth == 0 && b > 0 && toks[b - 1].Kind == Tok.Ident)
                        {
                            ctx.AssignTarget = toks[b - 1].Text;
                            ctx.AssignThroughIndex = true;
                            break;
                        }
                    }
                }
            }
            return ctx;
        }

        if (prev != none && prev.Kind == Tok.KwReturn)
        {
            ctx.Kind = CompletionCtxKind.Expression;
            return ctx;
        }

        if (prev != none && prev.Kind is Tok.KwVar or Tok.KwNew
            or Tok.KwClass or Tok.KwStruct or Tok.KwEnum)
        {
            ctx.Kind = prev.Kind == Tok.KwNew ? CompletionCtxKind.TypePosition : CompletionCtxKind.DeclName;
            return ctx;
        }
        if (prev != none && IsTypeish(prev) && prev.Kind != Tok.Ident)
        {
            ctx.Kind = CompletionCtxKind.DeclName;
            return ctx;
        }

        if (prev != none && prev.Kind == Tok.Ident && KnownTypeName(prev.Text))
        {
            bool stmtish = prev2 == none || prev2.Kind is Tok.Semi or Tok.LBrace or Tok.RBrace
                or Tok.KwPublic or Tok.Gt || prev2.Line < prev.Line;
            if (stmtish)
            {
                ctx.Kind = CompletionCtxKind.DeclName;
                return ctx;
            }
            ctx.Kind = CompletionCtxKind.Expression;
            return ctx;
        }

        bool lineBreak = prev != none && prev.Line < pos.Line;
        bool afterStmtEnd = prev == none || prev.Kind is Tok.Semi or Tok.LBrace or Tok.RBrace;
        bool operatorCont = prev != none && prev.Kind is Tok.Plus or Tok.Minus or Tok.Star or Tok.Slash
            or Tok.Percent or Tok.AndAnd or Tok.OrOr or Tok.QuestionQuestion or Tok.Question
            or Tok.FatArrow or Tok.Dot or Tok.QuestionDot or Tok.Eq or Tok.NotEq
            or Tok.Lt or Tok.Gt or Tok.LtEq or Tok.GtEq or Tok.Colon;

        if (afterStmtEnd || (lineBreak && !operatorCont))
        {

            if (prev.Kind == Tok.Ident && prev2 != none
                && (IsTypeish(prev2) || prev2.Kind == Tok.Ident))
            {
                ctx.Kind = CompletionCtxKind.None;
                return ctx;
            }

            if (prev.Kind is Tok.KwClass or Tok.KwStruct or Tok.KwEnum)
            {
                ctx.Kind = CompletionCtxKind.DeclName;
                return ctx;
            }
            if (prev.Kind == Tok.KwPublic)
            {
                ctx.Kind = CompletionCtxKind.StatementStart;
                return ctx;
            }
            if (IsTypeish(prev) && (prev.Kind != Tok.Ident || KnownTypeName(prev.Text)))
            {
                ctx.Kind = CompletionCtxKind.DeclName;
                return ctx;
            }
            ctx.Kind = CompletionCtxKind.StatementStart;
            return ctx;
        }

        ctx.Kind = CompletionCtxKind.Expression;
        return ctx;
    }

    private static bool KnownTypeName(string name) =>
        RuntimeApi.HandleMembers.ContainsKey(name) || RuntimeApi.IsStaticClass(name)
        || name is "task" or "map";

    private static bool IsTypeish(Token t) =>
        t.Kind is Tok.TyInt or Tok.TyFloat or Tok.TyBool or Tok.TyString or Tok.TyList or Tok.TyBuffer
        || t.Kind == Tok.KwVoid
        || (t.Kind == Tok.Ident && !ControlKws.Contains(t.Text));

    private static bool ArrowSince(List<Token> toks, int from, int cut)
    {
        for (int i = from + 1; i < cut; i++)
            if (toks[i].Kind == Tok.FatArrow) return true;
        return false;
    }

}

