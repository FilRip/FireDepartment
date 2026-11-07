using System.Numerics;

namespace OpenFD.Game;

/// <summary>Largage d'eau demandé au bombardier (caserne « AS1 ») : point visé, point réel du largage, horaires du passage.</summary>
public sealed class AirStrike
{
    /// <summary>Point visé par le joueur ; point réel (tiré au hasard à RandomRadius près, [AIRSTRIKE] RANDOMRADIUS).</summary>
    public Vector2 Target, Drop;
    /// <summary>Moment de l'appel ; moment où l'avion est au-dessus du point (largage) ; fin du passage.</summary>
    public float Called, Pass, End;
    /// <summary>Direction de vol (radians).</summary>
    public float Heading;
    public bool Dropped;
}
