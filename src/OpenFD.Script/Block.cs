using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Block(int Line, List<Stmt> Body) : Stmt(Line);
