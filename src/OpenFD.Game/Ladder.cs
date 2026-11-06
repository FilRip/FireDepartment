using System.Numerics;

namespace OpenFD.Game;

/// <summary>
/// Échelle d'un camion échelle dressée devant une fenêtre d'étage : la tourelle tourne, l'échelle se lève (Raise de 0 à 1)
/// puis s'allonge (Extend, à Ladder_Translation_Speed). Dressée et la fenêtre ouverte ou cassée, elle relie le sol (Foot)
/// à l'étage (Inside) pour les pompiers qui savent monter à l'échelle (CanLadder).
/// </summary>
public sealed class Ladder
{
    public required Unit Truck { get; init; }
    public required Opening Window { get; init; }
    /// <summary>Bout de l'échelle : devant la fenêtre, sous l'appui (là où se tient le pompier en haut).</summary>
    public Vector3 Tip;
    /// <summary>Pied de l'échelle sur le camion (charnière), en coordonnées du monde.</summary>
    public Vector3 Base;
    /// <summary>Au sol derrière le camion : là où l'on monte ; dans l'étage, derrière la fenêtre : là où l'on arrive.</summary>
    public Vector2 Foot, Inside;
    public int FootCell = -1, InsideCell = -1;
    /// <summary>Camion arrivé et échelle calculée (avant : le camion roule vers la fenêtre).</summary>
    public bool Placed;
    /// <summary>Tourelle et levée de l'échelle (0 couchée, 1 dressée) ; allongement en cours et voulu (au-delà de la longueur repliée).</summary>
    public float Raise, Extend, ExtendWanted;
    /// <summary>Angles voulus dans le repère du modèle : rotation de la tourelle, inclinaison de l'échelle (radians).</summary>
    public float Yaw, Pitch;
    public bool Retracting;
    public bool Ready => Placed && !Retracting && Raise >= 1 && Extend >= ExtendWanted - 0.5f;
}
