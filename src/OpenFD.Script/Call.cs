using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Call(int Line, string Name, List<Expr> Args) : Expr(Line);
