using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Cast(int Line, string Type, Expr E) : Expr(Line);
