using System.Globalization;

namespace OpenFD.Script;

/// <summary>Erreur qui doit arrêter toute la fonction (boucle infinie, récursion) : on ne passe pas à la ligne suivante.</summary>
public sealed class ScriptAbort(string message) : ScriptError(message);
