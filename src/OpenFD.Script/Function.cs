using System.Globalization;
using System.Text;

namespace OpenFD.Script;

public sealed record Function(string ReturnType, string Name, List<(string type, string name)> Params, Block Body, string File);
