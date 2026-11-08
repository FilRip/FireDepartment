using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>Civil à sauver (entité Civilian de la carte ; état de départ lu dans le composant A02A).</summary>
public sealed class Civilian
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required string Model { get; init; }
    public string ClassPath = "";                         // Design\Unit\Civilian\….class (animations)
    public Vector3 Position;
    public float Heading;
    public CivilianState State;
    public Unit? Leader;                                  // pompier ou médecin qu'il suit
    public Unit? Carrier;                                 // pompier qui le porte (inconscient)
    public bool Hidden;                                   // caché au départ par le script de mission
    public readonly Queue<Vector2> Path = new();
    /// <summary>Chemin vers son guide (calculé sur la grille), but visé et délai avant de le recalculer.</summary>
    public readonly Queue<Vector2> FollowPath = new();
    public Vector2 FollowGoal;
    public float Replan;
    public float RunSpeed = 280f;                         // vitesse d'un trajet commandé par le script
    /// <summary>
    /// Mené par le script (IA_CmdMoveTo) : il déclenche les zones où il entre, comme une unité (Port : le conducteur du
    /// train qui rejoint sa locomotive). Un civil qui suit un pompier ou qu'on porte, non : le pompier les déclenche déjà.
    /// </summary>
    public bool ScriptWalk;
    /// <summary>Escalier en cours (il suit son guide dans un escalier : il le monte ou le descend d'un bout à l'autre).</summary>
    public Teleport? Stairs;
    public float StairsTimer;
    public Vector3 StairsFrom;
    public Vector3 StairsTo;
}
