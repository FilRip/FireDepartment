using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Passage au grappin : repère « koshka_symbol » (« кошка », le grappin) posé en haut d'un mur. Dans la grille du jeu,
/// les cases du mur sous le repère sont ouvertes (le jeu y met un lien « grappin ») : seul un pompier qui a le grappin
/// ([CAPACITIES_ACTIONS] CanGrapplin, le « scout ») peut y passer, en grimpant d'un côté et en redescendant de l'autre.
/// </summary>
public sealed class GrapplePoint
{
    public required uint Id { get; init; }
    public required Vector3 Top { get; init; }        // haut du mur (repère)
    public required Vector2 Axis { get; init; }       // le long du mur
    public required Vector2 Perp { get; init; }       // en travers du mur
    public readonly HashSet<int> Cells = new();
    public Vector2 SideA, SideB;                      // points au sol de part et d'autre (côté +Perp, côté −Perp)
}
