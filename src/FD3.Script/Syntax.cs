using System.Globalization;
using System.Text;

namespace FD3.Script;

// Langage des scripts de mission (.scp) : un C simplifié.
// Types : void, s32, f32, mcString, mcBool, EntityID. Instructions : déclarations, affectations,
// if/else, for, while, return, appels. Les noms de fonctions du moteur ne tiennent pas compte de la casse.

public enum Tok { Ident, Int, Float, Str, Op, End }

public readonly record struct Token(Tok Kind, string Text, int Line);

public static class Lexer
{
    private static readonly string[] Ops3 = { };
    private static readonly string[] Ops2 = { "++", "--", "+=", "-=", "*=", "/=", "==", "!=", "<=", ">=", "&&", "||" };

    public static List<Token> Tokenize(string src)
    {
        var toks = new List<Token>();
        int i = 0, line = 1;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/') { while (i < src.Length && src[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) { if (src[i] == '\n') line++; i++; }
                i += 2; continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i; while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                toks.Add(new Token(Tok.Ident, src[s..i], line)); continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                int s = i; bool flt = false;
                while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.')) { if (src[i] == '.') flt = true; i++; }
                if (i < src.Length && (src[i] == 'f' || src[i] == 'F')) { flt = true; i++; toks.Add(new Token(Tok.Float, src[s..(i - 1)], line)); continue; }
                toks.Add(new Token(flt ? Tok.Float : Tok.Int, src[s..i], line)); continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder(); i++;
                while (i < src.Length && src[i] != '"' && src[i] != '\n')
                {
                    if (src[i] == '\\' && i + 1 < src.Length) { sb.Append(src[i + 1] switch { 'n' => '\n', 't' => '\t', _ => src[i + 1] }); i += 2; continue; }
                    sb.Append(src[i++]);
                }
                i++; toks.Add(new Token(Tok.Str, sb.ToString(), line)); continue;
            }
            if (i + 1 < src.Length && Ops2.Contains(src.Substring(i, 2))) { toks.Add(new Token(Tok.Op, src.Substring(i, 2), line)); i += 2; continue; }
            toks.Add(new Token(Tok.Op, c.ToString(), line)); i++;
        }
        toks.Add(new Token(Tok.End, "", line));
        return toks;
    }
}

// ---------- arbre syntaxique ----------
public abstract record Expr(int Line);
public sealed record Num(int Line, double Value, bool IsInt) : Expr(Line);
public sealed record Str(int Line, string Value) : Expr(Line);
public sealed record Var(int Line, string Name) : Expr(Line);
public sealed record Unary(int Line, string Op, Expr E) : Expr(Line);
public sealed record Binary(int Line, string Op, Expr L, Expr R) : Expr(Line);
public sealed record Assign(int Line, string Op, string Name, Expr Value) : Expr(Line);
public sealed record Call(int Line, string Name, List<Expr> Args) : Expr(Line);
public sealed record Cast(int Line, string Type, Expr E) : Expr(Line);

public abstract record Stmt(int Line);
public sealed record Block(int Line, List<Stmt> Body) : Stmt(Line);
public sealed record Decl(int Line, string Type, List<(string name, Expr? init)> Vars) : Stmt(Line);
public sealed record ExprStmt(int Line, Expr E) : Stmt(Line);
public sealed record If(int Line, Expr Cond, Stmt Then, Stmt? Else) : Stmt(Line);
public sealed record For(int Line, Stmt? Init, Expr? Cond, Expr? Step, Stmt Body) : Stmt(Line);
public sealed record While(int Line, Expr Cond, Stmt Body) : Stmt(Line);
public sealed record Return(int Line, Expr? Value) : Stmt(Line);
public sealed record Empty(int Line) : Stmt(Line);

public sealed record Function(string ReturnType, string Name, List<(string type, string name)> Params, Block Body, string File);

public sealed class ParseError(string message) : Exception(message);

public sealed class Parser
{
    public static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        { "void", "s32", "f32", "mcString", "mcBool", "EntityID", "u32", "s16", "u16", "u8", "s8", "bool", "int", "float" };

