using System.Globalization;

namespace OpenFD.Script;

/// <summary>Le moteur de jeu vu par les scripts.</summary>
public interface IScriptHost
{
    double GameTime { get; }
    /// <summary>Renvoie null si la fonction n'est pas connue du moteur.</summary>
    Value? Call(string name, IReadOnlyList<Arg> args);
}
