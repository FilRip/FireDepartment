using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Return(int Line, Expr? Value) : Stmt(Line);
