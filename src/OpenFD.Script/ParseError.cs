using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed class ParseError(string message) : Exception(message);
