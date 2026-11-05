using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Empty(int Line) : Stmt(Line);
