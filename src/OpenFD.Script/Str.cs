using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Str(int Line, string Value) : Expr(Line);
