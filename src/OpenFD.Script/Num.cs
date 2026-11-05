using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Num(int Line, double Value, bool IsInt) : Expr(Line);
