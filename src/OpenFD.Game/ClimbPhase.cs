using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>Phases du passage d'un mur au grappin (animations SingleChar\sc_grappling_hook_…).</summary>
public enum ClimbPhase { None, Launch, Climb, Stand, Step, Turn, Top, Descend, Land }
