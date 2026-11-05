using System.Globalization;
using System.Text;

namespace OpenFD.Script;

// ---------- arbre syntaxique ----------
public abstract record Expr(int Line);
