using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Unary(int Line, string Op, Expr E) : Expr(Line);
