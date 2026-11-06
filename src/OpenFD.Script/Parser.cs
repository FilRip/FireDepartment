using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed class Parser
{
    public static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        { "void", "s32", "f32", "mcString", "mcBool", "EntityID", "u32", "s16", "u16", "u8", "s8", "bool", "int", "float" };

    private readonly List<Token> _t;
    private int _p;
    private readonly string _file;
    private Parser(List<Token> t, string file)
    {
        _t = t;
        _file = file;
    }

    public static List<Function> ParseFile(string src, string file)
    {
        Parser p = new Parser(Lexer.Tokenize(src), file);
        List<Function> res = new List<Function>();
        while (p.Peek.Kind != Tok.End)
        {
            if (p.IsOp(";"))
            {
                p._p++;
                continue;
            }
            res.Add(p.ParseFunction());
        }
        return res;
    }

    private Token Peek => _t[_p];
    private Token Next() => _t[_p++];
    private bool IsOp(string op) => Peek.Kind == Tok.Op && Peek.Text == op;
    private bool IsIdent(string id) => Peek.Kind == Tok.Ident && Peek.Text == id;
    private void Expect(string op)
    {
        if (!IsOp(op))
            throw new ParseError($"{_file}:{Peek.Line} « {op} » attendu, trouvé « {Peek.Text} »");
        _p++;
    }
    private string ExpectIdent()
    {
        if (Peek.Kind != Tok.Ident)
            throw new ParseError($"{_file}:{Peek.Line} nom attendu, trouvé « {Peek.Text} »");
        return Next().Text;
    }
    private bool AtType => Peek.Kind == Tok.Ident && Types.Contains(Peek.Text)
        && _t[_p + 1].Kind == Tok.Ident; // « s32 i » mais pas un appel

    private Function ParseFunction()
    {
        string ret = ExpectIdent();
        string name = ExpectIdent();
        Expect("(");
        List<(string, string)> ps = new List<(string, string)>();
        while (!IsOp(")"))
        {
            string ty = ExpectIdent();
            if (ty == "void" && IsOp(")"))
                break;
            if (IsOp("&"))
                _p++;
            string pn = ExpectIdent();
            ps.Add((ty, pn));
            if (IsOp(","))
                _p++;
        }
        Expect(")");
        if (IsOp(";"))
        {
            _p++;
            return new Function(ret, name, ps, new Block(Peek.Line, new()), _file);
        } // prototype
        return new Function(ret, name, ps, ParseBlock(), _file);
    }

    private Block ParseBlock()
    {
        int line = Peek.Line;
        Expect("{");
        List<Stmt> body = new List<Stmt>();
        while (!IsOp("}"))
        {
            if (Peek.Kind == Tok.End)
                throw new ParseError($"{_file}: accolade fermante manquante (bloc ligne {line})");
            body.Add(ParseStmt());
        }
        _p++;
        return new Block(line, body);
    }

    private Stmt ParseStmt()
    {
        int line = Peek.Line;
        if (IsOp("{"))
            return ParseBlock();
        if (IsOp(";"))
        {
            _p++;
            return new Empty(line);
        }
        if (IsIdent("if"))
        {
            _p++;
            Expect("(");
            Expr c = ParseExpr();
            Expect(")");
            Stmt th = ParseStmt();
            Stmt? el = null;
            if (IsIdent("else"))
            {
                _p++;
                el = ParseStmt();
            }
            return new If(line, c, th, el);
        }
        if (IsIdent("for"))
        {
            _p++;
            Expect("(");
            Stmt? init = null;
            if (!IsOp(";"))
                init = AtType ? ParseDecl() : new ExprStmt(line, ParseExpr());
            Expect(";");
            Expr? cond = IsOp(";") ? null : ParseExpr();
            Expect(";");
            Expr? step = IsOp(")") ? null : ParseExpr();
            Expect(")");
            return new For(line, init, cond, step, ParseStmt());
        }
        if (IsIdent("while"))
        {
            _p++;
            Expect("(");
            Expr c = ParseExpr();
            Expect(")");
            return new While(line, c, ParseStmt());
        }
        if (IsIdent("return"))
        {
            _p++;
            Expr? v = IsOp(";") ? null : ParseExpr();
            Expect(";");
            return new Return(line, v);
        }
        if (AtType)
        {
            Decl d = ParseDecl();
            Expect(";");
            return d;
        }
        Expr e = ParseExpr();
        Expect(";");
        return new ExprStmt(line, e);
    }

    private Decl ParseDecl()
    {
        int line = Peek.Line;
        string ty = ExpectIdent();
        List<(string, Expr?)> vars = new List<(string, Expr?)>();
        do
        {
            if (vars.Count > 0)
                _p++;
            string n = ExpectIdent();
            Expr? init = null;
            if (IsOp("="))
            {
                _p++;
                init = ParseExpr();
            }
            vars.Add((n, init));
        } while (IsOp(","));
        return new Decl(line, ty, vars);
    }

    // ---------- expressions (précédence de C) ----------
    public Expr ParseExpr() => ParseAssign();

    private Expr ParseAssign()
    {
        Expr left = ParseOr();
        if (Peek.Kind == Tok.Op && Peek.Text is "=" or "+=" or "-=" or "*=" or "/=")
        {
            int line = Peek.Line;
            string op = Next().Text;
            if (left is not Var v)
                throw new ParseError($"{_file}:{line} affectation impossible");
            return new Assign(line, op, v.Name, ParseAssign());
        }
        return left;
    }
    private Expr ParseOr()
    {
        Expr l = ParseAnd();
        while (IsOp("||"))
        {
            int ln = Next().Line;
            l = new Binary(ln, "||", l, ParseAnd());
        }
        return l;
    }
    private Expr ParseAnd()
    {
        Expr l = ParseEq();
        while (IsOp("&&"))
        {
            int ln = Next().Line;
            l = new Binary(ln, "&&", l, ParseEq());
        }
        return l;
    }
    private Expr ParseEq()
    {
        Expr l = ParseRel();
        while (IsOp("==") || IsOp("!="))
        {
            Token t = Next();
            l = new Binary(t.Line, t.Text, l, ParseRel());
        }
        return l;
    }
    private Expr ParseRel()
    {
        Expr l = ParseAdd();
        while (IsOp("<") || IsOp(">") || IsOp("<=") || IsOp(">="))
        {
            Token t = Next();
            l = new Binary(t.Line, t.Text, l, ParseAdd());
        }
        return l;
    }
    private Expr ParseAdd()
    {
        Expr l = ParseMul();
        while (IsOp("+") || IsOp("-"))
        {
            Token t = Next();
            l = new Binary(t.Line, t.Text, l, ParseMul());
        }
        return l;
    }
    private Expr ParseMul()
    {
        Expr l = ParseUnary();
        while (IsOp("*") || IsOp("/") || IsOp("%"))
        {
            Token t = Next();
            l = new Binary(t.Line, t.Text, l, ParseUnary());
        }
        return l;
    }
    private Expr ParseUnary()
    {
        if (IsOp("-") || IsOp("!") || IsOp("+"))
        {
            Token t = Next();
            return new Unary(t.Line, t.Text, ParseUnary());
        }
        if (IsOp("++") || IsOp("--"))
        {
            Token t = Next();
            string name = ExpectIdent();
            return new Assign(t.Line, t.Text == "++" ? "+=" : "-=", name, new Num(t.Line, 1, true));
        }
        // conversion de type : (f32) x
        if (IsOp("(") && _t[_p + 1].Kind == Tok.Ident && Types.Contains(_t[_p + 1].Text) && _t[_p + 2].Kind == Tok.Op && _t[_p + 2].Text == ")")
        {
            int line = Peek.Line;
            _p++;
            string ty = Next().Text;
            _p++;
            return new Cast(line, ty, ParseUnary());
        }
        return ParsePostfix();
    }
    private Expr ParsePostfix()
    {
        Expr e = ParsePrimary();
        if ((IsOp("++") || IsOp("--")) && e is Var v)
        {
            Token t = Next();
            return new Assign(t.Line, t.Text == "++" ? "+=" : "-=", v.Name, new Num(t.Line, 1, true));
        }
        return e;
    }
    private Expr ParsePrimary()
    {
        Token t = Next();
        switch (t.Kind)
        {
            case Tok.Int: return new Num(t.Line, double.Parse(t.Text, CultureInfo.InvariantCulture), true);
            case Tok.Float: return new Num(t.Line, double.Parse(t.Text, CultureInfo.InvariantCulture), false);
            case Tok.Str: return new Str(t.Line, t.Text);
            case Tok.Ident:
                if (IsOp("("))
                {
                    _p++;
                    List<Expr> args = new List<Expr>();
                    while (!IsOp(")"))
                    {
                        args.Add(ParseExpr());
                        if (IsOp(","))
                            _p++;
                        else if (!IsOp(")"))
                            throw new ParseError($"{_file}:{Peek.Line} « , » ou « ) » attendu dans l'appel de {t.Text}");
                    }
                    _p++;
                    return new Call(t.Line, t.Text, args);
                }
                return new Var(t.Line, t.Text);
            case Tok.Op when t.Text == "(":
                Expr e = ParseExpr();
                Expect(")");
                return e;
        }
        throw new ParseError($"{_file}:{t.Line} expression attendue, trouvé « {t.Text} »");
    }
}
