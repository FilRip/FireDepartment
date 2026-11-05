using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public static class Lexer
{
    private static readonly string[] Ops3 = { };
    private static readonly string[] Ops2 = { "++", "--", "+=", "-=", "*=", "/=", "==", "!=", "<=", ">=", "&&", "||" };

    public static List<Token> Tokenize(string src)
    {
        List<Token> toks = new List<Token>();
        int i = 0, line = 1;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n')
                    i++;
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                {
                    if (src[i] == '\n')
                        line++;
                    i++;
                }
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
                while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.'))
                {
                    if (src[i] == '.')
                        flt = true;
                    i++;
                }
                if (i < src.Length && (src[i] == 'f' || src[i] == 'F'))
                {
                    flt = true;
                    i++;
                    toks.Add(new Token(Tok.Float, src[s..(i - 1)], line));
                    continue;
                }
                toks.Add(new Token(flt ? Tok.Float : Tok.Int, src[s..i], line)); continue;
            }
            if (c == '"')
            {
                StringBuilder sb = new StringBuilder(); i++;
                while (i < src.Length && src[i] != '"' && src[i] != '\n')
                {
                    if (src[i] == '\\' && i + 1 < src.Length)
                    {
                        sb.Append(src[i + 1] switch { 'n' => '\n', 't' => '\t', _ => src[i + 1] });
                        i += 2;
                        continue;
                    }
                    sb.Append(src[i++]);
                }
                i++; toks.Add(new Token(Tok.Str, sb.ToString(), line)); continue;
            }
            if (i + 1 < src.Length && Ops2.Contains(src.Substring(i, 2)))
            {
                toks.Add(new Token(Tok.Op, src.Substring(i, 2), line));
                i += 2;
                continue;
            }
            toks.Add(new Token(Tok.Op, c.ToString(), line)); i++;
        }
        toks.Add(new Token(Tok.End, "", line));
        return toks;
    }
}
