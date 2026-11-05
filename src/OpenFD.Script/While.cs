using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record While(int Line, Expr Cond, Stmt Body) : Stmt(Line);
