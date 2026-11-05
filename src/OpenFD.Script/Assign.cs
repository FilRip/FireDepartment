using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Assign(int Line, string Op, string Name, Expr Value) : Expr(Line);
