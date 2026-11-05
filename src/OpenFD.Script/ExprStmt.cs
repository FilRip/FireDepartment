using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record ExprStmt(int Line, Expr E) : Stmt(Line);
