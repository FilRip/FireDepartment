using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Binary(int Line, string Op, Expr L, Expr R) : Expr(Line);
