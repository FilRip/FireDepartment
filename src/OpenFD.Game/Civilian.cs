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
    public Vector2 FollowGoal; public float Replan;
    public float RunSpeed = 280f;                         // vitesse d'un trajet commandé par le script
}
