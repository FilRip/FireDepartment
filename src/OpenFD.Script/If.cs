using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record If(int Line, Expr Cond, Stmt Then, Stmt? Else) : Stmt(Line);
