using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public readonly record struct Token(Tok Kind, string Text, int Line);
