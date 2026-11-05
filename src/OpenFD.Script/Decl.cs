using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Decl(int Line, string Type, List<(string name, Expr? init)> Vars) : Stmt(Line);
