using System.Globalization;

namespace OpenFD.Script;

/// <summary>Argument passé à une fonction du moteur ; certains sont modifiables (ENT_GetPosition(e, x, y)).</summary>
public sealed class Arg
{
    public Value Value;
    internal Action<Value>? Setter;
    public void Set(Value v) => Setter?.Invoke(v);
}
