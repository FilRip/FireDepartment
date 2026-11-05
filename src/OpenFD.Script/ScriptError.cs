using System.Globalization;

namespace OpenFD.Script;

public class ScriptError(string message) : Exception(message);
