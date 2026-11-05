using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record For(int Line, Stmt? Init, Expr? Cond, Expr? Step, Stmt Body) : Stmt(Line);