    private readonly List<Token> _t; private int _p; private readonly string _file;
    private Parser(List<Token> t, string file) { _t = t; _file = file; }

    public static List<Function> ParseFile(string src, string file)
    {
        var p = new Parser(Lexer.Tokenize(src), file);
        var res = new List<Function>();
        while (p.Peek.Kind != Tok.End)
        {
            if (p.IsOp(";")) { p._p++; continue; }
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
        if (!IsOp(op)) throw new ParseError($"{_file}:{Peek.Line} « {op} » attendu, trouvé « {Peek.Text} »");
        _p++;
    }
    private string ExpectIdent()
    {
        if (Peek.Kind != Tok.Ident) throw new ParseError($"{_file}:{Peek.Line} nom attendu, trouvé « {Peek.Text} »");
        return Next().Text;
    }
    private bool AtType => Peek.Kind == Tok.Ident && Types.Contains(Peek.Text)
        && _t[_p + 1].Kind == Tok.Ident; // « s32 i » mais pas un appel

    private Function ParseFunction()
    {
        var ret = ExpectIdent();
        var name = ExpectIdent();
        Expect("(");
        var ps = new List<(string, string)>();
        while (!IsOp(")"))
        {
            var ty = ExpectIdent();
            if (ty == "void" && IsOp(")")) break;
            if (IsOp("&")) _p++;
            var pn = ExpectIdent();
            ps.Add((ty, pn));
            if (IsOp(",")) _p++;
        }
        Expect(")");
        if (IsOp(";")) { _p++; return new Function(ret, name, ps, new Block(Peek.Line, new()), _file); } // prototype
        return new Function(ret, name, ps, ParseBlock(), _file);
    }

    private Block ParseBlock()
    {
        int line = Peek.Line; Expect("{");
        var body = new List<Stmt>();
        while (!IsOp("}"))
        {
            if (Peek.Kind == Tok.End) throw new ParseError($"{_file}: accolade fermante manquante (bloc ligne {line})");
            body.Add(ParseStmt());
        }
        _p++;
        return new Block(line, body);
    }

    private Stmt ParseStmt()
    {
        int line = Peek.Line;
        if (IsOp("{")) return ParseBlock();
        if (IsOp(";")) { _p++; return new Empty(line); }
        if (IsIdent("if"))
        {
            _p++; Expect("("); var c = ParseExpr(); Expect(")");
            var th = ParseStmt();
            Stmt? el = null;
            if (IsIdent("else")) { _p++; el = ParseStmt(); }
            return new If(line, c, th, el);
        }
        if (IsIdent("for"))
        {
            _p++; Expect("(");
            Stmt? init = null;
            if (!IsOp(";")) init = AtType ? ParseDecl() : new ExprStmt(line, ParseExpr());
            Expect(";");
            Expr? cond = IsOp(";") ? null : ParseExpr(); Expect(";");
            Expr? step = IsOp(")") ? null : ParseExpr(); Expect(")");
            return new For(line, init, cond, step, ParseStmt());
        }
        if (IsIdent("while")) { _p++; Expect("("); var c = ParseExpr(); Expect(")"); return new While(line, c, ParseStmt()); }
        if (IsIdent("return"))
        {
            _p++;
            Expr? v = IsOp(";") ? null : ParseExpr();
            Expect(";"); return new Return(line, v);
        }
        if (AtType) { var d = ParseDecl(); Expect(";"); return d; }
        var e = ParseExpr();
        Expect(";");
        return new ExprStmt(line, e);
    }

    private Decl ParseDecl()
    {
        int line = Peek.Line; var ty = ExpectIdent();
        var vars = new List<(string, Expr?)>();
        do
        {
            if (vars.Count > 0) _p++;
            var n = ExpectIdent();
            Expr? init = null;
            if (IsOp("=")) { _p++; init = ParseExpr(); }
            vars.Add((n, init));
        } while (IsOp(","));
        return new Decl(line, ty, vars);
    }

    // ---------- expressions (précédence de C) ----------
    public Expr ParseExpr() => ParseAssign();

    private Expr ParseAssign()
    {
        var left = ParseOr();
        if (Peek.Kind == Tok.Op && Peek.Text is "=" or "+=" or "-=" or "*=" or "/=")
        {
            int line = Peek.Line; var op = Next().Text;
            if (left is not Var v) throw new ParseError($"{_file}:{line} affectation impossible");
            return new Assign(line, op, v.Name, ParseAssign());
        }
        return left;
    }
    private Expr ParseOr() { var l = ParseAnd(); while (IsOp("||")) { int ln = Next().Line; l = new Binary(ln, "||", l, ParseAnd()); } return l; }
    private Expr ParseAnd() { var l = ParseEq(); while (IsOp("&&")) { int ln = Next().Line; l = new Binary(ln, "&&", l, ParseEq()); } return l; }
    private Expr ParseEq() { var l = ParseRel(); while (IsOp("==") || IsOp("!=")) { var t = Next(); l = new Binary(t.Line, t.Text, l, ParseRel()); } return l; }
    private Expr ParseRel() { var l = ParseAdd(); while (IsOp("<") || IsOp(">") || IsOp("<=") || IsOp(">=")) { var t = Next(); l = new Binary(t.Line, t.Text, l, ParseAdd()); } return l; }
    private Expr ParseAdd() { var l = ParseMul(); while (IsOp("+") || IsOp("-")) { var t = Next(); l = new Binary(t.Line, t.Text, l, ParseMul()); } return l; }
    private Expr ParseMul() { var l = ParseUnary(); while (IsOp("*") || IsOp("/") || IsOp("%")) { var t = Next(); l = new Binary(t.Line, t.Text, l, ParseUnary()); } return l; }
    private Expr ParseUnary()
    {
        if (IsOp("-") || IsOp("!") || IsOp("+")) { var t = Next(); return new Unary(t.Line, t.Text, ParseUnary()); }
        if (IsOp("++") || IsOp("--"))
        {
            var t = Next(); var name = ExpectIdent();
            return new Assign(t.Line, t.Text == "++" ? "+=" : "-=", name, new Num(t.Line, 1, true));
        }
        // conversion de type : (f32) x
        if (IsOp("(") && _t[_p + 1].Kind == Tok.Ident && Types.Contains(_t[_p + 1].Text) && _t[_p + 2].Kind == Tok.Op && _t[_p + 2].Text == ")")
        {
            int line = Peek.Line; _p++; var ty = Next().Text; _p++;
            return new Cast(line, ty, ParseUnary());
        }
        return ParsePostfix();
    }
    private Expr ParsePostfix()
    {
        var e = ParsePrimary();
        if ((IsOp("++") || IsOp("--")) && e is Var v)
        {
            var t = Next(); return new Assign(t.Line, t.Text == "++" ? "+=" : "-=", v.Name, new Num(t.Line, 1, true));
        }
        return e;
    }
    private Expr ParsePrimary()
    {
        var t = Next();
        switch (t.Kind)
        {
            case Tok.Int: return new Num(t.Line, double.Parse(t.Text, CultureInfo.InvariantCulture), true);
            case Tok.Float: return new Num(t.Line, double.Parse(t.Text, CultureInfo.InvariantCulture), false);
            case Tok.Str: return new Str(t.Line, t.Text);
            case Tok.Ident:
                if (IsOp("("))
                {
                    _p++;
                    var args = new List<Expr>();
                    while (!IsOp(")")) { args.Add(ParseExpr()); if (IsOp(",")) _p++; else if (!IsOp(")")) throw new ParseError($"{_file}:{Peek.Line} « , » ou « ) » attendu dans l'appel de {t.Text}"); }
                    _p++;
                    return new Call(t.Line, t.Text, args);
                }
                return new Var(t.Line, t.Text);
            case Tok.Op when t.Text == "(":
                var e = ParseExpr(); Expect(")"); return e;
        }
        throw new ParseError($"{_file}:{t.Line} expression attendue, trouvé « {t.Text} »");
    }
}
